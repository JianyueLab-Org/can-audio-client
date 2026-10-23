using System.Globalization;
using System.Text.Json;

namespace ControllerClient.Services.Ptt;

public enum PttKind { Keyboard, Mouse, Joystick }

/// <summary>
/// 一条 PTT 绑定。ToJson 的字段名就是设置文件里的键名，改名会让老配置里的绑定
/// 静默失效：程序照常起来，PTT 不响，用户以为是麦克风坏了。
/// </summary>
public sealed class PttBinding : IEquatable<PttBinding>
{
    /// <summary>能绑的鼠标键。左 / 中 / 右键是全局监听，绑上之后在任何窗口点一下都会发话。</summary>
    public static readonly IReadOnlyList<string> MouseButtons = ["x1", "x2"];

    private PttBinding(PttKind kind, string key, int button, int device, string deviceName)
    {
        Kind = kind;
        Key = key;
        Button = button;
        Device = device;
        DeviceName = deviceName;
    }

    public PttKind Kind { get; }
    /// <summary>键盘：规范化键名（见 <see cref="PttKeys"/>）；鼠标："x1" / "x2"。</summary>
    public string Key { get; }
    /// <summary>摇杆按钮序号（SDL，从 0 开始）。</summary>
    public int Button { get; }
    /// <summary>摇杆设备序号（SDL）。</summary>
    public int Device { get; }
    /// <summary>摇杆设备名，只用于界面显示，不参与比较：换 USB 口后 SDL 给的名字可能带不同后缀。</summary>
    public string DeviceName { get; }

    public static PttBinding Keyboard(string key) => new(PttKind.Keyboard, (key ?? "").ToLowerInvariant(), 0, 0, "");
    public static PttBinding Mouse(string button) => new(PttKind.Mouse, (button ?? "").ToLowerInvariant(), 0, 0, "");
    public static PttBinding Joystick(int button, int device, string deviceName = "") =>
        new(PttKind.Joystick, "", button, device, deviceName ?? "");

    /// <summary>监听器上报"按下了什么"时用的同一套记号。</summary>
    public string InputToken => Kind == PttKind.Joystick ? JoystickToken(Device, Button) : Key;
    public static string JoystickToken(int device, int button) => $"{device}:{button}";

    public string Identity => Kind switch
    {
        PttKind.Keyboard => "keyboard:" + Key,
        PttKind.Mouse => "mouse:" + Key,
        _ => $"joystick:{Device}:{Button}"
    };

    /// <summary>界面上的键名，不含可翻译的词。</summary>
    public string Token => Kind switch
    {
        PttKind.Keyboard => Key.Length == 1 ? Key.ToUpperInvariant() : Key,
        PttKind.Mouse => Key.ToUpperInvariant(),
        _ => Button.ToString(CultureInfo.InvariantCulture)
    };

    public JsonElement ToJson() => Kind switch
    {
        PttKind.Keyboard => JsonSerializer.SerializeToElement(new { kind = "keyboard", key = Key }),
        PttKind.Mouse => JsonSerializer.SerializeToElement(new { kind = "mouse", button = Key }),
        _ => JsonSerializer.SerializeToElement(new { kind = "joystick", button = Button, device = Device, device_name = DeviceName })
    };

    /// <summary>读一条绑定，读不懂返回 null。配置可手改、也可能来自更新的版本，坏的一条不能让客户端起不来。</summary>
    public static PttBinding? FromJson(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        switch (Text(data, "kind"))
        {
            case "keyboard":
                var key = Text(data, "key").ToLowerInvariant();
                return key.Length > 0 ? Keyboard(key) : null;
            case "mouse":
                var button = Text(data, "button").ToLowerInvariant();
                return MouseButtons.Contains(button) ? Mouse(button) : null;
            case "joystick":
                if (!Integer(data, "button", null, out var index) || index < 0 || !Integer(data, "device", 0, out var device))
                    return null;
                return Joystick(index, device, Text(data, "device_name"));
            default:
                return null;
        }
    }

    /// <summary>设置里的 ptt_bindings → 去重后的绑定表，坏条目丢弃并记日志。</summary>
    public static List<PttBinding> Load(IEnumerable<JsonElement>? data)
    {
        var bindings = new List<PttBinding>();
        foreach (var item in data ?? [])
        {
            var binding = FromJson(item);
            if (binding is null) AppLog.Write("client", "WARN", $"忽略无法识别的 PTT 绑定：{item}");
            else if (!bindings.Contains(binding)) bindings.Add(binding);
        }
        return bindings;
    }

    private static string Text(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value.ToString().Trim() : "";

    private static bool Integer(JsonElement data, string name, int? fallback, out int result)
    {
        result = fallback ?? 0;
        if (!data.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return fallback.HasValue;
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetInt32(out result);
        return value.ValueKind == JsonValueKind.String &&
               int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    public bool Equals(PttBinding? other) => other is not null && Identity == other.Identity;
    public override bool Equals(object? obj) => Equals(obj as PttBinding);
    public override int GetHashCode() => Identity.GetHashCode(StringComparison.Ordinal);
    public override string ToString() => $"<Binding {Identity}>";
}
