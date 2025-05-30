// File: UI/ViewModels/SettingsWindowViewModel.cs
using SyncFiles.Core.Management;
using SyncFiles.Core.Models;
using SyncFiles.Core.Settings;
using SyncFiles.UI.Common; // For RelayCommand
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Forms; // For FolderBrowserDialog and OpenFileDialog - Add reference to System.Windows.Forms assembly
using System.Windows.Input;
using Application = System.Windows.Application; // To resolve ambiguity with System.Windows.Forms.Application

namespace SyncFiles.UI.ViewModels
{
    // Helper class for DataGrid items (to enable easy property changes for existing models)
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
        private readonly string _projectBasePath;
        private SyncFilesSettingsState _originalSettings; // To check for modifications

        // Settings Properties for Binding
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

        // Commands
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


        public SettingsWindowViewModel(SyncFilesSettingsManager settingsManager, string projectBasePath)
        {
            _settingsManager = settingsManager;
            _projectBasePath = projectBasePath;

            Mappings = new ObservableCollection<MappingViewModel>();
            WatchEntries = new ObservableCollection<WatchEntryViewModel>();
            EnvironmentVariables = new ObservableCollection<EnvironmentVariableViewModel>();

            LoadSettings();

            // Initialize Commands
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

            ApplyCommand = new RelayCommand(() => {
                ApplySettings();
                RequestCloseDialog?.Invoke(this, true); // Add this line to also close the dialog
            }, () => IsModified);
            ApplyAndCloseCommand = new RelayCommand(() => { ApplySettings(); RequestCloseDialog?.Invoke(this, true); });
            CancelCommand = new RelayCommand(() => RequestCloseDialog?.Invoke(this, false));
        }


