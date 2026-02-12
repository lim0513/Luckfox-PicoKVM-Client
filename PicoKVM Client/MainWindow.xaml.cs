using System.Windows;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace PicoKVM_Client    
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        // 低级键盘钩子常量
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        // 键盘钩子委托和句柄
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private readonly LowLevelKeyboardProc _proc;
        private IntPtr _hookID = IntPtr.Zero;
        private bool _isInputCaptured = false;

        // 连接状态
        private string _kvmUrl = string.Empty;
        private bool _isConnected = false;

        // 被钩子拦截的修饰键状态（GetAsyncKeyState对被拦截的键不可靠）
        private bool _winKeyDown = false;
        private bool _altKeyDown = false;

        // Windows API导入
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        public MainWindow()
        {
            InitializeComponent();
            _proc = HookCallback;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            txtStatusBar.Text = "正在初始化WebView2...";

            try
            {
                // 预初始化WebView2，避免连接时等待
                await webView.EnsureCoreWebView2Async();
                txtStatusBar.Text = "就绪 - 输入KVM地址并点击连接，按F11切换输入捕获";
            }
            catch (Exception ex)
            {
                txtStatusBar.Text = $"WebView2初始化失败: {ex.Message}";
            }
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            DisconnectFromKvm();
            UninstallKeyboardHook();
        }

        #region 连接管理

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            await ConnectToKvmAsync();
        }

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            DisconnectFromKvm();
        }

        private async Task ConnectToKvmAsync()
        {
            _kvmUrl = txtKvmUrl.Text.Trim();
            if (string.IsNullOrEmpty(_kvmUrl))
            {
                MessageBox.Show("请输入KVM地址", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                txtStatus.Text = "正在连接...";
                txtStatus.Visibility = Visibility.Visible;
                
                // 确保WebView2已初始化（正常情况下在Window_Loaded已完成）
                if (webView.CoreWebView2 == null)
                {
                    await webView.EnsureCoreWebView2Async();
                }
                
                // 导航到KVM页面
                webView.CoreWebView2.Navigate(_kvmUrl);
                
                _isConnected = true;
                webView.Visibility = Visibility.Visible;
                btnConnect.IsEnabled = false;
                btnDisconnect.IsEnabled = true;
                txtKvmUrl.IsEnabled = false;
                txtStatus.Visibility = Visibility.Collapsed;
                txtStatusBar.Text = $"已连接到 {_kvmUrl} - WebView2模式，按F11捕获输入";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"连接失败: {ex.Message}\n\n请确保已安装WebView2运行时", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                txtStatus.Text = "连接失败";
                DisconnectFromKvm();
            }
        }

        private void DisconnectFromKvm()
        {
            if (webView?.CoreWebView2 != null)
            {
                webView.CoreWebView2.Navigate("about:blank");
            }
            webView.Visibility = Visibility.Collapsed;

            _isConnected = false;
            btnConnect.IsEnabled = true;
            btnDisconnect.IsEnabled = false;
            txtKvmUrl.IsEnabled = true;
            chkCaptureInput.IsChecked = false;
            txtStatus.Text = "未连接";
            txtStatus.Visibility = Visibility.Visible;
            txtStatusBar.Text = "已断开连接";
        }

        #endregion

        #region HID转发 (通过WebView2 JavaScript注入)

        // 通过WebView2注入JavaScript模拟单个键盘事件
        private void SendKeyToWebView(int vkCode, bool isDown)
        {
            string jsCode = VkCodeToJsCode(vkCode);
            string jsKey = VkCodeToJsKey(vkCode);
            if (string.IsNullOrEmpty(jsCode)) return;

            // 被钩子拦截的键用手动跟踪状态，其他用GetAsyncKeyState
            bool alt = _altKeyDown || (GetAsyncKeyState(0x12) & 0x8000) != 0;
            bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
            bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
            bool meta = _winKeyDown;

            // 如果当前按下的就是修饰键本身，设为正确的值
            if (vkCode is 0x12 or 0xA4 or 0xA5) alt = isDown;
            if (vkCode is 0x11 or 0xA2 or 0xA3) ctrl = isDown;
            if (vkCode is 0x10 or 0xA0 or 0xA1) shift = isDown;
            if (vkCode is 0x5B or 0x5C) meta = isDown;

            string eventType = isDown ? "keydown" : "keyup";
            string script = $@"document.dispatchEvent(new KeyboardEvent('{eventType}',{{code:'{jsCode}',key:'{jsKey}',keyCode:{vkCode},altKey:{(alt ? "true" : "false")},ctrlKey:{(ctrl ? "true" : "false")},shiftKey:{(shift ? "true" : "false")},metaKey:{(meta ? "true" : "false")},bubbles:true,cancelable:true}}));";

            Debug.WriteLine($"[JS] {eventType}: {jsCode}");

            Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    if (webView?.CoreWebView2 != null)
                        await webView.CoreWebView2.ExecuteScriptAsync(script);
                }
                catch { }
            });
        }

        // VK码 → JavaScript KeyboardEvent.code
        private static string VkCodeToJsCode(int vkCode)
        {
            return vkCode switch
            {
                // 修饰键
                0x5B => "MetaLeft",
                0x5C => "MetaRight",
                0x12 or 0xA4 => "AltLeft",
                0xA5 => "AltRight",
                0x11 or 0xA2 => "ControlLeft",
                0xA3 => "ControlRight",
                0x10 or 0xA0 => "ShiftLeft",
                0xA1 => "ShiftRight",
                // 功能键
                0x09 => "Tab",
                0x1B => "Escape",
                0x0D => "Enter",
                0x20 => "Space",
                0x08 => "Backspace",
                0x2E => "Delete",
                0x2D => "Insert",
                // F键
                0x70 => "F1", 0x71 => "F2", 0x72 => "F3", 0x73 => "F4",
                0x74 => "F5", 0x75 => "F6", 0x76 => "F7", 0x77 => "F8",
                0x78 => "F9", 0x79 => "F10", 0x7A => "F11", 0x7B => "F12",
                // 方向键
                0x25 => "ArrowLeft", 0x26 => "ArrowUp",
                0x27 => "ArrowRight", 0x28 => "ArrowDown",
                // 导航
                0x24 => "Home", 0x23 => "End",
                0x21 => "PageUp", 0x22 => "PageDown",
                // 字母 A-Z
                >= 0x41 and <= 0x5A => $"Key{(char)vkCode}",
                // 数字 0-9
                >= 0x30 and <= 0x39 => $"Digit{(char)vkCode}",
                // 符号
                0xBA => "Semicolon", 0xBB => "Equal", 0xBC => "Comma",
                0xBD => "Minus", 0xBE => "Period", 0xBF => "Slash",
                0xC0 => "Backquote", 0xDB => "BracketLeft",
                0xDC => "Backslash", 0xDD => "BracketRight",
                0xDE => "Quote",
                // 其他
                0x14 => "CapsLock", 0x90 => "NumLock", 0x91 => "ScrollLock",
                0x2C => "PrintScreen", 0x13 => "Pause",
                0x5D => "ContextMenu",
                _ => ""
            };
        }

        // VK码 → JavaScript KeyboardEvent.key
        private static string VkCodeToJsKey(int vkCode)
        {
            return vkCode switch
            {
                0x5B or 0x5C => "Meta",
                0x12 or 0xA4 or 0xA5 => "Alt",
                0x11 or 0xA2 or 0xA3 => "Control",
                0x10 or 0xA0 or 0xA1 => "Shift",
                0x09 => "Tab",
                0x1B => "Escape",
                0x0D => "Enter",
                0x20 => " ",
                0x08 => "Backspace",
                0x2E => "Delete",
                0x2D => "Insert",
                0x70 => "F1", 0x71 => "F2", 0x72 => "F3", 0x73 => "F4",
                0x74 => "F5", 0x75 => "F6", 0x76 => "F7", 0x77 => "F8",
                0x78 => "F9", 0x79 => "F10", 0x7A => "F11", 0x7B => "F12",
                0x25 => "ArrowLeft", 0x26 => "ArrowUp",
                0x27 => "ArrowRight", 0x28 => "ArrowDown",
                0x24 => "Home", 0x23 => "End",
                0x21 => "PageUp", 0x22 => "PageDown",
                >= 0x41 and <= 0x5A => ((char)(vkCode + 32)).ToString(), // 小写字母
                >= 0x30 and <= 0x39 => ((char)vkCode).ToString(),
                0xBA => ";", 0xBB => "=", 0xBC => ",",
                0xBD => "-", 0xBE => ".", 0xBF => "/",
                0xC0 => "`", 0xDB => "[",
                0xDC => "\\", 0xDD => "]",
                0xDE => "'",
                0x14 => "CapsLock", 0x90 => "NumLock", 0x91 => "ScrollLock",
                0x2C => "PrintScreen", 0x13 => "Pause",
                0x5D => "ContextMenu",
                _ => ""
            };
        }

        #endregion

        #region 输入捕获

        private void ChkCaptureInput_Changed(object sender, RoutedEventArgs e)
        {
            ToggleInputCapture();
        }

        private void ToggleInputCapture()
        {
            if (!_isConnected)
            {
                if (chkCaptureInput.IsChecked == true)
                {
                    chkCaptureInput.IsChecked = false;
                    MessageBox.Show("请先连接到KVM设备", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return;
            }

            _isInputCaptured = chkCaptureInput.IsChecked == true;

            if (_isInputCaptured)
            {
                InstallKeyboardHook();
                webView.Focus();
                txtStatusBar.Text = "✓ 输入已激活 - 所有输入发送到远程 (按F11退出)";
            }
            else
            {
                UninstallKeyboardHook();
                _winKeyDown = false;
                _altKeyDown = false;
                txtStatusBar.Text = $"已连接到 {_kvmUrl} (按F11激活输入)";
            }
        }

        private void InstallKeyboardHook()
        {
            if (_hookID != IntPtr.Zero) return;

            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            if (curModule != null)
            {
                _hookID = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        private void UninstallKeyboardHook()
        {
            if (_hookID != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookID);
                _hookID = IntPtr.Zero;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _isInputCaptured)
            {
                int vkCode = Marshal.ReadInt32(lParam);
                bool isKeyDown = (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN);
                
                // F11 始终用于切换捕获模式，不转发
                if (vkCode == 0x7A && isKeyDown) // F11 = 0x7A
                {
                    Dispatcher.Invoke(() => chkCaptureInput.IsChecked = false);
                    return (IntPtr)1;
                }

                // 手动跟踪被拦截的修饰键状态
                bool isWinKey = (vkCode == 0x5B || vkCode == 0x5C);
                bool isAltKey = (vkCode is 0x12 or 0xA4 or 0xA5); // VK_MENU, VK_LMENU, VK_RMENU

                if (isWinKey) _winKeyDown = isKeyDown;
                if (isAltKey) _altKeyDown = isKeyDown;

                // 需要拦截的情况（会被系统抢走的快捷键）：
                // 1. Win键本身
                // 2. Win按住时的任何键（Win+E, Win+D, Win+Tab, Win+方向键...）
                // 3. Alt+Tab / Alt+Esc / Alt+F4（这些会被系统抢走）
                bool shouldIntercept = false;

                if (isWinKey)
                {
                    shouldIntercept = true;
                }
                else if (_winKeyDown)
                {
                    shouldIntercept = true;
                }
                else if (_altKeyDown && (vkCode == 0x09 || vkCode == 0x1B || vkCode == 0x73))
                {
                    // Alt+Tab, Alt+Esc, Alt+F4
                    shouldIntercept = true;
                }

                if (shouldIntercept)
                {
                    string jsCode = VkCodeToJsCode(vkCode);
                    if (!string.IsNullOrEmpty(jsCode))
                    {
                        SendKeyToWebView(vkCode, isKeyDown);
                    }
                    return (IntPtr)1;
                }

                // 其他按键不拦截，正常传递给WebView2（浏览器自己处理）
                return CallNextHookEx(_hookID, nCode, wParam, lParam);
            }

            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F11)
            {
                chkCaptureInput.IsChecked = !chkCaptureInput.IsChecked;
                e.Handled = true;
            }
        }

        private void Window_KeyUp(object sender, KeyEventArgs e)
        {
        }

        #endregion
    }
}