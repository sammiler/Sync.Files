using Microsoft.VisualStudio.Shell;
using SyncFiles.Core.Management;
using Microsoft.VisualStudio.PlatformUI;
using System.Windows;
using SyncFiles.Core.Models;
using SyncFiles.Core.Services;
using SyncFiles.Core.Settings;
using SyncFiles.UI.Common;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell.Interop;
using EnvDTE;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio;
using System.Windows.Media;
using SyncFiles.UI.ToolWindows;
using SyncFiles.UI.Controls;
using System.Windows.Threading;

namespace SyncFiles.UI.ViewModels
{
    public class ScriptExecutionOutputLine
    {
        public DateTime Timestamp { get; }
        public string ScriptName { get; }
        public string Line { get; }
        public bool IsError { get; }
        public string DisplayLine => $"[{Timestamp:HH:mm:ss}] {(string.IsNullOrEmpty(ScriptName) ? "" : $"({ScriptName}) ")}{(IsError ? "ERROR: " : "")}{Line}";

        public ScriptExecutionOutputLine(string scriptName, string line, bool isError = false)
        {
            Timestamp = DateTime.Now;
            ScriptName = scriptName;
            Line = line;
            IsError = isError;
        }
    }
    public class SyncFilesToolWindowViewModel : ViewModelBase, IDisposable
    {
        private SyncFilesSettingsManager _settingsManager;
        private GitHubSyncService _gitHubSyncService;
        private FileSystemWatcherService _fileSystemWatcherService;
        private SmartWorkflowService _smartWorkflowService;
        private string _projectBasePath;
        private FileSystemWatcher _pythonScriptDirWatcher;
        public ICommand SaveSettingsCommand { get; }
        private CancellationTokenSource _workflowCts;
        public ObservableCollection<ScriptGroupViewModel> ScriptGroups { get; }
        public ICommand RefreshScriptsCommand { get; }
        public ICommand AddGroupCommand { get; }
        public ICommand SyncGitHubFilesCommand { get; }
        public ICommand LoadSmartWorkflowCommand { get; }
        public ICommand CancelWorkflowCommand { get; }
        private string _currentScriptExecutionStatus;
        // 添加到SyncFilesToolWindowViewModel类的私有字段
        private Dictionary<string, bool> _expandedStates = new Dictionary<string, bool>();
        public string CurrentScriptExecutionStatus
        {
            get => _currentScriptExecutionStatus;
            private set => SetProperty(ref _currentScriptExecutionStatus, value);
        }

        public ObservableCollection<ScriptExecutionOutputLine> ScriptOutputLog { get; }

        private bool _isScriptOutputVisible;
        public bool IsScriptOutputVisible
        {
            get => _isScriptOutputVisible;
            set => SetProperty(ref _isScriptOutputVisible, value);
        }

        /// <summary>
        /// 保存所有脚本组的展开状态
        /// </summary>
        private void SaveExpandedStates()
        {
            _expandedStates.Clear();

            foreach (var group in ScriptGroups)
            {
                // 保存组ID和它的展开状态
                _expandedStates[group.Id] = group.IsExpanded;
            }

            System.Diagnostics.Debug.WriteLine($"[INFO] 保存了 {_expandedStates.Count} 个组的展开状态");
        }

