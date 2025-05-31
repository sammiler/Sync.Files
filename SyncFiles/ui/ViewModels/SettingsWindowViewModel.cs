using SyncFiles.Core.Management;
using SyncFiles.Core.Models;
using SyncFiles.Core.Settings;
using SyncFiles.UI.Common;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using Application = System.Windows.Application;
using Microsoft.VisualStudio.Threading;
using SyncFiles;
using Microsoft.VisualStudio.Shell;
using System.Threading.Tasks;

namespace SyncFiles.UI.ViewModels
{
    public class MappingViewModel : ViewModelBase
    {
        private string _sourceUrl;
        public string SourceUrl { get => _sourceUrl; set => SetProperty(ref _sourceUrl, value); }

        private string _targetPath;
        public string TargetPath { get => _targetPath; set => SetProperty(ref _targetPath, value); }

        public MappingViewModel(string sourceUrl, string targetPath)
        {
            SourceUrl = sourceUrl;
            TargetPath = targetPath;
        }
        public Mapping ToModel() => new Mapping(SourceUrl, TargetPath);
    }

    public class WatchEntryViewModel : ViewModelBase
    {
        private string _watchedPath;
        public string WatchedPath { get => _watchedPath; set => SetProperty(ref _watchedPath, value); }

        private string _onEventScript;
        public string OnEventScript { get => _onEventScript; set => SetProperty(ref _onEventScript, value); }

        public WatchEntryViewModel(string watchedPath, string onEventScript)
        {
            WatchedPath = watchedPath;
            OnEventScript = onEventScript;
        }
        public WatchEntry ToModel() => new WatchEntry(WatchedPath, OnEventScript);
    }

    public class EnvironmentVariableViewModel : ViewModelBase
    {
        private string _name;
        public string Name { get => _name; set => SetProperty(ref _name, value); }

        private string _value;
        public string Value { get => _value; set => SetProperty(ref _value, value); }

        public EnvironmentVariableViewModel(string name, string value)
        {
            Name = name;
            Value = value;
        }
        public EnvironmentVariableEntry ToModel() => new EnvironmentVariableEntry(Name, Value);
    }


    public class SettingsWindowViewModel : ViewModelBase
    {
        private readonly SyncFilesSettingsManager _settingsManager;
        private readonly string _projectBasePath; // This is the project base path *at the time the settings window is opened*
        private SyncFilesSettingsState _originalSettings;
        private readonly IAsyncServiceProvider _serviceProvider; // To get access to the Package

        public ObservableCollection<MappingViewModel> Mappings { get; }
        public MappingViewModel SelectedMapping { get; set; }

        public ObservableCollection<WatchEntryViewModel> WatchEntries { get; }
        public WatchEntryViewModel SelectedWatchEntry { get; set; }

        private string _pythonScriptPath;
        public string PythonScriptPath { get => _pythonScriptPath; set => SetProperty(ref _pythonScriptPath, value); }

        private string _pythonExecutablePath;
        public string PythonExecutablePath { get => _pythonExecutablePath; set => SetProperty(ref _pythonExecutablePath, value); }

        public ObservableCollection<EnvironmentVariableViewModel> EnvironmentVariables { get; }
        public EnvironmentVariableViewModel SelectedEnvironmentVariable { get; set; }

        public ICommand AddMappingCommand { get; }
        public ICommand RemoveMappingCommand { get; }
        public ICommand AddWatchEntryCommand { get; }
        public ICommand RemoveWatchEntryCommand { get; }
        public ICommand BrowsePythonScriptPathCommand { get; }
        public ICommand BrowsePythonExecutableCommand { get; }
        public ICommand AddEnvironmentVariableCommand { get; }
        public ICommand RemoveEnvironmentVariableCommand { get; }

        public ICommand ApplyCommand { get; }
        public ICommand ApplyAndCloseCommand { get; }
        public ICommand CancelCommand { get; }

        public event EventHandler<bool> RequestCloseDialog;
        private bool _isApplyingSettings = false;


