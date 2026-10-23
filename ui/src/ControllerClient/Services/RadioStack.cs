using System.Globalization;

namespace ControllerClient.Services;

/// <summary>栈里的一部电台。</summary>
public sealed class Radio(int frequencyKhz, string callsign = "")
{
    public int FrequencyKhz { get; } = frequencyKhz;
    public string Callsign { get; } = callsign;
    public bool Rx { get; set; }
    public bool Tx { get; set; }
    public bool Xc { get; set; }
    /// <summary>0-100，和主音量相乘。</summary>
    public int Volume { get; set; } = 100;
    public bool Muted { get; set; }
    public bool TxManuallyDisabled { get; set; }

    // 运行时状态，不持久化
    public bool CurrentlyRx { get; set; }
    public bool CurrentlyTx { get; set; }
    public string LastReceivedCallsign { get; set; } = "";
    public DateTime? LastReceivedAt { get; set; }

    public string Frequency => RadioStack.FormatFrequency(FrequencyKhz);
    public string Label => Callsign.Length > 0 ? $"{Callsign} {Frequency}" : Frequency;
    public int EffectiveVolume => Muted ? 0 : Volume;
}

/// <summary>推给语音引擎的结果：语音那边只照着做，不再判断任何规则。</summary>
public sealed record VoiceSyncPlan(int[] Rx, int[] Tx, int[] Xc, int? Selected, int[] Locked, Dictionary<string, int> Volumes);

/// <summary>
/// 电台栈：管制员同时守听 / 发话的一组频率，模型照搬 TrackAudio 的 radioStore。联动规则：
/// 关掉 RX → TX、XC 一起关；打开 TX → 自动打开 RX；打开 XC → 自动打开 RX 和 TX。
/// XC（交叉耦合）把开了 XC 的频率连起来，在其中一个上收到的话音转发到其它的上。
/// 这里只有状态，没有 I/O。
/// </summary>
public sealed class RadioStack : IEnumerable<Radio>
{
    private List<Radio> _radios = [];
    private bool _dutyChecked;

    public event Action? Changed;

    /// <summary>主频率：真正进入的那个 Mumble 频道。</summary>
    public int? SelectedKhz { get; set; }

    /// <summary>
    /// 数据源上本人没有在管制席位时只许收、不许发。语音服务器没有花名册检查，
    /// 这道闸是客户端自觉守的那一层。
    /// </summary>
    public bool TransmitAllowed { get; private set; } = true;

    /// <summary>本人正在管的频率：不许删，完成值守检查后也只有这些能当主频率。</summary>
    public IReadOnlySet<int> LockedKhz { get; private set; } = new HashSet<int>();

