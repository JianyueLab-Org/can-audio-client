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

    public void UpdateVoice(JsonElement snapshot)
    {
        if (!snapshot.GetProperty("connected").GetBoolean())
        {
            if (snapshot.GetProperty("running").GetBoolean()) SetVoiceState("reconnecting");
            else if (VoiceText.Contains("已连接") || VoiceText.Contains("重连中")) SetVoiceState("stopped");
            return;
        }
        var transport = snapshot.GetProperty("transport").GetString();
        var latency = snapshot.GetProperty("latency_ms");
        var rtt = latency.ValueKind == JsonValueKind.Number && latency.TryGetDouble(out var ms) && double.IsFinite(ms) && ms >= 0
            ? ms.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) : "--";
        VoiceText = $"语音：已连接 · {transport} · {rtt} ms";
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