        public SettingsWindowViewModel(SyncFilesSettingsManager settingsManager, string projectBasePath, IAsyncServiceProvider serviceProvider)
        {
            _settingsManager = settingsManager;
            _projectBasePath = projectBasePath;
            _serviceProvider = serviceProvider; // Store the service provider (Package)

            Mappings = new ObservableCollection<MappingViewModel>();
            WatchEntries = new ObservableCollection<WatchEntryViewModel>();
            EnvironmentVariables = new ObservableCollection<EnvironmentVariableViewModel>();

            LoadSettings();

            AddMappingCommand = new RelayCommand(() => Mappings.Add(new MappingViewModel("", "")));
            RemoveMappingCommand = new RelayCommand(() => { if (SelectedMapping != null) Mappings.Remove(SelectedMapping); },
                                                 () => SelectedMapping != null);

            AddWatchEntryCommand = new RelayCommand(() => WatchEntries.Add(new WatchEntryViewModel("", "")));
            RemoveWatchEntryCommand = new RelayCommand(() => { if (SelectedWatchEntry != null) WatchEntries.Remove(SelectedWatchEntry); },
                                                    () => SelectedWatchEntry != null);

            BrowsePythonScriptPathCommand = new RelayCommand(BrowseForPythonScriptPath);
            BrowsePythonExecutableCommand = new RelayCommand(BrowseForPythonExecutable);

            AddEnvironmentVariableCommand = new RelayCommand(() => EnvironmentVariables.Add(new EnvironmentVariableViewModel("", "")));
            RemoveEnvironmentVariableCommand = new RelayCommand(() => { if (SelectedEnvironmentVariable != null) EnvironmentVariables.Remove(SelectedEnvironmentVariable); },
                                                             () => SelectedEnvironmentVariable != null);

            ApplyCommand = new RelayCommand(async () => await ApplySettingsAsync(false), () => IsModified && !_isApplyingSettings);
            ApplyAndCloseCommand = new RelayCommand(async () => await ApplySettingsAsync(true), () => !_isApplyingSettings);
            CancelCommand = new RelayCommand(() => RequestCloseDialog?.Invoke(this, false));
        }


        private void LoadSettings()
        {
            _originalSettings = _settingsManager.LoadSettings(_projectBasePath);

            Mappings.Clear();
            if (_originalSettings.Mappings != null)
            {
                foreach (var m in _originalSettings.Mappings)
                {
                    if (m != null) Mappings.Add(new MappingViewModel(m.SourceUrl, m.TargetPath));
                }
            }

            WatchEntries.Clear();
            if (_originalSettings.WatchEntries != null)
            {
                foreach (var w in _originalSettings.WatchEntries)
                    WatchEntries.Add(new WatchEntryViewModel(w.WatchedPath, w.OnEventScript));
            }

            PythonScriptPath = _originalSettings.PythonScriptPath;
            PythonExecutablePath = _originalSettings.PythonExecutablePath;

            EnvironmentVariables.Clear();
            if (_originalSettings.EnvironmentVariablesList != null)
            {
                foreach (var ev in _originalSettings.EnvironmentVariablesList)
                    EnvironmentVariables.Add(new EnvironmentVariableViewModel(ev.Name, ev.Value));
            }
        }

