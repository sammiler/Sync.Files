// File: SyncFiles.VSIX/UI/ToolWindows/SyncFilesToolWindow.cs
using Microsoft.VisualStudio.Shell;
using SyncFiles.UI.ToolWindows;
using System;
using System.Runtime.InteropServices;
// SyncFiles.VSIX.UI.ToolWindows 是 SyncFilesToolWindowControl 所在的命名空间
// 如果 SyncFilesToolWindowControl 在 SyncFiles.UI 项目中，你需要确保 VSIX 项目引用了 UI 项目
// 并且使用正确的 using 语句，例如:
// using SyncFiles.UI.ToolWindows; 

namespace SyncFiles.UI.ToolWindows
{
    [Guid("e8b7c134-64a0-4049-9aa4-9bba578ad69d")] // **替换为一个新的 GUID**
    public class SyncFilesToolWindow : ToolWindowPane
    {
        private readonly SyncFilesToolWindowControl _control;

        public SyncFilesToolWindow() : base(null)
        {
            this.Caption = "Sync Files";
            _control = new SyncFilesToolWindowControl(); // 创建 WPF UserControl
            this.Content = _control; // 设置为工具窗口的内容
        }

        /// <summary>
        /// 公共属性，允许外部（如 Package 类）访问 UserControl 以设置 DataContext。
        /// </summary>
        public SyncFilesToolWindowControl ToolWindowControl => _control;

        // 如果需要，可以重写 Dispose 方法来清理 _control 或其 DataContext (ViewModel)
        // protected override void Dispose(bool disposing)
        // {
        //     if (disposing)
        //     {
        //         if (_control?.DataContext is IDisposable viewModel)
        //         {
        //             viewModel.Dispose();
        //         }
        //     }
        //     base.Dispose(disposing);
        // }
    }
}