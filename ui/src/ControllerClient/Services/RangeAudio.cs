namespace ControllerClient.Services;

/// <summary>数据源里一个在线的飞行员或管制席位，以及它的坐标（没有坐标时为 null）。</summary>
public sealed record NetworkPeer(string Cid, string Callsign, bool IsController, int Facility, double? Latitude, double? Longitude)
{
    /// <summary>有限且在合法范围内才算有坐标。"NaN" 能被 double.TryParse 读出来，算出的距离也是 NaN，
    /// 任何比较都是 false——不挡在这里，这个人就会被当成"超出范围"静音。</summary>
    public bool HasPosition => Latitude is { } lat && Longitude is { } lon
        && double.IsFinite(lat) && double.IsFinite(lon) && Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180;
}

/// <summary>
/// 距离静音：按数据源坐标算出"谁的话音该放、谁的该静音"，结果是语音引擎
/// range_audio 命令要的 CID → allow 完整快照（协议见 can-audio 的 range_audio_protocol.md）。
///
/// 收听范围由**收听者**决定：飞行员 200 海里；管制员按席位，TWR 及以下 100、APP 300、
/// CTR 600、FSS 2000 海里。管制员与管制员之间不设限。
///
/// 判断不了就放行——快照里没有的 CID 引擎默认允许：自己不在线（没有坐标）时整张快照为空，
/// 对方没有坐标时不写进快照。宁可多听一个远处的人，也不能把该听的人静音。
/// </summary>
public static class RangeAudio
{
    private const double EarthRadiusNm = 3440.065;

    // 数据源 facility，沿用 VATSIM 的编号
    public const int Observer = 0, Fss = 1, Delivery = 2, Ground = 3, Tower = 4, Approach = 5, Center = 6;

    /// <summary>收听者能听到多远（海里）。</summary>
    public static double RangeNm(bool isController, int facility) => !isController ? 200 : facility switch
    {
        Fss => 2000,
        Center => 600,
        Approach => 300,
        _ => 100,                       // TWR 及以下（含 DEL、GND、观察员）
    };

    /// <summary>两点大圆距离（海里）。</summary>
    public static double DistanceNm(double lat1, double lon1, double lat2, double lon2)
    {
        static double Rad(double deg) => deg * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusNm * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>
    /// 给 <paramref name="myCid"/> 算完整快照。同一个 CID 可能有多个席位（比如同时开 TWR 和 APP）：
    /// 自己这边任一席位能听到就算听得到；对方只要有一个席位是管制员，就按管制员算。
    /// </summary>
    public static Dictionary<string, bool> BuildPolicy(string myCid, IEnumerable<NetworkPeer> peers)
    {
        var policy = new Dictionary<string, bool>();
        var byCid = peers.Where(p => p.Cid.Length > 0).GroupBy(p => p.Cid).ToDictionary(g => g.Key, g => g.ToList());
        if (!byCid.TryGetValue(myCid, out var mine)) return policy;
        // 只要有管制记录就按管制员收听，哪怕那条记录没坐标——不能因为管制席位缺坐标就退回
        // 飞行员的 200 海里。管制席位都没坐标就判断不了，整张快照为空、全部放行。
        var listenerIsController = mine.Any(p => p.IsController);
        var listeners = mine.Where(p => p.IsController == listenerIsController && p.HasPosition).ToList();
        if (listeners.Count == 0) return policy;

        foreach (var (cid, entries) in byCid)
        {
            if (cid == myCid) continue;
            if (listenerIsController && entries.Any(p => p.IsController))
            {
                policy[cid] = true;          // 管制员之间不设限
                continue;
            }
            var located = entries.Where(p => p.HasPosition).ToList();
            if (located.Count == 0) continue;  // 没坐标，判断不了，交给默认放行
            policy[cid] = listeners.Any(me => located.Any(them =>
                DistanceNm(me.Latitude!.Value, me.Longitude!.Value, them.Latitude!.Value, them.Longitude!.Value)
                <= RangeNm(me.IsController, me.Facility)));
        }
        return policy;
    }

    /// <summary>两份快照内容是否相同（键和值都一样）。</summary>
    public static bool SameAs(IReadOnlyDictionary<string, bool> a, IReadOnlyDictionary<string, bool> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value) && value == pair.Value);
}