    public static int ParseFrequency(string text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0) throw new FormatException("频率不能为空");
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz) || !double.IsFinite(mhz))
            throw new FormatException($"频率格式无效: {text}");
        var khz = (int)Math.Round(mhz * 1000, MidpointRounding.ToEven);
        if (khz is < 100000 or > 199999) throw new FormatException($"频率超出甚高频范围: {text}");
        return khz;
    }

    public static string FormatFrequency(int khz) => (khz / 1000m).ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>频率对应的 Mumble 频道名，全网统一的约定。</summary>
    public static string ChannelName(int khz) => "FREQ_" + khz.ToString("D6", CultureInfo.InvariantCulture);

    // ---------- 查询 ----------
    public int Count => _radios.Count;
    public Radio? Get(int khz) => _radios.FirstOrDefault(r => r.FrequencyKhz == khz);
    public Radio? Selected => SelectedKhz is { } khz ? Get(khz) : null;
    public int[] RxFrequencies => [.. _radios.Where(r => r.Rx).Select(r => r.FrequencyKhz)];
    public int[] TxFrequencies => [.. _radios.Where(r => r.Tx).Select(r => r.FrequencyKhz)];
    public int[] XcFrequencies => [.. _radios.Where(r => r.Xc).Select(r => r.FrequencyKhz)];
    public bool IsLocked(int khz) => LockedKhz.Contains(khz);
    public IEnumerator<Radio> GetEnumerator() => _radios.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    // ---------- 增删 ----------
    public Radio Add(string frequency, string callsign = "") => Add(ParseFrequency(frequency), callsign);

    public Radio Add(int khz, string callsign = "")
    {
        if (Get(khz) is not null) throw new InvalidOperationException($"{FormatFrequency(khz)} 已经在电台栈里了");
        var radio = new Radio(khz, callsign.Trim().ToUpperInvariant());
        _radios.Add(radio);
        _radios.Sort((a, b) => a.FrequencyKhz.CompareTo(b.FrequencyKhz));
        if (SelectedKhz is null && !_dutyChecked) SelectedKhz = khz;
        OnChanged();
        return radio;
    }

    public bool Remove(int khz)
    {
        var radio = Get(khz);
        // 静默拒绝：界面上的按钮本来就该是禁用的，走到这里是别的路径碰到了
        if (radio is null || IsLocked(khz)) return false;
        _radios.Remove(radio);
        if (SelectedKhz == khz) SelectedKhz = _radios.Count > 0 ? _radios[0].FrequencyKhz : null;
        OnChanged();
        return true;
    }

    public void Clear()
    {
        _radios = [];
        SelectedKhz = null;
        OnChanged();
    }

    // ---------- 值守 ----------
    /// <summary>
    /// 能不能发信。关掉时把已经打开的 TX / XC 一并落下——只把按钮画灰不够，
    /// 栈里开着的 TX 会被编进 VoiceTarget，人照样发得出去。
    /// </summary>
    public bool SetTransmitAllowed(bool allowed)
    {
        if (allowed == TransmitAllowed) return false;
        TransmitAllowed = allowed;
        var changed = false;
        if (!allowed)
            foreach (var radio in _radios.Where(r => r.Tx || r.Xc))
            {
                radio.Tx = radio.Xc = radio.CurrentlyTx = false;
                changed = true;
            }
        OnChanged();
        return changed;
    }

    /// <summary>设置本人正在管的频率集合；null 表示尚未完成值守检查。</summary>
    public bool SetLocked(IEnumerable<int>? khz)
    {
        var locked = khz is null ? new HashSet<int>() : [.. khz];
        var dutyChecked = khz is not null;
        if (locked.SetEquals(LockedKhz) && dutyChecked == _dutyChecked) return false;
        LockedKhz = locked;
        _dutyChecked = dutyChecked;
        if (dutyChecked && locked.Count == 0) SelectedKhz = null;
        else if (locked.Count > 0 && !(SelectedKhz is { } selected && locked.Contains(selected)))
            SelectedKhz = locked.Order().Where(value => Get(value) is not null).Select(value => (int?)value).FirstOrDefault();
        OnChanged();
        return true;
    }

    // ---------- 开关（联动规则同 TrackAudio） ----------
    public Radio? SetRx(int khz, bool value)
    {
        if (Get(khz) is not { } radio) return null;
        radio.Rx = value;
        if (!value) radio.Tx = radio.Xc = radio.CurrentlyRx = false; // 不收了，发和耦合无从谈起
        else SelectedKhz ??= khz;
        OnChanged();
        return radio;
    }

    public Radio? SetTx(int khz, bool value)
    {
        if (Get(khz) is not { } radio) return null;
        if (value && !TransmitAllowed)
        {
            AppLog.Info("未在数据源上管制席位，拒绝打开 TX");
            return radio;
        }
        radio.Tx = value;
        radio.TxManuallyDisabled = !value;
        if (value) radio.Rx = true; // 不存在只发不收
        else radio.Xc = radio.CurrentlyTx = false;
        OnChanged();
        return radio;
    }

    public Radio? SetXc(int khz, bool value)
    {
        if (Get(khz) is not { } radio) return null;
        if (value && !TransmitAllowed)
        {
            AppLog.Info("未在数据源上管制席位，拒绝打开 XC");
            return radio;
        }
        radio.Xc = value;
        if (value) radio.Rx = radio.Tx = true;
        OnChanged();
        return radio;
    }

    public Radio? ToggleRx(int khz) => Get(khz) is { } radio ? SetRx(khz, !radio.Rx) : null;
    public Radio? ToggleTx(int khz) => Get(khz) is { } radio ? SetTx(khz, !radio.Tx) : null;
    public Radio? ToggleXc(int khz) => Get(khz) is { } radio ? SetXc(khz, !radio.Xc) : null;

    /// <summary>选主频率；完成值守检查后只能选本人正在管的频率。</summary>
    public void Select(int khz)
    {
        if (Get(khz) is null || (_dutyChecked && !IsLocked(khz))) return;
        SelectedKhz = khz;
        OnChanged();
    }

    public void SetVolume(int khz, int volume)
    {
        if (Get(khz) is not { } radio) return;
        radio.Volume = Math.Clamp(volume, 0, 100);
        OnChanged();
    }

    public void SetMuted(int khz, bool muted)
    {
        if (Get(khz) is not { } radio) return;
        radio.Muted = muted;
        OnChanged();
    }

    // ---------- 运行时状态 ----------
    public Radio? SetCurrentlyRx(int khz, bool value, string callsign = "", DateTime? timestamp = null)
    {
        if (Get(khz) is not { } radio) return null;
        radio.CurrentlyRx = value;
        if (value && callsign.Length > 0)
        {
            radio.LastReceivedCallsign = callsign;
            radio.LastReceivedAt = timestamp;
        }
        OnChanged();
        return radio;
    }

    public void SetCurrentlyTx(bool value)
    {
        foreach (var radio in _radios) radio.CurrentlyTx = value && radio.Tx;
        OnChanged();
    }

    // ---------- 恢复与输出 ----------
    /// <summary>整体换掉电台；运行时状态不恢复。主频率优先选开着 RX 的那部。</summary>
    public void Load(IEnumerable<Radio> radios)
    {
        _radios = [.. radios.DistinctBy(r => r.FrequencyKhz).OrderBy(r => r.FrequencyKhz)];
        SelectedKhz = (_radios.FirstOrDefault(r => r.Rx) ?? _radios.FirstOrDefault())?.FrequencyKhz;
        OnChanged();
    }

    public VoiceSyncPlan ToSyncPlan() => new(RxFrequencies, TxFrequencies, XcFrequencies, SelectedKhz,
        [.. LockedKhz.Order()],
        _radios.ToDictionary(r => r.FrequencyKhz.ToString(CultureInfo.InvariantCulture), r => r.EffectiveVolume));

    private void OnChanged() => Changed?.Invoke();
}
