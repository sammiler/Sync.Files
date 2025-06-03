using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
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
        // In ShowToolWindowCommand.cs
        public void Execute(object sender, EventArgs e) // <<< NOW SYNCHRONOUS VOID
        {
            this.package.JoinableTaskFactory.RunAsync(async () =>
            {
                if (!(this.package is SyncFilesPackage sfp))
                {
                    Console.WriteLine("[ERROR] ShowToolWindowCommand: Package is not of type SyncFilesPackage.");
                    // Call a helper like ShowErrorAsync if you create one
                    VsShellUtilities.ShowMessageBox(this.package, "Internal Error: Package type mismatch.", "SyncFiles Error", OLEMSGICON.OLEMSGICON_CRITICAL, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                    return;
                }

                try
                {
                    // The ShowToolWindowAsync method in SyncFilesPackage should handle UI thread switching internally
                    await sfp.ShowToolWindowAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] ShowToolWindowCommand.Execute: {ex}");
                    VsShellUtilities.ShowMessageBox(this.package, $"Error showing tool window: {ex.Message}", "SyncFiles Error", OLEMSGICON.OLEMSGICON_CRITICAL, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                }

            }).FileAndForget("SyncFiles/ShowToolWindow");
        }
    }
}