        private void LoadSettings()
        {
            _originalSettings = _settingsManager.LoadSettings(_projectBasePath);
            System.Diagnostics.Debug.WriteLine($"[LoadSettings] Loaded _originalSettings. Mappings count: {_originalSettings.Mappings?.Count ?? 0}");
            if (_originalSettings.Mappings != null && _originalSettings.Mappings.Count > 0)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadSettings] Original Mapping[0]: Source='{_originalSettings.Mappings[0].SourceUrl}', Target='{_originalSettings.Mappings[0].TargetPath}'");
            }

            Mappings.Clear(); // ObservableCollection for the UI
            System.Diagnostics.Debug.WriteLine($"[LoadSettings] Mappings collection cleared. Count: {Mappings.Count}");

            if (_originalSettings.Mappings != null) // Null check for safety
            {
                foreach (var m in _originalSettings.Mappings)
                {
                    if (m == null)
                    {
                        System.Diagnostics.Debug.WriteLine("[LoadSettings] Encountered a null mapping object in _originalSettings.Mappings. Skipping.");
                        continue;
                    }
                    System.Diagnostics.Debug.WriteLine($"[LoadSettings] Processing original mapping: Source='{m.SourceUrl}', Target='{m.TargetPath}'");

                    var newMappingVm = new MappingViewModel(m.SourceUrl, m.TargetPath);
                    System.Diagnostics.Debug.WriteLine($"[LoadSettings] Created MappingViewModel: Source='{newMappingVm.SourceUrl}', Target='{newMappingVm.TargetPath}'");

                    Mappings.Add(newMappingVm);
                    System.Diagnostics.Debug.WriteLine($"[LoadSettings] Added to Mappings collection. New count: {Mappings.Count}");
                    if (Mappings.Count > 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"[LoadSettings] Mappings[0] after add: Source='{Mappings[0].SourceUrl}', Target='{Mappings[0].TargetPath}'");
                    }
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[LoadSettings] _originalSettings.Mappings is null.");
            }
            System.Diagnostics.Debug.WriteLine($"[LoadSettings] Finished populating Mappings. Final count: {Mappings.Count}");


            // Populate other settings
            WatchEntries.Clear();
            if (_originalSettings.WatchEntries != null)
            {
                foreach (var w in _originalSettings.WatchEntries)
                    WatchEntries.Add(new WatchEntryViewModel(w.WatchedPath, w.OnEventScript));
            }
            System.Diagnostics.Debug.WriteLine($"[LoadSettings] Finished populating WatchEntries. Final count: {WatchEntries.Count}");

            PythonScriptPath = _originalSettings.PythonScriptPath;
            System.Diagnostics.Debug.WriteLine($"[LoadSettings] PythonScriptPath set to: '{PythonScriptPath}'");
            PythonExecutablePath = _originalSettings.PythonExecutablePath;
            System.Diagnostics.Debug.WriteLine($"[LoadSettings] PythonExecutablePath set to: '{PythonExecutablePath}'");

            EnvironmentVariables.Clear();
            if (_originalSettings.EnvironmentVariablesList != null)
            {
                foreach (var ev in _originalSettings.EnvironmentVariablesList)
                    EnvironmentVariables.Add(new EnvironmentVariableViewModel(ev.Name, ev.Value));
            }
            System.Diagnostics.Debug.WriteLine($"[LoadSettings] Finished populating EnvironmentVariables. Final count: {EnvironmentVariables.Count}");
        }

        private void ApplySettings()
        {
            var newSettings = new SyncFilesSettingsState
            {
                Mappings = Mappings.Select(vm => vm.ToModel()).ToList(),
                WatchEntries = WatchEntries.Select(vm => vm.ToModel()).ToList(),
                PythonScriptPath = this.PythonScriptPath?.Trim() ?? string.Empty,
                PythonExecutablePath = this.PythonExecutablePath?.Trim() ?? string.Empty,
                EnvironmentVariablesList = EnvironmentVariables.Select(vm => vm.ToModel()).ToList(),
                // Preserve script groups - this settings window doesn't manage them directly
                ScriptGroups = _originalSettings.ScriptGroups
            };

            // Basic Validation (can be expanded)
            foreach (var mapping in newSettings.Mappings)
            {
                if (string.IsNullOrWhiteSpace(mapping.SourceUrl) || string.IsNullOrWhiteSpace(mapping.TargetPath))
                {
                    System.Windows.MessageBox.Show("Mappings cannot have empty Source URL or Target Path.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            // Add more validation as needed for other fields
            foreach (var watchEntryVm in WatchEntries)
            {
                string resolvedWatchedPath = ResolvePath(watchEntryVm.WatchedPath); // You'll need ResolvePath
                string resolvedScriptPath = ResolvePathInScriptsDir(watchEntryVm.OnEventScript); // You'll need this too

                if (string.IsNullOrWhiteSpace(resolvedWatchedPath)) { /* error */ return; }
                if (string.IsNullOrWhiteSpace(resolvedScriptPath)) { /* error */ return; }

                if (!File.Exists(resolvedWatchedPath) && !Directory.Exists(resolvedWatchedPath))
                {
                    System.Windows.MessageBox.Show($"Watcher Error: Watched path '{watchEntryVm.WatchedPath}' (resolved to '{resolvedWatchedPath}') does not exist.", "Settings Validation", MessageBoxButton.OK, MessageBoxImage.Error);
                    return; // Stop saving
                }
                if (!File.Exists(resolvedScriptPath))
                {
                    System.Windows.MessageBox.Show($"Watcher Error: Script '{watchEntryVm.OnEventScript}' (resolved to '{resolvedScriptPath}') for watcher does not exist.", "Settings Validation", MessageBoxButton.OK, MessageBoxImage.Error);
                    return; // Stop saving
                }
                newSettings.WatchEntries.Add(new WatchEntry(resolvedWatchedPath, resolvedScriptPath));
            }
            _settingsManager.SaveSettings(newSettings, _projectBasePath);
            _originalSettings = newSettings; // Update original settings after saving

            // Optionally, notify other parts of the application that settings have changed
            // This might involve a message bus or events if other ViewModels need to react
            Console.WriteLine("[INFO] SettingsWindowViewModel: Settings applied and saved.");

            // If the main tool window ViewModel needs to refresh after settings change:
            if (SyncFilesPackage.ToolWindowViewModel != null)
            {
                Application.Current.Dispatcher.Invoke(async () =>
                {
                    await SyncFilesPackage.ToolWindowViewModel.LoadAndRefreshScriptsAsync(true); 
                                                                                                 
                    var currentLoadedSettings = _settingsManager.LoadSettings(_projectBasePath); 
                    SyncFilesPackage.ToolWindowViewModel.UpdateFileWatchers(currentLoadedSettings);
                });
            }
        }
        // Helper methods in SettingsWindowViewModel:
        private string ResolvePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            // Basic resolution, assuming $PROJECT_DIR$ or absolute
            string tempPath = path.Replace("$PROJECT_DIR$", _projectBasePath);
            tempPath = Environment.ExpandEnvironmentVariables(tempPath);
            if (!System.IO.Path.IsPathRooted(tempPath))
            {
                tempPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(_projectBasePath, tempPath));
            }
            return tempPath;
        }
        private string ResolvePathInScriptsDir(string relativeOrAbsolutePathToScript)
        {
            if (string.IsNullOrWhiteSpace(relativeOrAbsolutePathToScript)) return relativeOrAbsolutePathToScript;
            if (System.IO.Path.IsPathRooted(relativeOrAbsolutePathToScript)) return relativeOrAbsolutePathToScript;

            // If PythonScriptPath is set and valid, resolve relative to it
            if (!string.IsNullOrWhiteSpace(this.PythonScriptPath) && Directory.Exists(this.PythonScriptPath))
            {
                return System.IO.Path.GetFullPath(System.IO.Path.Combine(this.PythonScriptPath, relativeOrAbsolutePathToScript));
            }
            // Fallback: resolve relative to project if script path is not set/valid
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(_projectBasePath, relativeOrAbsolutePathToScript));
        }

        private void BrowseForPythonScriptPath()
        {
            using (var dialog = new FolderBrowserDialog())
                {
                dialog.Description = "Select Python Scripts Directory";
                dialog.SelectedPath = PythonScriptPath; // Start from current path if set
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
                dialog.Filter = "Python Executable (python.exe, python)|python.exe;python|All files (*.*)|*.*";
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

        // You might want an IsModified property for the "Apply" button enable/disable logic
        public bool IsModified
        {
            get
            {
                if (_originalSettings == null) return true; // If never loaded, assume modified

                var currentMappings = Mappings.Select(vm => vm.ToModel()).ToList();
                if (!AreListsEqual(_originalSettings.Mappings, currentMappings, (m1, m2) => m1.Equals(m2))) return true;

                var currentWatchEntries = WatchEntries.Select(vm => vm.ToModel()).ToList();
                if (!AreListsEqual(_originalSettings.WatchEntries, currentWatchEntries, (w1, w2) => w1.Equals(w2))) return true;

                if (_originalSettings.PythonScriptPath != (this.PythonScriptPath?.Trim() ?? string.Empty)) return true;
                if (_originalSettings.PythonExecutablePath != (this.PythonExecutablePath?.Trim() ?? string.Empty)) return true;

                var currentEnvVars = EnvironmentVariables.Select(vm => vm.ToModel()).ToList();
                if (!AreListsEqual(_originalSettings.EnvironmentVariablesList, currentEnvVars,
                   (e1, e2) => e1.Name == e2.Name && e1.Value == e2.Value)) return true;

                return false;
            }
        }

        private bool AreListsEqual<T>(List<T> list1, List<T> list2, Func<T, T, bool> comparer)
        {
            if (list1 == null && list2 == null) return true;
            if (list1 == null || list2 == null) return false;
            if (list1.Count != list2.Count) return false;
            for (int i = 0; i < list1.Count; i++)
            {
                if (!comparer(list1[i], list2[i])) return false;
            }
            return true;
        }
    }
}