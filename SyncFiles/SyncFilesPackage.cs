using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SyncFiles.Commands;
using SyncFiles.Core.Management;
using SyncFiles.Core.Services;
using SyncFiles.UI.ToolWindows;
using SyncFiles.UI.ViewModels;
using System;
using System.IO;
using System.Runtime.InteropServices; // For GuidAttribute
using System.Threading;
using System.Threading.Tasks; // For Task
using Microsoft.VisualStudio.Threading; // For JoinableTaskFactory extensions like FileAndForget

// Alias Task to avoid ambiguity if System.Windows.Tasks is ever referenced, though not strictly needed here.
// using Task = System.Threading.Tasks.Task; 
// No, MessageBox is System.Windows.MessageBox, so a direct using System.Windows is fine.

namespace SyncFiles
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("#110", "#112", "1.0", IconResourceID = 400)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(SyncFilesToolWindow), Style = VsDockStyle.Tabbed, Window = EnvDTE.Constants.vsWindowKindSolutionExplorer)]
    [Guid(SyncFilesPackage.PackageGuidString)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class SyncFilesPackage : AsyncPackage
    {
        public const string PackageGuidString = "f844e235-75b7-4cf6-8e53-4f5cb0866969";
        public static SyncFilesToolWindowViewModel ToolWindowViewModel { get; private set; }

        private string _projectBasePath = null; // Initialize to null
        private SyncFilesSettingsManager _settingsManager;
        public SyncFilesSettingsManager SettingsManager => _settingsManager;

        private GitHubSyncService _gitHubSyncService;
        private FileSystemWatcherService _fileSystemWatcherService;
        private SmartWorkflowService _smartWorkflowService;

        private readonly object _serviceLock = new object(); // For thread-safe updates if needed

        // ###################################################################################
        // #region Initialization and Lifecycle
        // ###################################################################################

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress); // Important: Call base first
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            System.Diagnostics.Debug.WriteLine("[INFO] [PACKAGE_INIT] Initializing SyncFilesPackage core components...");

            // 1. Initialize components that DO NOT depend on a project path immediately
            _settingsManager = new SyncFilesSettingsManager();

            // 2. Instantiate services - their constructors MUST handle a null project path gracefully
            //    They will be passed null initially for their project path.
            _gitHubSyncService = new GitHubSyncService(null);
            _fileSystemWatcherService = new FileSystemWatcherService(null);
            // SmartWorkflowService depends on settingsManager and gitHubSyncService instances
            _smartWorkflowService = new SmartWorkflowService(null, _settingsManager, _gitHubSyncService);

            // 3. Initialize ViewModels - their InitializeAsync MUST handle null path
            //    and potentially "unready" or path-unaware services.
            ToolWindowViewModel = new SyncFilesToolWindowViewModel();
            await ToolWindowViewModel.InitializeAsync(
                null, // Pass null for projectBasePath initially
                _settingsManager,
                _gitHubSyncService,
                _fileSystemWatcherService,
                _smartWorkflowService
            );

            // 4. Initialize commands
            //    Commands will call EnsureProjectSpecificServicesAsync before executing actions
            //    that require project context or fully initialized services.
            await ShowToolWindowCommand.InitializeAsync(this);
            await ShowSettingsWindowCommand.InitializeAsync(this, _settingsManager); // Pass package to get path resolver & settings manager

            // 5. Perform an initial attempt to set up project-specific parts
            //    This will try to get _projectBasePath and update services/ViewModel.
            await EnsureProjectSpecificServicesAsync();

            // 6. TODO: Subscribe to solution events (IVsSolutionEvents)
            //    In the event handlers (OnAfterOpenSolution, OnBeforeCloseSolution, OnAfterCloseSolution),
            //    call EnsureProjectSpecificServicesAsync() to react to project changes.
            //    This is crucial for robustly handling project open/close after package load.
            //    Example: SubscribeToSolutionEvents();

            Console.WriteLine("[INFO] [PACKAGE_INIT] SyncFilesPackage initialized successfully.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // TODO: Unsubscribe from solution events if subscribed.
                // UnsubscribeFromSolutionEvents();

                ToolWindowViewModel?.Dispose();
                _fileSystemWatcherService?.Dispose();
                _gitHubSyncService?.Dispose();
                _smartWorkflowService?.Dispose();
                // _settingsManager does not currently implement IDisposable.
            }
            base.Dispose(disposing);
        }

        // #endregion Initialization and Lifecycle

        // ###################################################################################
        // #region Project Context Management
        // ###################################################################################

        public async Task<string> GetProjectBasePathAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            string determinedPath = null;
            try
            {
                IVsSolution solutionService = await GetServiceAsync(typeof(SVsSolution)) as IVsSolution;
                if (solutionService != null)
                {
                    // Check if in "Open Folder" mode (includes CMake)
                    if (ErrorHandler.Succeeded(solutionService.GetProperty((int)__VSPROPID7.VSPROPID_IsInOpenFolderMode, out object isOpenFolderModeObj)) &&
                        isOpenFolderModeObj is bool isOpenFolderMode && isOpenFolderMode)
                    {
                        if (ErrorHandler.Succeeded(solutionService.GetSolutionInfo(out string openFolderPath, out _, out _)) &&
                            !string.IsNullOrEmpty(openFolderPath) && Directory.Exists(openFolderPath))
                        {
                            determinedPath = openFolderPath;
                            // System.Diagnostics.Debug.WriteLine($"[DEBUG] GetProjectBasePathAsync: Open Folder path = {determinedPath}");
                        }
                    }
                    else // Traditional solution mode
                    {
                        if (ErrorHandler.Succeeded(solutionService.GetSolutionInfo(out _, out string solutionFile, out _)) && // pszSolutionFile
                            !string.IsNullOrEmpty(solutionFile) && File.Exists(solutionFile))
                        {
                            determinedPath = Path.GetDirectoryName(solutionFile);
                            // System.Diagnostics.Debug.WriteLine($"[DEBUG] GetProjectBasePathAsync: Solution File path = {determinedPath}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ERROR] GetProjectBasePathAsync: Exception while getting path: {ex.Message}");
                // Optionally log to activity log
            }

            // _projectBasePath field is updated in EnsureProjectSpecificServicesAsync after this call
            return determinedPath;
        }

        // 文件: SyncFilesPackage.cs
        public async Task EnsureProjectSpecificServicesAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            string newDeterminedPath = await GetProjectBasePathAsync();

            bool pathEffectivelyChanged;
            lock (_serviceLock)
            {
                pathEffectivelyChanged = (_projectBasePath != newDeterminedPath);

                if (pathEffectivelyChanged)
                {
                    _projectBasePath = newDeterminedPath;
                    Console.WriteLine($"[INFO] Package._projectBasePath updated to: '{_projectBasePath ?? "null"}'");
                }

                _gitHubSyncService?.UpdateProjectPath(_projectBasePath);
                _fileSystemWatcherService?.UpdateProjectPath(_projectBasePath);
                _smartWorkflowService?.UpdateProjectPath(_projectBasePath, _gitHubSyncService);
            }

            if (ToolWindowViewModel != null)
            {
                // 即使路径在包级别没有变化，服务实例可能被重新创建或更新，
                // 或者我们只是想确保ViewModel与当前服务状态一致。
                // 让ViewModel的UpdateProjectContextAsync自己去判断是否需要做实质性的工作。
                JoinableTaskFactory.RunAsync(async () =>
                {
                    await ToolWindowViewModel.UpdateProjectContextAsync(
                        _projectBasePath, // Pass the package's current _projectBasePath
                        _gitHubSyncService,
                        _fileSystemWatcherService,
                        _smartWorkflowService
                    );
                }).FileAndForget("SyncFiles/VMContextUpdate");
            }
            Console.WriteLine("[INFO] Project-specific services and ViewModel context ensured.");
        }

        public static async Task<object> GetGlobalServiceAsync(Type serviceType)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            return await ServiceProvider.GetGlobalServiceAsync(serviceType);
        }
        public async Task ShowToolWindowAsync()
        {
            // 1. Ensure project context is up-to-date before showing window
            await EnsureProjectSpecificServicesAsync();

            // 2. Now proceed to show the window (already on UI thread from Ensure...)
            // await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken); // Might be redundant if Ensure already did

            try
            {
                ToolWindowPane window = await FindToolWindowAsync(typeof(SyncFilesToolWindow), 0, true, DisposalToken);
                if (window == null || window.Frame == null)
                {
                    throw new NotSupportedException("Cannot create tool window " + nameof(SyncFilesToolWindow));
                }

                if (window is SyncFilesToolWindow customToolWindow && customToolWindow.ToolWindowControl != null)
                {
                    // Ensure ViewModel is present (it should be from InitializeAsync)
                    if (ToolWindowViewModel == null)
                    {
                        // This case should ideally not happen if InitializeAsync is robust.
                        // Consider if re-creating VM here is the right approach or indicates a deeper issue.
                        Console.WriteLine("[WARN] [PACKAGE_SHOW_TOOL_WINDOW] ToolWindowViewModel was unexpectedly null. Attempting re-init (limited).");
                        ToolWindowViewModel = new SyncFilesToolWindowViewModel();
                        // A minimal re-init; a full re-init would need all services again.
                        await ToolWindowViewModel.InitializeAsync(_projectBasePath, _settingsManager, _gitHubSyncService, _fileSystemWatcherService, _smartWorkflowService);
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
                // Use VS Shell Utilities for user-facing messages
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken); // Ensure on UI thread for MessageBox
                VsShellUtilities.ShowMessageBox(
                    this,
                    $"Error showing tool window: {ex.Message}",
                    "SyncFiles Error",
                    OLEMSGICON.OLEMSGICON_CRITICAL,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                Console.WriteLine($"[ERROR] [PACKAGE_SHOW_TOOL_WINDOW] {ex.ToString()}");
            }
        }
        // #endregion Public Actions
    }
}