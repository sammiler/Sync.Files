// File: Commands/ShowSettingsWindowCommand.cs
using Microsoft.VisualStudio.Shell;
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


        private ShowSettingsWindowCommand(AsyncPackage package, OleMenuCommandService commandService, SyncFilesSettingsManager settingsManager, string projectBasePath)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));
            _settingsManager = settingsManager;
            _projectBasePath = projectBasePath;

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(this.Execute, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        public static ShowSettingsWindowCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package, SyncFilesSettingsManager settingsManager, string projectBasePath)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            OleMenuCommandService commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new ShowSettingsWindowCommand(package, commandService, settingsManager, projectBasePath);
        }

        private void Execute(object sender, EventArgs e)
        {
            // Ensure we are on the UI thread if the window needs it, though ShowDialog usually handles this.
            this.package.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

                var viewModel = new SettingsWindowViewModel(_settingsManager, _projectBasePath);
                var settingsWindow = new SettingsWindow(viewModel)
                {
                    // Attempt to set owner for proper modal behavior
                    Owner = Application.Current?.MainWindow?.IsVisible == true ? Application.Current.MainWindow : null
                };
                settingsWindow.ShowDialog(); // Show as a modal dialog
            }).FileAndForget("SyncFiles/ShowSettingsWindow");
        }
    }
}