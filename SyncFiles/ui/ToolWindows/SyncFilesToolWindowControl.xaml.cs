using System.Windows.Controls;
using System.Windows.Input;
namespace SyncFiles.UI.ToolWindows
{
    public partial class SyncFilesToolWindowControl : UserControl
    {
        public SyncFilesToolWindowControl()
        {
            InitializeComponent();
        }
        private void TreeViewItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.Source is TreeViewItem item) // 确保事件源是 TreeViewItem
            {
                if (item.DataContext is ViewModels.ScriptEntryViewModel scriptEntryVm)
                {
                    if (scriptEntryVm.ExecuteCommand != null && scriptEntryVm.ExecuteCommand.CanExecute(null))
                    {
                        scriptEntryVm.ExecuteCommand.Execute(null);
                        e.Handled = true; // 标记事件已处理，防止进一步冒泡或默认行为
                    }
                }
            }
        }
    }
}