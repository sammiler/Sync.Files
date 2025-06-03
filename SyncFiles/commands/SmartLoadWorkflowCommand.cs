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
        public const int CommandId = 0x0102; // ��VSCT�ļ��ж���һ��
        public static readonly Guid CommandSet = new Guid("1d2c490a-9d9c-43ce-b45e-9e05a7e80d91"); // ������������ͬ��GUID

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
                    await ShowErrorAsync("�ڲ����󣺰����Ͳ�ƥ�䡣");
                    return;
                }

                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(sfp.DisposalToken);

                    // ȷ����Ŀ·���ͷ����Ѹ���
                    await sfp.EnsureProjectSpecificServicesAsync();
                    string currentProjectPath = await sfp.GetProjectBasePathAsync();

                    if (string.IsNullOrEmpty(currentProjectPath))
                    {
                        await ShowErrorAsync("��Ҫ��һ����Ŀ/�����������ʹ�ô˹��ܡ�");
                        return;
                    }

                    // ��ʾ�Ի����ȡYAML URL
                    var dialog = new WorkflowUrlDialog();
                    bool? result = dialog.ShowDialog();
                    if (result != true || string.IsNullOrWhiteSpace(dialog.YamlUrl))
                    {
                        return; // �û�ȡ��
                    }

                    string yamlUrl = dialog.YamlUrl;
                    System.Diagnostics.Debug.WriteLine($"[INFO] SmartLoadWorkflowCommand: ��ʼ�� {yamlUrl} ���ع���������...");

                    // ��ʾ������ʾ
                    await ShowInfoAsync($"���ڴ� {yamlUrl} ���ع���������...\n\n�������Ҫһ��ʱ�䣬���Ժ�");

                    // ʹ��SmartWorkflowService���ز�����YAML
                    var cts = new CancellationTokenSource();
                    await _smartWorkflowService.PrepareWorkflowFromYamlUrlAsync(yamlUrl, cts.Token);
                    
                    // �������Ӧ��
                    _smartWorkflowService.FinalizeWorkflowConfiguration();

                    // ����ִ�гɹ���֪ͨ�û�
                    await ShowInfoAsync("�����������ѳɹ����ز�Ӧ�á�");

                    // ֪ͨViewModelˢ��UI
                    if (SyncFilesPackage.ToolWindowViewModel != null)
                    {
                        await SyncFilesPackage.ToolWindowViewModel.LoadAndRefreshScriptsAsync(true);
                        SyncFilesPackage.ToolWindowViewModel.AppendLogMessage("[INFO] �����������ѳɹ�Ӧ�ò�ˢ��");
                    }

                    // �Զ���ʾ���ߴ���
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
                    if (commandService != null && SyncFiles.Commands.ShowToolWindowCommand.Instance != null)
                    {
                        SyncFiles.Commands.ShowToolWindowCommand.Instance.Execute(this, EventArgs.Empty);
                        System.Diagnostics.Debug.WriteLine("[INFO] SmartLoadWorkflowCommand: ���Զ���ʾ���ߴ���");
                    }
                }
                catch (OperationCanceledException)
                {
                    await ShowInfoAsync("��ȡ�����ع�����������");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ERROR] SmartLoadWorkflowCommand.Execute: {ex}");
                    await ShowErrorAsync($"���ع�����ʱ����: {ex.Message}");
                }

            }).FileAndForget("SyncFiles/SmartLoadWorkflow");
        }

        private async Task ShowInfoAsync(string message)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            VsShellUtilities.ShowMessageBox(
                this.package,
                message,
                "SyncFiles ��Ϣ",
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
                "SyncFiles ����",
                OLEMSGICON.OLEMSGICON_CRITICAL,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
    }
}