using System.Text.Json;

namespace ControllerClient.ViewModels;

/// <summary>保留语音引擎配置格式的一条 PTT 绑定。</summary>
public sealed class PttBindingViewModel
{
    public JsonElement Binding { get; }
    public string Identity { get; }
    public string Label { get; }

    public PttBindingViewModel(JsonElement binding)
    {
        Binding = binding.Clone();
        var kind = Text("kind");
        var key = Text("key").ToLowerInvariant();
        var button = Text("button").ToLowerInvariant();
        var device = Text("device");
        Identity = kind switch
        {
            "keyboard" => $"keyboard:{key}",
            "mouse" => $"mouse:{button}",
            "joystick" => $"joystick:{(device.Length == 0 ? "0" : device)}:{button}",
            _ => binding.ToString()
        };
        Label = kind switch
        {
            "keyboard" => "键盘 · " + (key switch
            {
                "ctrl_l" => "左 Ctrl", "ctrl_r" => "右 Ctrl",
                "shift" or "shift_l" => "左 Shift", "shift_r" => "右 Shift",
                "alt_l" => "左 Alt", "alt_r" => "右 Alt", "space" => "空格",
                _ => key.ToUpperInvariant()
            }),
            "mouse" => "鼠标 · 侧键 " + (button == "x1" ? "1" : "2"),
            "joystick" => $"{(Text("device_name").Length > 0 ? Text("device_name") : "摇杆 " + device)} · 按钮 {(int.TryParse(button, out var index) ? (index + 1).ToString() : button)}",
            _ => binding.ToString()
        };
    }

    private string Text(string field) => Binding.TryGetProperty(field, out var value) ? value.ToString() : "";
}
