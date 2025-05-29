using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Design;
using Task = System.Threading.Tasks.Task;

namespace SyncFiles.Commands
{
    internal sealed class ShowToolWindowCommand
    {
        public const int CommandId = 0x0100; // 命令 ID，需要在 VSCT 文件中定义
        public static readonly Guid CommandSet = new Guid("1d2c490a-9d9c-43ce-b45e-9e05a7e80d91"); // **为命令集生成一个新的 GUID**

        private readonly AsyncPackage package;

        private ShowToolWindowCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new OleMenuCommand(this.Execute, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        public static ShowToolWindowCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            OleMenuCommandService commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new ShowToolWindowCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            // 确保在 UI 线程上执行显示窗口的操作
            this.package.JoinableTaskFactory.RunAsync(async () =>
            {
                if (this.package is SyncFilesPackage syncFilesPackage)
                {
                    await syncFilesPackage.ShowToolWindowAsync();
                }
            }).FileAndForget("SyncFiles/ShowToolWindow"); // FileAndForget 用于不需要等待完成的异步操作
        }
    }
}