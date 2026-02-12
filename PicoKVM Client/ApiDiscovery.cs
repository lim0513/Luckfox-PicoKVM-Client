using System.Net.Http;
using System.Diagnostics;

namespace PicoKVM_Client
{
    /// <summary>
    /// API发现工具 - 帮助找到Luckfox KVM的可用API端点
    /// </summary>
    public static class ApiDiscovery
    {
        public static async Task<List<string>> DiscoverApisAsync(string baseUrl, HttpClient httpClient)
        {
            var discoveredApis = new List<string>();
            
            var testPaths = new[]
            {
                // 设备信息
                "/device",
                "/device/status",
                "/device/info",
                "/api/info",
                "/api/device",
                "/status",
                
                // 视频/屏幕相关
                "/snapshot",
                "/snap",
                "/screen",
                "/frame",
                "/jpeg",
                "/image",
                "/api/snapshot",
                "/api/screen/snapshot",
                "/api/screen/capture",
                "/api/frame",
                
                // 流媒体
                "/stream",
                "/video",
                "/mjpeg",
                "/live",
                "/api/stream",
                "/api/video",
                "/api/video/stream",
                
                // HID控制
                "/api/hid",
                "/api/hid/keyboard",
                "/api/hid/mouse",
                "/api/keyboard",
                "/api/mouse",
                "/hid/keyboard",
                "/hid/mouse",
                
                // WebRTC相关
                "/api/webrtc",
                "/api/signaling",
                "/ws",
                "/websocket",
            };
            
            foreach (var path in testPaths)
            {
                try
                {
                    var response = await httpClient.GetAsync($"{baseUrl}{path}");
                    if (response.IsSuccessStatusCode)
                    {
                        var contentType = response.Content.Headers.ContentType?.MediaType ?? "unknown";
                        var size = response.Content.Headers.ContentLength ?? 0;
                        var apiInfo = $"{path} [{response.StatusCode}] - {contentType} ({size} bytes)";
                        discoveredApis.Add(apiInfo);
                        Debug.WriteLine($"? Found API: {apiInfo}");
                    }
                }
                catch
                {
                    // 忽略错误
                }
            }
            
            return discoveredApis;
        }
    }
}
