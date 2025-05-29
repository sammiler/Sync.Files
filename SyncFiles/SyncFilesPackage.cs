using Microsoft.VisualStudio; // For ErrorHandler
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop; // For IVsWindowFrame, IVsUIShell
using SyncFiles.Commands;

// 你的核心和服务类所在的命名空间
using SyncFiles.Core.Management;
using SyncFiles.Core.Services;
using SyncFiles.UI.ToolWindows; // SyncFilesToolWindow 和 SyncFilesToolWindowControl
using SyncFiles.UI.ViewModels;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows; // For MessageBox (or use VS services for notifications)
using Task = System.Threading.Tasks.Task;

namespace SyncFiles // 确保这是你的 VSIX 项目的根命名空间
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("#110", "#112", "1.0", IconResourceID = 400)] // About box info
    [ProvideMenuResource("Menus.ctmenu", 1)] // For command registration (VSCT file)
    [ProvideToolWindow(typeof(SyncFilesToolWindow), Style = VsDockStyle.Tabbed, Window = EnvDTE.Constants.vsWindowKindSolutionExplorer)] // 注册工具窗口
    [Guid(SyncFilesPackage.PackageGuidString)]
    [ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)] // Fixed CS0117 by using "SolutionExists" instead of "SolutionExists_string"

    public sealed class SyncFilesPackage : AsyncPackage
    {
        public const string PackageGuidString = "f844e235-75b7-4cf6-8e53-4f5cb0866969"; // 你提供的 GUID

        // 单例模式或服务容器来持有服务和 ViewModel 实例
        public static SyncFilesToolWindowViewModel ToolWindowViewModel { get; private set; }
        // 服务实例
        private SyncFilesSettingsManager _settingsManager;
        private GitHubSyncService _gitHubSyncService;
        private FileSystemWatcherService _fileSystemWatcherService;
        private SmartWorkflowService _smartWorkflowService;
        // private ScriptExecutor _scriptExecutor; // ScriptExecutor 通常由调用者按需创建

        private string _projectBasePath; // 将存储当前项目的路径

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            System.Diagnostics.Debug.WriteLine("[INFO] [PACKAGE_INIT] Initializing SyncFilesPackage...");
            await base.InitializeAsync(cancellationToken, progress);

            // 切换到 UI 线程进行需要 UI 访问的操作
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            // 1. 获取当前项目/解决方案路径 (这是一个简化的示例)
            // 在实际中，项目路径的获取和更新会更复杂，需要监听解决方案事件
            EnvDTE.DTE dte = await GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            if (dte != null && dte.Solution != null && !string.IsNullOrEmpty(dte.Solution.FullName))
            {
                _projectBasePath = System.IO.Path.GetDirectoryName(dte.Solution.FullName);
                // 如果没有解决方案打开，但有项目打开，逻辑会更复杂
                // 对于以项目为中心的插件，通常监听 SolutionEvents.Opened 和 ProjectEvents.ProjectOpened
            }
            else
            {
                // 没有解决方案打开，可能需要禁用某些功能或使用默认/全局设置路径
                _projectBasePath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); // 临时回退
                MessageBox.Show("No solution loaded. SyncFiles functionality may be limited or use default paths.", "SyncFiles Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // 如果 _projectBasePath 获取失败，应有更好的处理
            if (string.IsNullOrEmpty(_projectBasePath))
            {
                _projectBasePath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); // 最终回退
                Console.WriteLine("[ERROR] [PACKAGE_INIT] Failed to determine project base path. Using MyDocuments as fallback.");
            }
            Console.WriteLine($"[INFO] [PACKAGE_INIT] Project base path set to: {_projectBasePath}");


            // 2. 初始化核心服务
            _settingsManager = new SyncFilesSettingsManager(); // 它内部处理路径
            _gitHubSyncService = new GitHubSyncService(_projectBasePath);
            _fileSystemWatcherService = new FileSystemWatcherService(_projectBasePath);
            _smartWorkflowService = new SmartWorkflowService(_projectBasePath, _settingsManager, _gitHubSyncService);
            // _scriptExecutor 不需要在这里全局实例化，因为它通常按需创建

            // 3. 初始化 ViewModel (单例)
            ToolWindowViewModel = new SyncFilesToolWindowViewModel();
            await ToolWindowViewModel.InitializeAsync(
                _projectBasePath,
                _settingsManager,
                _gitHubSyncService,
                _fileSystemWatcherService,
                _smartWorkflowService
            );

            // 4. 初始化打开工具窗口的命令
            await ShowToolWindowCommand.InitializeAsync(this); // 假设你有一个 ShowToolWindowCommand 类

            Console.WriteLine("[INFO] [PACKAGE_INIT] SyncFilesPackage initialized successfully.");
        }

        /// <summary>
        /// Shows the tool window.
        /// </summary>
        public async Task ShowToolWindowAsync()
        {
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);

            try
            {
                ToolWindowPane window = await this.FindToolWindowAsync(typeof(SyncFilesToolWindow), 0, true, DisposalToken);
                if (window == null || window.Frame == null)
                {
                    throw new NotSupportedException("Cannot create tool window " + nameof(SyncFilesToolWindow));
                }

                // 将 ViewModel 实例传递给工具窗口的 UserControl
                // SyncFilesToolWindow 的构造函数会创建 SyncFilesToolWindowControl
                // 我们需要在 SyncFilesToolWindow 中获取该 Control 并设置其 DataContext
                if (window is SyncFilesToolWindow customToolWindow && customToolWindow.ToolWindowControl != null)
                {
                    // 确保 ViewModel 已经初始化
                    if (ToolWindowViewModel == null)
                    {
                        // 这种情况理论上不应该发生，因为 InitializeAsync 应该先运行
                        // 但作为防御，可以尝试再次初始化
                        // (或者更好地处理初始化顺序和依赖)
                        EnvDTE.DTE dte = await GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                        if (dte != null && dte.Solution != null && !string.IsNullOrEmpty(dte.Solution.FullName))
                            _projectBasePath = System.IO.Path.GetDirectoryName(dte.Solution.FullName);
                        else _projectBasePath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

                        _settingsManager = new SyncFilesSettingsManager();
                        _gitHubSyncService = new GitHubSyncService(_projectBasePath);
                        _fileSystemWatcherService = new FileSystemWatcherService(_projectBasePath);
                        _smartWorkflowService = new SmartWorkflowService(_projectBasePath, _settingsManager, _gitHubSyncService);

                        ToolWindowViewModel = new SyncFilesToolWindowViewModel();
                        await ToolWindowViewModel.InitializeAsync(_projectBasePath, _settingsManager, _gitHubSyncService, _fileSystemWatcherService, _smartWorkflowService);
                        Console.WriteLine("[WARN] [PACKAGE_SHOW_TOOL_WINDOW] ViewModel was null and re-initialized.");
                    }
                    customToolWindow.ToolWindowControl.DataContext = ToolWindowViewModel;
                    Console.WriteLine("[INFO] [PACKAGE_SHOW_TOOL_WINDOW] DataContext set for ToolWindowControl.");
                }


                IVsWindowFrame windowFrame = (IVsWindowFrame)window.Frame;
                ErrorHandler.ThrowOnFailure(windowFrame.Show());
                Console.WriteLine("[INFO] [PACKAGE_SHOW_TOOL_WINDOW] Tool window shown.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error showing tool window: {ex.Message}", "SyncFiles Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Console.WriteLine($"[ERROR] [PACKAGE_SHOW_TOOL_WINDOW] {ex.ToString()}");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // 释放由包创建的服务
                // ViewModel 也应该有机会释放它订阅的事件等
                ToolWindowViewModel?.Dispose(); // 如果 ViewModel 实现了 IDisposable
                _fileSystemWatcherService?.Dispose();
                _gitHubSyncService?.Dispose();
                _smartWorkflowService?.Dispose(); // 它内部会取消订阅 GitHubSyncService
            }
            base.Dispose(disposing);
        }
    }
}