        private async Task ApplySettingsAsync(bool closeAfterApply)
        {
            if (_isApplyingSettings) return;
            _isApplyingSettings = true;
            (ApplyCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ApplyAndCloseCommand as RelayCommand)?.RaiseCanExecuteChanged();

            bool settingsAppliedSuccessfully = false;
            SyncFilesPackage package = _serviceProvider as SyncFilesPackage;

            try
            {
                package?.SuspendConfigWatcher(); // Suspend watcher before saving

                settingsAppliedSuccessfully = await Task.Run(() =>
                {
                    var newSettings = new SyncFilesSettingsState
                    {
                        Mappings = Mappings.Select(vm => vm.ToModel()).ToList(),
                        WatchEntries = new List<WatchEntry>(),
                        PythonScriptPath = this.PythonScriptPath?.Trim() ?? string.Empty,
                        PythonExecutablePath = this.PythonExecutablePath?.Trim() ?? string.Empty,
                        EnvironmentVariablesList = EnvironmentVariables.Select(vm => vm.ToModel()).ToList(),
                        ScriptGroups = _originalSettings.ScriptGroups
                    };

                    foreach (var mapping in newSettings.Mappings)
                    {
                        if (string.IsNullOrWhiteSpace(mapping.SourceUrl) || string.IsNullOrWhiteSpace(mapping.TargetPath))
                        {
                            Application.Current?.Dispatcher?.Invoke(() =>
                                System.Windows.MessageBox.Show("Mappings cannot have empty Source URL or Target Path.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error));
                            return false;
                        }
                    }

                    var tempWatchEntriesData = WatchEntries.Select(vm => new { vm.WatchedPath, vm.OnEventScript }).ToList();
                    foreach (var watchEntryData in tempWatchEntriesData)
                    {
                        string resolvedWatchedPath = ResolvePath(watchEntryData.WatchedPath);
                        string resolvedScriptPath = ResolvePathInScriptsDir(watchEntryData.OnEventScript, newSettings.PythonScriptPath);

                        bool watchPathProvided = !string.IsNullOrWhiteSpace(watchEntryData.WatchedPath);
                        bool scriptPathProvided = !string.IsNullOrWhiteSpace(watchEntryData.OnEventScript);

                        if (watchPathProvided)
                        {
                            if (string.IsNullOrWhiteSpace(resolvedWatchedPath) || (!File.Exists(resolvedWatchedPath) && !Directory.Exists(resolvedWatchedPath)))
                            {
                                Application.Current?.Dispatcher?.Invoke(() =>
                                    System.Windows.MessageBox.Show($"Watcher Error: Watched path '{watchEntryData.WatchedPath}' (resolved to '{resolvedWatchedPath}') does not exist or is invalid.", "Settings Validation", MessageBoxButton.OK, MessageBoxImage.Error));
                                return false;
                            }
                        }
                        if (scriptPathProvided)
                        {
                            if (string.IsNullOrWhiteSpace(resolvedScriptPath) || !File.Exists(resolvedScriptPath))
                            {
                                Application.Current?.Dispatcher?.Invoke(() =>
                                    System.Windows.MessageBox.Show($"Watcher Error: Script '{watchEntryData.OnEventScript}' (resolved to '{resolvedScriptPath}') for watcher does not exist or is invalid.", "Settings Validation", MessageBoxButton.OK, MessageBoxImage.Error));
                                return false;
                            }
                        }

                        if (watchPathProvided && scriptPathProvided)
                        {
                            newSettings.WatchEntries.Add(new WatchEntry(resolvedWatchedPath, resolvedScriptPath));
                        }
                        else if (watchPathProvided && !scriptPathProvided)
                        {
                            Application.Current?.Dispatcher?.Invoke(() =>
                                   System.Windows.MessageBox.Show($"Watcher Error: Watched path '{watchEntryData.WatchedPath}' is specified, but no script to run.", "Settings Validation", MessageBoxButton.OK, MessageBoxImage.Error));
                            return false;
                        }
                        else if (!watchPathProvided && scriptPathProvided)
                        {
                            Application.Current?.Dispatcher?.Invoke(() =>
                                   System.Windows.MessageBox.Show($"Watcher Error: Script '{watchEntryData.OnEventScript}' is specified, but no path to watch.", "Settings Validation", MessageBoxButton.OK, MessageBoxImage.Error));
                            return false;
                        }
                    }

                    _settingsManager.SaveSettings(newSettings, _projectBasePath);
                    _originalSettings = newSettings;
                    System.Diagnostics.Debug.WriteLine("[INFO] SettingsWindowViewModel: Settings applied and saved by Task.Run.");
                    return true;
                });

                // No explicit refresh call here; ResumeConfigWatcher(true) will trigger it.
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ERROR] ApplySettingsAsync outer error: {ex}");
                Application.Current?.Dispatcher?.Invoke(() =>
                   System.Windows.MessageBox.Show($"An unexpected error occurred while applying settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error));
                settingsAppliedSuccessfully = false;
            }
            finally
            {
                // Resume watcher *after* all saving and potential UI updates from this window are done.
                // The ResumeConfigWatcher(true) will trigger the necessary refresh in the main tool window.
                package?.ResumeConfigWatcher(settingsAppliedSuccessfully);

                _isApplyingSettings = false;
                Application.Current?.Dispatcher?.Invoke(() => {
                    (ApplyCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (ApplyAndCloseCommand as RelayCommand)?.RaiseCanExecuteChanged();
                });

                // Close dialog only if apply was successful and closeAfterApply is true
                if (closeAfterApply && settingsAppliedSuccessfully)
                {
                    RequestCloseDialog?.Invoke(this, true);
                }
            }
        }

        private string ResolvePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            string tempPath = path;
            if (!string.IsNullOrEmpty(_projectBasePath))
            {
                tempPath = tempPath.Replace("$PROJECT_DIR$", _projectBasePath);
            }
            tempPath = Environment.ExpandEnvironmentVariables(tempPath);
            if (string.IsNullOrEmpty(tempPath)) return string.Empty;

            try
            {
                if (!System.IO.Path.IsPathRooted(tempPath))
                {
                    if (string.IsNullOrEmpty(_projectBasePath) || !Directory.Exists(_projectBasePath))
                    {
                        System.Diagnostics.Debug.WriteLine($"[WARN] ResolvePath: Cannot resolve relative path '{path}' without a valid project base path.");
                        return string.Empty;
                    }
                    tempPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(_projectBasePath, tempPath));
                }
                else
                {
                    tempPath = System.IO.Path.GetFullPath(tempPath);
                }
                return tempPath;
            }
            catch (ArgumentException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ERROR] ResolvePath: Invalid path characters in '{path}'. Details: {ex.Message}");
                return string.Empty;
            }
        }

        private string ResolvePathInScriptsDir(string relativeOrAbsolutePathToScript, string currentPythonScriptPath)
        {
            if (string.IsNullOrWhiteSpace(relativeOrAbsolutePathToScript)) return string.Empty;

            try
            {
                if (System.IO.Path.IsPathRooted(relativeOrAbsolutePathToScript))
                    return System.IO.Path.GetFullPath(relativeOrAbsolutePathToScript);

                string scriptBase = currentPythonScriptPath;
                if (string.IsNullOrWhiteSpace(scriptBase) || !Directory.Exists(scriptBase))
                {
                    if (string.IsNullOrEmpty(_projectBasePath) || !Directory.Exists(_projectBasePath))
                    {
                        System.Diagnostics.Debug.WriteLine($"[WARN] ResolvePathInScriptsDir: Cannot resolve relative script '{relativeOrAbsolutePathToScript}' without a valid Python script path or project base path.");
                        return string.Empty;
                    }
                    scriptBase = _projectBasePath;
                }
                return System.IO.Path.GetFullPath(System.IO.Path.Combine(scriptBase, relativeOrAbsolutePathToScript));
            }
            catch (ArgumentException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ERROR] ResolvePathInScriptsDir: Invalid path characters in '{relativeOrAbsolutePathToScript}'. Details: {ex.Message}");
                return string.Empty;
            }
        }

