using Microsoft.VisualStudio; // For ErrorHandler
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop; // For IVsWindowFrame, IVsUIShell
using SyncFiles.Commands;
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
        public static SyncFilesToolWindowViewModel ToolWindowViewModel { get; private set; }
        private SyncFilesSettingsManager _settingsManager;
        private GitHubSyncService _gitHubSyncService;
        private FileSystemWatcherService _fileSystemWatcherService;
        private SmartWorkflowService _smartWorkflowService;
        private string _projectBasePath; // 将存储当前项目的路径
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            System.Diagnostics.Debug.WriteLine("[INFO] [PACKAGE_INIT] Initializing SyncFilesPackage...");
            await base.InitializeAsync(cancellationToken, progress);
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            EnvDTE.DTE dte = await GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            if (dte != null && dte.Solution != null && !string.IsNullOrEmpty(dte.Solution.FullName))
            {
                _projectBasePath = System.IO.Path.GetDirectoryName(dte.Solution.FullName);
            }
            else
            {
                _projectBasePath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); // 临时回退
                MessageBox.Show("No solution loaded. SyncFiles functionality may be limited or use default paths.", "SyncFiles Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            if (string.IsNullOrEmpty(_projectBasePath))
            {
                _projectBasePath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); // 最终回退
                Console.WriteLine("[ERROR] [PACKAGE_INIT] Failed to determine project base path. Using MyDocuments as fallback.");
            }
            Console.WriteLine($"[INFO] [PACKAGE_INIT] Project base path set to: {_projectBasePath}");
            _settingsManager = new SyncFilesSettingsManager(); // 它内部处理路径
            _gitHubSyncService = new GitHubSyncService(_projectBasePath);
            _fileSystemWatcherService = new FileSystemWatcherService(_projectBasePath);
            _smartWorkflowService = new SmartWorkflowService(_projectBasePath, _settingsManager, _gitHubSyncService);
            ToolWindowViewModel = new SyncFilesToolWindowViewModel();
            await ToolWindowViewModel.InitializeAsync(
                _projectBasePath,
                _settingsManager,
                _gitHubSyncService,
                _fileSystemWatcherService,
                _smartWorkflowService
            );
            await ShowToolWindowCommand.InitializeAsync(this); // 假设你有一个 ShowToolWindowCommand 类
            Console.WriteLine("[INFO] [PACKAGE_INIT] SyncFilesPackage initialized successfully.");
        }
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
                if (window is SyncFilesToolWindow customToolWindow && customToolWindow.ToolWindowControl != null)
                {
                    if (ToolWindowViewModel == null)
                    {
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
                ToolWindowViewModel?.Dispose(); // 如果 ViewModel 实现了 IDisposable
                _fileSystemWatcherService?.Dispose();
                _gitHubSyncService?.Dispose();
                _smartWorkflowService?.Dispose(); // 它内部会取消订阅 GitHubSyncService
            }
            base.Dispose(disposing);
        }
    }
}