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
    /// PTY �ն˿ؼ���֧�� ANSI ת�����кͽ���ʽ����
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

        // ���һ���µ��ֶ��������Ƿ������Զ���ȡ����
        private bool _allowFocusCapture = true;

        // ���һ���ֶμ�¼���λ�ú�״̬
        private TextPointer _caretPosition;
        private bool _caretVisible = true;
        private SolidColorBrush _caretBrush = new SolidColorBrush(Colors.White);
        private TextRange _caretRange;
        private bool _waitingForInput = false;

        // ����ֶθ����Ƿ����ڸ��¹��
        private bool _isUpdatingCaret = false;

        // ���һ���ֶ�����������ļ��̻
        private DateTime _lastKeyboardActivity = DateTime.MinValue;
        private const int MinimumBlinkDelayAfterKeyboardMs = 500; // ���̻�����С��˸�ӳ�

        // ���һ�����ֶ������ƹ�����Ϊ
        private bool _isAutoScrolling = false;
        private DispatcherTimer _delayedInputTimer;

        // ����µ��ֶ������õع����������״̬
        private bool _isWaitingForPromptProcessing = false;
        private string _lastDetectedPrompt = string.Empty;
        private int _promptEndPosition = 0;

        // ����µ��ֶ�������ȷ�ؿ�����ʾ����������
        private StringBuilder _outputBuffer = new StringBuilder();
        private DispatcherTimer _outputProcessTimer;
        private const int OutputBufferProcessDelay = 100; // ms

        public event EventHandler ProcessExited;

        public PtyTerminalControl()
        {
            InitializeComponent();

            // ����Ĭ����ɫ
            _defaultForeground = new SolidColorBrush(Colors.LightGray);
            _defaultBackground = new SolidColorBrush(Colors.Black);
            _caretBrush = new SolidColorBrush(Colors.White); // ���ù����ɫ

            InitializeColorMap();

            // ���ù����˸��ʱ��
            _caretBlinkTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _caretBlinkTimer.Tick += CaretBlinkTimer_Tick;
            
            // �����ӳ����붨ʱ��
            _delayedInputTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(600) // ���ӵ�600�����ӳ�
            };
            _delayedInputTimer.Tick += DelayedInputTimer_Tick;
            _delayedInputTimer.Stop();
            
            // �����������ʱ��
            _outputProcessTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(OutputBufferProcessDelay)
            };
            _outputProcessTimer.Tick += OutputProcessTimer_Tick;
            _outputProcessTimer.Stop();

            // �����������Ҽ��˵�
            CreateContextMenu();

            // �����ı��༭ - ���ǻ��Լ��������е�����
            terminalTextBox.IsReadOnly = true;

            // ��������Ԥ�����¼�
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
            // ����л�Ľ��̣���ȡ����
            if (_process != null && !_process.HasExited)
            {
                terminalTextBox.Focus();
            }
        }

        private void TerminalTextBox_MouseLeave(object sender, MouseEventArgs e)
        {
            _isMouseOverTerminal = false;
            // ������뿪ʱ�������ͷŽ��㣬���û�ȥ��������ؼ�
        }

        /// <summary>
        /// ���������Ĳ˵������ӵ��ն�
        /// </summary>
        private void CreateContextMenu()
        {
            var menu = new ContextMenu();
            menu.Opened += (s, e) => {
                // ��ֹ�˵���ʱ���㱻����
                e.Handled = true;
            };
            menu.Closed += (s, e) => {
                // �˵��رպ󽫽��㷵�ص��ն�
                Dispatcher.InvokeAsync(() => terminalTextBox.Focus());
            };

            // ���Ʋ˵���
            var copyItem = new MenuItem { Header = "����" };
            copyItem.Click += (s, e) => CopySelectedText();
            menu.Items.Add(copyItem);

            // ճ���˵���
            var pasteItem = new MenuItem { Header = "ճ��" };
            pasteItem.Click += (s, e) => PasteText();
            menu.Items.Add(pasteItem);

            // �ָ���
            menu.Items.Add(new Separator());

            // ����˵���
            var clearItem = new MenuItem { Header = "����ն�" };
            clearItem.Click += (s, e) => ClearTerminal();
            menu.Items.Add(clearItem);

            // ���˵�Ӧ�õ��ն�
            terminalTextBox.ContextMenu = menu;

            // ȷ���˵��ܱ��ִ�״̬
            terminalTextBox.ContextMenuOpening += (s, e) =>
            {
                // ��ֹĬ�ϴ���ȷ���˵�������ʾ
                e.Handled = false;
            };
        }

        /// <summary>
        /// ����ֱ���ı�����
        /// </summary>
        private void TerminalTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (_process != null && !_process.HasExited && _inputWriter != null)
            {
                // ֱ�ӷ����ı�������ַ�
                foreach (char c in e.Text)
                {
                    SendInputChar(c);
                }
                e.Handled = true;
            }
        }

        /// <summary>
        /// ����ѡ���ı�
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
                Debug.WriteLine($"�����ı�ʱ����: {ex.Message}");
            }
        }

        /// <summary>
        /// ճ���ı����ն�
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
                    // ��ʾճ�����ı�
                    AppendText(text, _defaultForeground, _defaultBackground);

                    // ���͵�����
                    _inputWriter.Write(text);
                    _inputWriter.Flush();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ճ���ı�ʱ����: {ex.Message}");
            }
        }

        private void InitializeColorMap()
        {
            _ansiColorMap = new Dictionary<string, SolidColorBrush>
            {
                // ��׼ ANSI ��ɫ
                { "30", new SolidColorBrush(Colors.Black) },
                { "31", new SolidColorBrush(Colors.Red) },
                { "32", new SolidColorBrush(Colors.Green) },
                { "33", new SolidColorBrush(Colors.Yellow) },
                { "34", new SolidColorBrush(Colors.Blue) },
                { "35", new SolidColorBrush(Colors.Magenta) },
                { "36", new SolidColorBrush(Colors.Cyan) },
                { "37", new SolidColorBrush(Colors.White) },
                { "39", _defaultForeground }, // Ĭ��ǰ��ɫ

                // ����ɫ
                { "40", new SolidColorBrush(Colors.Black) },
                { "41", new SolidColorBrush(Colors.Red) },
                { "42", new SolidColorBrush(Colors.Green) },
                { "43", new SolidColorBrush(Colors.Yellow) },
                { "44", new SolidColorBrush(Colors.Blue) },
                { "45", new SolidColorBrush(Colors.Magenta) },
                { "46", new SolidColorBrush(Colors.Cyan) },
                { "47", new SolidColorBrush(Colors.White) },
                { "49", _defaultBackground }, // Ĭ�ϱ���ɫ

                // ��ɫϵ��
                { "90", new SolidColorBrush(Color.FromRgb(128, 128, 128)) },  // ���� (��ɫ)
                { "91", new SolidColorBrush(Color.FromRgb(255, 100, 100)) },  // ����
                { "92", new SolidColorBrush(Color.FromRgb(100, 255, 100)) },  // ����
                { "93", new SolidColorBrush(Color.FromRgb(255, 255, 100)) },  // ����
                { "94", new SolidColorBrush(Color.FromRgb(100, 100, 255)) },  // ����
                { "95", new SolidColorBrush(Color.FromRgb(255, 100, 255)) },  // ����
                { "96", new SolidColorBrush(Color.FromRgb(100, 255, 255)) },  // ����
                { "97", new SolidColorBrush(Colors.White) }                   // ����
            };
        }

        // �Ľ�CaretBlinkTimer_Tick����
        private void CaretBlinkTimer_Tick(object sender, EventArgs e)
        {
            try 
            {
                // �������м��̻����ʱ���ֹ��ɼ�
                TimeSpan timeSinceLastActivity = DateTime.Now - _lastKeyboardActivity;
                if (timeSinceLastActivity.TotalMilliseconds < MinimumBlinkDelayAfterKeyboardMs)
                {
                    // ȷ�����ɼ���������ת״̬
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
                
                // ������˸�߼�
                _caretVisible = !_caretVisible;
                
                if (!_isProcessing)
                {
                    UpdateCaretDisplay();
                }
                
                // �������߼�
                if (_allowFocusCapture && _isMouseOverTerminal && _process != null && !_process.HasExited && 
                    IsVisible && IsEnabled && !IsMenuOpen())
                {
                    terminalTextBox.Focus();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"�����˸ʱ����: {ex.Message}");
                // ��Ҫ���쳣�ж϶�ʱ��
            }
        }

        // �޸�UpdateCaretDisplay����������ݹ����
        private void UpdateCaretDisplay()
        {
            // ����Ѿ��ڸ��¹�꣬���˳��Է�ֹ�ݹ�
            if (_isUpdatingCaret)
                return;

            try
            {
                // ���ñ�־ָʾ���ڸ��¹��
                _isUpdatingCaret = true;

                // ɾ��֮ǰ�Ĺ��
                if (_caretRange != null)
                {
                    try 
                    {
                        _caretRange.Text = "";
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"ɾ�����ʱ����: {ex.Message}");
                    }
                    _caretRange = null;
                }
                
                // ������Ӧ�ÿɼ����ҽ��������У�����ʾ���
                if (_caretVisible && _process != null && !_process.HasExited)
                {
                    try
                    {
                        // ��ȡ�ĵ�ĩβλ��
                        _caretPosition = terminalTextBox.Document.ContentEnd;
                        
                        // �������
                        _caretRange = new TextRange(_caretPosition, _caretPosition);
                        _caretRange.Text = "��"; // ʹ�÷����ַ���Ϊ���
                        
                        // Ӧ�ù����ʽ
                        _caretRange.ApplyPropertyValue(TextElement.ForegroundProperty, _caretBrush);
                        _caretRange.ApplyPropertyValue(TextElement.BackgroundProperty, _defaultBackground);
                        
                        // ȷ�����ɼ� - ʹ�ð�ȫ�Ĺ�������
                        EnsureScrollToEnd(true);
                    }
                    catch (Exception ex) 
                    {
                        Debug.WriteLine($"�������ʱ����: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"���¹��ʱ����: {ex.Message}");
            }
            finally
            {
                // ���ñ�־
                _isUpdatingCaret = false;
            }
        }

        // ����·�������ִ�нű�ǰ���ã�ȷ���ն�׼���ý�������
        public void PrepareForExecution()
        {
            // �������λ����Σ�ȷ���ն˿��Ի�ȡ����
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
        /// �������̲����ն���ִ��
        /// </summary>
        public void StartProcess(string pythonExecutable, string scriptPath,
                                 string arguments,
                                 Dictionary<string, string> environmentVariables,
                                 string workingDirectory)
        {
            try
            {
                ClearTerminal(); // �����½���ǰ������ն�
                _waitingForInput = false; // ���������־

                _process = new Process();
                _process.StartInfo.FileName = pythonExecutable;
                _process.StartInfo.Arguments = string.IsNullOrEmpty(arguments)
                    ? $"\"{scriptPath}\""
                    : $"\"{scriptPath}\" {arguments}";

                // ���ù���Ŀ¼
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

                // ʹ��ϵͳĬ�ϱ��룬�����������
                _process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                _process.StartInfo.StandardErrorEncoding = Encoding.UTF8;

                // ���û�������
                if (environmentVariables != null)
                {
                    foreach (var kvp in environmentVariables)
                    {
                        _process.StartInfo.EnvironmentVariables[kvp.Key] = kvp.Value;
                    }
                }

                // ���ùؼ���Python��������
                _process.StartInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                _process.StartInfo.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";

                // ����ANSI��ɫ֧��
                _process.StartInfo.EnvironmentVariables["FORCE_COLOR"] = "1";
                _process.StartInfo.EnvironmentVariables["TERM"] = "xterm-color";

                // ע������¼�
                _process.OutputDataReceived += Process_OutputDataReceived;
                _process.ErrorDataReceived += Process_ErrorDataReceived;
                _process.Exited += Process_Exited;
                _process.EnableRaisingEvents = true;

                // ��������
                _process.Start();
                _inputWriter = _process.StandardInput;
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();

                // ��ӳ�ʼ��ʾ��Ϣ����ʼ�����˸
                AppendText($"����������: {Path.GetFileName(scriptPath)}\n\r", _defaultForeground, _defaultBackground);
                _inputStartPosition = GetTextLength();

                StartCaretBlinkTimer();

                // ȷ���������ն���
                terminalTextBox.Focus();

                // �������̺�ȷ�������ʾ
                _caretVisible = true;
                UpdateCaretDisplay();
            }
            catch (Exception ex)
            {
                AppendText($"��������ʧ��: {ex.Message}\n", new SolidColorBrush(Colors.Red), _defaultBackground);
            }
        }

        // �޸�������ݽ��շ�����ʹ�û������
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
                            // �ۻ������������
                            _outputBuffer.AppendLine(e.Data);
                            
                            // ֹͣ�κ����ڽ��еĶ�ʱ��
                            _outputProcessTimer.Stop();
                            
                            // �����µĴ���ʱ��
                            _outputProcessTimer.Start();
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"�����������ʱ����: {ex.Message}");
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

                        AppendText($"\n�������˳����˳�����: {exitCode}\n",
                            exitCode == 0 ? new SolidColorBrush(Colors.Green) : new SolidColorBrush(Colors.Red),
                            _defaultBackground);
                    }

                    CleanupProcess();
                    ProcessExited?.Invoke(this, EventArgs.Empty);
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"��������˳��¼�ʱ����: {ex.Message}");
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
                    Debug.WriteLine($"���������Դʱ����: {ex.Message}");
                }
                finally
                {
                    _process = null;
                    _inputBuffer.Clear();
                }
            }
        }

        /// <summary>
        /// ����ANSIת������
        /// </summary>
        private void ProcessAnsiText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            // ��ӵ�������
            _outputBuffer.Append(text);
            
            // ֹͣ�κ����ڽ��еĴ���ʱ��
            _outputProcessTimer.Stop();
            
            // �����µĴ���ʱ�����ӳٴ���ȫ�����
            _outputProcessTimer.Start();
        }

        /// <summary>
        /// ����ANSIת����벢Ӧ����Ӧ����ʽ
        /// </summary>
        private void ParseAnsiCode(string code, ref SolidColorBrush currentForeground, ref SolidColorBrush currentBackground)
        {
            if (string.IsNullOrEmpty(code) || code.Length < 3)
                return;

            // ��ȡ���ֲ���
            var match = Regex.Match(code, @"\x1b\[(\d*(?:;\d+)*)([A-Za-z])");
            if (match.Success)
            {
                string parameters = match.Groups[1].Value;
                string command = match.Groups[2].Value;

                // ����������������
                switch (command)
                {
                    case "m": // SGR (Select Graphic Rendition)
                        string[] codes = parameters.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

                        if (codes.Length == 0 || (codes.Length == 1 && codes[0] == "0"))
                        {
                            // ������������
                            currentForeground = _defaultForeground;
                            currentBackground = _defaultBackground;
                            return;
                        }

                        foreach (string param in codes)
                        {
                            if (param == "0")
                            {
                                // ������������
                                currentForeground = _defaultForeground;
                                currentBackground = _defaultBackground;
                            }
                            else if (_ansiColorMap.TryGetValue(param, out SolidColorBrush brush))
                            {
                                // �ı���ɫ (30-37, 90-97)
                                if (param.StartsWith("3") || param.StartsWith("9"))
                                {
                                    currentForeground = brush;
                                }
                                // ������ɫ (40-47)
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
        /// ����ı����ն˲�Ӧ����ʽ��ȷ���ȶ���ʾ
        /// </summary>
        private void AppendText(string text, SolidColorBrush foreground, SolidColorBrush background)
        {
            if (string.IsNullOrEmpty(text))
                return;
                
            try
            {
                // ��ʱ���ù������Ա���ݹ�
                bool oldUpdatingState = _isUpdatingCaret;
                _isUpdatingCaret = true;
                
                // ȷ��UI�߳�
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.Invoke(() => AppendText(text, foreground, background));
                    return;
                }
                
                // ɾ����ǰ����Ա������
                if (_caretRange != null)
                {
                    try 
                    {
                        _caretRange.Text = "";
                        _caretRange = null;
                    }
                    catch { }
                }
                
                // ������ı�
                TextRange tr = new TextRange(terminalTextBox.Document.ContentEnd, terminalTextBox.Document.ContentEnd);
                tr.Text = text;
                tr.ApplyPropertyValue(TextElement.ForegroundProperty, foreground);
                tr.ApplyPropertyValue(TextElement.BackgroundProperty, background);

                // ��ȫ�ع�����ĩβ
                EnsureScrollToEnd(false);
                
                // �ָ���ǰ״̬
                _isUpdatingCaret = oldUpdatingState;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"����ı����ն�ʱ����: {ex.Message}");
                // �����������ԣ��Ա���Ǳ�ڵ�����ѭ��
            }
        }

        // ���һ��ȷ��������ĩβ�ķ���
        private void EnsureScrollToEnd(bool forceImmediate)
        {
            // ��������
            if (_isAutoScrolling)
                return;
                
            try
            {
                _isAutoScrolling = true;
                
                if (forceImmediate)
                {
                    // ��������
                    terminalTextBox.ScrollToEnd();
                    _isAutoScrolling = false;
                }
                else
                {
                    // �ӳٹ���������UI����
                    Dispatcher.InvokeAsync(() => {
                        try {
                            terminalTextBox.ScrollToEnd();
                        } 
                        catch (Exception ex) {
                            Debug.WriteLine($"�ӳٹ���ʱ����: {ex.Message}");
                        }
                        _isAutoScrolling = false;
                    }, DispatcherPriority.ContextIdle);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"������ĩβʱ����: {ex.Message}");
                _isAutoScrolling = false;
            }
        }

        /// <summary>
        /// ��ȡ��ǰ�ı�����
        /// </summary>
        private int GetTextLength()
        {
            try
            {
                return new TextRange(terminalTextBox.Document.ContentStart, terminalTextBox.Document.ContentEnd).Text.Length;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"��ȡ�ı�����ʱ����: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// ����ն�����
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
                Debug.WriteLine($"����ն�ʱ����: {ex.Message}");
            }
        }

        /// <summary>
        /// ֹͣ����
        /// </summary>
        public void StopProcess()
        {
            if (_process != null && !_process.HasExited)
            {
                try
                {
                    AppendText("\n������ֹ����...\n", new SolidColorBrush(Colors.Yellow), _defaultBackground);
                    _process.Kill();
                }
                catch (Exception ex)
                {
                    AppendText($"\n��ֹ����ʱ����: {ex.Message}\n", new SolidColorBrush(Colors.Red), _defaultBackground);
                }
            }
            else
            {
                AppendText("\nû���������еĽ���\n", new SolidColorBrush(Colors.Yellow), _defaultBackground);
            }
        }

        // �޸�Ԥ�����¼���֧�������ַ��練б��
        private void TerminalTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_process == null || _process.HasExited)
                return;

            // ���¼��̻ʱ���
            _lastKeyboardActivity = DateTime.Now;
            
            try
            {
                // ���ȴ��������ϼ�
                if (Keyboard.Modifiers == ModifierKeys.Control)
                {
                    // �����Ƽ�����ί�и�ר�ŵķ���
                    HandleControlKeyCommand(e.Key);
                    e.Handled = true;
                    return;
                }

                // ����������
                switch (e.Key)
                {
                    case Key.Enter:
                        SendInputChar('\n');
                        e.Handled = true;
                        break;

                    case Key.Tab:
                        // Tab�����⴦�� - ��ӵ�����������ʾ����������
                        _inputBuffer.Append('\t');
                        AppendText("    ", _defaultForeground, _defaultBackground); // ��ʾΪ4���ո�
                        e.Handled = true;
                        break;

                    // ����б�ܺͳ���·���ַ�
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
                        
                        
                    case Key.Oem5: // �����Ƿ�б��/����
                        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                            SendInputChar('|');
                        else
                            SendInputChar('\\');
                        e.Handled = true;
                        break;

                    // ����������������...
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
                            Debug.WriteLine($"�������ּ�ʱ����: {ex.Message}");
                            AppendText($"\n[����] �����������ʱ����\n", new SolidColorBrush(Colors.Red), _defaultBackground);
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
                            Debug.WriteLine($"����С�������ּ�ʱ����: {ex.Message}");
                            AppendText($"\n[����] ����С�������ּ�ʱ����\n", new SolidColorBrush(Colors.Red), _defaultBackground);
                        }
                        e.Handled = true;
                        break;
                    // ������ĸ��
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

                    // ����ո�
                    case Key.Space:
                        SendInputChar(' ');
                        e.Handled = true;
                        break;

                    // ���������ַ�
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
                Debug.WriteLine($"�����������: {ex.Message}");
            }
        }

        private void TerminalTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            // ��ֹ�����ؼ����ό��
            e.Handled = true;
        }

        // �޸�TextChanged�¼�������򣬱���ݹ����
        private void TerminalTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // ������ڴ���ANSI�ı�����¹�꣬��Ҫ�ظ�����
            if (_isProcessing || _isUpdatingCaret)
            {
                return;
            }

            try
            {
                // ȷ���ı��仯�������ĩβ
                EnsureScrollToEnd(false);
                
                // ����������UpdateCaretDisplay������TextChanged�¼�ʱ�Ÿ��¹��
                if (!_delayedInputTimer.IsEnabled) // ������ڵȴ������ӳ���
                {
                    _caretVisible = true;
                    UpdateCaretDisplay();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"�����ı��仯ʱ����: {ex.Message}");
            }
        }

        /// <summary>
        /// ���������ַ�������
        /// </summary>
        private void SendInputChar(char c)
        {
            if (_inputWriter == null || _process == null || _process.HasExited)
                return;

            // ���¼��̻ʱ���
            _lastKeyboardActivity = DateTime.Now;
            
            try
            {
                // ��ʼ������ֹ����
                _isProcessing = true;
                
                // ����Ƿ���ȷ���ַ�
                bool isConfirmChar = _waitingForInput && 
                    (c == 'y' || c == 'Y' || c == 'n' || c == 'N' || 
                     c == '��' || c == '��' || c == '0' || c == '1');
                
                // ����س������ַ�ȷ������
                if (c == '\n' || isConfirmChar)
                {
                    // ��ȡҪ���͵�����
                    string input = isConfirmChar ? c.ToString() : _inputBuffer.ToString();
                    _inputBuffer.Clear();

                    // ɾ����ǰ���
                    if (_caretRange != null)
                    {
                        _caretRange.Text = "";
                        _caretRange = null;
                    }

                    // ��ʾ�ַ�
                    AppendText(isConfirmChar ? c.ToString() : "", _defaultForeground, _defaultBackground);
                    
                    // ��ӻ��У�ʹ��һ����ʾ����������
                    AppendText("\n", _defaultForeground, _defaultBackground);

                    try 
                    {
                        // �������뵽����
                        if (isConfirmChar)
                        {
                            // ����ȷ���ַ������͵����ַ��������
                            _inputWriter.WriteLine(c);
                        }
                        else
                        {
                            // �����������룬ֱ�ӷ���������������
                            _inputWriter.WriteLine(input);
                        }
                        _inputWriter.Flush();
                        Debug.WriteLine($"[�ն�] ��������: '{input}'");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"�����������ʱ����: {ex.Message}");
                        AppendText($"[����] ��������ʧ��: {ex.Message}\n", new SolidColorBrush(Colors.Red), _defaultBackground);
                    }

                    // ��������״̬
                    _waitingForInput = false;
                    _lastDetectedPrompt = string.Empty;
                    _promptEndPosition = 0;
                    
                    // ȷ����������������
                    EnsureScrollToEnd(true);
                }
                else
                {
                    // �����ַ���ӵ�����������ʾ
                    _inputBuffer.Append(c);
                    
                    // ɾ�����
                    if (_caretRange != null)
                    {
                        _caretRange.Text = "";
                        _caretRange = null;
                    }
                    
                    // ��ʾ�ַ�
                    AppendText(c.ToString(), _defaultForeground, _defaultBackground);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"���������ַ�ʱ����: {ex.Message}");
                try 
                {
                    AppendText($"\n[����] �ַ�����ʧ��: {ex.Message}\n", new SolidColorBrush(Colors.Red), _defaultBackground);
                }
                catch { }
            }
            finally
            {
                // ��������״̬
                _isProcessing = false;
                
                // ���¹��λ��
                _caretVisible = true;
                UpdateCaretDisplay();
            }
        }

        /// <summary>
        /// ����Ctrl+C
        /// </summary>
        private void SendCtrlC()
        {
            if (_inputWriter == null || _process == null || _process.HasExited)
                return;

            try
            {
                // ��ʾ^C
                AppendText("^C\n", new SolidColorBrush(Colors.Red), _defaultBackground);
                _inputBuffer.Clear();

                // ����ETX (End of Text, Ctrl+C)�ַ�
                _inputWriter.Write('\x03');
                _inputWriter.Flush();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"����Ctrl+Cʱ����: {ex.Message}");
            }
        }

        // ��Ӵ˷�����������ʱ�ͷŽ���
        public void AllowFocusChange()
        {
            // ��ʱ�����Զ���ȡ����
            _caretBlinkTimer.Stop();
            
            // �ӳٺ�ָ�������Ϊ
            Dispatcher.InvokeAsync(() => {
                _caretBlinkTimer.Start();
            }, DispatcherPriority.ApplicationIdle);
        }
        
        // ��Ӹ����������˵��Ƿ��
        private bool IsMenuOpen()
        {
            return terminalTextBox.ContextMenu != null && terminalTextBox.ContextMenu.IsOpen;
        }

        /// <summary>
        /// ģ����ʾ������ʾ��
        /// </summary>
        /// <param name="prompt">��ʾ���ı������� "> "</param>
        public void DisplayPrompt(string prompt)
        {
            if (string.IsNullOrEmpty(prompt))
                prompt = "> ";
                
            AppendText(prompt, new SolidColorBrush(Colors.Green), _defaultBackground);
            _inputStartPosition = GetTextLength();
        }
        
        /// <summary>
        /// ���������ؼ���ʱ��ȡ����
        /// </summary>
        /// <param name="durationMs">�����ͷ�ʱ��(����)</param>
        public void ReleaseFocusTemporarily(int durationMs = 500)
        {
            SetFocusCaptureEnabled(false, durationMs);
        }

        /// <summary>
        /// �����ն��Ƿ�����Զ���ȡ����
        /// </summary>
        /// <param name="allow">�Ƿ������ȡ����</param>
        /// <param name="resumeAfterDelayMs">�ӳٻָ������ʱ��(����)��-1��ʾ���Զ��ָ�</param>
        public void SetFocusCaptureEnabled(bool allow, int resumeAfterDelayMs = -1)
        {
            _allowFocusCapture = allow;
            
            // ����������ӳٻָ�
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
        /// ǿ�ƻ�ȡ���㣨����Ҫȷ���ն˻�����뽹��ʱ���ã�
        /// </summary>
        public void ForceFocus()
        {
            _allowFocusCapture = true;
            terminalTextBox.Focus();
        }

        // ��Ӽ��Python������ʾ�ķ���
        private void DetectInputPrompt(string text)
        {
            // ��ֹ����
            if (_isWaitingForPromptProcessing)
                return;

            // ����ֹͣ�κ����ڽ��е��ӳ������ʱ��
            if (_delayedInputTimer.IsEnabled)
            {
                _delayedInputTimer.Stop();
            }

            // ʶ��ȷ����ʾ
            bool isConfirmPrompt = 
                Regex.IsMatch(text, @"\([yYnN](/[yYnN])?\):\s*$") ||
                Regex.IsMatch(text, @"\([yYnN]\)[^:\r\n]*$") ||
                text.Contains("(y/N)") ||
                text.Contains("(Y/n)") ||
                Regex.IsMatch(text, @"(?i)ȷ��.*��\?.*\s*$") ||
                Regex.IsMatch(text, @"(?i)�Ƿ�.*\?.*\s*$") ||
                Regex.IsMatch(text, @"(?i)confirm\?.*\s*$");

            // ʶ������һ����ʾ
            bool isGeneralPrompt = 
                Regex.IsMatch(text, @"(?:^|[\r\n])[^:\r\n]*[>:]\s*$") || 
                Regex.IsMatch(text, @"(?i)(?:input|enter|������|ѡ��|choice)[^:\r\n]*:\s*$");

            if (isConfirmPrompt || isGeneralPrompt)
            {
                // ��¼��ʾ�ı��Ա����
                _lastDetectedPrompt = text.Trim();
                Debug.WriteLine($"[�ն�] ��⵽��ʾ: '{_lastDetectedPrompt}' [ȷ����ʾ:{isConfirmPrompt}]");
                
                // ���ô����־����ֹ����
                _isWaitingForPromptProcessing = true;
                
                // ������뻺������׼��������
                _inputBuffer.Clear();
                
                // �ӳټ�������ģʽ������ʾ��ȫ��ʾ��ʱ��
                // ȷ����ʾ��Ҫ�������ӳ٣���Ϊ����ͨ�����������ı�
                _waitingForInput = false; // ����Ϊfalse���ӳٺ�����Ϊtrue
                _delayedInputTimer.Interval = TimeSpan.FromMilliseconds(isConfirmPrompt ? 500 : 300);
                _delayedInputTimer.Start();
            }
        }



        /// <summary>
        /// ���������ϼ�(��Ctrl+C, Ctrl+Z��)
        /// </summary>
        private void HandleControlKeyCommand(Key key)
        {
            if (_process == null || _process.HasExited || _inputWriter == null)
                return;

            try
            {
                switch (key)
                {
                    case Key.C: // Ctrl+C - �ж�
                        // ��ʾ^C
                        AppendText("^C\n", new SolidColorBrush(Colors.Red), _defaultBackground);
                        _inputBuffer.Clear();
                        
                        // ����ETX (End of Text, Ctrl+C)�ַ�
                        _inputWriter.Write('\x03');
                        _inputWriter.Flush();
                        StopProcess();
                        Debug.WriteLine("[�ն�] �����ź�: Ctrl+C (SIGINT)");
                        break;
                        
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"������Ƽ�ʱ����: {ex.Message}");
                AppendText($"\n[����] ���Ƽ�����ʧ��: {ex.Message}\n", new SolidColorBrush(Colors.Red), _defaultBackground);
            }
        }

        // ����������ʱ���Ĵ�����
        private void OutputProcessTimer_Tick(object sender, EventArgs e)
        {
            _outputProcessTimer.Stop();
            
            try
            {
                // һ���Դ���ȫ����������
                string bufferedOutput = _outputBuffer.ToString();
                _outputBuffer.Clear();
                
                if (!string.IsNullOrEmpty(bufferedOutput))
                {
                    // �����������������
                    ProcessFullOutput(bufferedOutput);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"���������ʱ����: {ex.Message}");
            }
        }

        // ����������������
        private void ProcessFullOutput(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;
                
            _isProcessing = true;
            
            try
            {
                // �ȴ���ANSIת������
                string pattern = @"(\x1b\[\d*(?:;\d+)*[A-Za-z])";
                string[] parts = Regex.Split(text, pattern);

                SolidColorBrush currentForeground = _defaultForeground;
                SolidColorBrush currentBackground = _defaultBackground;

                foreach (string part in parts)
                {
                    if (part.StartsWith("\x1b["))
                    {
                        // ����ANSIת������
                        ParseAnsiCode(part, ref currentForeground, ref currentBackground);
                    }
                    else if (!string.IsNullOrEmpty(part))
                    {
                        // �����ͨ�ı�
                        AppendText(part, currentForeground, currentBackground);
                    }
                }
                
                // ������ȫ������󣬼���Ƿ�����ʾ
                DetectPromptInFullText(text);
                
                _inputStartPosition = GetTextLength();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"�����������ʱ����: {ex.Message}");
            }
            finally
            {
                _isProcessing = false;
            }
        }

        // ��������ı������ʾ���·���
        private void DetectPromptInFullText(string fullText)
        {
            if (string.IsNullOrEmpty(fullText))
                return;

            try
            {
                // ֹͣ���ڽ��е����봦��
                if (_delayedInputTimer.IsEnabled)
                {
                    _delayedInputTimer.Stop();
                }
                
                // ��������״̬
                _isWaitingForPromptProcessing = false;
                _waitingForInput = false;

                // ʹ�ø��ϸ����ʾƥ�����
                Match menuPromptMatch = Regex.Match(fullText, @"����������ѡ��\s*>\s*$", RegexOptions.RightToLeft);
                Match confirmPromptMatch = Regex.Match(fullText, @"\(y/N\):\s*$", RegexOptions.RightToLeft);
                Match generalPromptMatch = Regex.Match(fullText, @"[>:]\s*$", RegexOptions.RightToLeft);
                
                bool hasPrompt = menuPromptMatch.Success || confirmPromptMatch.Success || generalPromptMatch.Success;
                
                if (hasPrompt)
                {
                    // ��¼��ʾ�ı�
                    _lastDetectedPrompt = hasPrompt ? fullText.Substring(Math.Max(0, fullText.Length - 30)).Trim() : "";
                    
                    Debug.WriteLine($"[�ն�] �������ı��м�⵽��ʾ: '{_lastDetectedPrompt}'");
                    
                    // ������뻺����
                    _inputBuffer.Clear();
                    
                    // �����ӳ٣�ȷ��UI��ʱ����ȫ��Ⱦ��ʾ
                    _isWaitingForPromptProcessing = true;
                    _delayedInputTimer.Interval = TimeSpan.FromMilliseconds(confirmPromptMatch.Success ? 800 : 600);
                    _delayedInputTimer.Start();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"��������ı��е���ʾʱ����: {ex.Message}");
            }
        }

        // �޸�DelayedInputTimer_Tick������ȷ�������������ȷλ��
        private void DelayedInputTimer_Tick(object sender, EventArgs e)
        {
            _delayedInputTimer.Stop();
            _isWaitingForPromptProcessing = false;
            
            try
            {
                // ȷ��������һ���ȶ�������״̬
                _waitingForInput = true;
                
                // ��¼��ǰ�ı�������Ϊ��ʾ����λ��
                _promptEndPosition = GetTextLength();
                
                // �ڵ����м�¼����
                Debug.WriteLine($"[�ն�] ��ʾ������ɣ�׼���������룬��ʾλ��: {_promptEndPosition}");
                
                // �ӳ�һ֡����ʾ��꣬ȷ��UI����ȫ����
                Dispatcher.InvokeAsync(() => {
                    // ǿ�ƹ�����ĩβ
                    EnsureScrollToEnd(true);
                    
                    // ��ʾ���
                    _caretVisible = true;
                    UpdateCaretDisplay();
                    
                    // ȷ���������ն���
                    ForceFocus();
                }, DispatcherPriority.Render);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"�ӳ����봦��ʱ����: {ex.Message}");
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
                    catch { /* ���Դ��� */ }
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