        private void BrowseForPythonScriptPath()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select Python Scripts Directory";
                if (!string.IsNullOrEmpty(PythonScriptPath) && Directory.Exists(PythonScriptPath))
                {
                    dialog.SelectedPath = PythonScriptPath;
                }
                else if (!string.IsNullOrEmpty(_projectBasePath) && Directory.Exists(_projectBasePath))
                {
                    dialog.SelectedPath = _projectBasePath;
                }
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    PythonScriptPath = dialog.SelectedPath;
                }
            }
        }

        private void BrowseForPythonExecutable()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Select Python Executable";
                dialog.Filter = "Python Executable (python.exe, pythonw.exe, python)|python.exe;pythonw.exe;python|All files (*.*)|*.*";
                if (!string.IsNullOrEmpty(PythonExecutablePath) && File.Exists(PythonExecutablePath))
                {
                    dialog.InitialDirectory = Path.GetDirectoryName(PythonExecutablePath);
                    dialog.FileName = Path.GetFileName(PythonExecutablePath);
                }
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    PythonExecutablePath = dialog.FileName;
                }
            }
        }

        public bool IsModified
        {
            get
            {
                if (_originalSettings == null) return Mappings.Any() || WatchEntries.Any() || !string.IsNullOrEmpty(PythonScriptPath) || !string.IsNullOrEmpty(PythonExecutablePath) || EnvironmentVariables.Any();

                var currentMappings = Mappings.Select(vm => vm.ToModel()).ToList();
                if (!AreListsEqual(_originalSettings.Mappings, currentMappings, (m1, m2) => m1.Equals(m2))) return true;

                var currentWatchEntryModels = WatchEntries.Select(vm => vm.ToModel()).ToList();
                if (!AreListsEqual(_originalSettings.WatchEntries, currentWatchEntryModels, (w1, w2) => w1.WatchedPath == w2.WatchedPath && w1.OnEventScript == w2.OnEventScript)) return true;


                if (_originalSettings.PythonScriptPath != (this.PythonScriptPath?.Trim() ?? string.Empty)) return true;
                if (_originalSettings.PythonExecutablePath != (this.PythonExecutablePath?.Trim() ?? string.Empty)) return true;

                var currentEnvVarsModels = EnvironmentVariables.Select(vm => vm.ToModel()).ToList();
                if (!AreListsEqual(_originalSettings.EnvironmentVariablesList, currentEnvVarsModels,
                   (e1, e2) => e1.Name == e2.Name && e1.Value == e2.Value)) return true;

                return false;
            }
        }

        private bool AreListsEqual<T>(List<T> list1, List<T> list2, Func<T, T, bool> itemComparer)
        {
            if (list1 == null && list2 == null) return true;
            if (list1 == null || list2 == null) return false;
            if (list1.Count != list2.Count) return false;

            var tempList2 = new List<T>(list2);
            foreach (T item1 in list1)
            {
                T foundItem = tempList2.FirstOrDefault(item2 => itemComparer(item1, item2));
                if (foundItem == null) return false;
                tempList2.Remove(foundItem);
            }
            return !tempList2.Any();
        }
    }
}