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
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;


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

        private string _projectBasePath = null;
        private SyncFilesSettingsManager _settingsManager;
        public SyncFilesSettingsManager SettingsManager => _settingsManager;

        private GitHubSyncService _gitHubSyncService;
        private FileSystemWatcherService _fileSystemWatcherService;
        private SmartWorkflowService _smartWorkflowService;
        private FileSystemWatcher _configWatcher;

        private System.Threading.Timer _windowCheckTimer;
        private readonly object _timerLock = new object();
        private bool _isTimerRunning = false;

        private readonly object _serviceLock = new object();
        private readonly object _configWatcherLock = new object();
        private CancellationTokenSource _configChangedDebounceCts;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            try
            {
                await base.InitializeAsync(cancellationToken, progress);
                await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

                System.Diagnostics.Debug.WriteLine("[INFO] [PACKAGE_INIT] Initializing SyncFilesPackage core components...");

                _settingsManager = new SyncFilesSettingsManager();

                _gitHubSyncService = new GitHubSyncService(null);
                _fileSystemWatcherService = new FileSystemWatcherService(null);
                _smartWorkflowService = new SmartWorkflowService(null, _settingsManager, _gitHubSyncService);

                ToolWindowViewModel = new SyncFilesToolWindowViewModel(this);
                await ToolWindowViewModel.InitializeAsync(
                    null,
                    _settingsManager,
                    _gitHubSyncService,
                    _fileSystemWatcherService,
                    _smartWorkflowService
                );

                await ShowToolWindowCommand.InitializeAsync(this);
                await ShowSettingsWindowCommand.InitializeAsync(this, _settingsManager);

                await EnsureProjectSpecificServicesAsync();
                InitializeConfigWatcher();

                StartWindowCheckTimer();

                System.Diagnostics.Debug.WriteLine("[INFO] [PACKAGE_INIT] SyncFilesPackage initialized successfully.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ERROR] [PACKAGE_INIT] Initialization failed: {ex}");
            }
        }

        private void StartWindowCheckTimer()
        {
            lock (_timerLock)
            {
                if (!_isTimerRunning)
                {
                    _windowCheckTimer = new System.Threading.Timer(
                        CheckAndCloseWindowCallback,
                        null,
                        TimeSpan.FromSeconds(1), // 1秒后开始
                        TimeSpan.FromSeconds(1)  // 每秒检查一次
                    );
                    _isTimerRunning = true;
                    System.Diagnostics.Debug.WriteLine("[INFO] Window check timer started");
                }
            }
        }

        private void StopWindowCheckTimer()
        {
            lock (_timerLock)
            {
                if (_isTimerRunning)
                {
                    _windowCheckTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                    _windowCheckTimer?.Dispose();
                    _windowCheckTimer = null;
                    _isTimerRunning = false;
                    System.Diagnostics.Debug.WriteLine("[INFO] Window check timer stopped");
                }
            }
        }

        private async void CheckAndCloseWindowCallback(object state)
        {
            try
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();

                var window = await FindToolWindowAsync(typeof(SyncFilesToolWindow), 0, false, DisposalToken);
                if (window?.Frame != null)
                {
                    IVsWindowFrame windowFrame = (IVsWindowFrame)window.Frame;

                    // 使用 VSFPROPID_WindowState 检查窗口状态
                    Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(
                        windowFrame.GetProperty((int)__VSFPROPID.VSFPROPID_WindowState, out object windowState));
                    if (windowState is int windowStateValue && windowStateValue == 0) // 1 表示窗口可见
                    {
                        // 窗口可见，关闭它
                        Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(windowFrame.Hide());
                        System.Diagnostics.Debug.WriteLine("[INFO] Tool window was found and closed");

                        // 停止定时器
                        StopWindowCheckTimer();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ERROR] Error in window check timer: {ex.Message}");
            }
        }

        public void TriggerReinitializeConfigWatcher()
        {
            InitializeConfigWatcher();
        }

        public void SuspendConfigWatcher()
        {
            lock (_configWatcherLock)
            {
                if (_configWatcher != null && _configWatcher.EnableRaisingEvents)
                {
                    _configWatcher.EnableRaisingEvents = false;
                    System.Diagnostics.Debug.WriteLine("[CONFIG_WATCH] Config watcher suspended.");
                }
            }
        }

        public void ResumeConfigWatcher(bool triggerRefresh)
        {
            lock (_configWatcherLock)
            {
                if (_configWatcher != null && !_configWatcher.EnableRaisingEvents)
                {
                    // Ensure the path is still valid before re-enabling
                    if (Directory.Exists(_configWatcher.Path))
                    {
                        _configWatcher.EnableRaisingEvents = true;
                        System.Diagnostics.Debug.WriteLine("[CONFIG_WATCH] Config watcher resumed.");
                        if (triggerRefresh)
                        {
                            // Manually trigger a refresh as if the file changed
                            // This ensures UI consistency after settings save + watcher resume
                            System.Diagnostics.Debug.WriteLine("[CONFIG_WATCH] Manually triggering refresh after resume.");
                            OnConfigFileChanged(_configWatcher, new FileSystemEventArgs(WatcherChangeTypes.Changed, _configWatcher.Path, _configWatcher.Filter));
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[CONFIG_WATCH] Cannot resume watcher, path {_configWatcher.Path} no longer exists.");
                        StopConfigWatcherInternal(); // Path invalid, stop it properly
                    }
                }
            }
        }

        // In SyncFilesPackage.cs


        private void StopConfigWatcherInternal()
        {
            // This method assumes _configWatcherLock is ALREADY HELD by the caller
            if (_configWatcher != null)
            {
                FileSystemWatcher watcherToStop = _configWatcher;

                string pathForLog = "N/A";
                try { pathForLog = watcherToStop.Path + Path.DirectorySeparatorChar + watcherToStop.Filter; } catch { }
                System.Diagnostics.Debug.WriteLine($"[CONFIG_WATCH] Internal stop for: {pathForLog}");

                try
                {
                    if (watcherToStop.EnableRaisingEvents)
                    {
                        watcherToStop.EnableRaisingEvents = false;
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ERROR] [CONFIG_WATCH] Internal Exception EnableRaisingEvents=false: {ex.ToString()} for {pathForLog}"); }

                try
                {
                    watcherToStop.Changed -= OnConfigFileChangedDebounced;
                    watcherToStop.Created -= OnConfigFileChangedDebounced;
                    watcherToStop.Deleted -= OnConfigFileChangedDebounced;
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ERROR] [CONFIG_WATCH] Internal Exception unsubscribing events: {ex.ToString()} for {pathForLog}"); }

                var capturedWatcher = watcherToStop;
                System.Diagnostics.Debug.WriteLine($"[CONFIG_WATCH] Queuing background Dispose for: {pathForLog}");
                _ = Task.Run(() =>
                {
                    try
                    {
                        System.Diagnostics.Debug.WriteLine($"[CONFIG_WATCH] BG Dispose for: {pathForLog}");
                        capturedWatcher.Dispose();
                        System.Diagnostics.Debug.WriteLine($"[CONFIG_WATCH] BG Dispose completed for: {pathForLog}");
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ERROR] [CONFIG_WATCH] BG Dispose ex: {ex.ToString()} for {pathForLog}"); }
                });
            }
        }

        private void StopConfigWatcher()
        {
            lock (_configWatcherLock)
            {
                if (_configWatcher != null) // Check again inside lock
                {
                    StopConfigWatcherInternal(); // Call the one that does the work
                    _configWatcher = null; // Ensure field is null after stopping attempt initiated
                }
            }
            System.Diagnostics.Debug.WriteLine("[CONFIG_WATCH] StopConfigWatcher method finished.");
        }

        private void OnConfigFileChangedDebounced(object sender, FileSystemEventArgs e)
        {
            _configChangedDebounceCts?.Cancel();
            _configChangedDebounceCts?.Dispose();
            _configChangedDebounceCts = new CancellationTokenSource();
            CancellationToken token = _configChangedDebounceCts.Token;

            this.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await Task.Delay(500, token); // Debounce for 500ms
                    if (token.IsCancellationRequested) return;

                    OnConfigFileChanged(sender, e); // Call the actual handler
                }
                catch (TaskCanceledException) { /* Expected if debounced */ }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ERROR] Debounce task for OnConfigFileChanged failed: {ex}");
                }
            }).FileAndForget("DebouncedConfigChange");
        }

        private void InitializeConfigWatcher()
        {
            this.JoinableTaskFactory.RunAsync(async () =>
            {
                await this.JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
                string currentProjectBasePath = await GetProjectBasePathAsync();

                lock (_configWatcherLock) // Lock before accessing/modifying _configWatcher
                {
                    StopConfigWatcherInternal(); // Stop existing one first
                    _configWatcher = null;       // Explicitly nullify after stop, before new creation

                    if (!string.IsNullOrEmpty(currentProjectBasePath))
                    {
                        // ... (rest of the initialization logic to create and assign to _configWatcher) ...
                        // (Make sure this part is also within the lock if it assigns to _configWatcher)
                        string vsFolderPath = Path.Combine(currentProjectBasePath, ".vs");
                        if (!Directory.Exists(vsFolderPath))
                        {
                            try { Directory.CreateDirectory(vsFolderPath); }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[ERROR] [CONFIG_WATCH] Failed to create .vs directory: {ex.Message}");
                                return;
                            }
                        }
                        string configFilePath = Path.Combine(vsFolderPath, "syncFilesConfig.xml");
                        string directoryToWatch = Path.GetDirectoryName(configFilePath);
                        string fileToWatch = Path.GetFileName(configFilePath);

                        if (Directory.Exists(directoryToWatch))
                        {
                            try
                            {
                                var newWatcher = new FileSystemWatcher // Create new instance
                                {
                                    Path = directoryToWatch,
                                    Filter = fileToWatch,
                                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                                };
                                newWatcher.Changed += OnConfigFileChangedDebounced;
                                newWatcher.Created += OnConfigFileChangedDebounced;
                                newWatcher.Deleted += OnConfigFileChangedDebounced;
                                newWatcher.EnableRaisingEvents = true;
                                _configWatcher = newWatcher; // Assign to field last, under lock
                                ToolWindowViewModel?.AppendLogMessage($"Config watcher initialized for: {configFilePath}");
                                System.Diagnostics.Debug.WriteLine($"[CONFIG_WATCH] Config watcher initialized for: {configFilePath}");
                            }
                            // ... (catch block)
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[ERROR] [CONFIG_WATCH] Failed to initialize config watcher for {configFilePath}: {ex.Message}");
                                ToolWindowViewModel?.AppendLogMessage($"[ERROR] Failed to initialize config watcher: {ex.Message}");
                                _configWatcher = null; // Ensure null on failure
                            }
                        }
                        // ...
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[CONFIG_WATCH] Project base path is null or empty. Config watcher not started.");
                        _configWatcher = null; // Ensure null if not started
                    }
                }
            }).FileAndForget(nameof(InitializeConfigWatcher));
        }
        private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
        {
            FileSystemWatcher currentConfigWatcher;
            lock (_configWatcherLock)
            {
                currentConfigWatcher = _configWatcher;
            }

            if (currentConfigWatcher == null || sender != currentConfigWatcher)
            {
                System.Diagnostics.Debug.WriteLine($"[CONFIG_WATCH] OnConfigFileChanged invoked but current watcher is null or sender mismatch. Path: {e.FullPath}");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[CONFIG_WATCH] Config file '{e.FullPath}' event: {e.ChangeType}. Requesting refresh.");

            var viewModel = ToolWindowViewModel;
            if (viewModel != null)
            {
                this.JoinableTaskFactory.RunAsync(async () =>
                {
                    try
                    {
                        await viewModel.LoadAndRefreshScriptsAsync(true);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ERROR] [CONFIG_REFRESH_ON_CHANGE_TASK] {ex.ToString()}");
                        if (ToolWindowViewModel != null)
                        {
                            ToolWindowViewModel.AppendLogMessage($"[ERROR] Failed to refresh on config change: {ex.Message}");
                        }
                    }
                }).FileAndForget(nameof(OnConfigFileChanged));
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[CONFIG_WATCH] ToolWindowViewModel is null, cannot refresh on config change.");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopWindowCheckTimer();
                _configChangedDebounceCts?.Cancel();
                _configChangedDebounceCts?.Dispose();
                StopConfigWatcher();

                ToolWindowViewModel?.Dispose();
                _fileSystemWatcherService?.Dispose();
                _gitHubSyncService?.Dispose();
                _smartWorkflowService?.Dispose();
            }
            base.Dispose(disposing);
        }

        public async Task<string> GetProjectBasePathAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            string determinedPath = null;
            try
            {
                IVsSolution solutionService = await GetServiceAsync(typeof(SVsSolution)) as IVsSolution;
                if (solutionService != null)
                {
                    if (Microsoft.VisualStudio.ErrorHandler.Succeeded(solutionService.GetProperty((int)__VSPROPID7.VSPROPID_IsInOpenFolderMode, out object isOpenFolderModeObj)) &&
                        isOpenFolderModeObj is bool isOpenFolderMode && isOpenFolderMode)
                    {
                        if (Microsoft.VisualStudio.ErrorHandler.Succeeded(solutionService.GetSolutionInfo(out string openFolderPath, out _, out _)) &&
                            !string.IsNullOrEmpty(openFolderPath) && Directory.Exists(openFolderPath))
                        {
                            determinedPath = openFolderPath;
                        }
                    }
                    else
                    {
                        if (Microsoft.VisualStudio.ErrorHandler.Succeeded(solutionService.GetSolutionInfo(out _, out string solutionFile, out _)) &&
                            !string.IsNullOrEmpty(solutionFile) && File.Exists(solutionFile))
                        {
                            determinedPath = Path.GetDirectoryName(solutionFile);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ERROR] GetProjectBasePathAsync: Exception while getting path: {ex.Message}");
            }
            return determinedPath;
        }

        public async Task EnsureProjectSpecificServicesAsync()
        {
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            string newDeterminedPath = await GetProjectBasePathAsync();

            bool pathEffectivelyChanged;
            lock (_serviceLock)
            {
                pathEffectivelyChanged = (_projectBasePath != newDeterminedPath);

                if (pathEffectivelyChanged)
                {
                    _projectBasePath = newDeterminedPath;
                    System.Diagnostics.Debug.WriteLine($"[INFO] Package._projectBasePath updated to: '{_projectBasePath ?? "null"}'");
                    InitializeConfigWatcher();
                }

                _gitHubSyncService?.UpdateProjectPath(_projectBasePath);
                _fileSystemWatcherService?.UpdateProjectPath(_projectBasePath);
                _smartWorkflowService?.UpdateProjectPath(_projectBasePath, _gitHubSyncService);
            }

            if (ToolWindowViewModel != null)
            {
                this.JoinableTaskFactory.RunAsync(async () =>
                {
                    await ToolWindowViewModel.UpdateProjectContextAsync(
                        _projectBasePath,
                        _gitHubSyncService,
                        _fileSystemWatcherService,
                        _smartWorkflowService
                    );
                }).FileAndForget("SyncFiles/VMContextUpdate");
            }
            System.Diagnostics.Debug.WriteLine("[INFO] Project-specific services and ViewModel context ensured.");
        }

        public static async Task<object> GetGlobalServiceAsync(Type serviceType)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            return await ServiceProvider.GetGlobalServiceAsync(serviceType);
        }
        public async Task ShowToolWindowAsync()
        {
            await EnsureProjectSpecificServicesAsync();
            try
            {
                ToolWindowPane window = await FindToolWindowAsync(typeof(SyncFilesToolWindow), 0, true, DisposalToken);
                if (window == null || window.Frame == null)
                {
                    throw new NotSupportedException("Cannot create tool window " + nameof(SyncFilesToolWindow));
                }

                if (window is SyncFilesToolWindow customToolWindow && customToolWindow.ToolWindowControl != null)
                {
                    if (ToolWindowViewModel == null)
                    {
                        System.Diagnostics.Debug.WriteLine("[WARN] [PACKAGE_SHOW_TOOL_WINDOW] ToolWindowViewModel was unexpectedly null. Attempting re-init (limited).");
                        ToolWindowViewModel = new SyncFilesToolWindowViewModel(this);
                        await ToolWindowViewModel.InitializeAsync(_projectBasePath, _settingsManager, _gitHubSyncService, _fileSystemWatcherService, _smartWorkflowService);
                    }
                    customToolWindow.ToolWindowControl.DataContext = ToolWindowViewModel;
                    System.Diagnostics.Debug.WriteLine("[INFO] [PACKAGE_SHOW_TOOL_WINDOW] DataContext set for ToolWindowControl.");
                }

                IVsWindowFrame windowFrame = (IVsWindowFrame)window.Frame;
                Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(windowFrame.Show());
                System.Diagnostics.Debug.WriteLine("[INFO] [PACKAGE_SHOW_TOOL_WINDOW] Tool window shown.");
            }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
                VsShellUtilities.ShowMessageBox(
                    this,
                    $"Error showing tool window: {ex.Message}",
                    "SyncFiles Error",
                    OLEMSGICON.OLEMSGICON_CRITICAL,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                System.Diagnostics.Debug.WriteLine($"[ERROR] [PACKAGE_SHOW_TOOL_WINDOW] {ex.ToString()}");
            }
        }
    }
}