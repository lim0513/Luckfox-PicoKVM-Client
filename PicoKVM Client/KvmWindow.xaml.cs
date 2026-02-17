using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace PicoKVM_Client;

public partial class KvmWindow : Window
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookID = IntPtr.Zero;
    private bool _isInputCaptured;
    private bool _isFullscreen;
    private WindowState _previousWindowState;
    private Rect _restoreBounds;
    private bool _winKeyDown;
    private bool _altKeyDown;

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

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private readonly string _kvmUrl;
    private readonly string _password;

    public KvmWindow(string kvmUrl, string password)
    {
        InitializeComponent();
        _proc = HookCallback;
        _kvmUrl = kvmUrl;
        _password = password;
        txtTitle.Text = $"PicoKVM - {kvmUrl}";
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await webView.EnsureCoreWebView2Async();

        // 如果有密码，先调用 /auth/login-local 获取 authToken cookie
        if (!string.IsNullOrEmpty(_password))
        {
            try
            {
                var authToken = await LoginAsync(_kvmUrl, _password);
                if (!string.IsNullOrEmpty(authToken))
                {
                    // 将 authToken cookie 注入 WebView2
                    var uri = new Uri(_kvmUrl);
                    var cookie = webView.CoreWebView2.CookieManager.CreateCookie(
                        "authToken", authToken, uri.Host, "/");
                    webView.CoreWebView2.CookieManager.AddOrUpdateCookie(cookie);
                }
            }
            catch
            {
                // 登录失败，仍然打开页面让用户手动输入
            }
        }

        webView.CoreWebView2.Navigate(_kvmUrl);

        // 页面加载后注入 CSS/JS，隐藏其他元素只显示 video（假全屏）
        webView.CoreWebView2.NavigationCompleted += OnInjectVideoOnly;
    }

    private bool _videoOnlyInjected;

    private async void OnInjectVideoOnly(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || _videoOnlyInjected) return;

        string js = """
            (function tryInject(retries) {
                // 检查页面是否被隐藏（窗口关闭时会触发）
                if (document.hidden || !document.body) return;
                
                var video = document.querySelector('video');
                if (video && video.readyState >= 2 && video.videoWidth > 0) {
                    document.body.innerHTML = '';
                    document.body.appendChild(video);
                    var style = document.createElement('style');
                    style.textContent = `
                        *, *::before, *::after { margin:0; padding:0; }
                        html, body { width:100%; height:100%; overflow:hidden; background:#000; }
                        video { display:block; width:100%; height:100%; object-fit:contain; background:#000; }
                    `;
                    document.head.appendChild(style);
                    video.play().catch(function(){});
                    return;
                }
                if (retries > 0) {
                    setTimeout(function(){ tryInject(retries - 1); }, 500);
                }
            })(120);
            """;

        try
        {
            await webView.CoreWebView2.ExecuteScriptAsync(js);
            _videoOnlyInjected = true;
        }
        catch { }
    }

    private static async Task<string?> LoginAsync(string baseUrl, string password)
    {
        using var handler = new HttpClientHandler { UseCookies = true };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };

        var json = JsonSerializer.Serialize(new { password });
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PostAsync($"{baseUrl.TrimEnd('/')}/auth/login-local", content);

        if (!response.IsSuccessStatusCode) return null;

        // 从 Set-Cookie 中提取 authToken
        if (handler.CookieContainer.GetCookies(new Uri(baseUrl)) is { } cookies)
        {
            foreach (System.Net.Cookie c in cookies)
            {
                if (c.Name == "authToken")
                    return c.Value;
            }
        }

        return null;
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        UninstallKeyboardHook();
        if (webView.CoreWebView2 != null)
            webView.CoreWebView2.Navigate("about:blank");
    }

    #region 窗口控制

    private void BtnMinimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void BtnFullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (_isFullscreen) ExitFullscreen(); else EnterFullscreen();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void TglTopmost_Changed(object sender, RoutedEventArgs e)
    {
        Topmost = tglTopmost.IsChecked == true;
    }

    private void EnterFullscreen()
    {
        _previousWindowState = WindowState;
        _restoreBounds = new Rect(Left, Top, Width, Height);

        // 获取当前窗口所在显示器的完整尺寸（含任务栏区域）
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var hMonitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(hMonitor, ref mi);

        // DPI 缩放
        var source = PresentationSource.FromVisual(this);
        double dpiX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
        double dpiY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;

        WindowState = WindowState.Normal;
        Left = mi.rcMonitor.Left * dpiX;
        Top = mi.rcMonitor.Top * dpiY;
        Width = (mi.rcMonitor.Right - mi.rcMonitor.Left) * dpiX;
        Height = (mi.rcMonitor.Bottom - mi.rcMonitor.Top) * dpiY;

        titleBar.Visibility = Visibility.Collapsed;
        rootBorder.Margin = new Thickness(0);
        rootBorder.CornerRadius = new CornerRadius(0);
        ResizeMode = ResizeMode.NoResize;
        iconFullscreen.Kind = MaterialDesignThemes.Wpf.PackIconKind.WindowRestore;
        _isFullscreen = true;
    }

    private void ExitFullscreen()
    {
        Left = _restoreBounds.Left;
        Top = _restoreBounds.Top;
        Width = _restoreBounds.Width;
        Height = _restoreBounds.Height;
        WindowState = _previousWindowState;

        titleBar.Visibility = Visibility.Visible;
        rootBorder.Margin = new Thickness(4);
        rootBorder.CornerRadius = new CornerRadius(8);
        ResizeMode = ResizeMode.CanResizeWithGrip;
        iconFullscreen.Kind = MaterialDesignThemes.Wpf.PackIconKind.WindowMaximize;
        _isFullscreen = false;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F12)
        {
            if (_isFullscreen) ExitFullscreen(); else EnterFullscreen();
            e.Handled = true;
        }
    }

    #endregion

    #region 输入捕获

    private void Window_Activated(object? sender, EventArgs e)
    {
        if (!_isInputCaptured)
        {
            _isInputCaptured = true;
            InstallKeyboardHook();
            webView.Focus();
            txtCaptureStatus.Text = "✓ 输入已捕获";
        }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_isInputCaptured)
        {
            _isInputCaptured = false;
            UninstallKeyboardHook();
            _winKeyDown = false;
            _altKeyDown = false;
            txtCaptureStatus.Text = "";
        }
    }

    private void InstallKeyboardHook()
    {
        if (_hookID != IntPtr.Zero) return;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        if (curModule != null)
            _hookID = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(curModule.ModuleName), 0);
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
            bool isKeyDown = wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN;

            // F12 交给 WPF 窗口处理（全屏切换）
            if (vkCode == 0x7B) // F12
            {
                return CallNextHookEx(_hookID, nCode, wParam, lParam);
            }

            bool isWinKey = vkCode is 0x5B or 0x5C;
            bool isAltKey = vkCode is 0x12 or 0xA4 or 0xA5;
            bool isCtrlKey = vkCode is 0x11 or 0xA2 or 0xA3;
            bool isShiftKey = vkCode is 0x10 or 0xA0 or 0xA1;

            // 更新修饰键状态
            if (isWinKey) _winKeyDown = isKeyDown;
            if (isAltKey) _altKeyDown = isKeyDown;

            // 修饰键本身不拦截，让 WebView2 正常接收（保证组合键正常工作）
            if (isCtrlKey || isShiftKey || isAltKey)
            {
                return CallNextHookEx(_hookID, nCode, wParam, lParam);
            }

            // 只拦截 Win 键相关的组合键
            bool shouldIntercept = isWinKey || _winKeyDown
                || (_altKeyDown && vkCode is 0x09 or 0x1B or 0x73); // Alt+Tab, Alt+Esc, Alt+F4

            if (shouldIntercept)
            {
                if (!string.IsNullOrEmpty(VkCodeToJsCode(vkCode)))
                    SendKeyToWebView(vkCode, isKeyDown);
                return (IntPtr)1;
            }

            // 其他按键放行，让 WebView2 正常处理
            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }
        return CallNextHookEx(_hookID, nCode, wParam, lParam);
    }

    #endregion

    #region HID 转发

    private void SendKeyToWebView(int vkCode, bool isDown)
    {
        string jsCode = VkCodeToJsCode(vkCode);
        string jsKey = VkCodeToJsKey(vkCode);
        if (string.IsNullOrEmpty(jsCode)) return;

        bool alt = _altKeyDown || (GetAsyncKeyState(0x12) & 0x8000) != 0;
        bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
        bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
        bool meta = _winKeyDown;

        if (vkCode is 0x12 or 0xA4 or 0xA5) alt = isDown;
        if (vkCode is 0x11 or 0xA2 or 0xA3) ctrl = isDown;
        if (vkCode is 0x10 or 0xA0 or 0xA1) shift = isDown;
        if (vkCode is 0x5B or 0x5C) meta = isDown;

        string eventType = isDown ? "keydown" : "keyup";
        string script = $@"document.dispatchEvent(new KeyboardEvent('{eventType}',{{code:'{jsCode}',key:'{jsKey}',keyCode:{vkCode},altKey:{(alt ? "true" : "false")},ctrlKey:{(ctrl ? "true" : "false")},shiftKey:{(shift ? "true" : "false")},metaKey:{(meta ? "true" : "false")},bubbles:true,cancelable:true}}));";

        Dispatcher.BeginInvoke(async () =>
        {
            try { if (webView?.CoreWebView2 != null) await webView.CoreWebView2.ExecuteScriptAsync(script); }
            catch { }
        });
    }

    private static string VkCodeToJsCode(int vkCode) => vkCode switch
    {
        0x5B => "MetaLeft", 0x5C => "MetaRight",
        0x12 or 0xA4 => "AltLeft", 0xA5 => "AltRight",
        0x11 or 0xA2 => "ControlLeft", 0xA3 => "ControlRight",
        0x10 or 0xA0 => "ShiftLeft", 0xA1 => "ShiftRight",
        0x09 => "Tab", 0x1B => "Escape", 0x0D => "Enter", 0x20 => "Space",
        0x08 => "Backspace", 0x2E => "Delete", 0x2D => "Insert",
        0x70 => "F1", 0x71 => "F2", 0x72 => "F3", 0x73 => "F4",
        0x74 => "F5", 0x75 => "F6", 0x76 => "F7", 0x77 => "F8",
        0x78 => "F9", 0x79 => "F10", 0x7A => "F11", 0x7B => "F12",
        0x25 => "ArrowLeft", 0x26 => "ArrowUp", 0x27 => "ArrowRight", 0x28 => "ArrowDown",
        0x24 => "Home", 0x23 => "End", 0x21 => "PageUp", 0x22 => "PageDown",
        >= 0x41 and <= 0x5A => $"Key{(char)vkCode}",
        >= 0x30 and <= 0x39 => $"Digit{(char)vkCode}",
        0xBA => "Semicolon", 0xBB => "Equal", 0xBC => "Comma",
        0xBD => "Minus", 0xBE => "Period", 0xBF => "Slash",
        0xC0 => "Backquote", 0xDB => "BracketLeft",
        0xDC => "Backslash", 0xDD => "BracketRight", 0xDE => "Quote",
        0x14 => "CapsLock", 0x90 => "NumLock", 0x91 => "ScrollLock",
        0x2C => "PrintScreen", 0x13 => "Pause", 0x5D => "ContextMenu",
        _ => ""
    };

    private static string VkCodeToJsKey(int vkCode) => vkCode switch
    {
        0x5B or 0x5C => "Meta", 0x12 or 0xA4 or 0xA5 => "Alt",
        0x11 or 0xA2 or 0xA3 => "Control", 0x10 or 0xA0 or 0xA1 => "Shift",
        0x09 => "Tab", 0x1B => "Escape", 0x0D => "Enter", 0x20 => " ",
        0x08 => "Backspace", 0x2E => "Delete", 0x2D => "Insert",
        0x70 => "F1", 0x71 => "F2", 0x72 => "F3", 0x73 => "F4",
        0x74 => "F5", 0x75 => "F6", 0x76 => "F7", 0x77 => "F8",
        0x78 => "F9", 0x79 => "F10", 0x7A => "F11", 0x7B => "F12",
        0x25 => "ArrowLeft", 0x26 => "ArrowUp", 0x27 => "ArrowRight", 0x28 => "ArrowDown",
        0x24 => "Home", 0x23 => "End", 0x21 => "PageUp", 0x22 => "PageDown",
        >= 0x41 and <= 0x5A => ((char)(vkCode + 32)).ToString(),
        >= 0x30 and <= 0x39 => ((char)vkCode).ToString(),
        0xBA => ";", 0xBB => "=", 0xBC => ",", 0xBD => "-", 0xBE => ".", 0xBF => "/",
        0xC0 => "`", 0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xDE => "'",
        0x14 => "CapsLock", 0x90 => "NumLock", 0x91 => "ScrollLock",
        0x2C => "PrintScreen", 0x13 => "Pause", 0x5D => "ContextMenu",
        _ => ""
    };

    #endregion
}
