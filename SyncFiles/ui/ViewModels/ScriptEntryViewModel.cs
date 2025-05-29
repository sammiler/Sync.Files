using SyncFiles.Core.Models; // For ScriptEntry model
using SyncFiles.Core.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text; // For StringBuilder
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
        public bool IsMissing
        {
            get => _isMissing;
            set
            {
                if (SetProperty(ref _isMissing, value))
                {
                    OnPropertyChanged(nameof(DisplayNameWithStatus)); // 如果显示名包含状态
                    OnPropertyChanged(nameof(ToolTipText));
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
                    // TODO: 通知主ViewModel保存更改
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
                    OnPropertyChanged(nameof(ToolTipText));
                    // TODO: 通知主ViewModel保存更改
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
                    // TODO: 通知主ViewModel保存更改
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


        // --- Commands (稍后实现 DelegateCommand/RelayCommand) ---
        public ICommand ExecuteCommand { get; private set; }
        public ICommand RunInTerminalCommand { get; private set; }
        public ICommand RunDirectCommand { get; private set; }
        public ICommand OpenScriptFileCommand { get; private set; }
        public ICommand SetAliasCommand { get; private set; }
        public ICommand SetExecutionModeCommand { get; private set; }
        public ICommand SetDescriptionCommand { get; private set; }
        public ICommand MoveToGroupCommand { get; private set; }    // Needs interaction with parent/main VM
        public ICommand RemoveFromGroupCommand { get; private set; } // Needs interaction with parent/main VM

        // For ContextMenu Visibility
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
            // _scriptExecutor = new ScriptExecutor(_projectBasePath); // Or get from DI / service locator

            IsMissing = _model.IsMissing; // Initialize from model's persisted state if any

            // Initialize Commands (using placeholder actions for now)
            ExecuteCommand = new DelegateCommand(Execute, CanExecute);
            RunInTerminalCommand = new DelegateCommand(ExecuteInTerminal, CanExecute);
            RunDirectCommand = new DelegateCommand(ExecuteDirectly, CanExecute);
            OpenScriptFileCommand = new DelegateCommand(OpenScript, CanExecute);
            // SetAliasCommand = new DelegateCommand(() => parentViewModel.RequestSetAlias(this), CanExecute);
            // SetExecutionModeCommand = new DelegateCommand(() => parentViewModel.RequestSetExecutionMode(this), CanExecute);
            // SetDescriptionCommand = new DelegateCommand(() => parentViewModel.RequestSetDescription(this), CanExecute);
            // MoveToGroupCommand = new DelegateCommand(() => parentViewModel.RequestMoveScript(this), CanExecute);
            // RemoveFromGroupCommand = new DelegateCommand(() => parentViewModel.RequestRemoveScript(this), CanExecute);
        }

        // Placeholder for DelegateCommand or RelayCommand implementation
        // You'll need a common command class (e.g., RelayCommand.cs or use a library like Microsoft.Toolkit.Mvvm)
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

        private void Execute()
        {
            System.Console.WriteLine($"Executing script (mode: {ExecutionMode}): {Path}");
            if (IsMissing) return;

            // Resolve full script path
            string fullScriptPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(_pythonScriptBasePath, Path));

            if (!File.Exists(fullScriptPath))
            {
                IsMissing = true; // Update status
                // TODO: Show error message to user
                Console.WriteLine($"[ERROR] Script file not found: {fullScriptPath}");
                return;
            }

            var executor = new Core.Services.ScriptExecutor(_projectBasePath); // Consider injecting or making static/singleton

            // For simplicity, arguments are empty here. Real implementation might get them from user.
            var arguments = new List<string>();

            if (ExecutionMode.Equals("terminal", StringComparison.OrdinalIgnoreCase))
            {
                executor.LaunchInExternalTerminal(
                    _pythonExecutablePath,
                    fullScriptPath,
                    string.Join(" ", arguments.Select(a => $"\"{a}\"")), // Example: basic argument joining
                    DisplayName,
                    _environmentVariables,
                    _projectBasePath // Or script's directory: System.IO.Path.GetDirectoryName(fullScriptPath)
                );
            }
            else // Direct API
            {
                // Asynchronously execute and handle result (e.g., show in output pane)
                _ = executor.ExecuteAndCaptureOutputAsync(
                    _pythonExecutablePath,
                    fullScriptPath,
                    arguments,
                    _environmentVariables,
                    _projectBasePath, // Or script's directory
                    stdout => Console.WriteLine($"SCRIPT STDOUT: {stdout}"), // TODO: Route to UI
                    stderr => Console.WriteLine($"SCRIPT STDERR: {stderr}")  // TODO: Route to UI
                ).ContinueWith(task => {
                    if (task.IsFaulted)
                    {
                        Console.WriteLine($"SCRIPT EXECUTION FAILED: {task.Exception.InnerException?.Message}");
                        // TODO: Route to UI
                    }
                    else if (task.IsCompleted)
                    {
                        var result = task.Result;
                        Console.WriteLine($"SCRIPT EXITED: {result.ExitCode}");
                        // TODO: Route to UI
                    }
                });
            }
        }
        private void ExecuteInTerminal() { string tempMode = ExecutionMode; ExecutionMode = "terminal"; Execute(); ExecutionMode = tempMode; }
        private void ExecuteDirectly() { string tempMode = ExecutionMode; ExecutionMode = "directApi"; Execute(); ExecutionMode = tempMode; }

        private void OpenScript()
        {
            if (IsMissing) return;
            string fullScriptPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(_pythonScriptBasePath, Path));
            if (!File.Exists(fullScriptPath))
            {
                IsMissing = true;
                Console.WriteLine($"[ERROR] Script file not found for opening: {fullScriptPath}");
                return;
            }
            // TODO: Implement opening file in VS editor
            // This requires DTE or VS SDK services. For now, just log.
            Console.WriteLine($"Request to open script: {fullScriptPath}");
            try
            {
                // This is a system-level open, might not open in VS editor directly from a library project.
                // In a VSIX package, you'd use services like IVsUIShellOpenDocument.
                Process.Start(new ProcessStartInfo(fullScriptPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to open script file '{fullScriptPath}': {ex.Message}");
            }
        }

        // Method to update the underlying model if needed
        public ScriptEntry GetModel() => _model;
    }
}