# Luckfox PicoKVM Client

一个专为 [Luckfox PicoKVM](https://wiki.luckfox.com/Luckfox-PicoKVM/) 设计的 Windows 桌面客户端，提供纯净的远程 KVM 画面和完整的键盘输入捕获能力。

基于 WPF + WebView2 + Material Design 构建。

## ✨ 功能特性

### 🔗 智能连接
- 自动检测目标地址是否为 Luckfox PicoKVM 设备
- 支持密码自动登录（调用 `/auth/login-local` API，自动注入 authToken）
- 连接地址和密码本地持久化保存

### 🖥️ 纯净视频画面
- 页面加载后自动注入 CSS/JS，隐藏 Web UI 中的导航栏、侧边栏等元素
- 仅显示 `<video>` 标签内容，实现沉浸式远程桌面体验
- 等待视频流就绪后再注入，避免白屏闪烁

### ⌨️ 全局键盘捕获
- 使用 Windows 低级键盘钩子（`WH_KEYBOARD_LL`）拦截系统级快捷键
- 捕获 `Win`、`Alt+Tab`、`Alt+F4` 等组合键并转发至远程设备
- 窗口激活时自动捕获，失焦时自动释放

### 🔲 真全屏模式
- `F12` 快捷键或点击按钮切换全屏
- 完全覆盖任务栏和开始菜单（通过 `MonitorFromWindow` 获取显示器物理尺寸）
- 支持多显示器，自动适配 DPI 缩放

### 📌 窗口置顶
- 标题栏置顶按钮，一键切换窗口 Always on Top
- 选中/未选中状态通过 Material Design 主题色区分

### 🎨 Material Design 暗色主题
- 基于 [MaterialDesignInXAML](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit) 构建
- 暗色主题 + Indigo/Amber 配色
- 无边框自定义窗口 + 圆角设计

## 📋 系统要求

- Windows 10 1809+ / Windows 11
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)（Windows 11 已内置）

## 🚀 快速开始

1. 克隆仓库并构建：
   ```bash
   git clone https://github.com/lim0513/PicoKVM-Client.git
   cd PicoKVM-Client
   dotnet run --project "PicoKVM Client"
   ```

2. 输入 PicoKVM 设备的 IP 地址和密码，点击 **连接**。

3. 连接成功后自动显示远程桌面画面。

## ⌨️ 快捷键

| 快捷键 | 功能 |
|--------|------|
| `F12` | 切换全屏（覆盖任务栏） |

## 🏗️ 技术栈

| 组件 | 技术 |
|------|------|
| 框架 | WPF (.NET 10) |
| 视频渲染 | WebView2 (WebRTC) |
| UI 主题 | Material Design In XAML Toolkit |
| 键盘捕获 | Win32 Low-Level Keyboard Hook |
| 设备认证 | HTTP API (`/auth/login-local`) + Cookie 注入 |

## 📄 许可证

MIT License

## 🙏 致谢

- [Luckfox PicoKVM](https://github.com/luckfox-eng29/kvm) — 基于 JetKVM 的开源 IP KVM 固件
- [MaterialDesignInXAML](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit) — WPF Material Design 主题库
- [WebView2](https://developer.microsoft.com/microsoft-edge/webview2/) — 微软现代 Web 渲染引擎