        /// <summary>
        /// 恢复所有脚本组的展开状态
        /// </summary>
        private void RestoreExpandedStates()
        {
            foreach (var group in ScriptGroups)
            {
                if (_expandedStates.TryGetValue(group.Id, out bool isExpanded))
                {
                    group.IsExpanded = isExpanded;
                    System.Diagnostics.Debug.WriteLine($"[INFO] 恢复组 '{group.Name}' 的展开状态: {isExpanded}");
                }
            }
        }
        public ICommand ClearScriptOutputCommand { get; }
        public ICommand ToggleScriptOutputVisibilityCommand { get; }
        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    (RefreshScriptsCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (AddGroupCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (SyncGitHubFilesCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (LoadSmartWorkflowCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (CancelWorkflowCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        private string _refreshIconPath;
        public string RefreshIconPath { get => _refreshIconPath; private set => SetProperty(ref _refreshIconPath, value); }

        private string _addGroupIconPath;
        public string AddGroupIconPath { get => _addGroupIconPath; private set => SetProperty(ref _addGroupIconPath, value); }

        private string _syncGitIconPath;
        public string SyncGitIconPath { get => _syncGitIconPath; private set => SetProperty(ref _syncGitIconPath, value); }

        private string _toggleOutputIconPath;
        public string ToggleOutputIconPath { get => _toggleOutputIconPath; private set => SetProperty(ref _toggleOutputIconPath, value); }


        private string _assemblyName;
        private readonly object _logMessagesLock = new object();
        private readonly IAsyncServiceProvider _serviceProvider;
        private string _statusMessage;
        private FileSystemWatcher _pythonScriptParentDirWatcher;
        private readonly object _pythonScriptDirWatcherLock = new object();
        private readonly object _pythonScriptParentWatcherLock = new object();
        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value);
        }
        public ObservableCollection<string> LogMessages { get; }

        public bool _isTerminalVisible = true;
        public bool IsTerminalVisible
        {
            get => _isTerminalVisible;
            set => SetProperty(ref _isTerminalVisible, value);
        }

        public ICommand ToggleTerminalVisibilityCommand { get; private set; }

        public SyncFilesToolWindowViewModel()
        {
            _serviceProvider = null;

            ScriptGroups = new ObservableCollection<ScriptGroupViewModel>();
            LogMessages = new ObservableCollection<string>();
            ScriptOutputLog = new ObservableCollection<ScriptExecutionOutputLine>();

            RefreshScriptsCommand = new RelayCommand(() => { }, () => false);
            AddGroupCommand = new RelayCommand(() => { }, () => false);
            SyncGitHubFilesCommand = new RelayCommand(() => { }, () => false);
            LoadSmartWorkflowCommand = new RelayCommand(() => { }, () => false);
            CancelWorkflowCommand = new RelayCommand(() => { }, () => false);
            SaveSettingsCommand = new RelayCommand(() => { }, () => false);
            ClearScriptOutputCommand = new RelayCommand(() => { }, () => false);
            ToggleScriptOutputVisibilityCommand = new RelayCommand(() => { });

            CurrentScriptExecutionStatus = "Ready (Design Time)";
            IsScriptOutputVisible = true;
            IsBusy = false;

            _assemblyName = "SyncFiles";
            RefreshIconPath = $"/SyncFiles;component/Resources/Refresh.png";
            AddGroupIconPath = $"/SyncFiles;component/Resources/AddGroup.png";
            SyncGitIconPath = $"/SyncFiles;component/Resources/SyncGit.png";
            ToggleOutputIconPath = $"/SyncFiles;component/Resources/ToggleOutput.png";

            ToggleTerminalVisibilityCommand = new RelayCommand(() =>
            {
                IsTerminalVisible = !IsTerminalVisible;
            });
        }

        public SyncFilesToolWindowViewModel(IAsyncServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

            ScriptGroups = new ObservableCollection<ScriptGroupViewModel>();
            LogMessages = new ObservableCollection<string>();

            RefreshScriptsCommand = new RelayCommand(
                async () =>
                {
                    if (_serviceProvider is SyncFilesPackage package)
                    {
                        package.TriggerReinitializeConfigWatcher();
                    }
                    await LoadAndRefreshScriptsAsync(true);
                },
                () => !IsBusy);

            AddGroupCommand = new RelayCommand(AddNewGroup, () => !IsBusy);

            SyncGitHubFilesCommand = new RelayCommand(
                async () => await ExecuteGitHubSyncAsync(false),
                () => !IsBusy && _gitHubSyncService != null && _settingsManager != null);

            LoadSmartWorkflowCommand = new RelayCommand(async () => await LoadSmartWorkflowAsync(), () => !IsBusy);
            CancelWorkflowCommand = new RelayCommand(CancelWorkflow, () => IsBusy);
            SaveSettingsCommand = new RelayCommand(RequestSaveSettings, () => !IsBusy);

            ScriptOutputLog = new ObservableCollection<ScriptExecutionOutputLine>();
            CurrentScriptExecutionStatus = "Ready.";
            IsScriptOutputVisible = false;

            ClearScriptOutputCommand = new RelayCommand(ClearScriptOutput, () => ScriptOutputLog.Any());
            ToggleScriptOutputVisibilityCommand = new RelayCommand(() => IsScriptOutputVisible = !IsScriptOutputVisible);

            _assemblyName = GetType().Assembly.GetName().Name;
            UpdateIconsForTheme();
            var themeService = ThemeService.Instance;

            ToggleTerminalVisibilityCommand = new RelayCommand(() =>
            {
                IsTerminalVisible = !IsTerminalVisible;
            });
        }
        public async Task InitializeAsync(
            string projectBasePath,
            SyncFilesSettingsManager settingsManager,
            GitHubSyncService gitHubSyncService,
            FileSystemWatcherService fileSystemWatcherService,
            SmartWorkflowService smartWorkflowService)
        {
            IsBusy = true;
            _projectBasePath = projectBasePath;
            _settingsManager = settingsManager;
            DetachEventHandlers();
            _gitHubSyncService = gitHubSyncService;
            _fileSystemWatcherService = fileSystemWatcherService;
            _smartWorkflowService = smartWorkflowService;
            AttachEventHandlers();

            await LoadAndRefreshScriptsAsync(true);
            // InitializePythonScriptWatcher(); // Called at the end of LoadAndRefreshScriptsAsync
            UpdateIconsForTheme(); // **** Add this to ensure icons are set after initial load ****

            AppendLogMessage("SyncFiles Tool Window initialized.");
            IsBusy = false;
        }

        private void OnThemeChanged(ThemeChangedEventArgs e)
        {
            if (Application.Current == null) return;

            Application.Current?.Dispatcher?.Invoke(() =>
            {
                UpdateIconsForTheme();
            });
        }

        private bool IsDarkTheme()
        {
            if (Application.Current == null) return false;

            if (Application.Current != null && Application.Current.Dispatcher.CheckAccess())
            {
                var backgroundColor = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
                return (backgroundColor.R * 0.299 + backgroundColor.G * 0.587 + backgroundColor.B * 0.114) < 128;
            }
            else if (Application.Current != null)
            {
                return Application.Current.Dispatcher.Invoke(() =>
                {
                    var backgroundColor = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
                    return (backgroundColor.R * 0.299 + backgroundColor.G * 0.587 + backgroundColor.B * 0.114) < 128;
                });
            }
            return false;
        }

        private void UpdateIconsForTheme()
        {
            if (Application.Current == null && _serviceProvider == null)
            {
                return;
            }

            bool isDark = ThemeService.Instance.IsDarkTheme;

            RefreshIconPath = $"/{_assemblyName};component/Resources/Refresh{(isDark ? "_dark" : "")}.png";
            AddGroupIconPath = $"/{_assemblyName};component/Resources/AddGroup{(isDark ? "_dark" : "")}.png";
            SyncGitIconPath = $"/{_assemblyName};component/Resources/SyncGit{(isDark ? "_dark" : "")}.png";
            ToggleOutputIconPath = $"/{_assemblyName};component/Resources/ToggleOutput{(isDark ? "_dark" : "")}.png";

            string pythonIcon = $"/{_assemblyName};component/Resources/PythonFileIcon{(isDark ? "_dark" : "")}.png";
            string warningIcon = $"/{_assemblyName};component/Resources/WarningIcon{(isDark ? "_dark" : "")}.png";
            string folderIcon = $"/{_assemblyName};component/Resources/Folder{(isDark ? "_dark" : "")}.png";

            Action updateGroupIcons = () => {
                if (ScriptGroups == null) return;
                foreach (var groupVM in ScriptGroups)
                {
                    groupVM.FolderIconPath = folderIcon;
                    foreach (var scriptVM in groupVM.Scripts)
                    {
                        scriptVM.NormalScriptIconPath = pythonIcon;
                        scriptVM.WarningScriptIconPath = warningIcon;
                    }
                }
            };

            if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.Invoke(updateGroupIcons);
            }
            else
            {
                updateGroupIcons();
            }
        }

        public void UpdateFileWatchers(SyncFilesSettingsState settings)
        {
            if (_fileSystemWatcherService != null && settings != null)
            {
                AppendLogMessage("Updating file watchers based on new settings...");
                _fileSystemWatcherService.UpdateWatchers(settings);
                AppendLogMessage("File watchers updated.");
            }
            else
            {
                AppendLogMessage("[WARN] UpdateFileWatchers called but FileSystemWatcherService or settings are null.");
            }
        }

        private void InitializePythonScriptWatcher()
        {
            // Stop existing watchers first (this now uses locks internally)
            StopPythonScriptWatcher();

            if (_settingsManager == null) return;
            var settings = _settingsManager.LoadSettings(_projectBasePath);
            string scriptPath = settings.PythonScriptPath;

            if (string.IsNullOrWhiteSpace(scriptPath) || !Directory.Exists(scriptPath))
            {
                return;
            }

            DirectoryInfo scriptDirInfo = new DirectoryInfo(scriptPath);

            lock (_pythonScriptDirWatcherLock) // Lock before creating/assigning _pythonScriptDirWatcher
            {
                try
                {
                    _pythonScriptDirWatcher = new FileSystemWatcher(scriptPath)
                    {
                        Filter = "*.py",
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.CreationTime,
                        IncludeSubdirectories = false
                    };
                    _pythonScriptDirWatcher.Changed += OnPythonScriptDirectoryChanged;
                    _pythonScriptDirWatcher.Created += OnPythonScriptDirectoryChanged;
                    _pythonScriptDirWatcher.Deleted += OnPythonScriptDirectoryChanged;
                    _pythonScriptDirWatcher.Renamed += OnPythonScriptDirectoryChanged;
                    _pythonScriptDirWatcher.EnableRaisingEvents = true;
                    System.Diagnostics.Debug.WriteLine($"[PYTHON_WATCHER] Dir watcher initialized for: {scriptPath}");
                }
                catch (Exception ex)
                {
                    AppendLogMessage($"[ERROR] Failed to initialize watcher for Python script directory '{scriptPath}': {ex.Message}");
                    _pythonScriptDirWatcher = null; // Ensure it's null on failure
                }
            }

            if (scriptDirInfo.Parent != null && scriptDirInfo.Parent.Exists)
            {
                lock (_pythonScriptParentWatcherLock) // Lock before creating/assigning _pythonScriptParentDirWatcher
                {
                    try
                    {
                        _pythonScriptParentDirWatcher = new FileSystemWatcher(scriptDirInfo.Parent.FullName)
                        {
                            NotifyFilter = NotifyFilters.DirectoryName,
                        };
                        _pythonScriptParentDirWatcher.Deleted += OnPythonScriptParentDirectoryChanged;
                        _pythonScriptParentDirWatcher.Renamed += OnPythonScriptParentDirectoryChanged;
                        _pythonScriptParentDirWatcher.EnableRaisingEvents = true;
                        System.Diagnostics.Debug.WriteLine($"[PYTHON_WATCHER] Parent watcher initialized for: {scriptDirInfo.Parent.FullName}");
                    }
                    catch (Exception ex)
                    {
                        AppendLogMessage($"[ERROR] Failed to initialize watcher for parent of Python script directory '{scriptDirInfo.Parent.FullName}': {ex.Message}");
                        _pythonScriptParentDirWatcher = null; // Ensure it's null on failure
                    }
                }
            }
        }

        public void SetScriptExecutionStatus(string message)
        {
            if (Application.Current == null) return;
            Application.Current.Dispatcher.Invoke(() =>
            {
                CurrentScriptExecutionStatus = message;
            });
        }

        private async void OnPythonScriptParentDirectoryChanged(object sender, FileSystemEventArgs e)
        {
            FileSystemWatcher currentParentWatcher = null;
            lock (_pythonScriptParentWatcherLock)
            {
                currentParentWatcher = _pythonScriptParentDirWatcher;
            }
            // ... (similar check as above)
            if (currentParentWatcher == null || sender != currentParentWatcher)
            {
                System.Diagnostics.Debug.WriteLine($"[PYTHON_WATCHER] OnPythonScriptParentDirectoryChanged invoked with unexpected sender or disposed/null watcher. Path: {e.FullPath}");
                return;
            }
            // ... rest of the method
            if (_settingsManager == null) return;
            var settings = _settingsManager.LoadSettings(_projectBasePath);
            if (string.IsNullOrEmpty(settings.PythonScriptPath)) return;

            string currentScriptFolderName = "";
            try
            {
                currentScriptFolderName = new DirectoryInfo(settings.PythonScriptPath).Name;
            }
            catch (ArgumentException)
            {
                AppendLogMessage($"[WARN] Python script path '{settings.PythonScriptPath}' seems invalid after a directory event. Cannot determine folder name.");
                return;
            }

            if (e.Name.Equals(currentScriptFolderName, StringComparison.OrdinalIgnoreCase))
            {
                AppendLogMessage($"Python script directory '{e.Name}' event: {e.ChangeType}. Refreshing...");
                if (Application.Current?.Dispatcher != null)
                {
                    await Application.Current.Dispatcher.InvokeAsync(async () =>
                    {
                        await LoadAndRefreshScriptsAsync(true);
                    });
                }
            }
        }
        public void AppendScriptOutput(string scriptName, string outputLine)
        {
            if (Application.Current == null) return;
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (!IsScriptOutputVisible && !string.IsNullOrWhiteSpace(outputLine)) IsScriptOutputVisible = true;
                ScriptOutputLog.Insert(0, new ScriptExecutionOutputLine(scriptName, outputLine, false));
                if (ScriptOutputLog.Count > 200)
                {
                    ScriptOutputLog.RemoveAt(ScriptOutputLog.Count - 1);
                }
                (ClearScriptOutputCommand as RelayCommand)?.RaiseCanExecuteChanged();
            });
        }

