// File: Commands/ShowSettingsWindowCommand.cs
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SyncFiles.Core.Management;
using SyncFiles.UI.Configuration;
using SyncFiles.UI.ViewModels;
using System;
using System.ComponentModel.Design;
using System.Windows;
using Task = System.Threading.Tasks.Task;

namespace SyncFiles.Commands
{
    internal sealed class ShowSettingsWindowCommand
    {
        public const int CommandId = 0x0101; // CHOOSE A NEW UNIQUE ID
        public static readonly Guid CommandSet = new Guid("1d2c490a-9d9c-43ce-b45e-9e05a7e80d91"); // Same command set or new one

        private readonly AsyncPackage package;
        private readonly SyncFilesSettingsManager _settingsManager;
        private readonly string _projectBasePath;


        private ShowSettingsWindowCommand(AsyncPackage package, OleMenuCommandService commandService, SyncFilesSettingsManager settingsManager)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));
            _settingsManager = settingsManager;

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(this.Execute, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        public static ShowSettingsWindowCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package, SyncFilesSettingsManager settingsManager)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            OleMenuCommandService commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new ShowSettingsWindowCommand(package, commandService, settingsManager);
        }

        // In ShowSettingsWindowCommand.cs
        private void Execute(object sender, EventArgs e) // <<< NOW SYNCHRONOUS VOID
        {
            // this.package is already an AsyncPackage, which has JoinableTaskFactory
            this.package.JoinableTaskFactory.RunAsync(async () => // Asynchronous lambda
            {
                // Perform a defensive cast here if needed, or assume this.package is always SyncFilesPackage
                // For robustness, let's cast and check.
                if (!(this.package is SyncFilesPackage sfp))
                {
                    Console.WriteLine("[ERROR] ShowSettingsWindowCommand: Package is not of type SyncFilesPackage.");
                    // Optionally show a message to the user via VS services if this is critical
                    await ShowErrorAsync("Internal Error: Package type mismatch.");
                    return;
                }

                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(sfp.DisposalToken); // Ensure on main thread for UI

                    await sfp.EnsureProjectSpecificServicesAsync();
                    string currentProjectPath = await sfp.GetProjectBasePathAsync();

                    if (string.IsNullOrEmpty(currentProjectPath))
                    {
                        // Inform user (consider using VS Shell's dialog service for better integration)
                        await ShowInfoAsync("A project/solution needs to be open for full functionality. Some features may be disabled.");
                        // Decide if you still want to open the window. For now, we will.
                    }

                    // Assuming SettingsManager is accessible from sfp
                    var settingsManagerInstance = sfp.SettingsManager;
                    if (settingsManagerInstance == null)
                    {
                        await ShowErrorAsync("Internal Error: Settings manager not available.");
                        return;
                    }

                    var viewModel = new SettingsWindowViewModel(settingsManagerInstance, currentProjectPath);
                    var settingsWindow = new SettingsWindow(viewModel)
                    {
                        Owner = System.Windows.Application.Current?.MainWindow?.IsVisible == true ? System.Windows.Application.Current.MainWindow : null
                    };
                    settingsWindow.ShowDialog(); // This is a blocking call on the UI thread
                }
                catch (Exception ex)
                {
                    // Log the exception and show an error message to the user
                    Console.WriteLine($"[ERROR] ShowSettingsWindowCommand.Execute: {ex}");
                    // Use VS services to show error messages for better integration
                    // For example, using IVsUIShell:
                    await ShowErrorAsync($"Error opening settings: {ex.Message}");
                }

            }).FileAndForget("SyncFiles/ShowSettingsWindow"); // Provide a descriptive name for error reporting
        }

        // Helper methods to show messages (could be part of a utility class or base command class)
        private async Task ShowInfoAsync(string message)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            VsShellUtilities.ShowMessageBox(
                this.package,
                message,
                "SyncFiles Information",
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
                "SyncFiles Error",
                OLEMSGICON.OLEMSGICON_CRITICAL,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
    }
}