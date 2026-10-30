using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Text.Json;
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
    [ObservableProperty] private string _serverHost = DefaultServerHost;
    [ObservableProperty] private int _serverPort = DefaultServerPort;
    [ObservableProperty] private string _statusText = "";

    // —— 语音服务器选择：同 Python 版 serverpicker.py，列表来自公共服务器目录。
    // 开了调试端点时第一项是“自定义”，可以手填地址；否则只能在目录里的 CAN 服务器之间选 ——
    public static readonly ServerChoice CustomServer = new("自定义", null);
    public static readonly ServerChoice DefaultServer = new("CAN", new VoiceServer("CAN", DefaultServerHost, DefaultServerPort));
    public ObservableCollection<ServerChoice> ServerChoices { get; } = [];
    [ObservableProperty] private ServerChoice? _selectedServer;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshServersCommand))]
    private bool _isLoadingServers;
    [ObservableProperty] private string _serverListText = "";
    private (string Host, int Port) _customServer;
    private bool _syncingServer;

    public SettingsViewModel()
    {
        AddPttBinding(JsonSerializer.SerializeToElement(new { kind = "keyboard", key = "v" }));
        SettingsStore.Load(this);
        _customServer = (ServerHost, ServerPort);
        LoadServers([]);
    }

    /// <summary>不开调试端点时只认 CAN 自己的服务器（默认地址或 ceruleanavi.net 的子域名）。</summary>
    public static bool IsOfficialServer(string host) =>
        host == DefaultServerHost || host.EndsWith(".ceruleanavi.net", StringComparison.OrdinalIgnoreCase);

    partial void OnSelectedServerChanged(ServerChoice? value)
    {
        if (_syncingServer || value is null) return;
        var (host, port) = value.Server is { } server ? (server.Host, server.Port) : _customServer;
        _syncingServer = true;
        ServerHost = host;
        ServerPort = port;
        _syncingServer = false;
    }

    partial void OnServerHostChanged(string value) => ServerEdited();
    partial void OnServerPortChanged(int value) => ServerEdited();

    /// <summary>手改地址或端口就回到“自定义”，并记住手填的值。</summary>
    private void ServerEdited()
    {
        if (_syncingServer || !ShowServerEndpoints) return;
        _customServer = (ServerHost, ServerPort);
        if (SelectedServer == CustomServer) return;
        _syncingServer = true;
        SelectedServer = CustomServer;
        _syncingServer = false;
    }

    private bool CanRefreshServers() => !IsLoadingServers;

    /// <summary>刷新服务器列表；不改动当前填的地址，只把下拉框对到匹配的那一项。</summary>
    [RelayCommand(CanExecute = nameof(CanRefreshServers))]
    private async Task RefreshServersAsync()
    {
        IsLoadingServers = true;
        ServerListText = "正在获取服务器列表…";
        var servers = await ServerDirectory.FetchAsync();
        IsLoadingServers = false;
        if (servers is null)
        {
            ServerListText = ShowServerEndpoints ? "服务器列表获取失败，可手动填写地址。" : "服务器列表获取失败。";
            return;
        }
        LoadServers(servers);
        if (servers.Count == 0) ServerListText = ShowServerEndpoints ? "服务器列表为空，可手动填写地址。" : "服务器列表为空。";
    }

    public void LoadServers(IEnumerable<VoiceServer> servers)
    {
        _syncingServer = true;
        ServerChoices.Clear();
        if (ShowServerEndpoints) ServerChoices.Add(CustomServer);
        foreach (var server in servers)
            if (ShowServerEndpoints || IsOfficialServer(server.Host)) ServerChoices.Add(new(server.Label, server));
        if (!ShowServerEndpoints && !ServerChoices.Any(Matches(DefaultServerHost, DefaultServerPort)))
            ServerChoices.Insert(0, DefaultServer);
        var current = ServerChoices.FirstOrDefault(Matches(ServerHost, ServerPort));
        if (current is null && !ShowServerEndpoints)
        {
            // 存的是目录里（还）没有的 CAN 服务器：列表没拿到或刚下线，先照样显示它
            current = new ServerChoice(ServerHost, new VoiceServer(ServerHost, ServerHost, ServerPort));
            ServerChoices.Add(current);
        }
        SelectedServer = current ?? CustomServer;
        _syncingServer = false;
        ServerListText = "";
    }

    private static Func<ServerChoice, bool> Matches(string host, int port) =>
        c => c.Server is { } s && s.Host == host && s.Port == port;
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
            or nameof(SelectedServer) or nameof(IsLoadingServers) or nameof(ServerListText)))
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

    /// <summary>麦克风峰值电平（0~100），电平表尚未接入语音引擎。</summary>
    [ObservableProperty] private double _inputLevel;

    [ObservableProperty] private bool _disableAudioEffects;
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

/// <summary>服务器下拉框的一项；<see cref="Server"/> 为 null 表示“自定义”。</summary>
public sealed record ServerChoice(string Label, VoiceServer? Server)
{
    public override string ToString() => Label;
}
