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
        Roster.Clear();
        foreach (var group in new[] { "pilots", "controllers", "atis" })
        {
            if (!doc.RootElement.TryGetProperty(group, out var entries) || entries.ValueKind != JsonValueKind.Array) continue;
            foreach (var entry in entries.EnumerateArray())
            {
                var cid = entry.TryGetProperty("cid", out var c) ? c.ToString().Trim() : "";
                var callsign = entry.TryGetProperty("callsign", out var n) ? n.ToString().Trim() : "";
                if (cid.Length > 0 && callsign.Length > 0) Roster[cid] = callsign;
                if (group != "controllers" || !entry.TryGetProperty("facility", out var facility) ||
                    !int.TryParse(facility.ToString(), out var f) || f <= 0 || callsign.Length == 0) continue;
                try { positions.Add(new(cid, callsign, ParseFrequency(entry.GetProperty("frequency").ToString()))); }
                catch (Exception ex) when (ex is FormatException or KeyNotFoundException) { }
            }
        }
        Positions = positions.OrderBy(p => p.Khz).ThenBy(p => p.Callsign).ToList();
        RequestMilliseconds = elapsed.Elapsed.TotalMilliseconds;
        LastUpdatedAt = DateTime.Now;
    }
    public void Dispose() => _http.Dispose();
}
