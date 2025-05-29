// File: SyncFiles.VSIX/UI/ToolWindows/SyncFilesToolWindow.cs
using Microsoft.VisualStudio.Shell;
using SyncFiles.UI.ToolWindows;
using System;
using System.Runtime.InteropServices;
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
        public SyncFilesToolWindowControl ToolWindowControl => _control;
    }
}