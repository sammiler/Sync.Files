using SyncFiles.Core.Models; // For ScriptEntry model
using SyncFiles.Core.Services;
using SyncFiles.UI.Common;
using SyncFiles.UI.ToolWindows;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text; // For StringBuilder
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input; // For ICommand 

namespace SyncFiles.UI.ViewModels
{
    public class ScriptEntryViewModel : ViewModelBase
    {
        public static readonly string PlaceHolderId = "__PLACEHOLDER_SCRIPT_ID__";
        private readonly ScriptEntry _model;
        private readonly ScriptExecutor _scriptExecutor;
        private readonly string _pythonExecutablePath;
        private readonly string _pythonScriptBasePath;
        private readonly Dictionary<string, string> _environmentVariables;
        private readonly string _projectBasePath;
        private bool _isMissing;

        private string _normalScriptIconPath;
        public string NormalScriptIconPath { get => _normalScriptIconPath; set => SetProperty(ref _normalScriptIconPath, value); }

        private string _warningScriptIconPath;
        public string WarningScriptIconPath { get => _warningScriptIconPath; set => SetProperty(ref _warningScriptIconPath, value); }
        private readonly SyncFilesToolWindowViewModel _parentViewModel;
        public bool CanExecuteScript => !IsMissing;

        public bool IsExecutionModeTerminal => ExecutionMode.Equals("terminal", StringComparison.OrdinalIgnoreCase);
        public bool IsExecutionModeDirectApi => ExecutionMode.Equals("directApi", StringComparison.OrdinalIgnoreCase);
        public bool IsMissing
        {
            get => _isMissing;
            set
            {
                if (SetProperty(ref _isMissing, value))
                {
                    OnPropertyChanged(nameof(DisplayNameWithStatus));
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
                    OnPropertyChanged(nameof(IsExecutionModeTerminal));
                    OnPropertyChanged(nameof(IsExecutionModeDirectApi));
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
        public ICommand SetExecutionModeCommand { get; private set; } // This seems unused in XAML context menu, but keep if needed
        public ICommand SetDescriptionCommand { get; private set; }
        public ICommand MoveToGroupCommand { get; private set; }
        public ICommand RemoveFromGroupCommand { get; private set; }

        public ICommand SetExecutionModeToTerminalCommand { get; }
        public ICommand SetExecutionModeToDirectApiCommand { get; }

        // **** 已存在的属性，确保它们在这里 ****
        public bool IsScriptEntry => true;
        public bool IsScriptGroup => false; // ScriptEntry is not a group

        // **** 添加这些缺失的属性和命令的只读实现 ****
        public ICommand RenameGroupCommand => null;
        public ICommand DeleteGroupCommand => null;
        public bool IsScriptGroupAndNotDefault => false; // A ScriptEntry is never a non-default group
        // **** 添加结束 ****

        public ScriptEntryViewModel(
            ScriptEntry model,
            string pythonExecutablePath,
            string pythonScriptBasePath,
            Dictionary<string, string> environmentVariables,
            string projectBasePath,
            SyncFilesToolWindowViewModel parentViewModel)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _pythonExecutablePath = pythonExecutablePath;
            _pythonScriptBasePath = pythonScriptBasePath;
            _environmentVariables = environmentVariables ?? new Dictionary<string, string>();
            _projectBasePath = projectBasePath;
            _parentViewModel = parentViewModel ?? throw new ArgumentNullException(nameof(parentViewModel));
            IsMissing = _model.IsMissing;

            // CanExecute lambdas now match Func<bool> if your RelayCommand supports it,
            // otherwise they should be (obj) => !IsMissing;
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

        // DelegateCommand class (保持不变)
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
            if (!CanExecuteScript)
            {
                _parentViewModel?.SetScriptExecutionStatus($"无法执行: {DisplayName} (文件不存在或其他条件)");
                return;
            }

            string fullScriptPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(_pythonScriptBasePath ?? string.Empty, Path ?? string.Empty));
            if (string.IsNullOrEmpty(Path) || !File.Exists(fullScriptPath))
            {
                IsMissing = true;
                _parentViewModel?.SetScriptExecutionStatus($"错误: 找不到脚本文件 {DisplayName}: {fullScriptPath}");
                System.Diagnostics.Debug.WriteLine($"[ERROR] Script file not found: {fullScriptPath}");
                return;
            }

            var executor = new Core.Services.ScriptExecutor(_projectBasePath);
            // 确保使用正确的arguments变量
            var args = new List<string>();

            if (ExecutionMode.Equals("terminal", StringComparison.OrdinalIgnoreCase))
            {
                // 外部终端模式保持不变
                _parentViewModel?.SetScriptExecutionStatus($"正在启动终端: {DisplayName}...");
                try
                {
                    executor.LaunchInExternalTerminal(
                        _pythonExecutablePath,
                        fullScriptPath,
                        string.Join(" ", args.Select(a => $"\"{a}\"")), 
                        DisplayName,
                        _environmentVariables,
                        _projectBasePath
                    );
                    _parentViewModel?.SetScriptExecutionStatus($"已为 {DisplayName} 启动终端");
                }
                catch (Exception ex)
                {
                    _parentViewModel?.SetScriptExecutionStatus($"启动终端失败 {DisplayName}: {ex.Message}");
                    _parentViewModel?.AppendScriptError(DisplayName, $"Terminal launch error: {ex.Message}");
                }
            }
            else
            {
                // 确保终端面板可见
                if (_parentViewModel != null)
                {
                    _parentViewModel.IsTerminalVisible = true;
                    
                    // 使用ExecuteScriptInTerminal方法在嵌入式终端中执行
                    _parentViewModel.ExecuteScriptInTerminal(this);
                }
            }
        }

        private bool _isScriptOutputVisible;
        public bool IsScriptOutputVisible
        {
            get => _isScriptOutputVisible;
            set
            {
                if (SetProperty(ref _isScriptOutputVisible, value) && _parentViewModel != null)
                {
                    _parentViewModel.IsScriptOutputVisible = value;
                }
            }
        }
        private void SetExecutionMode(string mode)
        {
            if (!CanExecute()) return;
            ExecutionMode = mode;
            _parentViewModel.RequestSaveSettings();
        }

        private void RequestSetAlias()
        {
            if (!CanExecute()) return;
            string newAlias = ShowInputDialog("Enter new alias:", Alias);
            if (newAlias != null)
            {
                Alias = newAlias.Trim();
                _parentViewModel.RequestSaveSettings();
            }
        }

        private void RequestSetDescription()
        {
            if (!CanExecute()) return;
            string newDescription = ShowInputDialog("Enter new description:", Description, true);
            if (newDescription != null)
            {
                Description = newDescription;
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
            return _parentViewModel.ShowInputDialog(prompt, defaultValue, "", multiline);
        }
        private void ExecuteInTerminal() { string tempMode = ExecutionMode; ExecutionMode = "terminal"; Execute(); ExecutionMode = tempMode; }
        private void ExecuteDirectly() { string tempMode = ExecutionMode; ExecutionMode = "directApi"; Execute(); ExecutionMode = tempMode; }

        private async void OpenScript()
        {
            if (IsMissing || _parentViewModel == null) return;
            string fullScriptPath = string.Empty;
            try
            {
                fullScriptPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(_pythonScriptBasePath ?? string.Empty, Path ?? string.Empty));
            }
            catch (ArgumentException ex) // Path or basePath might contain invalid characters
            {
                IsMissing = true; // Consider it missing if path is invalid
                _parentViewModel.ShowErrorMessage("Error", $"Invalid script path: {(Path ?? "null")}. Details: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[ERROR] Invalid script path for opening: {Path}. Error: {ex.Message}");
                return;
            }

            if (string.IsNullOrEmpty(Path) || !File.Exists(fullScriptPath))
            {
                IsMissing = true;
                _parentViewModel.ShowErrorMessage("Error", $"Script file not found: {fullScriptPath}");
                System.Diagnostics.Debug.WriteLine($"[ERROR] Script file not found for opening: {fullScriptPath}");
                return;
            }
            System.Diagnostics.Debug.WriteLine($"Request to open script in IDE: {fullScriptPath}");
            await _parentViewModel.OpenFileInIdeAsync(fullScriptPath);
        }
        public ScriptEntry GetModel() => _model;
    }
}