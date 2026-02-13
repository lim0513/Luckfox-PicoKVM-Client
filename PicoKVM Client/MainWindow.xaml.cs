using System.Net.Http;
using System.Windows;

namespace PicoKVM_Client;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private KvmWindow? _kvmWindow;

    public MainWindow()
    {
        InitializeComponent();
        txtKvmUrl.Text = _settings.KvmUrl;
        pwdPassword.Password = _settings.Password;
    }

    private async void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        var url = txtKvmUrl.Text.Trim();
        if (string.IsNullOrEmpty(url))
        {
            MessageBox.Show("请输入KVM地址", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "http://" + url;

        _settings.KvmUrl = url;
        _settings.Password = pwdPassword.Password;
        _settings.Save();
        txtKvmUrl.Text = url;

        if (_kvmWindow != null)
        {
            _kvmWindow.Activate();
            return;
        }

        btnConnect.IsEnabled = false;
        btnConnect.Content = "检测中...";

        try
        {
            if (!await IsPicoKvmAsync(url))
            {
                MessageBox.Show("目标地址不是 LuckFox PicoKVM 设备，无法连接。",
                    "连接失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _kvmWindow = new KvmWindow(url, pwdPassword.Password) { Owner = this };
            _kvmWindow.Closed += (_, _) => _kvmWindow = null;
            _kvmWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法连接到 {url}\n\n{ex.Message}",
                "连接失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            btnConnect.IsEnabled = true;
            btnConnect.Content = "连接";
        }
    }

    private static async Task<bool> IsPicoKvmAsync(string url)
    {
        var response = await _httpClient.GetAsync(url);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            return true;

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        string[] keywords = ["pikvm", "picoKVM", "PicoKVM", "luckfox", "LuckFox", "kvmd", "kvm-video"];
        return keywords.Any(k => html.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closed(object sender, EventArgs e)
    {
        _settings.KvmUrl = txtKvmUrl.Text.Trim();
        _settings.Password = pwdPassword.Password;
        _settings.Save();
        _kvmWindow?.Close();
    }
}