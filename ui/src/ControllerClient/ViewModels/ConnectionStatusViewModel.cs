using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ControllerClient.ViewModels;

/// <summary>与 MSFS 客户端一致：连接状态 + 当前音频传输 + 实测平均 RTT。</summary>
public sealed partial class ConnectionStatusViewModel : ViewModelBase
{
    /// <summary>offline 是引擎重连耗尽后的终态，需要清理整个会话。</summary>
    internal static bool IsTerminalVoiceState(string state) => state is "error" or "stopped" or "offline";

    [ObservableProperty] private string _fsdText = "FSD：未检查（数据源）";
    [ObservableProperty] private string _voiceText = "语音：未连接";
    [ObservableProperty] private string _feedText = "席位数据：未更新";
    public void SetVoiceState(string state) => VoiceText = "语音：" + (state switch
    {
        "online" => "已连接 · 等待延迟数据",
        "connecting" => "连接中",
        "reconnecting" => "重连中",
        "error" => "连接错误",
        "stopped" => "已断开",
        _ => "未连接"
    });

    /// <summary>当前连接从所选网络协议回退时的说明（can-issues#26），没回退为 null。</summary>
    [ObservableProperty] private string? _fallbackText;
    private string? _addressKey;

    /// <summary>新的一次连接开始前清掉上一次的地址族记录，保证下次连上一定会记一行日志。</summary>
    public void ResetAddress() { _addressKey = null; FallbackText = null; }

    /// <summary>更新语音状态；地址族或回退情况变了时返回一行要写进日志的说明，否则返回 null。</summary>
    public string? UpdateVoice(JsonElement snapshot)
    {
        if (!snapshot.GetProperty("connected").GetBoolean())
        {
            if (snapshot.GetProperty("running").GetBoolean()) SetVoiceState("reconnecting");
            else if (VoiceText.Contains("已连接") || VoiceText.Contains("重连中")) SetVoiceState("stopped");
            return null;
        }
        var transport = snapshot.GetProperty("transport").GetString();
        var latency = snapshot.GetProperty("latency_ms");
        var rtt = latency.ValueKind == JsonValueKind.Number && latency.TryGetDouble(out var ms) && double.IsFinite(ms) && ms >= 0
            ? ms.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) : "--";
        // 旧引擎不带这两个字段，照旧只显示传输方式
        var family = snapshot.TryGetProperty("ip_family", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
        var fallback = snapshot.TryGetProperty("ip_fallback", out var fb) && fb.ValueKind == JsonValueKind.Object ? DescribeFallback(fb) : null;
        var note = fallback is null ? "" : $"（{fallback.Short}）";
        VoiceText = family is null ? $"语音：已连接 · {transport} · {rtt} ms" : $"语音：已连接 · {transport} · {family}{note} · {rtt} ms";

        var key = $"{family}|{fallback?.Full}";
        if (key == _addressKey) return null;
        _addressKey = key;
        FallbackText = fallback?.Full;
        if (family is null) return null;
        return fallback is null ? $"语音连接使用 {family}" : $"语音连接使用 {family}：{fallback.Full}";
    }

    private sealed record Fallback(string Short, string Full);

    private static Fallback? DescribeFallback(JsonElement fallback)
    {
        static string? Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var wanted = Text(fallback, "wanted") ?? "所选协议";
        var used = Text(fallback, "used") ?? "另一协议";
        return Text(fallback, "reason") switch
        {
            "missing" => new($"无 {wanted}，已回退", $"服务器没有 {wanted} 地址，已自动改用 {used} 连接。"),
            "unreachable" => new($"{wanted} 不通，已回退", $"{wanted} 连接失败，已自动改用 {used} 连接。"),
            _ => null,
        };
    }

    public void UpdateFsd(bool online, DateTime updatedAt, double requestMs)
    {
        FsdText = online ? "FSD：已连接（数据源确认）" : "FSD：未在线（数据源）";
        FeedText = $"席位数据：{updatedAt:HH:mm:ss} · HTTP {requestMs:F0} ms";
    }

    public void FeedFailed()
    {
        FsdText = "FSD：状态未知（数据源不可用）";
        FeedText = "席位数据：更新失败";
    }
}
