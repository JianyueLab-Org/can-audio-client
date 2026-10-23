using System.Globalization;
using System.Text.Json;
using System.Diagnostics;

namespace ControllerClient.Services;

public sealed record Position(string Cid, string Callsign, int Khz)
{
    public string Frequency => (Khz / 1000m).ToString("0.000", CultureInfo.InvariantCulture);
    public override string ToString() => $"{Callsign}  {Frequency}";
}

public sealed class NetworkDirectory : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    public List<Position> Positions { get; private set; } = [];
    public Dictionary<string, string> Roster { get; } = [];
    /// <summary>飞行员和管制席位（不含 ATIS），距离静音用。</summary>
    public List<NetworkPeer> Peers { get; private set; } = [];
    public DateTime? LastUpdatedAt { get; private set; }
    public double RequestMilliseconds { get; private set; }
    public NetworkDirectory() => _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; CanController/2.0)");
    public static int ParseFrequency(string value)
    {
        if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var frequency))
            throw new FormatException("频率格式错误");
        var khz = decimal.Round(frequency * 1000, 0, MidpointRounding.ToEven);
        if (khz is < 100000 or > 199999 || khz == 199998) throw new FormatException("频率超出范围");
        return (int)khz;
    }
    public async Task RefreshAsync()
    {
        var elapsed = Stopwatch.StartNew();
        using var doc = JsonDocument.Parse(await _http.GetStringAsync("https://data.ceruleanavi.net/v1/data.json"));
        var positions = new List<Position>();
        var peers = new List<NetworkPeer>();
        Roster.Clear();
        foreach (var group in new[] { "pilots", "controllers", "atis" })
        {
            if (!doc.RootElement.TryGetProperty(group, out var entries) || entries.ValueKind != JsonValueKind.Array) continue;
            foreach (var entry in entries.EnumerateArray())
            {
                var cid = entry.TryGetProperty("cid", out var c) ? c.ToString().Trim() : "";
                var callsign = entry.TryGetProperty("callsign", out var n) ? n.ToString().Trim() : "";
                if (cid.Length > 0 && callsign.Length > 0) Roster[cid] = callsign;
                // ATIS 的 cid 是开通播的那位管制员，通播本身不按距离静音
                if (group != "atis" && cid.Length > 0) peers.Add(ParsePeer(entry, cid, callsign, group == "controllers"));
                if (group != "controllers" || !entry.TryGetProperty("facility", out var facility) ||
                    !int.TryParse(facility.ToString(), out var f) || f <= 0 || callsign.Length == 0) continue;
                try { positions.Add(new(cid, callsign, ParseFrequency(entry.GetProperty("frequency").ToString()))); }
                catch (Exception ex) when (ex is FormatException or KeyNotFoundException) { }
            }
        }
        Positions = positions.OrderBy(p => p.Khz).ThenBy(p => p.Callsign).ToList();
        Peers = peers;
        RequestMilliseconds = elapsed.Elapsed.TotalMilliseconds;
        LastUpdatedAt = DateTime.Now;
    }
    /// <summary>飞行员的坐标是数字，管制员的是字符串，两种都认；(0, 0) 当作没有坐标。</summary>
    internal static NetworkPeer ParsePeer(JsonElement entry, string cid, string callsign, bool isController)
    {
        static double? Number(JsonElement entry, string name)
        {
            if (!entry.TryGetProperty(name, out var value)) return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String &&
                double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return number;
            return null;
        }
        var lat = Number(entry, "latitude");
        var lon = Number(entry, "longitude");
        if (lat is null || lon is null || Math.Abs(lat.Value) > 90 || Math.Abs(lon.Value) > 180 || (lat == 0 && lon == 0))
            lat = lon = null;
        var facility = (int)(Number(entry, "facility") ?? RangeAudio.Observer);
        return new(cid, callsign, isController, facility, lat, lon);
    }

    public void Dispose() => _http.Dispose();
}
