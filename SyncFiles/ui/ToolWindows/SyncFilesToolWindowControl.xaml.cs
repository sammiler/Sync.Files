using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SyncFiles.UI.ViewModels;
using SyncFiles.Core.Services;
using SyncFiles.Core.Models;

namespace SyncFiles.UI.ToolWindows
{
    public partial class SyncFilesToolWindowControl : UserControl
    {
        private SyncFilesToolWindowViewModel _viewModel;

        public SyncFilesToolWindowControl()
        {
            InitializeComponent();
            
            // 监听DataContext变化，以便在设置DataContext时自动更新_viewModel
            this.DataContextChanged += SyncFilesToolWindowControl_DataContextChanged;
        }

        private void SyncFilesToolWindowControl_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // 当DataContext变化时自动更新_viewModel
            if (e.NewValue is SyncFilesToolWindowViewModel viewModel)
            {
                SetViewModel(viewModel);
            }
        }

        public void SetViewModel(SyncFilesToolWindowViewModel viewModel)
        {
            _viewModel = viewModel;
            DataContext = viewModel;
            System.Diagnostics.Debug.WriteLine("[INFO] SyncFilesToolWindowControl: ViewModel已设置");
        }

        private void TreeViewItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.Source is TreeViewItem item) // 确保事件源是 TreeViewItem
            {
                if (item.DataContext is ScriptEntryViewModel scriptEntryVm)
                {
                    if (scriptEntryVm.ExecuteCommand != null && scriptEntryVm.ExecuteCommand.CanExecute(null))
                    {
                        // 使用模式判断如何执行脚本
                        if (scriptEntryVm.IsExecutionModeTerminal)
                        {
                            // 对于外部终端模式，使用原有的执行方法
                            scriptEntryVm.ExecuteCommand.Execute(null);
                        }
                        else
                        {
                            // 对于直接API模式，使用嵌入式终端
                            // 确保终端面板可见
                            if (_viewModel != null)
                            {
                                _viewModel.IsTerminalVisible = true;
                            }
                            
                            // 如果终端控件未初始化，则按原来方式执行
                            if (terminalControl != null)
                            {
                                RunScriptInTerminal(scriptEntryVm);
                            }
                            else if (_viewModel != null)
                            {
                                _viewModel.AppendLogMessage("[错误] 终端控件未初始化");
                            }
                        }
                        
                        e.Handled = true; // 标记事件已处理，防止进一步冒泡
                    }
                }
            }
        }


        private void RunScriptInTerminal(ScriptEntryViewModel scriptEntryVm)
        {
            try
            {
                if (_viewModel == null)
                {
                    MessageBox.Show("无法执行脚本：视图模型未初始化", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    System.Diagnostics.Debug.WriteLine("[ERROR] RunScriptInTerminal: _viewModel is null");
                    return;
                }

                ScriptEntry scriptEntry = scriptEntryVm.GetModel();
                if (scriptEntry == null)
                {
                    _viewModel.AppendLogMessage("[错误] 无法获取脚本模型");
                    return;
                }

                // 获取Python执行路径
                string pythonExecutablePath = _viewModel.GetPythonExecutablePath();
                if (string.IsNullOrEmpty(pythonExecutablePath))
                {
                    _viewModel.AppendLogMessage("[错误] Python可执行文件路径未设置");
                    return;
                }

                // 获取脚本路径
                string pythonScriptBasePath = _viewModel.GetPythonScriptBasePath();
                string fullScriptPath = string.IsNullOrEmpty(scriptEntry.Path) ? string.Empty : 
                    Path.GetFullPath(Path.Combine(pythonScriptBasePath ?? string.Empty, scriptEntry.Path ?? string.Empty));

                if (string.IsNullOrEmpty(fullScriptPath) || !File.Exists(fullScriptPath))
                {
                    _viewModel.AppendLogMessage($"[错误] 脚本文件不存在: {fullScriptPath}");
                    scriptEntryVm.IsMissing = true;
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
                Dictionary<string, string> environmentVariables = _viewModel.GetEnvironmentVariables();

                // 确保控件已实例化
                if (terminalControl == null)
                {
                    _viewModel.AppendLogMessage("[错误] 终端控件未初始化");
                    return;
                }

                // 准备终端以接收输入
                terminalControl.PrepareForExecution();

                // 在嵌入式终端中执行脚本
                terminalControl.StartProcess(
                    pythonExecutablePath,
                    fullScriptPath,
                    arguments,
                    environmentVariables,
                    workingDirectory
                );

                // 更新UI状态
                _viewModel.SetScriptExecutionStatus($"正在终端中执行: {scriptEntry.GetDisplayName()}");
            }
            catch (Exception ex)
            {
                string errorMessage = $"启动脚本时出错: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"[ERROR] {errorMessage}\n{ex.StackTrace}");
                
                if (_viewModel != null)
                {
                    _viewModel.AppendLogMessage($"[错误] {errorMessage}");
                }
                else
                {
                    MessageBox.Show(errorMessage, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void ClearTerminal_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            try
            {
                if (terminalControl == null)
                {
                    System.Diagnostics.Debug.WriteLine("[ERROR] ClearTerminal_Click: terminalControl is null");
                    return;
                }
                
                terminalControl.ClearTerminal();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ERROR] 清理终端时出错: {ex.Message}");
            }
        }

        private void StopProcess_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            try
            {
                if (terminalControl == null)
                {
                    System.Diagnostics.Debug.WriteLine("[ERROR] StopProcess_Click: terminalControl is null");
                    return;
                }
                
                terminalControl.StopProcess();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ERROR] 终止进程时出错: {ex.Message}");
            }
        }
    }
}