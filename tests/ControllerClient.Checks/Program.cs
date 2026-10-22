using System.Text.Json;
using ControllerClient.ViewModels;
using ControllerClient.Services;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static JsonElement Binding(object data) => JsonSerializer.SerializeToElement(data);

var settings = new SettingsViewModel { PttBindings = [] };
settings.MarkSaved();
var keyboard = Binding(new { kind = "keyboard", key = "v" });
var mouse = Binding(new { kind = "mouse", button = "x1" });
var joystick = Binding(new { kind = "joystick", button = 0, device = 0, device_name = "脚踏" });
settings.AddPttBinding(keyboard);
settings.AddPttBinding(mouse);
settings.AddPttBinding(joystick);
Check(settings.PttBindings.Length == 3 && settings.IsDirty, "添加多个 PTT 应保留全部绑定并允许应用");
settings.MarkSaved();
settings.AddPttBinding(Binding(new { kind = "keyboard", key = "V" }));
settings.AddPttBinding(Binding(new { kind = "joystick", button = 0, device = 0, device_name = "新名称" }));
Check(settings.PttBindings.Length == 3 && !settings.IsDirty, "相同按键或相同摇杆按钮不应重复添加");
var saved = JsonSerializer.Serialize(settings.PttBindings);
var restored = new SettingsViewModel { PttBindings = JsonSerializer.Deserialize<JsonElement[]>(saved)! };
Check(restored.PttBindingItems.Select(item => item.Label).SequenceEqual(settings.PttBindingItems.Select(item => item.Label)), "全部绑定应可通过配置 JSON 恢复");
settings.RemovePttCommand.Execute(settings.PttBindingItems[1]);
Check(settings.PttBindings.Length == 2 && settings.PttBindings.All(b => b.GetProperty("kind").GetString() != "mouse"), "移除一项不应清除其他绑定");
settings.IsWaitingForPtt = true;
settings.ClearPttCommand.Execute(null);
Check(settings.PttBindings.Length == 2 && !settings.CanEditPtt, "录制中不应清除绑定");
settings.IsWaitingForPtt = false;
settings.BridgeCommand = command =>
{
    if (command == "capture")
    {
        Check(settings.IsWaitingForPtt, "发起录制前必须已进入等待状态");
        settings.AddPttBinding(mouse);
        settings.IsWaitingForPtt = false;
    }
    return Task.CompletedTask;
};
await settings.SetPttCommand.ExecuteAsync(null);
Check(settings.PttBindings.Length == 3 && !settings.IsWaitingForPtt, "即时录制结果应追加绑定且正确结束等待");
settings.ClearPttCommand.Execute(null);
Check(settings.PttBindings.Length == 0 && !settings.CanClearPtt, "清空绑定后应允许保存空数组");
Console.WriteLine("PASS: 多 PTT 添加、去重、JSON 恢复、单项删除、录制竞态及清空检查");

var status = new ConnectionStatusViewModel();
status.SetVoiceState("connecting");
status.UpdateVoice(Binding(new { connected = false, running = false, transport = (string?)null, latency_ms = (double?)null }));
Check(status.VoiceText.Contains("连接中"), "启动阶段的空快照不能覆盖连接中状态");
status.UpdateVoice(Binding(new { connected = true, running = true, transport = "UDP", latency_ms = 23.0 }));
Check(status.VoiceText == "语音：已连接 · UDP · 23 ms", "UDP 必须使用当前通道延迟");
status.UpdateVoice(Binding(new { connected = true, running = true, transport = "TCP", latency_ms = (double?)null }));
Check(status.VoiceText.Contains("TCP · -- ms"), "无 Ping 样本不能显示虚构延迟");
status.UpdateVoice(Binding(new { connected = false, running = true, transport = (string?)null, latency_ms = (double?)null }));
Check(status.VoiceText == "语音：重连中", "掉线应清除旧传输和延迟");
status.SetVoiceState("error");
status.UpdateVoice(Binding(new { connected = false, running = false, transport = (string?)null, latency_ms = (double?)null }));
Check(status.VoiceText == "语音：连接错误", "空快照不能抹去连接错误");
status.UpdateFsd(true, new DateTime(2026, 10, 23, 12, 34, 56), 50);
Check(status.FsdText.Contains("已连接（数据源确认）") && status.FeedText.Contains("12:34:56"), "FSD 状态应注明数据源及更新时间");
status.FeedFailed();
Check(status.FsdText.Contains("未知"), "数据源失败不能保留在线状态");
Check(AppLog.FilePath(new DateTime(2026, 10, 23), "voice") != AppLog.FilePath(new DateTime(2026, 10, 24), "voice"), "跨日必须更换日志文件");
Check(AppLog.FilePath(DateTime.Now, "client") != AppLog.FilePath(DateTime.Now, "voice"), "主程序与语音日志应分开");
Console.WriteLine("PASS: 实时连接状态、UDP/TCP 延迟、掉线/错误状态和按日日志路径检查");
