using SyncFiles.Core.Models; // For ScriptEntry model
using SyncFiles.Core.Services;
using SyncFiles.UI.Common;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text; // For StringBuilder
using System.Threading.Tasks;
using System.Windows.Input; // For ICommand (稍后会添加命令实现)
namespace SyncFiles.UI.ViewModels
{
    public class ScriptEntryViewModel : ViewModelBase
    {
        public static readonly string PlaceHolderId = "__PLACEHOLDER_SCRIPT_ID__"; // 定义常量
        private readonly ScriptEntry _model;
        private readonly ScriptExecutor _scriptExecutor; // 稍后会注入或创建
        private readonly string _pythonExecutablePath; // 从主配置获取
        private readonly string _pythonScriptBasePath; // 从主配置获取
        private readonly Dictionary<string, string> _environmentVariables; // 从主配置获取
        private readonly string _projectBasePath; // 从主配置或服务获取
        private bool _isMissing;

        private string _normalScriptIconPath;
        public string NormalScriptIconPath { get => _normalScriptIconPath; set => SetProperty(ref _normalScriptIconPath, value); }

        private string _warningScriptIconPath;
        public string WarningScriptIconPath { get => _warningScriptIconPath; set => SetProperty(ref _warningScriptIconPath, value); }
        private readonly SyncFilesToolWindowViewModel _parentViewModel; // Store parent
        public bool CanExecuteScript => !IsMissing; // Or any other logic

