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
            _gitHubSyncService = gitHubSyncService;
            _fileSystemWatcherService = fileSystemWatcherService;
            _smartWorkflowService = smartWorkflowService;
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
            AppendLogMessage("SyncFiles Tool Window initialized.");
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
        public void RequestDeleteGroup(ScriptGroupViewModel groupVM) { AppendLogMessage($"TODO: Delete Group {groupVM.Name}"); }
        public void Dispose()
        {
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