using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SyncFiles.UI.Controls
{
    /// <summary>
    /// PTY 终端控件，支持 ANSI 转义序列和交互式输入
    /// </summary>
    public partial class PtyTerminalControl : UserControl, IDisposable
    {
        private Process _process;
        private StreamWriter _inputWriter;
        private StringBuilder _inputBuffer = new StringBuilder();
        private int _inputStartPosition = 0;
        private bool _isProcessing = false;
        private Dictionary<string, SolidColorBrush> _ansiColorMap;
        private SolidColorBrush _defaultForeground;
        private SolidColorBrush _defaultBackground;
        private bool _isMouseOverTerminal = false;
        private bool _isDisposed = false;
        private DispatcherTimer _caretBlinkTimer;

        // 添加一个新的字段来控制是否允许自动获取焦点
        private bool _allowFocusCapture = true;

        // 添加一个字段记录光标位置和状态
        private TextPointer _caretPosition;
        private bool _caretVisible = true;
        private SolidColorBrush _caretBrush = new SolidColorBrush(Colors.White);
        private TextRange _caretRange;
        private bool _waitingForInput = false;

        // 添加字段跟踪是否正在更新光标
        private bool _isUpdatingCaret = false;

        // 添加一个字段来跟踪最近的键盘活动
        private DateTime _lastKeyboardActivity = DateTime.MinValue;
        private const int MinimumBlinkDelayAfterKeyboardMs = 500; // 键盘活动后的最小闪烁延迟

        // 添加一个新字段来控制滚动行为
        private bool _isAutoScrolling = false;
        private DispatcherTimer _delayedInputTimer;

        // 添加新的字段来更好地管理光标和输入状态
        private bool _isWaitingForPromptProcessing = false;
        private string _lastDetectedPrompt = string.Empty;
        private int _promptEndPosition = 0;

        // 添加新的字段来更精确地控制提示和输入流程
        private StringBuilder _outputBuffer = new StringBuilder();
        private DispatcherTimer _outputProcessTimer;
        private const int OutputBufferProcessDelay = 100; // ms

        public event EventHandler ProcessExited;

        public PtyTerminalControl()
        {
            InitializeComponent();

            // 设置默认颜色
            _defaultForeground = new SolidColorBrush(Colors.LightGray);
            _defaultBackground = new SolidColorBrush(Colors.Black);
            _caretBrush = new SolidColorBrush(Colors.White); // 设置光标颜色

            InitializeColorMap();

            // 设置光标闪烁定时器
            _caretBlinkTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _caretBlinkTimer.Tick += CaretBlinkTimer_Tick;
            
            // 创建延迟输入定时器
            _delayedInputTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(600) // 增加到600毫秒延迟
            };
            _delayedInputTimer.Tick += DelayedInputTimer_Tick;
            _delayedInputTimer.Stop();
            
            // 创建输出处理定时器
            _outputProcessTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(OutputBufferProcessDelay)
            };
            _outputProcessTimer.Tick += OutputProcessTimer_Tick;
            _outputProcessTimer.Stop();

            // 创建和配置右键菜单
            CreateContextMenu();

            // 禁用文本编辑 - 我们会自己处理所有的输入
            terminalTextBox.IsReadOnly = true;

            // 设置输入预处理事件
            terminalTextBox.PreviewTextInput += TerminalTextBox_PreviewTextInput;
            terminalTextBox.PreviewKeyDown += TerminalTextBox_PreviewKeyDown;
            terminalTextBox.KeyDown += TerminalTextBox_KeyDown;
            terminalTextBox.MouseEnter += TerminalTextBox_MouseEnter;
            terminalTextBox.MouseLeave += TerminalTextBox_MouseLeave;
            terminalTextBox.TextChanged += TerminalTextBox_TextChanged;

            terminalTextBox.Focus();
        }

        private void TerminalTextBox_MouseEnter(object sender, MouseEventArgs e)
        {
            _isMouseOverTerminal = true;
            // 如果有活动的进程，获取焦点
            if (_process != null && !_process.HasExited)
            {
                terminalTextBox.Focus();
            }
        }

        private void TerminalTextBox_MouseLeave(object sender, MouseEventArgs e)
        {
            _isMouseOverTerminal = false;
            // 当鼠标离开时不主动释放焦点，让用户去点击其他控件
        }

        /// <summary>
        /// 创建上下文菜单并附加到终端
        /// </summary>
        private void CreateContextMenu()
        {
            var menu = new ContextMenu();
            menu.Opened += (s, e) => {
                // 防止菜单打开时焦点被抢走
                e.Handled = true;
            };
            menu.Closed += (s, e) => {
                // 菜单关闭后将焦点返回到终端
                Dispatcher.InvokeAsync(() => terminalTextBox.Focus());
            };

            // 复制菜单项
            var copyItem = new MenuItem { Header = "复制" };
            copyItem.Click += (s, e) => CopySelectedText();
            menu.Items.Add(copyItem);

            // 粘贴菜单项
            var pasteItem = new MenuItem { Header = "粘贴" };
            pasteItem.Click += (s, e) => PasteText();
            menu.Items.Add(pasteItem);

            // 分隔符
            menu.Items.Add(new Separator());

            // 清除菜单项
            var clearItem = new MenuItem { Header = "清除终端" };
            clearItem.Click += (s, e) => ClearTerminal();
            menu.Items.Add(clearItem);

            // 将菜单应用到终端
            terminalTextBox.ContextMenu = menu;

            // 确保菜单能保持打开状态
            terminalTextBox.ContextMenuOpening += (s, e) =>
            {
                // 禁止默认处理，确保菜单正常显示
                e.Handled = false;
            };
        }

        /// <summary>
        /// 处理直接文本输入
        /// </summary>
        private void TerminalTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (_process != null && !_process.HasExited && _inputWriter != null)
            {
                // 直接发送文本输入的字符
                foreach (char c in e.Text)
                {
                    SendInputChar(c);
                }
                e.Handled = true;
            }
        }

        /// <summary>
        /// 复制选中文本
        /// </summary>
        private void CopySelectedText()
        {
            if (terminalTextBox.Selection.IsEmpty)
                return;

            try
            {
                Clipboard.SetText(terminalTextBox.Selection.Text);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"复制文本时出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 粘贴文本到终端
        /// </summary>
        private void PasteText()
        {
            try
            {
                if (_inputWriter == null || _process == null || _process.HasExited)
                    return;

                string text = Clipboard.GetText();
                if (!string.IsNullOrEmpty(text))
                {
                    // 显示粘贴的文本
                    AppendText(text, _defaultForeground, _defaultBackground);

                    // 发送到进程
                    _inputWriter.Write(text);
                    _inputWriter.Flush();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"粘贴文本时出错: {ex.Message}");
            }
        }

        private void InitializeColorMap()
        {
            _ansiColorMap = new Dictionary<string, SolidColorBrush>
            {
                // 标准 ANSI 颜色
                { "30", new SolidColorBrush(Colors.Black) },
                { "31", new SolidColorBrush(Colors.Red) },
                { "32", new SolidColorBrush(Colors.Green) },
                { "33", new SolidColorBrush(Colors.Yellow) },
                { "34", new SolidColorBrush(Colors.Blue) },
                { "35", new SolidColorBrush(Colors.Magenta) },
                { "36", new SolidColorBrush(Colors.Cyan) },
                { "37", new SolidColorBrush(Colors.White) },
                { "39", _defaultForeground }, // 默认前景色

                // 背景色
                { "40", new SolidColorBrush(Colors.Black) },
                { "41", new SolidColorBrush(Colors.Red) },
                { "42", new SolidColorBrush(Colors.Green) },
                { "43", new SolidColorBrush(Colors.Yellow) },
                { "44", new SolidColorBrush(Colors.Blue) },
                { "45", new SolidColorBrush(Colors.Magenta) },
                { "46", new SolidColorBrush(Colors.Cyan) },
                { "47", new SolidColorBrush(Colors.White) },
                { "49", _defaultBackground }, // 默认背景色

                // 亮色系列
                { "90", new SolidColorBrush(Color.FromRgb(128, 128, 128)) },  // 亮黑 (灰色)
                { "91", new SolidColorBrush(Color.FromRgb(255, 100, 100)) },  // 亮红
                { "92", new SolidColorBrush(Color.FromRgb(100, 255, 100)) },  // 亮绿
                { "93", new SolidColorBrush(Color.FromRgb(255, 255, 100)) },  // 亮黄
                { "94", new SolidColorBrush(Color.FromRgb(100, 100, 255)) },  // 亮蓝
                { "95", new SolidColorBrush(Color.FromRgb(255, 100, 255)) },  // 亮紫
                { "96", new SolidColorBrush(Color.FromRgb(100, 255, 255)) },  // 亮青
                { "97", new SolidColorBrush(Colors.White) }                   // 亮白
            };
        }

        // 改进CaretBlinkTimer_Tick方法
        private void CaretBlinkTimer_Tick(object sender, EventArgs e)
        {
            try 
            {
                // 如果最近有键盘活动，暂时保持光标可见
                TimeSpan timeSinceLastActivity = DateTime.Now - _lastKeyboardActivity;
                if (timeSinceLastActivity.TotalMilliseconds < MinimumBlinkDelayAfterKeyboardMs)
                {
                    // 确保光标可见，但不翻转状态
                    if (!_caretVisible)
                    {
                        _caretVisible = true;
                        if (!_isProcessing)
                        {
                            UpdateCaretDisplay();
                        }
                    }
                    return;
                }
                
                // 正常闪烁逻辑
                _caretVisible = !_caretVisible;
                
                if (!_isProcessing)
                {
                    UpdateCaretDisplay();
                }
                
                // 处理焦点逻辑
                if (_allowFocusCapture && _isMouseOverTerminal && _process != null && !_process.HasExited && 
                    IsVisible && IsEnabled && !IsMenuOpen())
                {
                    terminalTextBox.Focus();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"光标闪烁时出错: {ex.Message}");
                // 不要让异常中断定时器
            }
        }

        // 修改UpdateCaretDisplay方法，避免递归调用
        private void UpdateCaretDisplay()
        {
            // 如果已经在更新光标，则退出以防止递归
            if (_isUpdatingCaret)
                return;

            try
            {
                // 设置标志指示正在更新光标
                _isUpdatingCaret = true;

                // 删除之前的光标
                if (_caretRange != null)
                {
                    try 
                    {
                        _caretRange.Text = "";
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"删除光标时出错: {ex.Message}");
                    }
                    _caretRange = null;
                }
                
                // 如果光标应该可见，且进程运行中，则显示光标
                if (_caretVisible && _process != null && !_process.HasExited)
                {
                    try
                    {
                        // 获取文档末尾位置
                        _caretPosition = terminalTextBox.Document.ContentEnd;
                        
                        // 创建光标
                        _caretRange = new TextRange(_caretPosition, _caretPosition);
                        _caretRange.Text = "▌"; // 使用方块字符作为光标
                        
                        // 应用光标样式
                        _caretRange.ApplyPropertyValue(TextElement.ForegroundProperty, _caretBrush);
                        _caretRange.ApplyPropertyValue(TextElement.BackgroundProperty, _defaultBackground);
                        
                        // 确保光标可见 - 使用安全的滚动方法
                        EnsureScrollToEnd(true);
                    }
                    catch (Exception ex) 
                    {
                        Debug.WriteLine($"创建光标时出错: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新光标时出错: {ex.Message}");
            }
            finally
            {
                // 重置标志
                _isUpdatingCaret = false;
            }
        }

        // 添加新方法：在执行脚本前调用，确保终端准备好接收输入
        public void PrepareForExecution()
        {
            // 无论鼠标位置如何，确保终端可以获取焦点
            _isMouseOverTerminal = true;
            terminalTextBox.Focus();
        }
        private void StartCaretBlinkTimer()
        {
            _caretBlinkTimer.Start();
        }

        private void StopCaretBlinkTimer()
        {
            _caretBlinkTimer.Stop();
        }

        /// <summary>
        /// 启动进程并在终端中执行
        /// </summary>
        public void StartProcess(string pythonExecutable, string scriptPath,
                                 string arguments,
                                 Dictionary<string, string> environmentVariables,
                                 string workingDirectory)
        {
            try
            {
                ClearTerminal(); // 启动新进程前先清除终端
                _waitingForInput = false; // 重置输入标志

                _process = new Process();
                _process.StartInfo.FileName = pythonExecutable;
                _process.StartInfo.Arguments = string.IsNullOrEmpty(arguments)
                    ? $"\"{scriptPath}\""
                    : $"\"{scriptPath}\" {arguments}";

                // 设置工作目录
                if (string.IsNullOrEmpty(workingDirectory) || !Directory.Exists(workingDirectory))
                {
                    workingDirectory = Path.GetDirectoryName(scriptPath);
                }
                _process.StartInfo.WorkingDirectory = workingDirectory;

                _process.StartInfo.UseShellExecute = false;
                _process.StartInfo.RedirectStandardInput = true;
                _process.StartInfo.RedirectStandardOutput = true;
                _process.StartInfo.RedirectStandardError = true;
                _process.StartInfo.CreateNoWindow = true;

                // 使用系统默认编码，避免编码问题
                _process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                _process.StartInfo.StandardErrorEncoding = Encoding.UTF8;

                // 设置环境变量
                if (environmentVariables != null)
                {
                    foreach (var kvp in environmentVariables)
                    {
                        _process.StartInfo.EnvironmentVariables[kvp.Key] = kvp.Value;
                    }
                }

                // 设置关键的Python环境变量
                _process.StartInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                _process.StartInfo.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";

                // 设置ANSI颜色支持
                _process.StartInfo.EnvironmentVariables["FORCE_COLOR"] = "1";
                _process.StartInfo.EnvironmentVariables["TERM"] = "xterm-color";

                // 注册进程事件
                _process.OutputDataReceived += Process_OutputDataReceived;
                _process.ErrorDataReceived += Process_ErrorDataReceived;
                _process.Exited += Process_Exited;
                _process.EnableRaisingEvents = true;

                // 启动进程
                _process.Start();
                _inputWriter = _process.StandardInput;
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();

                // 添加初始提示信息并开始光标闪烁
                AppendText($"已启动进程: {Path.GetFileName(scriptPath)}\n\r", _defaultForeground, _defaultBackground);
                _inputStartPosition = GetTextLength();

                StartCaretBlinkTimer();

                // 确保焦点在终端上
                terminalTextBox.Focus();

                // 启动进程后确保光标显示
                _caretVisible = true;
                UpdateCaretDisplay();
            }
            catch (Exception ex)
            {
                AppendText($"启动进程失败: {ex.Message}\n", new SolidColorBrush(Colors.Red), _defaultBackground);
            }
        }

        // 修改输出数据接收方法，使用缓冲机制
        private void Process_OutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                try
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            // 累积输出到缓冲区
                            _outputBuffer.AppendLine(e.Data);
                            
                            // 停止任何正在进行的定时器
                            _outputProcessTimer.Stop();
                            
                            // 启动新的处理定时器
                            _outputProcessTimer.Start();
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"缓冲输出数据时出错: {ex.Message}");
                        }
                    });
                }
                catch { }
            }
        }

        private void Process_ErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                try
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        AppendText(e.Data + "\n", new SolidColorBrush(Colors.Red), _defaultBackground);
                    });
                }
                catch { }
            }
        }

        private void Process_Exited(object sender, EventArgs e)
        {
            try
            {
                Dispatcher.InvokeAsync(() =>
                {
                    StopCaretBlinkTimer();

                    if (_process != null)
                    {
                        int exitCode = 0;
                        try
                        {
                            exitCode = _process.ExitCode;
                        }
                        catch
                        {
                            exitCode = -1;
                        }

                        AppendText($"\n进程已退出，退出代码: {exitCode}\n",
                            exitCode == 0 ? new SolidColorBrush(Colors.Green) : new SolidColorBrush(Colors.Red),
                            _defaultBackground);
                    }

                    CleanupProcess();
                    ProcessExited?.Invoke(this, EventArgs.Empty);
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理进程退出事件时出错: {ex.Message}");
            }
        }

        private void CleanupProcess()
        {
            if (_process != null)
            {
                try
                {
                    if (_inputWriter != null)
                    {
                        _inputWriter.Dispose();
                        _inputWriter = null;
                    }

                    _process.OutputDataReceived -= Process_OutputDataReceived;
                    _process.ErrorDataReceived -= Process_ErrorDataReceived;
                    _process.Exited -= Process_Exited;
                    _process.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"清理进程资源时出错: {ex.Message}");
                }
                finally
                {
                    _process = null;
                    _inputBuffer.Clear();
                }
            }
        }

        /// <summary>
        /// 处理ANSI转义序列
        /// </summary>
        private void ProcessAnsiText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            // 添加到缓冲区
            _outputBuffer.Append(text);
            
            // 停止任何正在进行的处理定时器
            _outputProcessTimer.Stop();
            
            // 启动新的处理定时器，延迟处理全部输出
            _outputProcessTimer.Start();
        }

        /// <summary>
        /// 解析ANSI转义代码并应用相应的样式
        /// </summary>
        private void ParseAnsiCode(string code, ref SolidColorBrush currentForeground, ref SolidColorBrush currentBackground)
        {
            if (string.IsNullOrEmpty(code) || code.Length < 3)
                return;

            // 提取数字部分
            var match = Regex.Match(code, @"\x1b\[(\d*(?:;\d+)*)([A-Za-z])");
            if (match.Success)
            {
                string parameters = match.Groups[1].Value;
                string command = match.Groups[2].Value;

                // 处理常见的命令类型
                switch (command)
                {
                    case "m": // SGR (Select Graphic Rendition)
                        string[] codes = parameters.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

                        if (codes.Length == 0 || (codes.Length == 1 && codes[0] == "0"))
                        {
                            // 重置所有属性
                            currentForeground = _defaultForeground;
                            currentBackground = _defaultBackground;
                            return;
                        }

                        foreach (string param in codes)
                        {
                            if (param == "0")
                            {
                                // 重置所有属性
                                currentForeground = _defaultForeground;
                                currentBackground = _defaultBackground;
                            }
                            else if (_ansiColorMap.TryGetValue(param, out SolidColorBrush brush))
                            {
                                // 文本颜色 (30-37, 90-97)
                                if (param.StartsWith("3") || param.StartsWith("9"))
                                {
                                    currentForeground = brush;
                                }
                                // 背景颜色 (40-47)
                                else if (param.StartsWith("4"))
                                {
                                    currentBackground = brush;
                                }
                            }
                        }
                        break;
                }
            }
        }

        /// <summary>
        /// 添加文本到终端并应用样式，确保稳定显示
        /// </summary>
        private void AppendText(string text, SolidColorBrush foreground, SolidColorBrush background)
        {
            if (string.IsNullOrEmpty(text))
                return;
                
            try
            {
                // 临时禁用光标更新以避免递归
                bool oldUpdatingState = _isUpdatingCaret;
                _isUpdatingCaret = true;
                
                // 确保UI线程
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.Invoke(() => AppendText(text, foreground, background));
                    return;
                }
                
                // 删除当前光标以避免干扰
                if (_caretRange != null)
                {
                    try 
                    {
                        _caretRange.Text = "";
                        _caretRange = null;
                    }
                    catch { }
                }
                
                // 添加新文本
                TextRange tr = new TextRange(terminalTextBox.Document.ContentEnd, terminalTextBox.Document.ContentEnd);
                tr.Text = text;
                tr.ApplyPropertyValue(TextElement.ForegroundProperty, foreground);
                tr.ApplyPropertyValue(TextElement.BackgroundProperty, background);

                // 安全地滚动到末尾
                EnsureScrollToEnd(false);
                
                // 恢复先前状态
                _isUpdatingCaret = oldUpdatingState;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"添加文本到终端时出错: {ex.Message}");
                // 不在这里重试，以避免潜在的无限循环
            }
        }

        // 添加一个确保滚动到末尾的方法
        private void EnsureScrollToEnd(bool forceImmediate)
        {
            // 避免重入
            if (_isAutoScrolling)
                return;
                
            try
            {
                _isAutoScrolling = true;
                
                if (forceImmediate)
                {
                    // 立即滚动
                    terminalTextBox.ScrollToEnd();
                    _isAutoScrolling = false;
                }
                else
                {
                    // 延迟滚动以允许UI更新
                    Dispatcher.InvokeAsync(() => {
                        try {
                            terminalTextBox.ScrollToEnd();
                        } 
                        catch (Exception ex) {
                            Debug.WriteLine($"延迟滚动时出错: {ex.Message}");
                        }
                        _isAutoScrolling = false;
                    }, DispatcherPriority.ContextIdle);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"滚动到末尾时出错: {ex.Message}");
                _isAutoScrolling = false;
            }
        }

        /// <summary>
        /// 获取当前文本长度
        /// </summary>
        private int GetTextLength()
        {
            try
            {
                return new TextRange(terminalTextBox.Document.ContentStart, terminalTextBox.Document.ContentEnd).Text.Length;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取文本长度时出错: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// 清除终端内容
        /// </summary>
        public void ClearTerminal()
        {
            try
            {
                terminalTextBox.Document.Blocks.Clear();
                _inputStartPosition = 0;
                _inputBuffer.Clear();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"清除终端时出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 停止进程
        /// </summary>
        public void StopProcess()
        {
            if (_process != null && !_process.HasExited)
            {
                try
                {
                    AppendText("\n正在终止进程...\n", new SolidColorBrush(Colors.Yellow), _defaultBackground);
                    _process.Kill();
                }
                catch (Exception ex)
                {
                    AppendText($"\n终止进程时出错: {ex.Message}\n", new SolidColorBrush(Colors.Red), _defaultBackground);
                }
            }
            else
            {
                AppendText("\n没有正在运行的进程\n", new SolidColorBrush(Colors.Yellow), _defaultBackground);
            }
        }

        // 修改预处理事件以支持特殊字符如反斜杠
        private void TerminalTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_process == null || _process.HasExited)
                return;

            // 更新键盘活动时间戳
            _lastKeyboardActivity = DateTime.Now;
            
            try
            {
                // 首先处理控制组合键
                if (Keyboard.Modifiers == ModifierKeys.Control)
                {
                    // 将控制键处理委托给专门的方法
                    HandleControlKeyCommand(e.Key);
                    e.Handled = true;
                    return;
                }

                // 处理其他键
                switch (e.Key)
                {
                    case Key.Enter:
                        SendInputChar('\n');
                        e.Handled = true;
                        break;

                    case Key.Tab:
                        // Tab键特殊处理 - 添加到缓冲区并显示，但不发送
                        _inputBuffer.Append('\t');
                        AppendText("    ", _defaultForeground, _defaultBackground); // 显示为4个空格
                        e.Handled = true;
                        break;

                    // 处理反斜杠和常见路径字符
                    case Key.OemBackslash:
                        SendInputChar('\\');
                        e.Handled = true;
                        break;
                        
                    case Key.OemQuestion:
                        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                            SendInputChar('?');
                        else
                            SendInputChar('/');
                        e.Handled = true;
                        break;
                        
                    case Key.OemSemicolon:
                        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                            SendInputChar(':');
                        else
                            SendInputChar(';');
                        e.Handled = true;
                        break;
                        
                        
                    case Key.Oem5: // 可能是反斜杠/竖线
                        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                            SendInputChar('|');
                        else
                            SendInputChar('\\');
                        e.Handled = true;
                        break;

                    // 继续处理其他按键...
                    case Key.D0:
                    case Key.D1:
                    case Key.D2:
                    case Key.D3:
                    case Key.D4:
                    case Key.D5:
                    case Key.D6:
                    case Key.D7:
                    case Key.D8:
                    case Key.D9:
                        try
                        {
                            char digit = (char)('0' + (e.Key - Key.D0));
                            SendInputChar(digit);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"处理数字键时出错: {ex.Message}");
                            AppendText($"\n[错误] 处理键入数字时出错\n", new SolidColorBrush(Colors.Red), _defaultBackground);
                        }
                        e.Handled = true;
                        break;
                    case Key.NumPad0:
                    case Key.NumPad1:
                    case Key.NumPad2:
                    case Key.NumPad3:
                    case Key.NumPad4:
                    case Key.NumPad5:
                    case Key.NumPad6:
                    case Key.NumPad7:
                    case Key.NumPad8:
                    case Key.NumPad9:
                        try
                        {
                            char digit = (char)('0' + (e.Key - Key.NumPad0));
                            SendInputChar(digit);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"处理小键盘数字键时出错: {ex.Message}");
                            AppendText($"\n[错误] 处理小键盘数字键时出错\n", new SolidColorBrush(Colors.Red), _defaultBackground);
                        }
                        e.Handled = true;
                        break;
                    // 处理字母键
                    case Key.A:
                    case Key.B:
                    case Key.C:
                    case Key.D:
                    case Key.E:
                    case Key.F:
                    case Key.G:
                    case Key.H:
                    case Key.I:
                    case Key.J:
                    case Key.K:
                    case Key.L:
                    case Key.M:
                    case Key.N:
                    case Key.O:
                    case Key.P:
                    case Key.Q:
                    case Key.R:
                    case Key.S:
                    case Key.T:
                    case Key.U:
                    case Key.V:
                    case Key.W:
                    case Key.X:
                    case Key.Y:
                    case Key.Z:
                        char letter = (char)('a' + (e.Key - Key.A));
                        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                        {
                            letter = char.ToUpper(letter);
                        }
                        SendInputChar(letter);
                        e.Handled = true;
                        break;

                    // 处理空格
                    case Key.Space:
                        SendInputChar(' ');
                        e.Handled = true;
                        break;

                    // 处理特殊字符
                    case Key.OemPeriod:
                        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                            SendInputChar('>');
                        else
                            SendInputChar('.');
                        e.Handled = true;
                        break;

                    case Key.OemComma:
                        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                            SendInputChar('<');
                        else
                            SendInputChar(',');
                        e.Handled = true;
                        break;

                    case Key.OemMinus:
                        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                            SendInputChar('_');
                        else
                            SendInputChar('-');
                        e.Handled = true;
                        break;

                    case Key.OemPlus:
                        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                            SendInputChar('+');
                        else
                            SendInputChar('=');
                        e.Handled = true;
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"按键处理错误: {ex.Message}");
            }
        }

        private void TerminalTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            // 防止其他控件抢夺焦点
            e.Handled = true;
        }

        // 修改TextChanged事件处理程序，避免递归调用
        private void TerminalTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // 如果正在处理ANSI文本或更新光标，不要重复处理
            if (_isProcessing || _isUpdatingCaret)
            {
                return;
            }

            try
            {
                // 确保文本变化后滚动到末尾
                EnsureScrollToEnd(false);
                
                // 仅当不是由UpdateCaretDisplay触发的TextChanged事件时才更新光标
                if (!_delayedInputTimer.IsEnabled) // 如果不在等待输入延迟中
                {
                    _caretVisible = true;
                    UpdateCaretDisplay();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理文本变化时出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 发送输入字符到进程
        /// </summary>
        private void SendInputChar(char c)
        {
            if (_inputWriter == null || _process == null || _process.HasExited)
                return;

            // 更新键盘活动时间戳
            _lastKeyboardActivity = DateTime.Now;
            
            try
            {
                // 开始处理，防止重入
                _isProcessing = true;
                
                // 检查是否是确认字符
                bool isConfirmChar = _waitingForInput && 
                    (c == 'y' || c == 'Y' || c == 'n' || c == 'N' || 
                     c == '是' || c == '否' || c == '0' || c == '1');
                
                // 处理回车键或单字符确认输入
                if (c == '\n' || isConfirmChar)
                {
                    // 获取要发送的输入
                    string input = isConfirmChar ? c.ToString() : _inputBuffer.ToString();
                    _inputBuffer.Clear();

                    // 删除当前光标
                    if (_caretRange != null)
                    {
                        _caretRange.Text = "";
                        _caretRange = null;
                    }

                    // 显示字符
                    AppendText(isConfirmChar ? c.ToString() : "", _defaultForeground, _defaultBackground);
                    
                    // 添加换行，使下一个提示出现在新行
                    AppendText("\n", _defaultForeground, _defaultBackground);

                    try 
                    {
                        // 发送输入到进程
                        if (isConfirmChar)
                        {
                            // 对于确认字符，发送单个字符后跟换行
                            _inputWriter.WriteLine(c);
                        }
                        else
                        {
                            // 对于其他输入，直接发送整个缓冲内容
                            _inputWriter.WriteLine(input);
                        }
                        _inputWriter.Flush();
                        Debug.WriteLine($"[终端] 发送输入: '{input}'");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"发送命令到进程时出错: {ex.Message}");
                        AppendText($"[错误] 发送输入失败: {ex.Message}\n", new SolidColorBrush(Colors.Red), _defaultBackground);
                    }

                    // 重置输入状态
                    _waitingForInput = false;
                    _lastDetectedPrompt = string.Empty;
                    _promptEndPosition = 0;
                    
                    // 确保滚动到最新内容
                    EnsureScrollToEnd(true);
                }
                else
                {
                    // 其他字符添加到缓冲区并显示
                    _inputBuffer.Append(c);
                    
                    // 删除光标
                    if (_caretRange != null)
                    {
                        _caretRange.Text = "";
                        _caretRange = null;
                    }
                    
                    // 显示字符
                    AppendText(c.ToString(), _defaultForeground, _defaultBackground);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理输入字符时出错: {ex.Message}");
                try 
                {
                    AppendText($"\n[错误] 字符输入失败: {ex.Message}\n", new SolidColorBrush(Colors.Red), _defaultBackground);
                }
                catch { }
            }
            finally
            {
                // 结束处理状态
                _isProcessing = false;
                
                // 更新光标位置
                _caretVisible = true;
                UpdateCaretDisplay();
            }
        }

        /// <summary>
        /// 发送Ctrl+C
        /// </summary>
        private void SendCtrlC()
        {
            if (_inputWriter == null || _process == null || _process.HasExited)
                return;

            try
            {
                // 显示^C
                AppendText("^C\n", new SolidColorBrush(Colors.Red), _defaultBackground);
                _inputBuffer.Clear();

                // 发送ETX (End of Text, Ctrl+C)字符
                _inputWriter.Write('\x03');
                _inputWriter.Flush();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"发送Ctrl+C时出错: {ex.Message}");
            }
        }

        // 添加此方法以允许临时释放焦点
        public void AllowFocusChange()
        {
            // 临时禁用自动获取焦点
            _caretBlinkTimer.Stop();
            
            // 延迟后恢复焦点行为
            Dispatcher.InvokeAsync(() => {
                _caretBlinkTimer.Start();
            }, DispatcherPriority.ApplicationIdle);
        }
        
        // 添加辅助方法检查菜单是否打开
        private bool IsMenuOpen()
        {
            return terminalTextBox.ContextMenu != null && terminalTextBox.ContextMenu.IsOpen;
        }

        /// <summary>
        /// 模拟显示命令提示符
        /// </summary>
        /// <param name="prompt">提示符文本，例如 "> "</param>
        public void DisplayPrompt(string prompt)
        {
            if (string.IsNullOrEmpty(prompt))
                prompt = "> ";
                
            AppendText(prompt, new SolidColorBrush(Colors.Green), _defaultBackground);
            _inputStartPosition = GetTextLength();
        }
        
        /// <summary>
        /// 允许其他控件临时获取焦点
        /// </summary>
        /// <param name="durationMs">焦点释放时间(毫秒)</param>
        public void ReleaseFocusTemporarily(int durationMs = 500)
        {
            SetFocusCaptureEnabled(false, durationMs);
        }

        /// <summary>
        /// 设置终端是否可以自动获取焦点
        /// </summary>
        /// <param name="allow">是否允许获取焦点</param>
        /// <param name="resumeAfterDelayMs">延迟恢复焦点的时间(毫秒)，-1表示不自动恢复</param>
        public void SetFocusCaptureEnabled(bool allow, int resumeAfterDelayMs = -1)
        {
            _allowFocusCapture = allow;
            
            // 如果设置了延迟恢复
            if (!allow && resumeAfterDelayMs > 0)
            {
                var timer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(resumeAfterDelayMs)
                };
                
                timer.Tick += (s, e) =>
                {
                    _allowFocusCapture = true;
                    (s as DispatcherTimer).Stop();
                };
                
                timer.Start();
            }
        }
        
        /// <summary>
        /// 强制获取焦点（在需要确保终端获得输入焦点时调用）
        /// </summary>
        public void ForceFocus()
        {
            _allowFocusCapture = true;
            terminalTextBox.Focus();
        }

        // 添加检测Python输入提示的方法
        private void DetectInputPrompt(string text)
        {
            // 防止重入
            if (_isWaitingForPromptProcessing)
                return;

            // 首先停止任何正在进行的延迟输入计时器
            if (_delayedInputTimer.IsEnabled)
            {
                _delayedInputTimer.Stop();
            }

            // 识别确认提示
            bool isConfirmPrompt = 
                Regex.IsMatch(text, @"\([yYnN](/[yYnN])?\):\s*$") ||
                Regex.IsMatch(text, @"\([yYnN]\)[^:\r\n]*$") ||
                text.Contains("(y/N)") ||
                text.Contains("(Y/n)") ||
                Regex.IsMatch(text, @"(?i)确定.*吗\?.*\s*$") ||
                Regex.IsMatch(text, @"(?i)是否.*\?.*\s*$") ||
                Regex.IsMatch(text, @"(?i)confirm\?.*\s*$");

            // 识别其他一般提示
            bool isGeneralPrompt = 
                Regex.IsMatch(text, @"(?:^|[\r\n])[^:\r\n]*[>:]\s*$") || 
                Regex.IsMatch(text, @"(?i)(?:input|enter|请输入|选择|choice)[^:\r\n]*:\s*$");

            if (isConfirmPrompt || isGeneralPrompt)
            {
                // 记录提示文本以便调试
                _lastDetectedPrompt = text.Trim();
                Debug.WriteLine($"[终端] 检测到提示: '{_lastDetectedPrompt}' [确认提示:{isConfirmPrompt}]");
                
                // 设置处理标志，防止重入
                _isWaitingForPromptProcessing = true;
                
                // 清空输入缓冲区以准备新输入
                _inputBuffer.Clear();
                
                // 延迟激活输入模式，给提示完全显示的时间
                // 确认提示需要更长的延迟，因为它们通常包含更多文本
                _waitingForInput = false; // 先设为false，延迟后再设为true
                _delayedInputTimer.Interval = TimeSpan.FromMilliseconds(isConfirmPrompt ? 500 : 300);
                _delayedInputTimer.Start();
            }
        }



        /// <summary>
        /// 处理控制组合键(如Ctrl+C, Ctrl+Z等)
        /// </summary>
        private void HandleControlKeyCommand(Key key)
        {
            if (_process == null || _process.HasExited || _inputWriter == null)
                return;

            try
            {
                switch (key)
                {
                    case Key.C: // Ctrl+C - 中断
                        // 显示^C
                        AppendText("^C\n", new SolidColorBrush(Colors.Red), _defaultBackground);
                        _inputBuffer.Clear();
                        
                        // 发送ETX (End of Text, Ctrl+C)字符
                        _inputWriter.Write('\x03');
                        _inputWriter.Flush();
                        StopProcess();
                        Debug.WriteLine("[终端] 发送信号: Ctrl+C (SIGINT)");
                        break;
                        
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理控制键时出错: {ex.Message}");
                AppendText($"\n[错误] 控制键处理失败: {ex.Message}\n", new SolidColorBrush(Colors.Red), _defaultBackground);
            }
        }

        // 添加输出处理定时器的处理方法
        private void OutputProcessTimer_Tick(object sender, EventArgs e)
        {
            _outputProcessTimer.Stop();
            
            try
            {
                // 一次性处理全部缓冲的输出
                string bufferedOutput = _outputBuffer.ToString();
                _outputBuffer.Clear();
                
                if (!string.IsNullOrEmpty(bufferedOutput))
                {
                    // 进行完整的输出处理
                    ProcessFullOutput(bufferedOutput);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理缓冲输出时出错: {ex.Message}");
            }
        }

        // 添加完整输出处理方法
        private void ProcessFullOutput(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;
                
            _isProcessing = true;
            
            try
            {
                // 先处理ANSI转义序列
                string pattern = @"(\x1b\[\d*(?:;\d+)*[A-Za-z])";
                string[] parts = Regex.Split(text, pattern);

                SolidColorBrush currentForeground = _defaultForeground;
                SolidColorBrush currentBackground = _defaultBackground;

                foreach (string part in parts)
                {
                    if (part.StartsWith("\x1b["))
                    {
                        // 解析ANSI转义序列
                        ParseAnsiCode(part, ref currentForeground, ref currentBackground);
                    }
                    else if (!string.IsNullOrEmpty(part))
                    {
                        // 添加普通文本
                        AppendText(part, currentForeground, currentBackground);
                    }
                }
                
                // 处理完全部输出后，检查是否有提示
                DetectPromptInFullText(text);
                
                _inputStartPosition = GetTextLength();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"完整处理输出时出错: {ex.Message}");
            }
            finally
            {
                _isProcessing = false;
            }
        }

        // 针对完整文本检测提示的新方法
        private void DetectPromptInFullText(string fullText)
        {
            if (string.IsNullOrEmpty(fullText))
                return;

            try
            {
                // 停止正在进行的输入处理
                if (_delayedInputTimer.IsEnabled)
                {
                    _delayedInputTimer.Stop();
                }
                
                // 重置输入状态
                _isWaitingForPromptProcessing = false;
                _waitingForInput = false;

                // 使用更严格的提示匹配规则
                Match menuPromptMatch = Regex.Match(fullText, @"请输入您的选择\s*>\s*$", RegexOptions.RightToLeft);
                Match confirmPromptMatch = Regex.Match(fullText, @"\(y/N\):\s*$", RegexOptions.RightToLeft);
                Match generalPromptMatch = Regex.Match(fullText, @"[>:]\s*$", RegexOptions.RightToLeft);
                
                bool hasPrompt = menuPromptMatch.Success || confirmPromptMatch.Success || generalPromptMatch.Success;
                
                if (hasPrompt)
                {
                    // 记录提示文本
                    _lastDetectedPrompt = hasPrompt ? fullText.Substring(Math.Max(0, fullText.Length - 30)).Trim() : "";
                    
                    Debug.WriteLine($"[终端] 在完整文本中检测到提示: '{_lastDetectedPrompt}'");
                    
                    // 清空输入缓冲区
                    _inputBuffer.Clear();
                    
                    // 设置延迟，确保UI有时间完全渲染提示
                    _isWaitingForPromptProcessing = true;
                    _delayedInputTimer.Interval = TimeSpan.FromMilliseconds(confirmPromptMatch.Success ? 800 : 600);
                    _delayedInputTimer.Start();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"检测完整文本中的提示时出错: {ex.Message}");
            }
        }

        // 修改DelayedInputTimer_Tick方法，确保光标总是在正确位置
        private void DelayedInputTimer_Tick(object sender, EventArgs e)
        {
            _delayedInputTimer.Stop();
            _isWaitingForPromptProcessing = false;
            
            try
            {
                // 确保我们有一个稳定的输入状态
                _waitingForInput = true;
                
                // 记录当前文本长度作为提示结束位置
                _promptEndPosition = GetTextLength();
                
                // 在调试中记录操作
                Debug.WriteLine($"[终端] 提示处理完成，准备接收输入，提示位置: {_promptEndPosition}");
                
                // 延迟一帧再显示光标，确保UI已完全更新
                Dispatcher.InvokeAsync(() => {
                    // 强制滚动到末尾
                    EnsureScrollToEnd(true);
                    
                    // 显示光标
                    _caretVisible = true;
                    UpdateCaretDisplay();
                    
                    // 确保焦点在终端上
                    ForceFocus();
                }, DispatcherPriority.Render);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"延迟输入处理时出错: {ex.Message}");
            }
        }

        #region IDisposable Support
        public void Dispose()
        {
            if (!_isDisposed)
            {
                _caretBlinkTimer?.Stop();

                if (_process != null && !_process.HasExited)
                {
                    try
                    {
                        _process.Kill();
                    }
                    catch { /* 忽略错误 */ }
                }

                CleanupProcess();

                foreach (var brush in _ansiColorMap.Values)
                {
                    try { brush.Freeze(); } catch { }
                }

                _isDisposed = true;
            }
        }
        #endregion
    }
}