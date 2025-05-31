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


        private readonly object _serviceLock = new object();
        private readonly object _configWatcherLock = new object();
        private CancellationTokenSource _configChangedDebounceCts;


        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
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

            System.Diagnostics.Debug.WriteLine("[INFO] [PACKAGE_INIT] SyncFilesPackage initialized successfully.");
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


        private void InitializeConfigWatcher()
        {
            this.JoinableTaskFactory.RunAsync(async () =>
            {
                await this.JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
                string currentProjectBasePath = await GetProjectBasePathAsync();

                lock (_configWatcherLock)
                {
                    StopConfigWatcherInternal();
                    if (!string.IsNullOrEmpty(currentProjectBasePath))
                    {
                        string vsFolderPath = Path.Combine(currentProjectBasePath, ".vs");
                        if (!Directory.Exists(vsFolderPath)) // Ensure .vs folder exists
                        {
                            try { Directory.CreateDirectory(vsFolderPath); }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[ERROR] [CONFIG_WATCH] Failed to create .vs directory: {ex.Message}");
                                return; // Cannot proceed without .vs folder
                            }
                        }
                        string configFilePath = Path.Combine(vsFolderPath, "syncFilesConfig.xml");

                        // FileSystemWatcher needs directory path, not file path for Path property
                        string directoryToWatch = Path.GetDirectoryName(configFilePath);
                        string fileToWatch = Path.GetFileName(configFilePath);

                        if (Directory.Exists(directoryToWatch))
                        {
                            try
                            {
                                _configWatcher = new FileSystemWatcher
                                {
                                    Path = directoryToWatch,
                                    Filter = fileToWatch,
                                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                                };
                                _configWatcher.Changed += OnConfigFileChangedDebounced;
                                _configWatcher.Created += OnConfigFileChangedDebounced;
                                _configWatcher.Deleted += OnConfigFileChangedDebounced;
                                _configWatcher.EnableRaisingEvents = true; // Enable after attaching handlers
                                ToolWindowViewModel?.AppendLogMessage($"Config watcher initialized for: {configFilePath}");
                                System.Diagnostics.Debug.WriteLine($"[CONFIG_WATCH] Config watcher initialized for: {configFilePath}");
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[ERROR] [CONFIG_WATCH] Failed to initialize config watcher for {configFilePath}: {ex.Message}");
                                ToolWindowViewModel?.AppendLogMessage($"[ERROR] Failed to initialize config watcher: {ex.Message}");
                            }
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[CONFIG_WATCH] Directory for config file not found: {directoryToWatch}. Watcher not started.");
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[CONFIG_WATCH] Project base path is null or empty. Config watcher not started.");
                    }
                }
            }).FileAndForget(nameof(InitializeConfigWatcher));
        }

        private void StopConfigWatcherInternal()
        {
            if (_configWatcher != null)
            {
                try
                {
                    _configWatcher.EnableRaisingEvents = false;
                    _configWatcher.Changed -= OnConfigFileChangedDebounced;
                    _configWatcher.Created -= OnConfigFileChangedDebounced;
                    _configWatcher.Deleted -= OnConfigFileChangedDebounced;
                    _configWatcher.Dispose();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ERROR] [CONFIG_WATCH] Exception during StopConfigWatcherInternal: {ex.ToString()}");
                }
                finally
                {
                    _configWatcher = null;
                }
            }
        }

        private void StopConfigWatcher()
        {
            lock (_configWatcherLock)
            {
                StopConfigWatcherInternal();
            }
            System.Diagnostics.Debug.WriteLine("[CONFIG_WATCH] Config watcher stopped (or attempt to stop completed).");
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