using System.Windows.Controls;
using System.Windows.Input;
// using SyncFiles.UI.ViewModels; // 稍后会用到 ViewModel 类型

namespace SyncFiles.UI.ToolWindows
{
    /// <summary>
    /// Interaction logic for SyncFilesToolWindowControl.xaml
    /// </summary>
    public partial class SyncFilesToolWindowControl : UserControl
    {
        public SyncFilesToolWindowControl()
        {
            InitializeComponent();
            // DataContext 会由 XAML 中 <UserControl.DataContext> 部分设置
            // 或者，你也可以在这里实例化并设置 ViewModel:
            // this.DataContext = new SyncFilesToolWindowViewModel();
        }

        private void TreeViewItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // 我们将在 ViewModel 中处理双击逻辑，通过命令绑定或直接调用 ViewModel 方法
            // 这个事件处理器主要用于将事件源（被双击的 TreeViewItem 的 DataContext）
            // 传递给 ViewModel。

            if (e.Source is TreeViewItem item) // 确保事件源是 TreeViewItem
            {
                // TreeViewItem 的 DataContext 就是我们的 ViewModel (ScriptGroupViewModel 或 ScriptEntryViewModel)
                if (item.DataContext is ViewModels.ScriptEntryViewModel scriptEntryVm)
                {
                    // 检查命令是否存在且可执行，然后执行
                    if (scriptEntryVm.ExecuteCommand != null && scriptEntryVm.ExecuteCommand.CanExecute(null))
                    {
                        scriptEntryVm.ExecuteCommand.Execute(null);
                        e.Handled = true; // 标记事件已处理，防止进一步冒泡或默认行为
                    }
                }
                // else if (item.DataContext is ViewModels.ScriptGroupViewModel groupVm)
                // {
                //    // 对组的双击可以实现为展开/折叠，但 TreeView 默认会处理这个。
                //    // 如果有其他自定义行为，可以在这里添加。
                //    // item.IsExpanded = !item.IsExpanded;
                //    // e.Handled = true;
                // }
            }
        }
    }
}