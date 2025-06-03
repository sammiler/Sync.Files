using System.Windows;

namespace SyncFiles.UI.Dialogs
{
    public partial class WorkflowUrlDialog : Window
    {
        public string YamlUrl { get; private set; }

        public WorkflowUrlDialog()
        {
            InitializeComponent();
        }

        private void LoadButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(UrlTextBox.Text))
            {
                MessageBox.Show("URL²»ÄÜÎª¿Õ", "´íÎó", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            YamlUrl = UrlTextBox.Text.Trim();
            DialogResult = true;
            Close();
        }
    }
}