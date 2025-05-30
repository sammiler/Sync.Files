// File: UI/Configuration/SettingsWindow.xaml.cs
using SyncFiles.UI.ViewModels;
using System.Windows;

namespace SyncFiles.UI.Configuration
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindowViewModel ViewModel { get; }

        public SettingsWindow(SettingsWindowViewModel viewModel)
        {
            InitializeComponent();
            ViewModel = viewModel;
            DataContext = ViewModel;

            // Handle ApplyAndCloseCommand and CancelCommand from ViewModel to close the window
            ViewModel.RequestCloseDialog += (sender, success) =>
            {
                this.DialogResult = success;
                this.Close();
            };
        }
    }
}