        public void AppendScriptError(string scriptName, string errorLine)
        {
            if (Application.Current == null) return;
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (!IsScriptOutputVisible && !string.IsNullOrWhiteSpace(errorLine)) IsScriptOutputVisible = true;
                ScriptOutputLog.Insert(0, new ScriptExecutionOutputLine(scriptName, errorLine, true));
                if (ScriptOutputLog.Count > 200)
                {
                    ScriptOutputLog.RemoveAt(ScriptOutputLog.Count - 1);
                }
                 (ClearScriptOutputCommand as RelayCommand)?.RaiseCanExecuteChanged();
            });
        }

        public void ClearScriptOutput()
        {
            if (Application.Current == null) return;
            Application.Current.Dispatcher.Invoke(() =>
            {
                ScriptOutputLog.Clear();
                CurrentScriptExecutionStatus = "Script output cleared.";
                (ClearScriptOutputCommand as RelayCommand)?.RaiseCanExecuteChanged();
            });
        }

        public void HandleScriptExecutionCompletion(string scriptName, Core.Services.ScriptExecutionResult result, Exception exception = null)
        {
            if (Application.Current == null) return;
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (exception != null)
                {
                    CurrentScriptExecutionStatus = $"FAILED: {scriptName} - {exception.Message}";
                    AppendScriptError(scriptName, $"EXECUTION EXCEPTION: {exception.Message}");
                }
                else if (result != null)
                {
                    CurrentScriptExecutionStatus = $"DONE: {scriptName} (Exit Code: {result.ExitCode})";
                    if (result.ExitCode != 0)
                    {
                        AppendScriptError(scriptName, $"Exited with code {result.ExitCode}.");
                    }
                    else
                    {
                        AppendScriptOutput(scriptName, $"Exited with code {result.ExitCode}.");
                    }
                }
            });
        }
        private void StopPythonScriptWatcher()
        {
            System.Diagnostics.Debug.WriteLine("[PYTHON_WATCHER] Attempting to stop Python script watchers...");

            // Stop _pythonScriptDirWatcher
            FileSystemWatcher dirWatcherToStop = null;
            lock (_pythonScriptDirWatcherLock)
            {
                if (_pythonScriptDirWatcher != null)
                {
                    dirWatcherToStop = _pythonScriptDirWatcher;
                    _pythonScriptDirWatcher = null;
                }
            }

            if (dirWatcherToStop != null)
            {
                string dirPathForLog = "N/A";
                try { dirPathForLog = dirWatcherToStop.Path; } catch { }
                System.Diagnostics.Debug.WriteLine($"[PYTHON_WATCHER] Stopping directory watcher for: {dirPathForLog}");
                try
                {
                    if (dirWatcherToStop.EnableRaisingEvents)
                    {
                        dirWatcherToStop.EnableRaisingEvents = false;
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ERROR] [PYTHON_WATCHER] Exception EnableRaisingEvents=false for dir: {ex.Message} @ {dirPathForLog}"); }
                try
                {
                    dirWatcherToStop.Changed -= OnPythonScriptDirectoryChanged;
                    dirWatcherToStop.Created -= OnPythonScriptDirectoryChanged;
                    dirWatcherToStop.Deleted -= OnPythonScriptDirectoryChanged;
                    dirWatcherToStop.Renamed -= OnPythonScriptDirectoryChanged;
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ERROR] [PYTHON_WATCHER] Exception unsubscribing for dir: {ex.Message} @ {dirPathForLog}"); }

                var capturedDirWatcher = dirWatcherToStop;
                System.Diagnostics.Debug.WriteLine($"[PYTHON_WATCHER] Queuing background Dispose for directory watcher: {dirPathForLog}");
                _ = Task.Run(() =>
                {
                    try
                    {
                        System.Diagnostics.Debug.WriteLine($"[PYTHON_WATCHER] BG Dispose for dir: {dirPathForLog}");
                        capturedDirWatcher.Dispose();
                        System.Diagnostics.Debug.WriteLine($"[PYTHON_WATCHER] BG Dispose completed for dir: {dirPathForLog}");
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ERROR] [PYTHON_WATCHER] BG Dispose ex for dir: {ex.ToString()} @ {dirPathForLog}"); }
                });
            }

            // Stop _pythonScriptParentDirWatcher
            FileSystemWatcher parentWatcherToStop = null;
            lock (_pythonScriptParentWatcherLock)
            {
                if (_pythonScriptParentDirWatcher != null)
                {
                    parentWatcherToStop = _pythonScriptParentDirWatcher;
                    _pythonScriptParentDirWatcher = null;
                }
            }
            if (parentWatcherToStop != null)
            {
                string parentPathForLog = "N/A";
                try { parentPathForLog = parentWatcherToStop.Path; } catch { }
                System.Diagnostics.Debug.WriteLine($"[PYTHON_WATCHER] Stopping parent directory watcher for: {parentPathForLog}");
                try
                {
                    if (parentWatcherToStop.EnableRaisingEvents)
                    {
                        parentWatcherToStop.EnableRaisingEvents = false;
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ERROR] [PYTHON_WATCHER] Exception EnableRaisingEvents=false for parent: {ex.Message} @ {parentPathForLog}"); }
                try
                {
                    parentWatcherToStop.Deleted -= OnPythonScriptParentDirectoryChanged;
                    parentWatcherToStop.Renamed -= OnPythonScriptParentDirectoryChanged;
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ERROR] [PYTHON_WATCHER] Exception unsubscribing for parent: {ex.Message} @ {parentPathForLog}"); }

                var capturedParentWatcher = parentWatcherToStop;
                System.Diagnostics.Debug.WriteLine($"[PYTHON_WATCHER] Queuing background Dispose for parent watcher: {parentPathForLog}");
                _ = Task.Run(() =>
                {
                    try
                    {
                        System.Diagnostics.Debug.WriteLine($"[PYTHON_WATCHER] BG Dispose for parent: {parentPathForLog}");
                        capturedParentWatcher.Dispose();
                        System.Diagnostics.Debug.WriteLine($"[PYTHON_WATCHER] BG Dispose completed for parent: {parentPathForLog}");
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ERROR] [PYTHON_WATCHER] BG Dispose ex for parent: {ex.ToString()} @ {parentPathForLog}"); }
                });
            }
            System.Diagnostics.Debug.WriteLine("[PYTHON_WATCHER] StopPythonScriptWatcher method finished.");
        }


        private async void OnPythonScriptDirectoryChanged(object sender, FileSystemEventArgs e)
        {
            FileSystemWatcher currentWatcher = null;
            lock (_pythonScriptDirWatcherLock)
            {
                currentWatcher = _pythonScriptDirWatcher; // Get reference under lock
            }

            if (currentWatcher == null || sender != currentWatcher)
            {
                System.Diagnostics.Debug.WriteLine($"[PYTHON_WATCHER] OnPythonScriptDirectoryChanged invoked with unexpected sender or disposed/null watcher. Path: {e.FullPath}");
                return;
            }
            // ... rest of the method (AppendLogMessage, InvokeAsync, etc.)
            AppendLogMessage($"Python script directory change detected: {e.ChangeType} on {e.Name}. Refreshing scripts...");
            if (Application.Current?.Dispatcher != null)
            {
                await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    await LoadAndRefreshScriptsAsync(true);
                });
            }
        }
        public void RequestSaveSettings()
        {
            if (IsBusy) return;
            AppendLogMessage("Saving configuration changes...");
            try
            {
                var settings = _settingsManager.LoadSettings(_projectBasePath);

                settings.ScriptGroups.Clear();
                foreach (var groupVM in ScriptGroups)
                {
                    var groupModel = groupVM.GetModel();
                    groupModel.Scripts.Clear();
                    foreach (var scriptVM in groupVM.Scripts)
                    {
                        groupModel.Scripts.Add(scriptVM.GetModel());
                    }
                    settings.ScriptGroups.Add(groupModel);
                }

                _settingsManager.SaveSettings(settings, _projectBasePath);
                AppendLogMessage("Configuration saved successfully.");

                InitializePythonScriptWatcher();
                UpdateFileWatchers(settings);
            }
            catch (Exception ex)
            {
                AppendLogMessage($"[ERROR] Failed to save settings: {ex.Message}");
                ShowErrorMessage("Error Saving Settings", ex.Message);
            }
        }

        public void RequestMoveScriptToGroup(ScriptEntryViewModel scriptVM)
        {
            if (scriptVM == null) return;

            var availableGroups = ScriptGroups
                .Where(g => g.Scripts.All(s => s.Id != scriptVM.Id))
                .Select(g => g.Name)
                .ToList();

            if (!availableGroups.Any())
            {
                ShowInfoMessage("Move Script", "No other groups available to move the script to.");
                return;
            }

            string targetGroupName = ShowComboBoxDialog("Move Script", $"Move '{scriptVM.DisplayName}' to group:", availableGroups);

            if (!string.IsNullOrEmpty(targetGroupName))
            {
                ScriptGroupViewModel targetGroupVM = ScriptGroups.FirstOrDefault(g => g.Name == targetGroupName);
                ScriptGroupViewModel sourceGroupVM = ScriptGroups.FirstOrDefault(g => g.Scripts.Contains(scriptVM));

                if (targetGroupVM != null && sourceGroupVM != null && sourceGroupVM != targetGroupVM)
                {
                    sourceGroupVM.RemoveScript(scriptVM);
                    targetGroupVM.AddScript(scriptVM);
                    AppendLogMessage($"Moved script '{scriptVM.DisplayName}' from '{sourceGroupVM.Name}' to '{targetGroupVM.Name}'.");
                    RequestSaveSettings();
                }
            }
        }

        public void RequestRemoveScriptFromCurrentGroup(ScriptEntryViewModel scriptVM)
        {
            if (scriptVM == null) return;
            ScriptGroupViewModel sourceGroupVM = ScriptGroups.FirstOrDefault(g => g.Scripts.Contains(scriptVM));
            if (sourceGroupVM != null)
            {
                if (ShowConfirmDialog("Remove Script", $"Are you sure you want to remove '{scriptVM.DisplayName}' from group '{sourceGroupVM.Name}'?"))
                {
                    sourceGroupVM.RemoveScript(scriptVM);
                    AppendLogMessage($"Removed script '{scriptVM.DisplayName}' from group '{sourceGroupVM.Name}'.");
                    RequestSaveSettings();
                }
            }
        }

        public bool IsGroupNameDuplicate(string name, string excludeGroupId)
        {
            return ScriptGroups.Any(g => g.Id != excludeGroupId && g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        public void RequestDeleteGroup(ScriptGroupViewModel groupVMToDelete)
        {
            if (groupVMToDelete == null || groupVMToDelete.IsDefaultGroup) return;

            if (ShowConfirmDialog("Delete Group", $"Are you sure you want to delete group '{groupVMToDelete.Name}'? Scripts within will be moved to 'Default' group."))
            {
                ScriptGroupViewModel defaultGroupVM = ScriptGroups.FirstOrDefault(g => g.IsDefaultGroup);
                if (defaultGroupVM == null)
                {
                    ShowErrorMessage("Error", "Default group not found. Cannot delete group.");
                    return;
                }

                var scriptsToMove = new List<ScriptEntryViewModel>(groupVMToDelete.Scripts);
                foreach (var scriptVM in scriptsToMove)
                {
                    groupVMToDelete.RemoveScript(scriptVM);
                    defaultGroupVM.AddScript(scriptVM);
                }

                ScriptGroups.Remove(groupVMToDelete);
                AppendLogMessage($"Deleted group '{groupVMToDelete.Name}'. Its scripts were moved to Default group.");
                RequestSaveSettings();
            }
        }

        private async Task ShowMessageCoreAsync(string title, string message, OLEMSGICON icon)
        {
            if (_serviceProvider == null)
            {
                System.Windows.MessageBox.Show(message, title);
                return;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var serviceProvider = (IServiceProvider)_serviceProvider;
            var uiShell = serviceProvider.GetService(typeof(SVsUIShell)) as IVsUIShell;

            if (uiShell == null)
            {
                System.Windows.MessageBox.Show(message, title);
                return;
            }
            Guid clsid = Guid.Empty;
            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(uiShell.ShowMessageBox(
                0, ref clsid, title, message, string.Empty, 0,
                OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST,
                icon, 0, out _));
        }

        public async void ShowInfoMessage(string title, string message) => await ShowMessageCoreAsync(title, message, OLEMSGICON.OLEMSGICON_INFO);
        public async void ShowWarningMessage(string title, string message) => await ShowMessageCoreAsync(title, message, OLEMSGICON.OLEMSGICON_WARNING);
        public async void ShowErrorMessage(string title, string message) => await ShowMessageCoreAsync(title, message, OLEMSGICON.OLEMSGICON_CRITICAL);

        public bool ShowConfirmDialog(string title, string message)
        {
            if (_serviceProvider == null)
            {
                return System.Windows.MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            }
            return ThreadHelper.JoinableTaskFactory.Run(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var serviceProvider = (IServiceProvider)_serviceProvider;
                var uiShell = serviceProvider.GetService(typeof(SVsUIShell)) as IVsUIShell;

                if (uiShell == null)
                {
                    return System.Windows.MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                }
                Guid clsid = Guid.Empty;
                int result;
                Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(uiShell.ShowMessageBox(
                    0, ref clsid, title, message, string.Empty, 0,
                    OLEMSGBUTTON.OLEMSGBUTTON_YESNO, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST,
                    OLEMSGICON.OLEMSGICON_QUERY, 0, out result));
                return result == 6;
            });
        }

        public void AppendLogMessage(string message)
        {
            if (Application.Current == null && _serviceProvider == null) return;

            const int maxLogEntries = 100;
            string formattedMessage = $"[{DateTime.Now:HH:mm:ss}] {message}";

            Action updateAction = () =>
            {
                lock (_logMessagesLock)
                {
                    LogMessages.Insert(0, formattedMessage);
                    if (LogMessages.Count > maxLogEntries)
                    {
                        if (LogMessages.Count > 0)
                        {
                            LogMessages.RemoveAt(LogMessages.Count - 1);
                        }
                    }
                }
                StatusMessage = message;
            };

            if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.Invoke(updateAction);
            }
            else
            {
                updateAction();
            }
        }
        private void OnWatchedFileChanged_Handler(string scriptToExecute, string eventType, string affectedPath)
        {
            // 简要记录到主日志（如果需要）
            string scriptFileName = Path.GetFileName(scriptToExecute);

            if (_settingsManager == null)
            {
                AppendLogMessage($"[ERROR][{scriptFileName}] Settings manager not available.");
                return;
            }
            var settings = _settingsManager.LoadSettings(_projectBasePath);
            if (string.IsNullOrEmpty(settings.PythonExecutablePath) || !File.Exists(settings.PythonExecutablePath))
            {
                AppendLogMessage($"[ERROR][{scriptFileName}] Python executable not configured or not found.");
                return;
            }
            var executor = new ScriptExecutor(_projectBasePath);
            var arguments = new List<string> { eventType, affectedPath };

            // 不再将启动信息直接写入 ScriptOutputLog，而是写入主日志
            // AppendScriptOutput(Path.GetFileName(scriptToExecute), $"Event: {eventType} on '{affectedPath}'. Executing...");

            System.Threading.Tasks.Task.Run(async () => {
                try
                {
                    // 修改 ExecuteAndCaptureOutputAsync 的回调，使其记录到主日志或专门的日志文件，而不是 ScriptOutputLog
                    var result = await executor.ExecuteAndCaptureOutputAsync(
                        settings.PythonExecutablePath,
                        scriptToExecute,
                        arguments,
                        settings.EnvVariables,
                        string.IsNullOrEmpty(settings.PythonScriptPath) ? _projectBasePath : settings.PythonScriptPath,
                        // 对于文件监控脚本，stdout/stderr 可以考虑写入不同的地方，或者只记录摘要
                        stdout => System.Diagnostics.Debug.WriteLine($"[WATCH_SCRIPT_STDOUT][{scriptFileName}]: {stdout}"),
                        stderr => System.Diagnostics.Debug.WriteLine($"[WATCH_SCRIPT_STDERR][{scriptFileName}]: {stderr}")
                    );
                }
                catch (Exception exInner)
                {
                    System.Diagnostics.Debug.WriteLine($"[ERROR][WATCH_SCRIPT_EXEC][{scriptFileName}]: {exInner.ToString()}");
                }
            });
        }

        // AppendScriptOutput 和 AppendScriptError 现在只由用户通过UI执行脚本时调用
        // (ScriptEntryViewModel.Execute -> _parentViewModel.AppendScriptOutput)
        private void GitHubSyncService_RegularSyncCompleted_Handler(object sender, EventArgs e)
        {
            AppendLogMessage("GitHub file synchronization completed.");
        }
        private async void SmartWorkflowService_DownloadPhaseCompleted_Handler(object sender, EventArgs eventArgs)
        {
            AppendLogMessage("Smart workflow: File download phase complete. Proceeding to finalize configuration...");
            IsBusy = true;
            try
            {
                if (_smartWorkflowService == null)
                {
                    AppendLogMessage("[ERROR] SmartWorkflowService is null. Cannot finalize.");
                    IsBusy = false;
                    return;
                }
                _smartWorkflowService.FinalizeWorkflowConfiguration();
                AppendLogMessage("Smart workflow: Configuration finalized successfully by service.");
                AppendLogMessage("Reloading settings and refreshing UI after workflow finalization...");
                var latestSettings = _settingsManager.LoadSettings(_projectBasePath);
                _fileSystemWatcherService?.UpdateWatchers(latestSettings);
                AppendLogMessage("File watchers updated based on new workflow settings.");
                await LoadAndRefreshScriptsAsync(true);
                AppendLogMessage("Scripts tree refreshed after workflow.");
            }
            catch (Exception ex)
            {
                AppendLogMessage($"[ERROR] Smart workflow: Failed during configuration finalization or UI refresh: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[ERROR] SmartWorkflow_DownloadPhaseCompleted_Handler: {ex.ToString()}");
            }
            finally
            {
                IsBusy = false;
                _workflowCts?.Dispose();
                _workflowCts = null;
            }
        }

        public async Task OpenFileInIdeAsync(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;

            AppendLogMessage($"Opening file in IDE: {Path.GetFileName(filePath)}...");
            IsBusy = true;
            try
            {
                if (!File.Exists(filePath))
                {
                    await ShowErrorMessageAsync("Error Opening File", $"File not found: {filePath}");
                    AppendLogMessage($"[ERROR] File not found for opening: {filePath}");
                    return;
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var dte = await _serviceProvider.GetServiceAsync(typeof(SDTE)) as EnvDTE.DTE;
                if (dte != null)
                {
                    EnvDTE.Window wnd = dte.ItemOperations.OpenFile(filePath, EnvDTE.Constants.vsViewKindCode);
                    if (wnd != null)
                    {
                        wnd.Activate();
                        AppendLogMessage($"File '{Path.GetFileName(filePath)}' opened successfully via DTE.");
                    }
                    else
                    {
                        AppendLogMessage($"[WARN] DTE.OpenFile did not return a window object for '{Path.GetFileName(filePath)}', but operation may have succeeded.");
                    }
                }
                else
                {
                    AppendLogMessage("[ERROR] Visual Studio DTE service not available to open file.");
                    await ShowErrorMessageAsync("Error Opening File", "Visual Studio DTE service not available.");
                }
            }
            catch (Exception ex)
            {
                AppendLogMessage($"[ERROR] Failed to open script file '{Path.GetFileName(filePath)}' in IDE: {ex.Message}");
                await ShowErrorMessageAsync("Error Opening File", $"Could not open file in IDE: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ShowErrorMessageAsync(string title, string message)
        {
            await ShowMessageCoreAsync(title, message, OLEMSGICON.OLEMSGICON_CRITICAL);
        }

        public async Task LoadAndRefreshScriptsAsync(bool forceScanDisk)
        {
            // 保存每个组的展开状态
            Dictionary<string, bool> expandedStates = new Dictionary<string, bool>();
            foreach (var group in ScriptGroups)
            {
                expandedStates[group.Id] = group.IsExpanded;
                System.Diagnostics.Debug.WriteLine($"保存组 '{group.Name}' 的展开状态: {group.IsExpanded}");
            }

            if (_serviceProvider is SyncFilesPackage package)
            {
                package.TriggerReinitializeConfigWatcher();
            }

            if (_settingsManager == null)
            {
                AppendLogMessage("[ERROR] Settings manager not initialized. Cannot load scripts.");
                if (ScriptGroups != null && Application.Current?.Dispatcher != null)
                    Application.Current.Dispatcher.Invoke(() => ScriptGroups.Clear());
                else if (ScriptGroups != null)
                    ScriptGroups.Clear();
                IsBusy = false;
                return;
            }
            if (string.IsNullOrEmpty(_projectBasePath) && forceScanDisk)
            {
                AppendLogMessage("[INFO] No project/solution open. Scripts cannot be scanned from disk. Displaying configured scripts only.");
                forceScanDisk = false;
            }

            IsBusy = true;
            AppendLogMessage(forceScanDisk ? "Loading settings and scanning script directory..." : "Loading settings and refreshing script tree...");

            try
            {
                await ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
                {
                    SyncFilesSettingsState settings = null; // Declare here to be accessible in the finally-like block for UI update
                    List<ScriptGroupViewModel> newScriptGroups = new List<ScriptGroupViewModel>();

                    try
                    {
                        settings = _settingsManager.LoadSettings(_projectBasePath);
                        string pythonScriptBasePath = settings.PythonScriptPath;
                        string pythonExecutable = settings.PythonExecutablePath;
                        List<ScriptGroup> configuredGroupsModels = new List<ScriptGroup>(settings.ScriptGroups);
                        ScriptGroup defaultGroupModel = configuredGroupsModels.FirstOrDefault(g => g.Id == ScriptGroup.DefaultGroupId);
                        if (defaultGroupModel == null)
                        {
                            defaultGroupModel = new ScriptGroup(ScriptGroup.DefaultGroupId, ScriptGroup.DefaultGroupName);
                            configuredGroupsModels.Insert(0, defaultGroupModel);
                        }

                        bool settingsModifiedByScan = false;

                        if (forceScanDisk && !string.IsNullOrWhiteSpace(pythonScriptBasePath) && Directory.Exists(pythonScriptBasePath))
                        {
                            HashSet<string> diskScriptRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            try
                            {
                                diskScriptRelativePaths.UnionWith(
                                    Directory.GetFiles(pythonScriptBasePath, "*.py", SearchOption.TopDirectoryOnly)
                                        .Select(p => GetRelativePath(p, pythonScriptBasePath))
                                );
                            }
                            catch (Exception ex_scan)
                            {
                                System.Diagnostics.Debug.WriteLine($"[ERROR] Error scanning script directory '{pythonScriptBasePath}': {ex_scan.Message}");
                                string scanErrorMessage = $"[ERROR] Error scanning script directory '{pythonScriptBasePath}': {ex_scan.Message}";
                                Application.Current?.Dispatcher?.Invoke(() => AppendLogMessage(scanErrorMessage));
                            }
                            var allCurrentScriptModels = configuredGroupsModels.SelectMany(g => g.Scripts).ToList();
                            foreach (var scriptModel in allCurrentScriptModels)
                            {
                                scriptModel.IsMissing = !diskScriptRelativePaths.Contains(scriptModel.Path);
                            }

                            int removedCount = defaultGroupModel.Scripts.RemoveAll(s => s.IsMissing && s.Id != ScriptEntryViewModel.PlaceHolderId);
                            if (removedCount > 0) settingsModifiedByScan = true;

                            var allExistingPathsInModel = new HashSet<string>(allCurrentScriptModels.Where(s => !s.IsMissing).Select(s => s.Path), StringComparer.OrdinalIgnoreCase);
                            foreach (string diskPath in diskScriptRelativePaths)
                            {
                                if (!allExistingPathsInModel.Contains(diskPath))
                                {
                                    var newEntry = new ScriptEntry(diskPath) { Description = $"Auto-added {DateTime.Now:yyyy/MM/dd}" };
                                    defaultGroupModel.Scripts.Add(newEntry);
                                    allExistingPathsInModel.Add(diskPath);
                                    settingsModifiedByScan = true;
                                }
                            }
                            if (settingsModifiedByScan)
                            {
                                settings.ScriptGroups = configuredGroupsModels;
                                _settingsManager.SaveSettings(settings, _projectBasePath);
                            }
                        }
                        else if (string.IsNullOrWhiteSpace(pythonScriptBasePath) || !Directory.Exists(pythonScriptBasePath))
                        {
                            if (forceScanDisk)
                            {
                                string msg = string.IsNullOrWhiteSpace(pythonScriptBasePath) ? "[INFO] Python script path is not configured." : $"[WARN] Python script directory not found: {pythonScriptBasePath}";
                                Application.Current?.Dispatcher?.Invoke(() => AppendLogMessage(msg));
                            }
                            foreach (var groupModel in configuredGroupsModels)
                                foreach (var scriptModel in groupModel.Scripts)
                                    scriptModel.IsMissing = true;
                        }

                        var sortedGroupModels = configuredGroupsModels.OrderBy(g => g.Id == ScriptGroup.DefaultGroupId ? 0 : 1)
                                                                     .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase);
                        foreach (var groupModel in sortedGroupModels)
                        {
                            var groupVM = new ScriptGroupViewModel(groupModel, pythonExecutable, pythonScriptBasePath, settings.EnvVariables, _projectBasePath, this);
                            newScriptGroups.Add(groupVM);
                        }
                    }
                    catch (Exception ex_load_process)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ERROR] Exception during script data load/processing: {ex_load_process}");
                        Application.Current?.Dispatcher?.Invoke(() => AppendLogMessage($"[ERROR] Failed to process script data: {ex_load_process.Message}"));
                    }

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(CancellationToken.None);

                    try
                    {
                        ScriptGroups.Clear();
                        foreach (var groupVM in newScriptGroups)
                        {
                            // 在添加到集合前恢复展开状态
                            if (expandedStates.TryGetValue(groupVM.Id, out bool wasExpanded))
                            {
                                groupVM.IsExpanded = wasExpanded;
                                System.Diagnostics.Debug.WriteLine($"恢复组 '{groupVM.Name}' 的展开状态: {wasExpanded}");
                            }
                            ScriptGroups.Add(groupVM);
                        }

                        // 额外的延迟处理以确保UI更新后TreeViewItem正确展开
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            foreach (var groupVM in ScriptGroups)
                            {
                                if (expandedStates.TryGetValue(groupVM.Id, out bool wasExpanded))
                                {
                                    groupVM.IsExpanded = wasExpanded;
                                    System.Diagnostics.Debug.WriteLine($"强制刷新组 '{groupVM.Name}' 的展开状态: {wasExpanded}");
                                }
                            }
                        }), DispatcherPriority.Loaded);

                        AppendLogMessage("Scripts tree UI refreshed.");
                        InitializePythonScriptWatcher();
                        UpdateIconsForTheme();

                        if (_settingsManager != null && settings != null)
                        {
                            UpdateFileWatchers(settings);
                        }
                    }
                    catch (Exception ex_ui_update)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ERROR] Exception during UI update in LoadAndRefreshScriptsAsync: {ex_ui_update}");
                        AppendLogMessage($"[ERROR] Failed to update script UI: {ex_ui_update.Message}");
                    }
                });
            }
            catch (Exception ex_outer)
            {
                System.Diagnostics.Debug.WriteLine($"[ERROR] Outer exception in LoadAndRefreshScriptsAsync: {ex_outer}");
                AppendLogMessage($"[ERROR] Failed to load/refresh scripts: {ex_outer.Message}");
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(CancellationToken.None);
                IsBusy = false;
                (RefreshScriptsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        private string GetRelativePath(string fullPath, string basePath)
        {
            if (string.IsNullOrEmpty(basePath))
            {
                return fullPath;
            }
            if (!basePath.EndsWith(Path.DirectorySeparatorChar.ToString()) && !basePath.EndsWith(Path.AltDirectorySeparatorChar.ToString()))
            {
                basePath += Path.DirectorySeparatorChar;
            }

            Uri baseUri = new Uri(basePath, UriKind.Absolute);
            Uri fullUri = new Uri(fullPath, UriKind.Absolute);

            if (baseUri.IsBaseOf(fullUri))
            {
                return Uri.UnescapeDataString(baseUri.MakeRelativeUri(fullUri).ToString().Replace('/', Path.DirectorySeparatorChar));
            }
            else
            {
                return fullPath;
            }
        }
        public string ShowInputDialog(string title, string prompt, string defaultValue = "", bool multiline = false)
        {
            var inputDialog = new System.Windows.Window
            {
                Title = title,
                Width = 350,
                MinWidth = 300,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
            };

            bool isDarkTheme = IsDarkTheme(); // Check current theme
            Brush defaultWindowForeground = isDarkTheme ? System.Windows.Media.Brushes.White : (Brush)Application.Current.Resources[VsBrushes.WindowTextKey];
            Brush defaultWindowBackground = (Brush)Application.Current.Resources[VsBrushes.WindowKey];

            inputDialog.Background = defaultWindowBackground;
            inputDialog.Foreground = defaultWindowForeground; // Set for the whole window

            if (Application.Current?.MainWindow?.IsVisible == true) inputDialog.Owner = Application.Current.MainWindow;

            var panel = new StackPanel { Margin = new Thickness(15) };

            var promptTextBlock = new TextBlock
            {
                Text = prompt,
                Margin = new Thickness(0, 0, 0, 8),
                Foreground = defaultWindowForeground // Apply themed foreground
            };
            panel.Children.Add(promptTextBlock);

            TextBox inputTextBox;
            if (multiline)
                inputTextBox = new TextBox { Text = defaultValue, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70, MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            else
                inputTextBox = new TextBox { Text = defaultValue };

            if (Application.Current != null && Application.Current.Resources.Contains(VsResourceKeys.TextBoxStyleKey))
                inputTextBox.Style = (Style)Application.Current.Resources[VsResourceKeys.TextBoxStyleKey];

            // Explicitly set TextBox foreground if not covered by style or for better control
            inputTextBox.Foreground = defaultWindowForeground;
            inputTextBox.Background = isDarkTheme ? (Brush)Application.Current.Resources[VsBrushes.ComboBoxBackgroundKey] : (Brush)Application.Current.Resources[VsBrushes.WindowKey];


            panel.Children.Add(inputTextBox);
            inputDialog.Loaded += (s, e) => inputTextBox.Focus();

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 15, 0, 0) };
            var okButton = new Button { Content = "OK", IsDefault = true, MinWidth = 75, Margin = new Thickness(0, 0, 10, 0) };
            var cancelButton = new Button { Content = "Cancel", IsCancel = true, MinWidth = 75 };

            if (Application.Current != null && Application.Current.Resources.Contains(VsResourceKeys.ButtonStyleKey))
            {
                okButton.Style = (Style)Application.Current.Resources[VsResourceKeys.ButtonStyleKey];
                cancelButton.Style = (Style)Application.Current.Resources[VsResourceKeys.ButtonStyleKey];
            }

            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            panel.Children.Add(buttonPanel);
            inputDialog.Content = panel;

            string result = null;
            okButton.Click += (s, e) => { result = inputTextBox.Text; inputDialog.DialogResult = true; inputDialog.Close(); };
            cancelButton.Click += (s, e) => { inputDialog.DialogResult = false; inputDialog.Close(); };

            inputDialog.ShowDialog();
            return (inputDialog.DialogResult == true) ? result : null;
        }
        public string ShowComboBoxDialog(string title, string prompt, List<string> items)
        {
            if (items == null || !items.Any()) return null;

            var dialog = new System.Windows.Window
            {
                Title = title,
                Width = 350,
                MinWidth = 300,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
            };
            bool isDarkTheme = IsDarkTheme();
            Brush defaultWindowForeground = isDarkTheme ? System.Windows.Media.Brushes.White : (Brush)Application.Current.Resources[VsBrushes.WindowTextKey];
            Brush defaultWindowBackground = (Brush)Application.Current.Resources[VsBrushes.WindowKey];

            dialog.Background = defaultWindowBackground;
            dialog.Foreground = defaultWindowForeground;

            if (Application.Current?.MainWindow?.IsVisible == true) dialog.Owner = Application.Current.MainWindow;

            var panel = new StackPanel { Margin = new Thickness(15) };
            var promptTextBlock = new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8), Foreground = defaultWindowForeground };
            panel.Children.Add(promptTextBlock);

            var comboBox = new ComboBox { ItemsSource = items, SelectedIndex = 0 };
            if (Application.Current != null && Application.Current.Resources.Contains(VsResourceKeys.ComboBoxStyleKey))
                comboBox.Style = (Style)Application.Current.Resources[VsResourceKeys.ComboBoxStyleKey];

            comboBox.Foreground = defaultWindowForeground; // Ensure ComboBox text is also themed

            panel.Children.Add(comboBox);
            dialog.Loaded += (s, e) => comboBox.Focus();

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 15, 0, 0) };
            var okButton = new Button { Content = "OK", IsDefault = true, MinWidth = 75, Margin = new Thickness(0, 0, 10, 0) };
            var cancelButton = new Button { Content = "Cancel", IsCancel = true, MinWidth = 75 };
            if (Application.Current != null && Application.Current.Resources.Contains(VsResourceKeys.ButtonStyleKey))
            {
                okButton.Style = (Style)Application.Current.Resources[VsResourceKeys.ButtonStyleKey];
                cancelButton.Style = (Style)Application.Current.Resources[VsResourceKeys.ButtonStyleKey];
            }
            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            panel.Children.Add(buttonPanel);
            dialog.Content = panel;

            string result = null;
            okButton.Click += (s, e) => { result = comboBox.SelectedItem as string; dialog.DialogResult = true; dialog.Close(); };
            cancelButton.Click += (s, e) => { dialog.DialogResult = false; dialog.Close(); };

            dialog.ShowDialog();
            return (dialog.DialogResult == true) ? result : null;
        }
        private void AddNewGroup()
        {
            int newGroupCounter = 1;
            string baseName = "New Group ";
            string groupName;
            do
            {
                groupName = baseName + newGroupCounter++;
            } while (IsGroupNameDuplicate(groupName, null));

            string finalGroupName = ShowInputDialog("Add New Group", "Enter group name:", groupName);
            if (string.IsNullOrWhiteSpace(finalGroupName)) return;

            finalGroupName = finalGroupName.Trim();
            if (IsGroupNameDuplicate(finalGroupName, null))
            {
                ShowWarningMessage("Add Group", $"Group '{finalGroupName}' already exists.");
                return;
            }

            if (_settingsManager == null)
            {
                ShowErrorMessage("Error", "Settings Manager is not available.");
                return;
            }

            var settings = _settingsManager.LoadSettings(_projectBasePath);
            var newGroupModel = new ScriptGroup(Guid.NewGuid().ToString(), finalGroupName);
            settings.ScriptGroups.Add(newGroupModel);
            _settingsManager.SaveSettings(settings, _projectBasePath);

            var groupVM = new ScriptGroupViewModel(newGroupModel, settings.PythonExecutablePath, settings.PythonScriptPath, settings.EnvVariables, _projectBasePath, this);

            Application.Current?.Dispatcher?.Invoke(() => ScriptGroups.Add(groupVM));
            AppendLogMessage($"Group '{finalGroupName}' added.");
        }

        private async Task ExecuteGitHubSyncAsync(bool isPartOfWorkflow)
        {
            if (_gitHubSyncService == null || _settingsManager == null)
            {
                AppendLogMessage("[ERROR] GitHub sync service or settings manager not initialized.");
                return;
            }

            IsBusy = true;
            AppendLogMessage("Starting GitHub file synchronization...");

            try
            {
                await ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    var settings = _settingsManager.LoadSettings(_projectBasePath);
                    await _gitHubSyncService.SyncAllAsync(settings, _workflowCts?.Token ?? CancellationToken.None);
                });

                if (!isPartOfWorkflow)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    AppendLogMessage("GitHub sync (standalone) completed.");
                    await LoadAndRefreshScriptsAsync(true);

                    var currentSettings = _settingsManager.LoadSettings(_projectBasePath);
                    _fileSystemWatcherService?.UpdateWatchers(currentSettings);
                }
            }
            catch (OperationCanceledException)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                AppendLogMessage("[CANCELLED] GitHub sync was cancelled.");
            }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                AppendLogMessage($"[ERROR] GitHub sync failed: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[ERROR] ExecuteGitHubSyncAsync: {ex}");
            }
            finally
            {
                if (!isPartOfWorkflow)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    IsBusy = false;
                }
            }
        }

        private async Task LoadSmartWorkflowAsync()
        {
            if (_smartWorkflowService == null || _settingsManager == null || _gitHubSyncService == null)
            {
                AppendLogMessage("[ERROR] Workflow services not initialized.");
                return;
            }

            string yamlUrl = ShowInputDialog("Load Smart Workflow", "Enter YAML Configuration URL:", "https://raw.githubusercontent.com/sammiler/CodeConf/refs/heads/main/Cpp/SyncFiles/Clion/workflow.yaml");

            if (string.IsNullOrWhiteSpace(yamlUrl))
            {
                AppendLogMessage("Smart workflow loading cancelled or URL is empty.");
                return;
            }

            IsBusy = true;
            AppendLogMessage($"Loading smart workflow from: {yamlUrl}...");
            _workflowCts = new CancellationTokenSource();

            try
            {
                await ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await _smartWorkflowService.PrepareWorkflowFromYamlUrlAsync(yamlUrl, _workflowCts.Token);
                });
            }
            catch (OperationCanceledException)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                AppendLogMessage("[CANCELLED] Smart workflow operation was cancelled.");
                IsBusy = false;
                _workflowCts?.Dispose();
                _workflowCts = null;
            }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                AppendLogMessage($"[ERROR] Smart workflow loading failed during preparation: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[ERROR] LoadSmartWorkflowAsync: {ex}");
                IsBusy = false;
                _workflowCts?.Dispose();
                _workflowCts = null;
            }
        }
        private void CancelWorkflow()
        {
            if (_workflowCts != null && !_workflowCts.IsCancellationRequested)
            {
                AppendLogMessage("Attempting to cancel ongoing workflow operation...");
                _workflowCts.Cancel();
            }
        }
        public void RequestSetAlias(ScriptEntryViewModel scriptVM) { AppendLogMessage($"TODO: Set Alias for {scriptVM.DisplayName}"); }
        public void RequestSetExecutionMode(ScriptEntryViewModel scriptVM) { AppendLogMessage($"TODO: Set Exec Mode for {scriptVM.DisplayName}"); }
        public void RequestSetDescription(ScriptEntryViewModel scriptVM) { AppendLogMessage($"TODO: Set Desc for {scriptVM.DisplayName}"); }
        public void RequestMoveScript(ScriptEntryViewModel scriptVM) { AppendLogMessage($"TODO: Move Script {scriptVM.DisplayName}"); }
        public void RequestRemoveScript(ScriptEntryViewModel scriptVM, ScriptGroupViewModel groupVM) { AppendLogMessage($"TODO: Remove {scriptVM.DisplayName} from {groupVM.Name}"); }
        public void RequestAddScriptToGroup(ScriptGroupViewModel groupVM) { AppendLogMessage($"TODO: Add script to {groupVM.Name}"); }
        public void RequestRenameGroup(ScriptGroupViewModel groupVM) { AppendLogMessage($"TODO: Rename Group {groupVM.Name}"); }

        public async Task UpdateProjectContextAsync(
            string newProjectBasePath,
            GitHubSyncService newGitHubSyncService,
            FileSystemWatcherService newFileSystemWatcherService,
            SmartWorkflowService newSmartWorkflowService)
        {
            bool pathChanged = _projectBasePath != newProjectBasePath;
            bool servicesChanged = _gitHubSyncService != newGitHubSyncService ||
                                   _fileSystemWatcherService != newFileSystemWatcherService ||
                                   _smartWorkflowService != newSmartWorkflowService;

            if (!pathChanged && !servicesChanged && _settingsManager != null)
            {
                return;
            }

            AppendLogMessage($"Updating ViewModel project context. New path: '{newProjectBasePath ?? "null"}'");

            _projectBasePath = newProjectBasePath;

            if (servicesChanged)
            {
                DetachEventHandlers();

                _gitHubSyncService = newGitHubSyncService;
                _fileSystemWatcherService = newFileSystemWatcherService;
                _smartWorkflowService = newSmartWorkflowService;

                AttachEventHandlers();
            }

            await LoadAndRefreshScriptsAsync(true);

            if (_settingsManager != null)
            {
                var currentSettings = _settingsManager.LoadSettings(_projectBasePath);
                UpdateFileWatchers(currentSettings);
            }
            else
            {
                _fileSystemWatcherService?.UpdateWatchers(new SyncFilesSettingsState());
            }
            AppendLogMessage("ViewModel project context updated.");
        }

        private void AttachEventHandlers()
        {
            if (_fileSystemWatcherService != null)
            {
                _fileSystemWatcherService.WatchedFileChanged += OnWatchedFileChanged_Handler;
            }
            if (_gitHubSyncService != null)
            {
                _gitHubSyncService.SynchronizationCompleted += GitHubSyncService_RegularSyncCompleted_Handler;
                _gitHubSyncService.ProgressReported += GitHubSyncService_ProgressReported_Handler;
            }
            if (_smartWorkflowService != null)
            {
                _smartWorkflowService.WorkflowDownloadPhaseCompleted += SmartWorkflowService_DownloadPhaseCompleted_Handler;
            }
        }

        private void DetachEventHandlers()
        {
            if (_fileSystemWatcherService != null)
            {
                _fileSystemWatcherService.WatchedFileChanged -= OnWatchedFileChanged_Handler;
            }
            if (_gitHubSyncService != null)
            {
                _gitHubSyncService.SynchronizationCompleted -= GitHubSyncService_RegularSyncCompleted_Handler;
                _gitHubSyncService.ProgressReported -= GitHubSyncService_ProgressReported_Handler;
            }
            if (_smartWorkflowService != null)
            {
                _smartWorkflowService.WorkflowDownloadPhaseCompleted -= SmartWorkflowService_DownloadPhaseCompleted_Handler;
            }
        }

        private void GitHubSyncService_ProgressReported_Handler(string progressMessage)
        {
            AppendLogMessage($"[Sync] {progressMessage}");
        }

        public void Dispose()
        {
            StopPythonScriptWatcher();
            DetachEventHandlers();

            ThemeService.Instance.UnsubscribeThemeChangedEvent();

            _workflowCts?.Dispose();
            if (LogMessages != null)
            {
                lock (_logMessagesLock)
                {
                    LogMessages.Clear();
                }
            }
            System.Diagnostics.Debug.WriteLine("SyncFilesToolWindowViewModel Disposed.");
        }

        public string GetPythonExecutablePath()
        {
            return _settingsManager?.LoadSettings(_projectBasePath)?.PythonExecutablePath ?? "python";
        }

        public string GetPythonScriptBasePath()
        {
            return _settingsManager?.LoadSettings(_projectBasePath)?.PythonScriptPath;
        }

        public Dictionary<string, string> GetEnvironmentVariables()
        {
            var settings = _settingsManager?.LoadSettings(_projectBasePath);
            if (settings == null) return new Dictionary<string, string>();
            
            Dictionary<string, string> envVars = new Dictionary<string, string>();
            
            if (settings.EnvironmentVariablesList != null)
            {
                foreach (var envVar in settings.EnvironmentVariablesList)
                {
                    envVars[envVar.Name] = envVar.Value;
                }
            }
            
            // 添加必要的Python环境变量
            envVars["PYTHONUNBUFFERED"] = "1";
            envVars["PYTHONIOENCODING"] = "UTF-8";
            
            return envVars;
        }

        public void ExecuteScriptInTerminal(ScriptEntryViewModel scriptViewModel)
        {
            // 确保终端可见
            IsTerminalVisible = true;
            
            try
            {
                // 获取脚本信息
                ScriptEntry scriptEntry = scriptViewModel.GetModel();
                if (scriptEntry == null)
                {
                    AppendLogMessage("[错误] 无法获取脚本模型");
                    return;
                }

                // 获取Python执行路径
                string pythonExecutablePath = GetPythonExecutablePath();
                if (string.IsNullOrEmpty(pythonExecutablePath))
                {
                    AppendLogMessage("[错误] Python可执行文件路径未设置");
                    return;
                }

                // 获取脚本路径
                string pythonScriptBasePath = GetPythonScriptBasePath();
                string fullScriptPath = string.IsNullOrEmpty(scriptEntry.Path) ? string.Empty : 
                    Path.GetFullPath(Path.Combine(pythonScriptBasePath ?? string.Empty, scriptEntry.Path ?? string.Empty));

                if (string.IsNullOrEmpty(fullScriptPath) || !File.Exists(fullScriptPath))
                {
                    AppendLogMessage($"[错误] 脚本文件不存在: {fullScriptPath}");
                    scriptViewModel.IsMissing = true;
                    return;
                }

                // 获取工作目录
                string workingDirectory = Path.GetDirectoryName(fullScriptPath);
                
                // 获取脚本参数
                string arguments = string.Empty;
                if (scriptEntry.GetType().GetProperty("Arguments") != null)
                {
                    arguments = scriptEntry.GetType().GetProperty("Arguments").GetValue(scriptEntry) as string ?? string.Empty;
                }

                // 获取环境变量
                Dictionary<string, string> environmentVariables = GetEnvironmentVariables();

                // 直接使用静态实例引用访问终端控件
                var terminal = PtyTerminalControl.Instance;
                if (terminal == null)
                {
                    AppendLogMessage("[错误] 终端控件未初始化");
                    return;
                }

                // 准备终端以接收输入
                terminal.PrepareForExecution();
                terminal.GrabFocus(); // 使用新的显式焦点获取方法

                // 在嵌入式终端中执行脚本
                terminal.StartProcess(
                    pythonExecutablePath,
                    fullScriptPath,
                    arguments,
                    environmentVariables,
                    workingDirectory
                );

                // 更新UI状态
                SetScriptExecutionStatus($"正在终端中执行: {scriptEntry.GetDisplayName()}");
            }
            catch (Exception ex)
            {
                string errorMessage = $"启动脚本时出错: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"[ERROR] {errorMessage}\n{ex.StackTrace}");
                AppendLogMessage($"[错误] {errorMessage}");
            }
        }

        private SyncFilesToolWindowControl GetToolWindowControl()
        {
            var allWindows = Application.Current?.Windows.OfType<System.Windows.Window>();
            if (allWindows == null) return null;

            foreach (var window in allWindows)
            {
                var children = LogicalTreeHelper.GetChildren(window).OfType<DependencyObject>();
                foreach (var child in children)
                {
                    if (child is SyncFilesToolWindowControl control)
                        return control;
                }
            }
            return null;
        }
    }
}
public static class ScriptEntryViewModelExtensions
{
    public static readonly string PlaceHolderId = "__PLACEHOLDER_SCRIPT_ID__";
}