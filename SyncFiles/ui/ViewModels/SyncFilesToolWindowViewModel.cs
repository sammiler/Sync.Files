using SyncFiles.Core.Management;
using SyncFiles.Core.Models;
using SyncFiles.Core.Services;
using SyncFiles.Core.Settings;
using SyncFiles.UI.Common; // Assuming RelayCommand is here
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading; // For CancellationTokenSource, if used for workflow cancellation
using System.Threading.Tasks;
using System.Windows; // For Application.Current.Dispatcher
using System.Windows.Controls;
using System.Windows.Input;
namespace SyncFiles.UI.ViewModels
{
    public class SyncFilesToolWindowViewModel : ViewModelBase, IDisposable
    {
        private SyncFilesSettingsManager _settingsManager;
        private GitHubSyncService _gitHubSyncService;
        private FileSystemWatcherService _fileSystemWatcherService;
        private SmartWorkflowService _smartWorkflowService;
        private string _projectBasePath;
        private FileSystemWatcher _pythonScriptDirWatcher;
        public ICommand SaveSettingsCommand { get; } // For explicit save, or auto-save on change
        private CancellationTokenSource _workflowCts; // For cancelling an ongoing workflow
        public ObservableCollection<ScriptGroupViewModel> ScriptGroups { get; }
        public ICommand RefreshScriptsCommand { get; }
        public ICommand AddGroupCommand { get; }
        public ICommand SyncGitHubFilesCommand { get; }
        public ICommand LoadSmartWorkflowCommand { get; }
        public ICommand CancelWorkflowCommand { get; }
        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    ((RelayCommand)LoadSmartWorkflowCommand)?.RaiseCanExecuteChanged();
                    ((RelayCommand)SyncGitHubFilesCommand)?.RaiseCanExecuteChanged();
                    ((RelayCommand)CancelWorkflowCommand)?.RaiseCanExecuteChanged();
                }
            }
        }
        // In SyncFilesToolWindowViewModel.cs
        // ... existing code ...

        public void UpdateFileWatchers(SyncFilesSettingsState settings)
        {
            if (_fileSystemWatcherService != null && settings != null)
            {
                AppendLogMessage("Updating file watchers from new settings...");
                _fileSystemWatcherService.UpdateWatchers(settings);
                AppendLogMessage("File watchers updated.");
            }
        }
        // ... existing code ...
        private string _statusMessage;
        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value); // Make setter private if only updated internally
        }
        public ObservableCollection<string> LogMessages { get; } // For a richer log display
        public SyncFilesToolWindowViewModel()
        {
            ScriptGroups = new ObservableCollection<ScriptGroupViewModel>();
            LogMessages = new ObservableCollection<string>();
            RefreshScriptsCommand = new RelayCommand(async () => await LoadAndRefreshScriptsAsync(true), () => !IsBusy);
            AddGroupCommand = new RelayCommand(AddNewGroup, () => !IsBusy); // Add CanExecute later if needed
            SyncGitHubFilesCommand = new RelayCommand(async () => await SyncGitHubFilesAsync(false), () => !IsBusy); // false indicates not part of workflow
            LoadSmartWorkflowCommand = new RelayCommand(async () => await LoadSmartWorkflowAsync(), () => !IsBusy);
            CancelWorkflowCommand = new RelayCommand(CancelWorkflow, () => IsBusy); // Can only cancel if busy
            SaveSettingsCommand = new RelayCommand(RequestSaveSettings, () => !IsBusy);
        }
        public async Task InitializeAsync(
            string projectBasePath,
            SyncFilesSettingsManager settingsManager,
            GitHubSyncService gitHubSyncService,
            FileSystemWatcherService fileSystemWatcherService,
            SmartWorkflowService smartWorkflowService)
        {
            _projectBasePath = projectBasePath;
            _settingsManager = settingsManager;
            // 如果服务实例可能在 InitializeAsync 被多次调用时发生变化，则需要先解绑旧事件
            DetachEventHandlers(); // 先解绑，以防万一
            _gitHubSyncService = gitHubSyncService;
            _fileSystemWatcherService = fileSystemWatcherService;
            _smartWorkflowService = smartWorkflowService;
            AttachEventHandlers(); // 绑定到（可能新的）服务实例

            await LoadAndRefreshScriptsAsync(true); // forceScanDisk = true 用于初次加载
            if (_fileSystemWatcherService != null)
            {
                _fileSystemWatcherService.WatchedFileChanged += OnWatchedFileChanged_Handler;
            }
            if (_gitHubSyncService != null)
            {
                _gitHubSyncService.SynchronizationCompleted += GitHubSyncService_RegularSyncCompleted_Handler;
            }
            if (_smartWorkflowService != null)
            {
                _smartWorkflowService.WorkflowDownloadPhaseCompleted += SmartWorkflowService_DownloadPhaseCompleted_Handler;
            }
            await LoadAndRefreshScriptsAsync(true);
            InitializePythonScriptWatcher();
            AppendLogMessage("SyncFiles Tool Window initialized.");
        }

        private void InitializePythonScriptWatcher()
        {
            StopPythonScriptWatcher(); // Stop existing if any

            var settings = _settingsManager.LoadSettings(_projectBasePath);
            string scriptPath = settings.PythonScriptPath;

            if (!string.IsNullOrWhiteSpace(scriptPath) && Directory.Exists(scriptPath))
            {
                try
                {
                    _pythonScriptDirWatcher = new FileSystemWatcher(scriptPath)
                    {
                        Filter = "*.py",
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.CreationTime,
                        IncludeSubdirectories = false // Or true if you want to watch subfolders for scripts
                    };
                    _pythonScriptDirWatcher.Changed += OnPythonScriptDirectoryChanged;
                    _pythonScriptDirWatcher.Created += OnPythonScriptDirectoryChanged;
                    _pythonScriptDirWatcher.Deleted += OnPythonScriptDirectoryChanged;
                    _pythonScriptDirWatcher.Renamed += OnPythonScriptDirectoryChanged;
                    _pythonScriptDirWatcher.EnableRaisingEvents = true;
                    AppendLogMessage($"Watching Python script directory for changes: {scriptPath}");
                }
                catch (Exception ex)
                {
                    AppendLogMessage($"[ERROR] Failed to initialize watcher for Python script directory '{scriptPath}': {ex.Message}");
                    _pythonScriptDirWatcher = null;
                }
            }
            else
            {
                AppendLogMessage("[INFO] Python script path not configured or directory does not exist. Watcher not started.");
            }
        }
        private void StopPythonScriptWatcher()
        {
            if (_pythonScriptDirWatcher != null)
            {
                _pythonScriptDirWatcher.EnableRaisingEvents = false;
                _pythonScriptDirWatcher.Changed -= OnPythonScriptDirectoryChanged;
                _pythonScriptDirWatcher.Created -= OnPythonScriptDirectoryChanged;
                _pythonScriptDirWatcher.Deleted -= OnPythonScriptDirectoryChanged;
                _pythonScriptDirWatcher.Renamed -= OnPythonScriptDirectoryChanged;
                _pythonScriptDirWatcher.Dispose();
                _pythonScriptDirWatcher = null;
                AppendLogMessage("Python script directory watcher stopped.");
            }
        }
        private async void OnPythonScriptDirectoryChanged(object sender, FileSystemEventArgs e)
        {
            AppendLogMessage($"Python script directory change detected: {e.ChangeType} on {e.Name}. Refreshing scripts...");
            // Debounce or delay refresh if many events fire quickly? For now, direct refresh.
            if (Application.Current?.Dispatcher != null)
            {
                await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    await LoadAndRefreshScriptsAsync(true); // Force rescan
                });
            }
        }
        public void RequestSaveSettings()
        {
            if (IsBusy) return;
            AppendLogMessage("Saving configuration changes...");
            try
            {
                var settings = _settingsManager.LoadSettings(_projectBasePath); // Get current state as base

                // Update settings from ViewModels
                settings.ScriptGroups.Clear();
                foreach (var groupVM in ScriptGroups)
                {
                    var groupModel = groupVM.GetModel(); // Assumes GetModel() returns the up-to-date ScriptGroup
                    // Ensure scripts within the groupModel are also up-to-date from their VMs
                    groupModel.Scripts.Clear();
                    foreach (var scriptVM in groupVM.Scripts)
                    {
                        groupModel.Scripts.Add(scriptVM.GetModel());
                    }
                    settings.ScriptGroups.Add(groupModel);
                }
                // If Python paths etc. were editable in main tool window, update them too.
                // For now, settings like Python paths are managed by SettingsWindow.

                _settingsManager.SaveSettings(settings, _projectBasePath);
                AppendLogMessage("Configuration saved successfully.");

                // After saving, especially if PythonScriptPath changed, re-init watcher
                InitializePythonScriptWatcher();
                // Also, if general FileSystemWatcherService depends on these settings, update it.
                UpdateFileWatchers(settings);


            }
            catch (Exception ex)
            {
                AppendLogMessage($"[ERROR] Failed to save settings: {ex.Message}");
                ShowMessage("Error Saving Settings", ex.Message);
            }
        }

        public void RequestMoveScriptToGroup(ScriptEntryViewModel scriptVM)
        {
            if (scriptVM == null) return;

            var availableGroups = ScriptGroups
                .Where(g => g.Scripts.All(s => s.Id != scriptVM.Id)) // Exclude current group if desired, or all groups
                .Select(g => g.Name)
                .ToList();

            if (!availableGroups.Any())
            {
                ShowMessage("Move Script", "No other groups available to move the script to.");
                return;
            }

            // Simple dialog to select group (replace with a proper WPF dialog)
            string targetGroupName = ShowComboBoxDialog("Move Script", $"Move '{scriptVM.DisplayName}' to group:", availableGroups);

            if (!string.IsNullOrEmpty(targetGroupName))
            {
                ScriptGroupViewModel targetGroupVM = ScriptGroups.FirstOrDefault(g => g.Name == targetGroupName);
                ScriptGroupViewModel sourceGroupVM = ScriptGroups.FirstOrDefault(g => g.Scripts.Contains(scriptVM));

                if (targetGroupVM != null && sourceGroupVM != null && sourceGroupVM != targetGroupVM)
                {
                    sourceGroupVM.RemoveScript(scriptVM); // Removes from VM's collection and model's collection
                    targetGroupVM.AddScript(scriptVM);   // Adds to VM's collection and model's collection
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
                // Confirmation dialog
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
                    ShowMessage("Error", "Default group not found. Cannot delete group.");
                    return;
                }

                // Move scripts to default group
                var scriptsToMove = new List<ScriptEntryViewModel>(groupVMToDelete.Scripts); // Iterate over a copy
                foreach (var scriptVM in scriptsToMove)
                {
                    groupVMToDelete.RemoveScript(scriptVM); // Important to remove from model too
                    defaultGroupVM.AddScript(scriptVM);     // Important to add to model too
                }

                ScriptGroups.Remove(groupVMToDelete);
                AppendLogMessage($"Deleted group '{groupVMToDelete.Name}'. Its scripts were moved to Default group.");
                RequestSaveSettings();
            }
        }


        // Placeholder for input dialogs - IMPLEMENT THESE AS REAL WPF WINDOWS
        public string ShowInputDialog(string title, string prompt, string defaultValue = "", bool multiline = false)
        {
            // TODO: Replace with a proper WPF Input Dialog Window.
            // This is a very basic placeholder.
            var inputDialog = new Window { Title = title, Width = 300, Height = multiline ? 200 : 150, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            if (Application.Current?.MainWindow?.IsVisible == true) inputDialog.Owner = Application.Current.MainWindow;

            var panel = new StackPanel { Margin = new Thickness(10) };
            panel.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 5) });
            TextBox inputTextBox;
            if (multiline)
                inputTextBox = new TextBox { Text = defaultValue, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 70, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            else
                inputTextBox = new TextBox { Text = defaultValue };
            panel.Children.Add(inputTextBox);

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var okButton = new Button { Content = "OK", IsDefault = true, Width = 75, Margin = new Thickness(0, 0, 5, 0) };
            var cancelButton = new Button { Content = "Cancel", IsCancel = true, Width = 75 };
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
            // TODO: Replace with a proper WPF ComboBox Dialog Window.
            if (items == null || !items.Any()) return null;

            var dialog = new Window { Title = title, Width = 300, Height = 180, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            if (Application.Current?.MainWindow?.IsVisible == true) dialog.Owner = Application.Current.MainWindow;

            var panel = new StackPanel { Margin = new Thickness(10) };
            panel.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 5) });
            var comboBox = new ComboBox { ItemsSource = items, SelectedIndex = 0 };
            panel.Children.Add(comboBox);

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var okButton = new Button { Content = "OK", IsDefault = true, Width = 75, Margin = new Thickness(0, 0, 5, 0) };
            var cancelButton = new Button { Content = "Cancel", IsCancel = true, Width = 75 };
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

        public bool ShowConfirmDialog(string title, string message)
        {
            // Use MessageBox for simplicity, or create a custom dialog
            MessageBoxResult result = MessageBox.Show(Application.Current?.MainWindow, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
            return result == MessageBoxResult.Yes;
        }
        public void ShowMessage(string title, string message)
        {
            MessageBox.Show(Application.Current?.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void AppendLogMessage(string message)
        {
            if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.Invoke(() => LogMessages.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}"));
            }
            else
            {
                LogMessages.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
            }
            const int maxLogEntries = 200;
            if (LogMessages.Count > maxLogEntries)
            {
                LogMessages.RemoveAt(LogMessages.Count - 1);
            }
            StatusMessage = message; // Update a simpler single status message too
        }
        private async void OnWatchedFileChanged_Handler(string scriptToExecute, string eventType, string affectedFile)
        {
            AppendLogMessage($"File event: {eventType} on '{Path.GetFileName(affectedFile)}'. Triggering script: '{Path.GetFileName(scriptToExecute)}'");
            var settings = _settingsManager.LoadSettings(_projectBasePath);
            if (string.IsNullOrEmpty(settings.PythonExecutablePath) || !File.Exists(settings.PythonExecutablePath))
            {
                AppendLogMessage($"[ERROR] Python executable not configured or not found ('{settings.PythonExecutablePath}'). Cannot run script '{Path.GetFileName(scriptToExecute)}'.");
                return;
            }
            var executor = new ScriptExecutor(_projectBasePath);
            var arguments = new List<string> { eventType, affectedFile };
            try
            {
                _ = Task.Run(async () => {
                    try
                    {
                        var result = await executor.ExecuteAndCaptureOutputAsync(
                            settings.PythonExecutablePath,
                            scriptToExecute,
                            arguments,
                            settings.EnvVariables,
                            Path.GetDirectoryName(scriptToExecute), // Usually script's own directory
                            stdout => AppendLogMessage($"SCRIPT[{Path.GetFileName(scriptToExecute)}]: {stdout}"),
                            stderr => AppendLogMessage($"SCRIPT_ERR[{Path.GetFileName(scriptToExecute)}]: {stderr}")
                        );
                        AppendLogMessage($"Script '{Path.GetFileName(scriptToExecute)}' (event-triggered) finished with exit code {result.ExitCode}.");
                    }
                    catch (Exception exInner)
                    {
                        AppendLogMessage($"[ERROR] Executing watched script '{Path.GetFileName(scriptToExecute)}' (async) failed: {exInner.Message}");
                    }
                });
            }
            catch (Exception exOuter) // Should be rare as Task.Run handles its exceptions
            {
                AppendLogMessage($"[ERROR] Failed to start background task for watched script '{Path.GetFileName(scriptToExecute)}': {exOuter.Message}");
            }
        }
        private void GitHubSyncService_RegularSyncCompleted_Handler(object sender, EventArgs e)
        {
            AppendLogMessage("GitHub file synchronization completed.");
        }
        private async void SmartWorkflowService_DownloadPhaseCompleted_Handler(object sender, EventArgs eventArgs)
        {
            AppendLogMessage("Smart workflow: File download phase complete. Proceeding to finalize configuration...");
            try
            {
                if (_smartWorkflowService == null)
                {
                    AppendLogMessage("[ERROR] SmartWorkflowService is null. Cannot finalize.");
                    IsBusy = false;
                    return;
                }
                _smartWorkflowService.FinalizeWorkflowConfiguration(); // This is a synchronous call on SmartWorkflowService
                AppendLogMessage("Smart workflow: Configuration finalized successfully by service.");
                AppendLogMessage("Reloading settings and refreshing UI after workflow finalization...");
                var latestSettings = _settingsManager.LoadSettings(_projectBasePath);
                _fileSystemWatcherService?.UpdateWatchers(latestSettings);
                AppendLogMessage("File watchers updated based on new workflow settings.");
                await LoadAndRefreshScriptsAsync(true); // Force rescan, workflow likely changed scripts/paths
                AppendLogMessage("Scripts tree refreshed after workflow.");
            }
            catch (Exception ex)
            {
                AppendLogMessage($"[ERROR] Smart workflow: Failed during configuration finalization or UI refresh: {ex.Message}");
                Console.WriteLine($"[ERROR] SmartWorkflow_DownloadPhaseCompleted_Handler: {ex.ToString()}");
            }
            finally
            {
                IsBusy = false; // Ensure busy is cleared
                _workflowCts?.Dispose();
                _workflowCts = null;
            }
        }
        public async Task LoadAndRefreshScriptsAsync(bool forceScanDisk)
        {
            if (_settingsManager == null || string.IsNullOrEmpty(_projectBasePath))
            {
                AppendLogMessage("[ERROR] Settings manager or project path not initialized. Cannot load scripts.");
                return;
            }
            IsBusy = true; // Set busy before starting the async work
            ((RelayCommand)RefreshScriptsCommand)?.RaiseCanExecuteChanged(); // Update command states
            AppendLogMessage(forceScanDisk ? "Loading settings and scanning script directory..." : "Loading settings and refreshing script tree...");
            InitializePythonScriptWatcher();
            try
            {
                await Task.Run(() => // Perform potentially long-running load/scan on a background thread
                {
                    var settings = _settingsManager.LoadSettings(_projectBasePath);
                    string pythonScriptBasePath = settings.PythonScriptPath;
                    string pythonExecutable = settings.PythonExecutablePath;
                    List<ScriptGroup> configuredGroupsModels = new List<ScriptGroup>(settings.ScriptGroups);
                    ScriptGroup defaultGroupModel = configuredGroupsModels.FirstOrDefault(g => g.Id == ScriptGroup.DefaultGroupId);
                    if (defaultGroupModel == null)
                    {
                        defaultGroupModel = new ScriptGroup(ScriptGroup.DefaultGroupId, ScriptGroup.DefaultGroupName);
                        configuredGroupsModels.Insert(0, defaultGroupModel);
                    }
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
                        catch (Exception ex)
                        {
                            Application.Current.Dispatcher.Invoke(() => AppendLogMessage($"[ERROR] Error scanning script directory '{pythonScriptBasePath}': {ex.Message}"));
                        }
                        var allCurrentScriptModels = configuredGroupsModels.SelectMany(g => g.Scripts).ToList();
                        foreach (var scriptModel in allCurrentScriptModels)
                        {
                            scriptModel.IsMissing = !diskScriptRelativePaths.Contains(scriptModel.Path);
                        }
                        defaultGroupModel.Scripts.RemoveAll(s => s.IsMissing && s.Id != ScriptEntryViewModel.PlaceHolderId); // Placeholder if any
                        var allExistingPathsInModel = new HashSet<string>(allCurrentScriptModels.Where(s => !s.IsMissing).Select(s => s.Path), StringComparer.OrdinalIgnoreCase);
                        foreach (string diskPath in diskScriptRelativePaths)
                        {
                            if (!allExistingPathsInModel.Contains(diskPath))
                            {
                                var newEntry = new ScriptEntry(diskPath) { Description = $"Auto-added {DateTime.Now:yyyy/MM/dd}" };
                                defaultGroupModel.Scripts.Add(newEntry);
                                allExistingPathsInModel.Add(diskPath);
                            }
                        }
                        settings.ScriptGroups = configuredGroupsModels;
                        _settingsManager.SaveSettings(settings, _projectBasePath);
                    }
                    else if (string.IsNullOrWhiteSpace(pythonScriptBasePath) || !Directory.Exists(pythonScriptBasePath))
                    {
                        if (forceScanDisk) AppendLogMessage(string.IsNullOrWhiteSpace(pythonScriptBasePath) ? "[INFO] Python script path is not configured." : $"[WARN] Python script directory not found: {pythonScriptBasePath}");
                        foreach (var groupModel in configuredGroupsModels)
                            foreach (var scriptModel in groupModel.Scripts)
                                scriptModel.IsMissing = true;
                    }
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        ScriptGroups.Clear();
                        var sortedGroupModels = configuredGroupsModels.OrderBy(g => g.Id == ScriptGroup.DefaultGroupId ? 0 : 1)
                                                         .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase);
                        foreach (var groupModel in sortedGroupModels)
                        {
                            var groupVM = new ScriptGroupViewModel(groupModel, pythonExecutable, pythonScriptBasePath, settings.EnvVariables, _projectBasePath, this);
                            ScriptGroups.Add(groupVM);
                        }
                        AppendLogMessage("Scripts tree UI refreshed.");
                    });
                });
            }
            catch (Exception ex)
            {
                AppendLogMessage($"[ERROR] Failed to load/refresh scripts: {ex.Message}");
                Console.WriteLine($"[ERROR] LoadAndRefreshScriptsAsync: {ex.ToString()}");
            }
            finally
            {
                IsBusy = false;
                ((RelayCommand)RefreshScriptsCommand)?.RaiseCanExecuteChanged(); // Update command states
            }
        }
        private string GetRelativePath(string fullPath, string basePath)
        {
            if (!string.IsNullOrEmpty(basePath) && !basePath.EndsWith(Path.DirectorySeparatorChar.ToString()) && !basePath.EndsWith(Path.AltDirectorySeparatorChar.ToString()))
            {
                basePath += Path.DirectorySeparatorChar;
            }
            Uri baseUri = new Uri(basePath, UriKind.Absolute);
            Uri fullUri = new Uri(fullPath, UriKind.Absolute);
            return Uri.UnescapeDataString(baseUri.MakeRelativeUri(fullUri).ToString().Replace('/', Path.DirectorySeparatorChar));
        }
        private void AddNewGroup()
        {
            string groupName = "New Group " + (ScriptGroups.Count(g => g.Id != ScriptGroup.DefaultGroupId) + 1); // Example
            if (string.IsNullOrWhiteSpace(groupName)) return;
            var settings = _settingsManager.LoadSettings(_projectBasePath);
            if (settings.ScriptGroups.Any(g => g.Name.Equals(groupName, StringComparison.OrdinalIgnoreCase)))
            {
                AppendLogMessage($"[WARN] Group '{groupName}' already exists.");
                return;
            }
            var newGroupModel = new ScriptGroup(Guid.NewGuid().ToString(), groupName.Trim());
            settings.ScriptGroups.Add(newGroupModel);
            _settingsManager.SaveSettings(settings, _projectBasePath);
            var groupVM = new ScriptGroupViewModel(newGroupModel, settings.PythonExecutablePath, settings.PythonScriptPath, settings.EnvVariables, _projectBasePath, this);
            Application.Current.Dispatcher.Invoke(() => ScriptGroups.Add(groupVM)); // Add to UI
            AppendLogMessage($"Group '{groupName}' added.");
        }
        private async Task SyncGitHubFilesAsync(bool isPartOfWorkflow)
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
                var settings = _settingsManager.LoadSettings(_projectBasePath);
                await _gitHubSyncService.SyncAllAsync(settings, _workflowCts?.Token ?? CancellationToken.None);
                if (!isPartOfWorkflow) // Only do these if it's a standalone sync
                {
                    AppendLogMessage("GitHub sync (standalone) completed.");
                    await LoadAndRefreshScriptsAsync(true);
                    _fileSystemWatcherService?.UpdateWatchers(settings);
                }
            }
            catch (OperationCanceledException)
            {
                AppendLogMessage("[CANCELLED] GitHub sync was cancelled.");
            }
            catch (Exception ex)
            {
                AppendLogMessage($"[ERROR] GitHub sync failed: {ex.Message}");
            }
            finally
            {
                if (!isPartOfWorkflow) IsBusy = false; // Only set to false if not part of a larger workflow operation
            }
        }
        private async Task LoadSmartWorkflowAsync()
        {
            if (_smartWorkflowService == null || _settingsManager == null || _gitHubSyncService == null)
            {
                AppendLogMessage("[ERROR] Workflow services not initialized.");
                return;
            }
            string yamlUrl = string.Empty; // Default to empty
            var inputDialog = new Window { Title = "Load Smart Workflow", Width = 450, Height = 180, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
            if (Application.Current != null && Application.Current.MainWindow != null && Application.Current.MainWindow.IsVisible)
            {
                inputDialog.Owner = Application.Current.MainWindow; // Set owner if possible
            }
            var panel = new StackPanel { Margin = new Thickness(10) };
            panel.Children.Add(new TextBlock { Text = "Enter YAML Configuration URL:", Margin = new Thickness(0, 0, 0, 5) });
            var urlTextBox = new TextBox { Text = "https://raw.githubusercontent.com/sammiler/CodeConf/refs/heads/main/Cpp/SyncFiles/Clion/workflow.yaml", Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(2) };
            panel.Children.Add(urlTextBox);
            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okButton = new Button { Content = "Load", IsDefault = true, Width = 75, Margin = new Thickness(0, 0, 5, 0) };
            var cancelButton = new Button { Content = "Cancel", IsCancel = true, Width = 75 };
            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            panel.Children.Add(buttonPanel);
            inputDialog.Content = panel;
            bool? dialogResult = null;
            okButton.Click += (s, e) => { yamlUrl = urlTextBox.Text; dialogResult = true; inputDialog.Close(); };
            cancelButton.Click += (s, e) => { dialogResult = false; inputDialog.Close(); };
            if (Application.Current != null)
            {
                inputDialog.ShowDialog();
            }
            else
            {
                AppendLogMessage("[WARN] Cannot show URL dialog outside of WPF app context. Workflow load might fail if URL is not preset.");
            }
            if (dialogResult != true || string.IsNullOrWhiteSpace(yamlUrl))
            {
                AppendLogMessage("Smart workflow loading cancelled or URL is empty.");
                return;
            }
            IsBusy = true;
            AppendLogMessage($"Loading smart workflow from: {yamlUrl}...");
            _workflowCts = new CancellationTokenSource(); // Create a new CTS for this operation
            try
            {
                await _smartWorkflowService.PrepareWorkflowFromYamlUrlAsync(yamlUrl, _workflowCts.Token);
            }
            catch (OperationCanceledException)
            {
                AppendLogMessage("[CANCELLED] Smart workflow operation was cancelled.");
                IsBusy = false; // Ensure IsBusy is reset if preparation itself is cancelled
                _workflowCts?.Dispose();
                _workflowCts = null;
            }
            catch (Exception ex)
            {
                AppendLogMessage($"[ERROR] Smart workflow loading failed during preparation: {ex.Message}");
                IsBusy = false; // Reset IsBusy on error during prepare
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

        // 新增/修改: 当项目路径或服务实例改变时，用于更新 ViewModel 上下文的方法
        public async Task UpdateProjectContextAsync(
            string newProjectBasePath,
            GitHubSyncService newGitHubSyncService,       // 包可能会传递新的服务实例
            FileSystemWatcherService newFileSystemWatcherService,
            SmartWorkflowService newSmartWorkflowService)
        {
            bool pathChanged = _projectBasePath != newProjectBasePath;
            bool servicesChanged = _gitHubSyncService != newGitHubSyncService ||
                                   _fileSystemWatcherService != newFileSystemWatcherService ||
                                   _smartWorkflowService != newSmartWorkflowService;

            // 如果路径和服务实例都没变，可能不需要做太多事情
            if (!pathChanged && !servicesChanged && _settingsManager != null /*确保settingsManager已初始化*/)
            {
                // AppendLogMessage("项目上下文无显著变化。");
                // 即使路径和服务没变，可能配置变了，也考虑刷新一下脚本和监听器
                // 这一步可选，取决于你希望刷新的时机多么频繁
                // await LoadAndRefreshScriptsAsync(false); // false 表示不强制扫描磁盘，仅根据配置刷新
                // if (!string.IsNullOrEmpty(newProjectBasePath)) {
                //     var currentSettings = _settingsManager.LoadSettings(newProjectBasePath);
                //     UpdateFileWatchers(currentSettings);
                // }
                return;
            }

            AppendLogMessage($"正在更新 ViewModel 项目上下文。新路径: '{newProjectBasePath ?? "null"}'。服务实例是否重新分配: {servicesChanged}");

            _projectBasePath = newProjectBasePath; // 更新内部的项目路径

            // 如果服务实例本身发生了变化 (例如，被 Package 重新创建了)
            if (servicesChanged)
            {
                DetachEventHandlers(); // 从旧的服务实例解绑事件

                _gitHubSyncService = newGitHubSyncService;
                _fileSystemWatcherService = newFileSystemWatcherService;
                _smartWorkflowService = newSmartWorkflowService;

                AttachEventHandlers(); // 绑定到新的服务实例
            }

            // 如果路径改变了，或者有其他强制刷新逻辑，则重新加载脚本和监听器
            // LoadAndRefreshScriptsAsync 必须能健壮地处理 _projectBasePath 为 null 的情况
            await LoadAndRefreshScriptsAsync(true); // forceScanDisk = true 以反映新路径或空状态

            // 根据新路径和当前设置更新文件监听器
            if (_settingsManager != null && !string.IsNullOrEmpty(_projectBasePath))
            {
                var currentSettings = _settingsManager.LoadSettings(_projectBasePath);
                UpdateFileWatchers(currentSettings);
            }
            else if (_fileSystemWatcherService != null) // 如果路径现在为 null，确保监听器被清除
            {
                _fileSystemWatcherService.UpdateWatchers(new SyncFilesSettingsState());
            }
            AppendLogMessage("ViewModel 项目上下文更新完毕。");
        }

        // 用于添加和移除服务事件处理的辅助方法
        private void AttachEventHandlers()
        {
            if (_fileSystemWatcherService != null)
            {
                _fileSystemWatcherService.WatchedFileChanged += OnWatchedFileChanged_Handler;
            }
            if (_gitHubSyncService != null)
            {
                _gitHubSyncService.SynchronizationCompleted += GitHubSyncService_RegularSyncCompleted_Handler;
                _gitHubSyncService.ProgressReported += GitHubSyncService_ProgressReported_Handler; // 添加进度报告处理
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

        // GitHub 同步进度报告的处理方法
        private void GitHubSyncService_ProgressReported_Handler(string progressMessage)
        {
            AppendLogMessage($"[同步] {progressMessage}");
        }

        public void Dispose()
        {
            StopPythonScriptWatcher();
            if (_fileSystemWatcherService != null)
            {
                _fileSystemWatcherService.WatchedFileChanged -= OnWatchedFileChanged_Handler;
            }
            if (_gitHubSyncService != null)
            {
                _gitHubSyncService.SynchronizationCompleted -= GitHubSyncService_RegularSyncCompleted_Handler;
            }
            if (_smartWorkflowService != null)
            {
                _smartWorkflowService.WorkflowDownloadPhaseCompleted -= SmartWorkflowService_DownloadPhaseCompleted_Handler;
                _smartWorkflowService.UnsubscribeGitHubSyncEvents(); // Crucial
            }
            _workflowCts?.Dispose();
            LogMessages.Clear();
            Console.WriteLine("SyncFilesToolWindowViewModel Disposed.");
        }

       
    }
}
public static class ScriptEntryViewModelExtensions
{
    public static readonly string PlaceHolderId = "__PLACEHOLDER_SCRIPT_ID__";
}