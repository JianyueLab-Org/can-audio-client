using System.Text.Json;

namespace ControllerClient.Services;

/// <summary>公共服务器目录里的一个语音服务器（同 Python 版 serverdirectory.Server）。</summary>
public sealed record VoiceServer(string Name, string Host, int Port, string Location = "")
{
    public string Label => Location.Length > 0 ? $"{Name} · {Location}" : Name;
}

/// <summary>
/// 公共服务器目录（https://ceruleanavi.net/api/v1/servers），照搬 Python 版 controller/serverdirectory.py：
/// 只收 Mumble 端点；格式不对的条目跳过，同一 host:port 只留一个。
/// </summary>
public static class ServerDirectory
{
    public const string Url = "https://ceruleanavi.net/api/v1/servers";

    public static List<VoiceServer> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Number || status.GetInt32() != 200
            || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new FormatException("Invalid server directory response");
        var servers = new List<VoiceServer>();
        if (!data.TryGetProperty("audio", out var rows) || rows.ValueKind != JsonValueKind.Array) return servers;
        var seen = new HashSet<(string, int)>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || Text(row, "kind") != "audio") continue;
            var name = Text(row, "name")?.Trim();
            var host = Text(row, "host")?.Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(host) || host.Any(char.IsWhiteSpace) || host.Contains("://")) continue;
            if (!row.TryGetProperty("port", out var p) || p.ValueKind != JsonValueKind.Number
                || !p.TryGetInt32(out var port) || port is < 1 or > 65535) continue;
            if (!seen.Add((host, port))) continue;
            servers.Add(new(name, host, port, Text(row, "location") ?? ""));
        }
        return servers;
    }

    /// <summary>拿不到就返回 null 并记一行日志，不弹窗、不影响手填的地址。</summary>
    public static async Task<List<VoiceServer>?> FetchAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; CanController/2.0)");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            return Parse(await http.GetStringAsync(Url));
        }
        catch (Exception ex)
        {
            AppLog.Info($"服务器列表读取失败：{ex.Message}");
            return null;
        }
    }

    private static string? Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
