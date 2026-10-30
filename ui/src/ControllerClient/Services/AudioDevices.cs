using System.Text.Json;

namespace ControllerClient.Services;

/// <summary>
/// 语音引擎 devices 命令返回的设备表，以及"设置里保存的设备名 → 引擎的设备 index"。
///
/// 旧引擎把 PortAudio 的设备全列出来，Windows 上同一个耳机按 MME、DirectSound、WASAPI、WDM-KS
/// 各出现一次，名字相同。按名字存进字典时后一个覆盖前一个，最后留下的是 WDM-KS 那份——打开就是
/// -9999 Unanticipated host error，而"系统默认"能用。所以同名的只认第一个（PortAudio 里排在最前的是 MME，
/// 和"系统默认"走同一条路）；新引擎本来就只列默认 host API 的设备，这里也不会出现重名。
/// </summary>
public sealed class AudioDevices
{
    /// <summary>MME 的设备名最多 31 个字符（MAXPNAMELEN 含结尾的 0）。</summary>
    public const int MmeNameLength = 31;

    private readonly Dictionary<string, int> _inputs = [];
    private readonly Dictionary<string, int> _outputs = [];
    public List<string> InputNames { get; } = [];
    public List<string> OutputNames { get; } = [];

    public static AudioDevices Parse(JsonElement devices)
    {
        var result = new AudioDevices();
        foreach (var device in devices.EnumerateArray())
        {
            var name = device.GetProperty("name").GetString()!;
            var index = device.GetProperty("index").GetInt32();
            if (device.GetProperty("input").GetBoolean() && result._inputs.TryAdd(name, index)) result.InputNames.Add(name);
            if (device.GetProperty("output").GetBoolean() && result._outputs.TryAdd(name, index)) result.OutputNames.Add(name);
        }
        return result;
    }

    /// <summary>保存的输入设备对应的 index；"系统默认"或找不到时为 null，引擎用默认设备。</summary>
    public int? Input(string? saved) => Find(_inputs, saved) is { } name ? _inputs[name] : null;

    public int? Output(string? saved) => Find(_outputs, saved) is { } name ? _outputs[name] : null;

    /// <summary>保存的名字在设备表里对应的那一项，设置页下拉框要选中的就是它；找不到时为 null。</summary>
    public string? InputName(string? saved) => Find(_inputs, saved);

    public string? OutputName(string? saved) => Find(_outputs, saved);

    private static string? Find(Dictionary<string, int> devices, string? saved)
    {
        if (string.IsNullOrEmpty(saved)) return null;
        if (devices.ContainsKey(saved)) return saved;
        // 旧版本可能存的是 WASAPI / DirectSound 的完整名字，MME 只留前 31 个字符。
        // 只对恰好被截断的名字按前缀匹配，并且必须唯一，免得 "Speakers" 去匹配 "Speakers (USB)"。
        var matches = devices.Keys.Where(name => name.Length >= MmeNameLength
                                                 && saved.StartsWith(name, StringComparison.Ordinal)).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
}
