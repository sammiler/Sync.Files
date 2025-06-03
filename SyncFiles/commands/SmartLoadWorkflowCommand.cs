// File: Commands/SmartLoadWorkflowCommand.cs
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SyncFiles.Core.Services;
using SyncFiles.UI.Dialogs;
using System;
using System.ComponentModel.Design;
using System.Threading;
using System.Windows;
using Task = System.Threading.Tasks.Task;

namespace SyncFiles.Commands
{
    internal sealed class SmartLoadWorkflowCommand
    {
        public const int CommandId = 0x0102; // 与VSCT文件中定义一致
        public static readonly Guid CommandSet = new Guid("1d2c490a-9d9c-43ce-b45e-9e05a7e80d91"); // 与其他命令相同的GUID

        private readonly AsyncPackage package;
        private readonly SmartWorkflowService _smartWorkflowService;

        private SmartLoadWorkflowCommand(AsyncPackage package, OleMenuCommandService commandService, SmartWorkflowService smartWorkflowService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            _smartWorkflowService = smartWorkflowService ?? throw new ArgumentNullException(nameof(smartWorkflowService));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(this.Execute, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        public static SmartLoadWorkflowCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package, SmartWorkflowService smartWorkflowService)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            OleMenuCommandService commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new SmartLoadWorkflowCommand(package, commandService, smartWorkflowService);
        }

        private void Execute(object sender, EventArgs e)
        {
            this.package.JoinableTaskFactory.RunAsync(async () =>
            {
                if (!(this.package is SyncFilesPackage sfp))
                {
                    System.Diagnostics.Debug.WriteLine("[ERROR] SmartLoadWorkflowCommand: Package is not of type SyncFilesPackage.");
                    await ShowErrorAsync("内部错误：包类型不匹配。");
                    return;
                }

                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(sfp.DisposalToken);

                    // 确保项目路径和服务已更新
                    await sfp.EnsureProjectSpecificServicesAsync();
                    string currentProjectPath = await sfp.GetProjectBasePathAsync();

                    if (string.IsNullOrEmpty(currentProjectPath))
                    {
                        await ShowErrorAsync("需要打开一个项目/解决方案才能使用此功能。");
                        return;
                    }

                    // 显示对话框获取YAML URL
                    var dialog = new WorkflowUrlDialog();
                    bool? result = dialog.ShowDialog();
                    if (result != true || string.IsNullOrWhiteSpace(dialog.YamlUrl))
                    {
                        return; // 用户取消
                    }

                    string yamlUrl = dialog.YamlUrl;
                    System.Diagnostics.Debug.WriteLine($"[INFO] SmartLoadWorkflowCommand: 开始从 {yamlUrl} 加载工作流配置...");

                    // 显示进度提示
                    await ShowInfoAsync($"正在从 {yamlUrl} 加载工作流配置...\n\n这可能需要一点时间，请稍候。");

                    // 使用SmartWorkflowService下载并处理YAML
                    var cts = new CancellationTokenSource();
                    await _smartWorkflowService.PrepareWorkflowFromYamlUrlAsync(yamlUrl, cts.Token);
                    
                    // 完成配置应用
                    _smartWorkflowService.FinalizeWorkflowConfiguration();

                    // 命令执行成功后通知用户
                    await ShowInfoAsync("工作流配置已成功加载并应用。");

                    // 通知ViewModel刷新UI
                    if (SyncFilesPackage.ToolWindowViewModel != null)
                    {
                        await SyncFilesPackage.ToolWindowViewModel.LoadAndRefreshScriptsAsync(true);
                        SyncFilesPackage.ToolWindowViewModel.AppendLogMessage("[INFO] 工作流配置已成功应用并刷新");
                    }

                    // 自动显示工具窗口
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
                    if (commandService != null && SyncFiles.Commands.ShowToolWindowCommand.Instance != null)
                    {
                        SyncFiles.Commands.ShowToolWindowCommand.Instance.Execute(this, EventArgs.Empty);
                        System.Diagnostics.Debug.WriteLine("[INFO] SmartLoadWorkflowCommand: 已自动显示工具窗口");
                    }
                }
                catch (OperationCanceledException)
                {
                    await ShowInfoAsync("已取消加载工作流操作。");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ERROR] SmartLoadWorkflowCommand.Execute: {ex}");
                    await ShowErrorAsync($"加载工作流时出错: {ex.Message}");
                }

            }).FileAndForget("SyncFiles/SmartLoadWorkflow");
        }

        private async Task ShowInfoAsync(string message)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            VsShellUtilities.ShowMessageBox(
                this.package,
                message,
                "SyncFiles 信息",
                OLEMSGICON.OLEMSGICON_INFO,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }

        private async Task ShowErrorAsync(string message)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            VsShellUtilities.ShowMessageBox(
                this.package,
                message,
                "SyncFiles 错误",
                OLEMSGICON.OLEMSGICON_CRITICAL,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
    }
}