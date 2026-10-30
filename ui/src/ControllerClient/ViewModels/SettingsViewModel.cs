using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ControllerClient.Services;
using ControllerClient.Services.Ptt;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ControllerClient.ViewModels;

/// <summary>
/// 设置窗口，内容与 AFV SettingsForm 对应：
/// 用户信息、音频（含压缩）、按键通话 (PTT)、UniATIS、Stream Deck。
/// AFV 中“模拟器设置（非 ATC 用）”属于飞行员功能，管制员客户端不包含。
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    public ObservableCollection<PttBindingViewModel> PttBindingItems { get; } = [];
    public JsonElement[] PttBindings
    {
        get => PttBindingItems.Select(item => item.Binding).ToArray();
        set
        {
            PttBindingItems.Clear();
            foreach (var binding in value) AddPttBinding(binding);
            RefreshPttBindings();
        }
    }
    /// <summary>开始（true）或取消（false）录制一条 PTT 绑定。</summary>
    public Func<bool, Task>? CapturePtt { get; set; }
    public Action? Apply { get; set; }
    public const string DefaultServerHost = "audio.ceruleanavi.net";
    public const int DefaultServerPort = 64738;
    public bool ShowServerEndpoints { get; } = DebugOptions.ShowServerEndpoints;
    public string[] Servers { get; } = ["CAN"];
    public string SelectedServer => "CAN";
    [ObservableProperty] private string _serverHost = DefaultServerHost;
    [ObservableProperty] private int _serverPort = DefaultServerPort;
    [ObservableProperty] private string _statusText = "";
    public SettingsViewModel()
    {
        AddPttBinding(JsonSerializer.SerializeToElement(new { kind = "keyboard", key = "v" }));
        SettingsStore.Load(this);
    }
    /// <summary>有未应用的修改时启用“应用”按钮（同 AFV btnApply）。</summary>
    [ObservableProperty]
    private bool _isDirty;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is not (nameof(IsDirty) or nameof(IsWaitingForPtt) or nameof(PttButtonText)
            or nameof(CanClearPtt) or nameof(InputVolumeText) or nameof(OutputVolumeText)
            or nameof(CanEditPtt) or nameof(PttStatusText)
            or nameof(AudioQualityText) or nameof(PacketDurationText) or nameof(BitrateSummary)
            or nameof(InputLevel) or nameof(IsCalibrating) or nameof(CalibrateButtonText)
            or nameof(CalibrationStatus) or nameof(CalibrationFailed) or nameof(BaselineText)))
            IsDirty = true;
    }

    public void MarkSaved() => IsDirty = false;

    // —— 用户信息（AFV groupBox4 User Details） ——
    [ObservableProperty] private string _networkId = "";
    [ObservableProperty] private string _password = "";

    // —— 音频（AFV groupBox2 Audio Settings） ——
    public ObservableCollection<string> InputDevices { get; } = ["系统默认"];
    public ObservableCollection<string> OutputDevices { get; } = ["系统默认"];
    public ObservableCollection<string> SpeakerDevices { get; } = ["系统默认"];
    [ObservableProperty] private string? _inputDeviceName = "系统默认";
    [ObservableProperty] private string? _headsetDeviceName = "系统默认";
    [ObservableProperty] private string? _speakerDeviceName = "系统默认";

    /// <summary>麦克风音量（AFV tbMicLevel 范围 -72 ~ +72）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InputVolumeText))]
    private double _inputVolume;

    /// <summary>输出音量（AFV tbOutput 范围 -72 ~ +72）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputVolumeText))]
    private double _outputVolume;

    public string InputVolumeText => ((int)InputVolume).ToString("+#;-#;0");
    public string OutputVolumeText => ((int)OutputVolume).ToString("+#;-#;0");

    /// <summary>麦克风电平（0~100），校准时由语音引擎的 calibration 事件刷新。</summary>
    [ObservableProperty] private double _inputLevel;

    [ObservableProperty] private bool _disableAudioEffects;

    // —— 麦克风音量自动校准（Python 版 calibration.py / micgain.py）——
    // 引擎录 1 秒静音测底噪、8 秒朗读，算出把语音拉到 -20 dBFS 的基准增益。基准按设备名和降噪状态
    // 存在 mic_calibration 里，发送时 × 10^(基准/20) × 上面滑块的乘数；滑块本身不动。
    public const double MinBaselineDb = -12, MaxBaselineDb = 20;
    public const string CalibrationPhrase = "上海进近，东方五三五一，通过六千米下降到四千二百米，航向二七零，建立航向道报告。";

    /// <summary>设备名 → {gain_db, speech_dbfs, noise_dbfs, denoise, calibrated_at}，格式同 Python 版。</summary>
    public JsonObject MicCalibration { get; set; } = new();
    /// <summary>开始（true）或取消（false）一次校准。</summary>
    public Func<bool, Task>? RunCalibration { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CalibrateButtonText))]
    private bool _isCalibrating;
    [ObservableProperty] private string _calibrationStatus = "";
    [ObservableProperty] private bool _calibrationFailed;
    public string CalibrateButtonText => IsCalibrating ? "取消校准" : "麦克风音量自动校准";
    public string BaselineText => BaselineFor(InputDeviceName, !DisableAudioEffects) is { } db
        ? $"当前基准：{db:+0.0;-0.0;0.0} dB" : "当前基准：未校准";

    /// <summary>
    /// 该设备在这个降噪状态下的基准增益；没校准过返回 null（同 Python baseline_db）：
    /// 记录是在另一种降噪状态下做的也算没有，缺 denoise 键的老记录视为不降噪时做的。
    /// </summary>
    public double? BaselineFor(string? device, bool denoise)
    {
        if (string.IsNullOrEmpty(device) || MicCalibration[device] is not JsonObject entry) return null;
        var entryDenoise = entry["denoise"] is JsonValue d && d.TryGetValue(out bool b) && b;
        if (entryDenoise != denoise) return null;
        if (entry["gain_db"] is not JsonValue value || !value.TryGetValue(out double gain) || double.IsNaN(gain)) return null;
        return Math.Clamp(gain, MinBaselineDb, MaxBaselineDb);
    }

    partial void OnInputDeviceNameChanged(string? value) => CalibrationTargetChanged();
    partial void OnDisableAudioEffectsChanged(bool value) => CalibrationTargetChanged();

    private void CalibrationTargetChanged()
    {
        OnPropertyChanged(nameof(BaselineText));
        if (IsCalibrating) _ = CancelCalibrationAsync();
        CalibrationStatus = "";
        CalibrationFailed = false;
    }

    [RelayCommand]
    private async Task Calibrate()
    {
        if (IsCalibrating) { await CancelCalibrationAsync(); CalibrationStatus = "已取消。"; return; }
        if (RunCalibration is null) return;
        IsCalibrating = true;
        CalibrationFailed = false;
        CalibrationStatus = "正在打开麦克风……";
        try { await RunCalibration(true); }
        catch (Exception ex)
        {
            IsCalibrating = false;
            CalibrationFailed = true;
            CalibrationStatus = ex.Message.StartsWith("未知命令") ? "语音引擎版本过旧，不支持自动校准。" : "无法开始校准：" + ex.Message;
        }
    }

    public async Task CancelCalibrationAsync()
    {
        if (!IsCalibrating) return;
        IsCalibrating = false;
        InputLevel = 0;
        try { if (RunCalibration is not null) await RunCalibration(false); }
        catch (Exception ex) { AppLog.Info($"取消校准失败：{ex.Message}"); }
    }

    /// <summary>语音引擎的 calibration 事件（界面线程）。</summary>
    public void OnCalibrationEvent(JsonElement msg)
    {
        if (!IsCalibrating) return;
        switch (msg.GetProperty("stage").GetString())
        {
            case "quiet":
                CalibrationStatus = "请保持安静……";
                InputLevel = Meter(msg);
                break;
            case "read":
                CalibrationStatus = "请以平时通话的音量和麦克风距离朗读：";
                InputLevel = Meter(msg);
                break;
            case "done":
                IsCalibrating = false;
                InputLevel = 0;
                FinishCalibration(msg);
                break;
            case "error":
                IsCalibrating = false;
                InputLevel = 0;
                CalibrationFailed = true;
                CalibrationStatus = "打不开麦克风：" + msg.GetProperty("message").GetString();
                break;
            case "cancelled":
                IsCalibrating = false;
                InputLevel = 0;
                break;
        }
    }

    /// <summary>电平表按 dBFS 显示：-60 是空，0 满。</summary>
    private static double Meter(JsonElement msg) =>
        msg.TryGetProperty("level", out var level) ? Math.Clamp((level.GetDouble() + 60) / 60 * 100, 0, 100) : 0;

    private void FinishCalibration(JsonElement msg)
    {
        var reason = msg.GetProperty("reason").ValueKind == JsonValueKind.String ? msg.GetProperty("reason").GetString() : null;
        if (reason is not null || msg.GetProperty("gain_db").ValueKind != JsonValueKind.Number)
        {
            CalibrationFailed = true;
            CalibrationStatus = reason switch
            {
                "clipping" => "输入削波。请把麦克风拿远一点，或调低系统输入音量后重试。",
                "too_short" => "没有检测到足够的语音。请完整读出上面的文字后重试。",
                _ => "环境太吵或麦克风声音太小。请换到安静的环境，或调高系统输入音量后重试。",
            };
            return;
        }
        var gain = msg.GetProperty("gain_db").GetDouble();
        if (!string.IsNullOrEmpty(InputDeviceName))
            MicCalibration[InputDeviceName] = new JsonObject
            {
                ["gain_db"] = gain,
                ["speech_dbfs"] = msg.GetProperty("speech_dbfs").GetDouble(),
                ["noise_dbfs"] = msg.GetProperty("noise_dbfs").GetDouble(),
                ["denoise"] = msg.GetProperty("denoise").GetBoolean(),
                ["calibrated_at"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            };
        CalibrationFailed = false;
        CalibrationStatus = $"校准完成，基准 {gain:+0.0;-0.0;0.0} dB。点“应用”或“确定”保存。";
        OnPropertyChanged(nameof(BaselineText));
        IsDirty = true;
    }
    [ObservableProperty] private bool _hfSquelch = true;

    // —— 音频 · 压缩：编码质量与每包音频时长，默认 100 kb/s、40 ms ——
    /// <summary>每包音频时长可选值（毫秒），滑块值为其下标。</summary>
    private static readonly int[] PacketDurations = [10, 20, 40, 60];

    /// <summary>编码质量，单位 kb/s（范围 8 ~ 192）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioQualityText), nameof(BitrateSummary))]
    private double _audioQualityKbps = 100;

    /// <summary>每包音频时长滑块位置（0 ~ 3，对应 10 / 20 / 40 / 60 ms）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PacketDurationText), nameof(BitrateSummary))]
    private double _packetDurationIndex = 2;

    [ObservableProperty] private bool _allowLowDelayMode = true;

    private int PacketDurationMs => PacketDurations[Math.Clamp((int)PacketDurationIndex, 0, PacketDurations.Length - 1)];

    public string AudioQualityText => $"{AudioQualityKbps:0.0} kb/s";
    public string PacketDurationText => $"{PacketDurationMs} ms";

    /// <summary>总带宽估算：音频 + 位置 + 包头开销（IP 20 + UDP 8 + 加密 4 + 类型 1 + 序号 2 + 帧头，每 10 ms 一帧）。</summary>
    public string BitrateSummary
    {
        get
        {
            var frames = PacketDurationMs / 10;
            var overhead = 100.0 * 8 * (20 + 8 + 4 + 1 + 2 + frames) / frames / 1000;
            const double position = 0;
            var total = AudioQualityKbps + position + overhead;
            return $"{total:0.0} kb/s（音频 {AudioQualityKbps:0.0}，位置 {position:0.0}，开销 {overhead:0.0}）";
        }
    }

    // —— 按键通话（AFV groupBox3 PTT Settings） ——
    public string PttAssignment => PttBindingItems.Count == 0 ? "无" : string.Join(" / ", PttBindingItems.Select(item => item.Label));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PttButtonText), nameof(CanClearPtt), nameof(CanEditPtt))]
    private bool _isWaitingForPtt;

    public string PttButtonText => IsWaitingForPtt ? "取消录制" : "添加 PTT 按键或按钮";
    public bool CanClearPtt => !IsWaitingForPtt && PttBindingItems.Count > 0;
    public bool CanEditPtt => !IsWaitingForPtt;
    [ObservableProperty] private string _pttStatusText = "";

    public IReadOnlyList<PttBinding> PttBindingModels => PttBindingItems.Select(item => item.Model).ToList();

    public void AddPttBinding(JsonElement binding)
    {
        if (PttBinding.FromJson(binding) is { } model) AddPttBinding(model);
        else PttStatusText = "无法识别的 PTT 绑定，已忽略。";
    }

    public void AddPttBinding(PttBinding binding)
    {
        var item = new PttBindingViewModel(binding);
        if (PttBindingItems.Any(existing => existing.Identity == item.Identity))
        {
            PttStatusText = "该 PTT 已在列表中。";
            return;
        }
        PttBindingItems.Add(item);
        PttStatusText = "";
        RefreshPttBindings();
    }

    private void RefreshPttBindings()
    {
        OnPropertyChanged(nameof(PttBindings));
        OnPropertyChanged(nameof(PttAssignment));
        OnPropertyChanged(nameof(CanClearPtt));
    }

    [RelayCommand]
    private async Task SetPtt()
    {
        try
        {
            if (CapturePtt is null) return;
            var cancel = IsWaitingForPtt;
            IsWaitingForPtt = !cancel;
            PttStatusText = "";
            await CapturePtt(!cancel);
        }
        catch (Exception ex) { PttStatusText = ex.Message; IsWaitingForPtt = false; }
    }

    [RelayCommand]
    private void ClearPtt() { if (CanClearPtt) PttBindings = []; }

    [RelayCommand]
    private void RemovePtt(PttBindingViewModel item)
    {
        if (IsWaitingForPtt) return;
        PttBindingItems.Remove(item);
        RefreshPttBindings();
    }

    // —— UniATIS 集成（AFV groupBox5） ——
    [ObservableProperty] private bool _enableUniAtis;
    private static string UniAtisRoot => OperatingSystem.IsWindows() ? @"C:\UniATIS" :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "UniATIS");
    [ObservableProperty] private string _atisTextPath = Path.Combine(UniAtisRoot, "ATIS.TXT");
    [ObservableProperty] private string _atisSoundPath = Path.Combine(UniAtisRoot, "Sounds");

    // —— Stream Deck（AFV groupBox6） ——
    [ObservableProperty] private bool _enableStreamDeck;
}