        public bool IsExecutionModeTerminal => ExecutionMode.Equals("terminal", StringComparison.OrdinalIgnoreCase);
        public bool IsExecutionModeDirectApi => ExecutionMode.Equals("directApi", StringComparison.OrdinalIgnoreCase);
        public bool IsMissing
        {
            get => _isMissing;
            set
            {
                if (SetProperty(ref _isMissing, value))
                {
                    OnPropertyChanged(nameof(DisplayNameWithStatus)); // 如果显示名包含状态
                    OnPropertyChanged(nameof(ToolTipText));
                    // Icon change is now handled by parent VM setting NormalScriptIconPath/WarningScriptIconPath
                    // and XAML DataTrigger switching based on IsMissing.
                }
            }
        }
        public string Id => _model.Id;
        public string Path => _model.Path;
        public string Alias
        {
            get => _model.Alias;
            set
            {
                if (_model.Alias != value)
                {
                    _model.Alias = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayName));
                    OnPropertyChanged(nameof(ToolTipText));
                }
            }
        }
        public string ExecutionMode
        {
            get => _model.ExecutionMode;
            set
            {
                if (_model.ExecutionMode != value)
                {
                    _model.ExecutionMode = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsExecutionModeTerminal)); // Notify this dependent property
                    OnPropertyChanged(nameof(IsExecutionModeDirectApi)); // Notify this dependent property
                }
            }
        }
        public string Description
        {
            get => _model.Description;
            set
            {
                if (_model.Description != value)
                {
                    _model.Description = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ToolTipText));
                }
            }
        }
        public string DisplayName => _model.GetDisplayName();
        public string DisplayNameWithStatus => IsMissing ? $"{DisplayName} (Missing)" : DisplayName;
        public string ToolTipText
        {
            get
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Path: {Path}");
                if (!string.IsNullOrWhiteSpace(Alias))
                {
                    sb.AppendLine($"Alias: {Alias}");
                }
                sb.AppendLine($"Mode: {ExecutionMode}");
                if (!string.IsNullOrWhiteSpace(Description))
                {
                    sb.AppendLine("Description:");
                    sb.Append(Description);
                }
                if (IsMissing)
                {
                    sb.AppendLine();
                    sb.AppendLine("Status: File is missing!");
                }
                return sb.ToString();
            }
        }
        public string PathAndMissingToolTipText => IsMissing ? $"File not found: {System.IO.Path.Combine(_pythonScriptBasePath ?? "", Path)}\n\n{ToolTipText}" : ToolTipText;
        public ICommand ExecuteCommand { get; private set; }
        public ICommand RunInTerminalCommand { get; private set; }
        public ICommand RunDirectCommand { get; private set; }
        public ICommand OpenScriptFileCommand { get; private set; }
        public ICommand SetAliasCommand { get; private set; }
        public ICommand SetExecutionModeCommand { get; private set; }
        public ICommand SetDescriptionCommand { get; private set; }
        public ICommand MoveToGroupCommand { get; private set; }    // Needs interaction with parent/main VM
        public ICommand RemoveFromGroupCommand { get; private set; } // Needs interaction with parent/main VM

        public ICommand SetExecutionModeToTerminalCommand { get; }
        public ICommand SetExecutionModeToDirectApiCommand { get; }
        public bool IsScriptEntry => true;
        public bool IsScriptGroup => false;
        public ScriptEntryViewModel(
            ScriptEntry model,
            string pythonExecutablePath,
            string pythonScriptBasePath,
            Dictionary<string, string> environmentVariables,
            string projectBasePath,
            SyncFilesToolWindowViewModel parentViewModel) // Pass parent for actions like move/remove
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _pythonExecutablePath = pythonExecutablePath;
            _pythonScriptBasePath = pythonScriptBasePath;
            _environmentVariables = environmentVariables ?? new Dictionary<string, string>();
            _projectBasePath = projectBasePath;
            _parentViewModel = parentViewModel ?? throw new ArgumentNullException(nameof(parentViewModel));
            IsMissing = _model.IsMissing; // Initialize from model's persisted state if any
            ExecuteCommand = new DelegateCommand(Execute, CanExecute);
            RunInTerminalCommand = new DelegateCommand(ExecuteInTerminal, CanExecute);
            RunDirectCommand = new DelegateCommand(ExecuteDirectly, CanExecute);
            OpenScriptFileCommand = new DelegateCommand(OpenScript, CanExecute);
            SetExecutionModeToTerminalCommand = new RelayCommand(() => SetExecutionMode("terminal"), CanExecute);
            SetExecutionModeToDirectApiCommand = new RelayCommand(() => SetExecutionMode("directApi"), CanExecute);
            SetAliasCommand = new RelayCommand(RequestSetAlias, CanExecute);
            SetDescriptionCommand = new RelayCommand(RequestSetDescription, CanExecute);
            MoveToGroupCommand = new RelayCommand(RequestMoveToGroup, CanExecute);
            RemoveFromGroupCommand = new RelayCommand(RequestRemoveFromGroup, CanExecute);
        }
        public class DelegateCommand : ICommand
        {
            private readonly Action _execute;
            private readonly Func<bool> _canExecute;
            public event EventHandler CanExecuteChanged;
            public DelegateCommand(Action execute, Func<bool> canExecute = null)
            {
                _execute = execute ?? throw new ArgumentNullException(nameof(execute));
                _canExecute = canExecute;
            }
            public bool CanExecute(object parameter) => _canExecute == null || _canExecute();
            public void Execute(object parameter) => _execute();
            public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
        private bool CanExecute() => !IsMissing;

        private void Execute() // This is the method bound to ExecuteCommand
        {
            if (!CanExecuteScript) // Assuming CanExecuteScript is your guard property
            {
                _parentViewModel?.SetScriptExecutionStatus($"Cannot execute: {DisplayName} (missing or other condition).");
                return;
            }

            string fullScriptPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(_pythonScriptBasePath, Path));
            if (!File.Exists(fullScriptPath))
            {
                IsMissing = true; // Update status
                _parentViewModel?.SetScriptExecutionStatus($"ERROR: Script file not found for {DisplayName}: {fullScriptPath}");
                Console.WriteLine($"[ERROR] Script file not found: {fullScriptPath}");
                return;
            }

            var executor = new Core.Services.ScriptExecutor(_projectBasePath);
            var arguments = new List<string>(); // Populate if your scripts take arguments

            if (ExecutionMode.Equals("terminal", StringComparison.OrdinalIgnoreCase))
            {
                _parentViewModel?.SetScriptExecutionStatus($"Launching in terminal: {DisplayName}...");
                try
                {
                    executor.LaunchInExternalTerminal(
                        _pythonExecutablePath,
                        fullScriptPath,
                        string.Join(" ", arguments.Select(a => $"\"{a}\"")),
                        DisplayName,
                        _environmentVariables,
                        _projectBasePath
                    );
                    _parentViewModel?.SetScriptExecutionStatus($"Terminal launched for: {DisplayName}.");
                }
                catch (Exception ex)
                {
                    _parentViewModel?.SetScriptExecutionStatus($"ERROR launching terminal for {DisplayName}: {ex.Message}");
                    _parentViewModel?.AppendScriptError(DisplayName, $"Terminal launch error: {ex.Message}");
                }
            }
            else // Direct API
            {
                _parentViewModel?.ClearScriptOutput(); // Clear previous output
                _parentViewModel?.SetScriptExecutionStatus($"Executing (direct): {DisplayName}...");
                IsScriptOutputVisible = true; // Make sure output panel is visible

                // ExecuteAndCaptureOutputAsync returns a Task<ScriptExecutionResult>
                // We use ContinueWith to handle completion/failure without blocking the Execute method
                _ = executor.ExecuteAndCaptureOutputAsync(
                    _pythonExecutablePath,
                    fullScriptPath,
                    arguments,
                    _environmentVariables,
                    _projectBasePath,
                    // onOutputDataReceived:
                    stdout => _parentViewModel?.AppendScriptOutput(DisplayName, stdout),
                    // onErrorDataReceived:
                    stderr => _parentViewModel?.AppendScriptError(DisplayName, stderr)
                ).ContinueWith(task => {
                    // This continuation runs when the async operation completes (success, fault, or cancel)
                    // It's important to dispatch UI updates back to the UI thread.

                    if (task.Status == TaskStatus.RanToCompletion) // <--- USING TaskStatus.RanToCompletion
                    {
                        _parentViewModel?.HandleScriptExecutionCompletion(DisplayName, task.Result);
                    }
                    else if (task.Status == TaskStatus.Faulted)
                    {
                        _parentViewModel?.HandleScriptExecutionCompletion(DisplayName, null, task.Exception.Flatten().InnerException);
                    }
                    else if (task.Status == TaskStatus.Canceled)
                    {
                        _parentViewModel?.SetScriptExecutionStatus($"CANCELLED: {DisplayName}.");
                        _parentViewModel?.AppendScriptOutput(DisplayName, "Execution was cancelled.");
                    }
                }, TaskScheduler.Default); // Using TaskScheduler.Default for the continuation, then dispatching inside HandleScriptExecutionCompletion
            }
        }
        // Property to control output visibility from ScriptEntry (optional, could be global in parent)
        private bool _isScriptOutputVisible;
        public bool IsScriptOutputVisible
        {
            get => _isScriptOutputVisible;
            set
            {
                if (SetProperty(ref _isScriptOutputVisible, value) && _parentViewModel != null)
                {
                    _parentViewModel.IsScriptOutputVisible = value; // Sync with parent's property
                }
            }
        }
        private void SetExecutionMode(string mode)
        {
            if (!CanExecute()) return;
            ExecutionMode = mode; // This should trigger OnPropertyChanged if setter is correct
            _parentViewModel.RequestSaveSettings(); // Tell parent to save all settings
        }

        private void RequestSetAlias()
        {
            if (!CanExecute()) return;
            // Simplistic input - replace with a proper dialog window
            string newAlias = ShowInputDialog("Enter new alias:", Alias);
            if (newAlias != null) // Not cancelled
            {
                Alias = newAlias.Trim();
                _parentViewModel.RequestSaveSettings();
            }
        }

        private void RequestSetDescription()
        {
            if (!CanExecute()) return;
            string newDescription = ShowInputDialog("Enter new description:", Description, true); // true for multiline
            if (newDescription != null)
            {
                Description = newDescription; // Trim handled by property setter or here
                _parentViewModel.RequestSaveSettings();
            }
        }

        private void RequestMoveToGroup()
        {
            if (!CanExecute()) return;
            _parentViewModel.RequestMoveScriptToGroup(this);
        }

        private void RequestRemoveFromGroup()
        {
            if (!CanExecute()) return;
            _parentViewModel.RequestRemoveScriptFromCurrentGroup(this);
        }

        private string ShowInputDialog(string prompt, string defaultValue, bool multiline = false)
        {
            // This is a VERY basic example. You should create a proper WPF input dialog window.
            // For now, let's imagine a placeholder or skip to direct ViewModel interaction for brevity.
            // In a real app, you'd open a new Window.
            // For demonstration, let's assume _parentViewModel has a method to show a dialog.
            return _parentViewModel.ShowInputDialog(prompt, defaultValue, "", multiline);
        }
        private void ExecuteInTerminal() { string tempMode = ExecutionMode; ExecutionMode = "terminal"; Execute(); ExecutionMode = tempMode; }
        private void ExecuteDirectly() { string tempMode = ExecutionMode; ExecutionMode = "directApi"; Execute(); ExecutionMode = tempMode; }

        private async void OpenScript()
        {
            if (IsMissing || _parentViewModel == null) return;
            string fullScriptPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(_pythonScriptBasePath, Path));
            if (!File.Exists(fullScriptPath))
            {
                IsMissing = true;
                _parentViewModel.ShowErrorMessage("Error", $"Script file not found: {fullScriptPath}");
                Console.WriteLine($"[ERROR] Script file not found for opening: {fullScriptPath}");
                return;
            }
            Console.WriteLine($"Request to open script in IDE: {fullScriptPath}");
            // 只在VS中打开
            await _parentViewModel.OpenFileInIdeAsync(fullScriptPath);

            // 移除了 Process.Start(...)
            // try
            // {
            //     Process.Start(new ProcessStartInfo(fullScriptPath) { UseShellExecute = true });
            // }
            // catch (Exception ex)
            // {
            //     Console.WriteLine($"[ERROR] Failed to open script file '{fullScriptPath}' with system default: {ex.Message}");
            // }
        }
        public ScriptEntry GetModel() => _model;
    }
}