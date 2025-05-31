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
        public const int CommandId = 0x0101;
        public static readonly Guid CommandSet = new Guid("1d2c490a-9d9c-43ce-b45e-9e05a7e80d91");

        private readonly AsyncPackage package; // Keep AsyncPackage reference
        private readonly SyncFilesSettingsManager _settingsManager;
        // private readonly string _projectBasePath; // No longer needed here, get dynamically


        private ShowSettingsWindowCommand(AsyncPackage package, OleMenuCommandService commandService, SyncFilesSettingsManager settingsManager)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            // commandService is already checked by OleMenuCommandService constructor if it's null
            // commandService = commandService ?? throw new ArgumentNullException(nameof(commandService)); 
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));


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

        private void Execute(object sender, EventArgs e)
        {
            this.package.JoinableTaskFactory.RunAsync(async () =>
            {
                if (!(this.package is SyncFilesPackage sfp))
                {
                    System.Diagnostics.Debug.WriteLine("[ERROR] ShowSettingsWindowCommand: Package is not of type SyncFilesPackage.");
                    await ShowErrorAsync("Internal Error: Package type mismatch.");
                    return;
                }

                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(sfp.DisposalToken);

                    await sfp.EnsureProjectSpecificServicesAsync(); // Ensure path and services are up-to-date
                    string currentProjectPath = await sfp.GetProjectBasePathAsync(); // Get current path

                    if (string.IsNullOrEmpty(currentProjectPath))
                    {
                        await ShowInfoAsync("A project/solution needs to be open for full settings functionality. Some path-dependent features may use defaults or be disabled.");
                    }

                    var settingsManagerInstance = sfp.SettingsManager; // Already have _settingsManager
                    if (settingsManagerInstance == null) // Should use _settingsManager
                    {
                        await ShowErrorAsync("Internal Error: Settings manager not available.");
                        return;
                    }

                    // **** Pass the package instance (sfp, which is IAsyncServiceProvider) to the ViewModel ****
                    var viewModel = new SettingsWindowViewModel(settingsManagerInstance, currentProjectPath, sfp);
                    var settingsWindow = new SettingsWindow(viewModel)
                    {
                        Owner = Application.Current?.MainWindow?.IsVisible == true ? Application.Current.MainWindow : null
                    };
                    settingsWindow.ShowDialog();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ERROR] ShowSettingsWindowCommand.Execute: {ex}");
                    await ShowErrorAsync($"Error opening settings: {ex.Message}");
                }

            }).FileAndForget("SyncFiles/ShowSettingsWindow");
        }

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