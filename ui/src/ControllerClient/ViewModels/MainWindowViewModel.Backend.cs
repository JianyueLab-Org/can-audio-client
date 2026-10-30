using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ControllerClient.Services;
using ControllerClient.Services.Ptt;

namespace ControllerClient.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly VoiceBridge _bridge = new();
    private readonly NetworkDirectory _directory = new();
    private readonly DispatcherTimer _feedTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _syncTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private AudioDevices _devices = new();
    private PttWatcher _ptt = null!;
    private PttCapture? _capture;
    private bool _refreshing;
    private bool _applyingDuty;
    private bool _allowed;
    private bool _settingsOpen;
    private string _sessionCid = "";
    private RangePolicySender _range = null!;      // 距离静音快照，不等回执
    private DateTime _lastFeedAt = DateTime.Now;      // 上一次数据源读成功的时间，判断快照是否过期
    private string? _rangeUnavailable;                // 距离静音暂停的原因，变化时写日志
    public SettingsViewModel Settings { get; private set; } = new();
    public ConnectionStatusViewModel ConnectionStatus { get; } = new();
    public ObservableCollection<Position> OnlinePositions { get; } = [];
    [ObservableProperty] private Position? _selectedPosition;
    [ObservableProperty] private string _statusText = "请在设置中填写 CAN 账号";

    private void InitializeBackend()
    {
        // PTT 在 C# 里监听，翻转时只给语音引擎发一条 ptt 指令；松开优先级最高，不能被排在别的 UI 工作后面
        _ptt = new PttWatcher([], active => Dispatcher.UIThread.Post(() => OnPtt(active), DispatcherPriority.Send));
        ConfigureSettings(Settings);
        _bridge.Message += msg => Dispatcher.UIThread.Post(() => HandleMessage(msg));
        // 只发要静音的 CID（引擎对没列出的默认放行），写入不设超时：引擎暂时不读 stdin 时宁可晚到，
        // 也不能因为一份策略把整个引擎结束掉——那等于把管制员踢下语音
        _range = new RangePolicySender(policy =>
            _bridge.SendAsync("range_audio", RangeAudio.ToWire(policy), logFailure: false,
                timeout: TimeSpan.FromSeconds(10), boundWrite: false));
        _feedTimer.Tick += async (_, _) => await RefreshFeedAsync();
        _feedTimer.Start();
        _syncTimer.Tick += async (_, _) => { _syncTimer.Stop(); await SyncAsync(); };
    }

    private void ConfigureSettings(SettingsViewModel model)
    {
        model.CapturePtt = start =>
        {
            if (start) StartPttCapture();
            else StopPttCapture();
            return Task.CompletedTask;
        };
        model.Apply = () => { Settings = model; ConfigureSettings(model); _ = ApplyAudioAsync(); };
    }

    public async Task<SettingsViewModel> OpenSettingsAsync()
    {
        _settingsOpen = true;
        _ptt.Stop();
        await ExecuteAsync("ptt", new { active = false });
        var model = new SettingsViewModel { Password = Settings.Password };
        Settings = model;
        ConfigureSettings(model);
        try
        {
            _devices = AudioDevices.Parse(await _bridge.SendAsync("devices"));
            foreach (var name in _devices.InputNames) model.InputDevices.Add(name);
            foreach (var name in _devices.OutputNames) { model.OutputDevices.Add(name); model.SpeakerDevices.Add(name); }
            // 旧版本存的完整名字对上的是截断后的 MME 名字，下拉框里要选中后者，否则显示成空白
            model.InputDeviceName = _devices.InputName(model.InputDeviceName) ?? model.InputDeviceName;
            model.HeadsetDeviceName = _devices.OutputName(model.HeadsetDeviceName) ?? model.HeadsetDeviceName;
            model.SpeakerDeviceName = _devices.OutputName(model.SpeakerDeviceName) ?? model.SpeakerDeviceName;
        }
        catch (Exception ex) { model.StatusText = ex.Message; }
        model.MarkSaved();
        return model;
    }

    public async Task CloseSettingsAsync()
    {
        StopPttCapture();
        Settings = new SettingsViewModel { Password = Settings.Password };
        ConfigureSettings(Settings);
        _settingsOpen = false;
        await ApplyAudioAsync();
        await RefreshFeedAsync();
    }

    private async Task ConnectBackendAsync()
    {
        if (IsConnecting || IsConnected) return;
        if (string.IsNullOrWhiteSpace(Settings.NetworkId) || string.IsNullOrEmpty(Settings.Password))
            throw new InvalidOperationException("请先在设置 → 用户信息中填写 CAN ID 和密码。");
        IsConnecting = true;
        _sessionCid = Settings.NetworkId.Trim();
        ConnectionStatus.SetVoiceState("connecting");
        AppLog.Info($"请求连接语音服务器 {Settings.ServerHost}:{Settings.ServerPort}");
        try
        {
            // 每次连接重新枚举，避免保存过设备但未打开设置时退回系统默认。
            _devices = AudioDevices.Parse(await _bridge.SendAsync("devices"));
            var input = _devices.Input(Settings.InputDeviceName);
            var output = _devices.Output(Settings.HeadsetDeviceName);
            AppLog.Info($"音频设备：输入 {Settings.InputDeviceName} → {input?.ToString() ?? "默认"}，"
                        + $"输出 {Settings.HeadsetDeviceName} → {output?.ToString() ?? "默认"}");
            var result = await _bridge.SendAsync("connect", new { host = Settings.ServerHost, port = Settings.ServerPort,
                cid = _sessionCid, password = Settings.Password, input, output });
            if (result.ValueKind != JsonValueKind.True) throw new IOException(StatusText);
            IsConnected = true;
            IsExpanded = true;
            Callsign = Settings.NetworkId.Trim();
            await ApplyAudioAsync();
            await RefreshFeedAsync();
            _feedTimer.Start();
        }
        catch (Exception ex) { ConnectionStatus.SetVoiceState("error"); AppLog.Error("语音连接失败", ex); throw; }
        finally { IsConnecting = false; }
    }

    private async Task ApplyAudioAsync()
    {
        _ptt.SetBindings(Settings.PttBindingModels);
        if (!_settingsOpen) _ptt.Start();
        if (!IsConnected) return;
        await ExecuteAsync("audio", new { mic = Math.Min(200, 100 * Math.Pow(10, Settings.InputVolume / 20)),
            speaker = Math.Min(200, 100 * Math.Pow(10, Settings.OutputVolume / 20)), denoise = !Settings.DisableAudioEffects,
            input = _devices.Input(Settings.InputDeviceName),
            output = _devices.Output(IsSpeakerOutput ? Settings.SpeakerDeviceName : Settings.HeadsetDeviceName) });
    }

    partial void OnIsSpeakerOutputChanged(bool value) => _ = ApplyAudioAsync();

    private async Task RefreshFeedAsync()
    {
        if (_refreshing) return;
        var cid = IsConnected ? _sessionCid : Settings.NetworkId.Trim();
        if (cid.Length == 0) return;
        _refreshing = true;
        try
        {
            await _directory.RefreshAsync();
            _lastFeedAt = DateTime.Now;
            var previousFsd = ConnectionStatus.FsdText;
            ConnectionStatus.UpdateFsd(_directory.Roster.ContainsKey(cid), _directory.LastUpdatedAt!.Value, _directory.RequestMilliseconds);
            if (previousFsd != ConnectionStatus.FsdText) AppLog.Info(ConnectionStatus.FsdText);
            OnlinePositions.Clear();
            foreach (var position in _directory.Positions) OnlinePositions.Add(position);
            if (IsConnected)
            {
                ApplyDuty(_directory.Positions.Where(p => p.Cid == _sessionCid).ToList());
                // 每次刷新都推完整快照：变了就立即生效，没变也顺带重发一次，覆盖引擎重连或丢命令
                var policy = RangeAudio.BuildPolicy(_sessionCid, _directory.Peers, out var unavailable);
                NoteRangeAvailability(unavailable);
                SubmitRangePolicy(policy);
            }
        }
        catch (Exception ex)
        {
            ConnectionStatus.FeedFailed();
            AppLog.Error("席位数据更新失败", ex);
            if (IsConnected) ApplyDuty([]);
            StatusText = "席位数据读取失败，暂时仅收听：" + ex.Message;
            // 读不到就保留上一份快照，但不能无限期保留
            if (IsConnected && RangeAudio.PolicyExpired(_lastFeedAt, DateTime.Now) && _range.Latest?.ContainsValue(false) == true)
            {
                NoteRangeAvailability($"数据源已 {(DateTime.Now - _lastFeedAt).TotalSeconds:F0} 秒读取失败");
                SubmitRangePolicy([]);
            }
        }
        finally { _refreshing = false; }
    }

    /// <summary>
    /// 距离静音：把 CID → allow 完整快照交给语音引擎（range_audio）。只交出去、不等回执，
    /// 见 <see cref="RangePolicySender"/>。数据源读取失败时不调这里，引擎里保留上一份。
    /// </summary>
    private void NoteRangeAvailability(string? unavailable)
    {
        if (unavailable == _rangeUnavailable) return;
        AppLog.Info(unavailable is null ? "距离静音已恢复" : $"距离静音暂停（{unavailable}），所有人放行");
        _rangeUnavailable = unavailable;
    }

    private void SubmitRangePolicy(Dictionary<string, bool> policy)
    {
        var previous = _range.Latest;
        if (previous is null || !RangeAudio.SameAs(previous, policy))
        {
            var muted = policy.Where(p => !p.Value).Select(p => _directory.Roster.GetValueOrDefault(p.Key, p.Key)).Order().ToList();
            var before = previous?.Count(p => !p.Value) ?? 0;
            if (muted.Count > 0 || before > 0)
                AppLog.Info(muted.Count == 0 ? "距离静音：没有超出收听范围的人" : $"距离静音：{muted.Count} 人超出收听范围（{string.Join(", ", muted)}）");
        }
        _range.Submit(policy);
    }

    private void ApplyDuty(List<Position> own)
    {
        _applyingDuty = true;
        _allowed = own.Count > 0;
        try
        {
            foreach (var station in Stations)
            {
                station.IsPrimary = own.Any(p => p.Khz == NetworkDirectory.ParseFrequency(station.Frequency));
                station.TransmitAllowed = _allowed;
                if (!_allowed) { station.IsTx = false; station.IsCrossCoupled = false; }
            }
            foreach (var position in own)
            {
                if (Stations.All(s => s.Frequency != position.Frequency))
                {
                    var station = AddRadio(position.Callsign, position.Frequency);
                    station.IsPrimary = true;
                    station.IsTx = true;
                }
            }
            Callsign = own.Count > 0 ? string.Join(" / ", own.Select(p => p.Callsign)) : _sessionCid;
            StatusText = _allowed ? "已连接 · 席位已确认" : "已连接 · 未上管制席位，仅收听";
            TransceiverSource = "CAN 网络";
            TransceiverCount = Stations.Count;
        }
        finally { _applyingDuty = false; }
        QueueSync();
    }

    private StationViewModel AddRadio(string callsign, string frequency)
    {
        var khz = NetworkDirectory.ParseFrequency(frequency);
        var formatted = (khz / 1000m).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        var existing = Stations.FirstOrDefault(s => s.Frequency == formatted);
        if (existing is not null) return existing;
        var station = new StationViewModel(callsign, formatted) { TransmitAllowed = _allowed, IsRx = true };
        station.PropertyChanged += StationChanged;
        Stations.Add(station);
        TransceiverCount = Stations.Count;
        QueueSync();
        return station;
    }

    private void StationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StationViewModel.IsTx) or nameof(StationViewModel.IsRx) or nameof(StationViewModel.IsCrossCoupled)) QueueSync();
    }

    [RelayCommand]
    private void AddOnlinePosition()
    {
        if (SelectedPosition is { } position) AddRadio(position.Callsign, position.Frequency);
    }

    private void QueueSync()
    {
        if (!IsConnected || _applyingDuty) return;
        _syncTimer.Stop(); _syncTimer.Start();
    }

    private async Task SyncAsync()
    {
        if (!IsConnected) return;
        var plan = BuildSyncPlan(Stations, _allowed);
        // 语音引擎照着计划进频道、挂监听、编 VoiceTarget；TX 为空时它会自己松开发话
        await ExecuteAsync("sync", new { rx = plan.Rx, tx = plan.Tx, xc = plan.Xc, selected = plan.Selected,
            locked = plan.Locked, volumes = plan.Volumes });
    }

    /// <summary>界面上的台站 → 推给语音引擎的计划。值守规则全在 <see cref="RadioStack"/> 里。</summary>
    internal static VoiceSyncPlan BuildSyncPlan(IEnumerable<StationViewModel> stations, bool allowed)
    {
        var list = stations.ToList();
        var stack = new RadioStack();
        stack.Load(list.Select(s => new Radio(NetworkDirectory.ParseFrequency(s.Frequency), s.Name)
            { Rx = s.IsRx, Tx = s.IsTx, Xc = s.IsCrossCoupled }));
        stack.SelectedKhz = list.FirstOrDefault(s => s.IsPrimary) is { } primary ? NetworkDirectory.ParseFrequency(primary.Frequency) : null;
        stack.SetLocked(list.Where(s => s.IsPrimary).Select(s => NetworkDirectory.ParseFrequency(s.Frequency)));
        stack.SetTransmitAllowed(allowed);
        return stack.ToSyncPlan();
    }

    private void OnPtt(bool active)
    {
        // 松开永远要送到：设置窗口打开时只挡按下
        if (IsConnected && (!active || !_settingsOpen)) _ = ExecuteAsync("ptt", new { active = _allowed && active });
    }

    private void StartPttCapture()
    {
        _capture?.Stop();
        _ptt.Stop(); // 录制时按下的那一下不能真的发出去
        _capture = new PttCapture(binding => Dispatcher.UIThread.Post(() =>
        {
            if (!_settingsOpen || !Settings.IsWaitingForPtt) return;
            Settings.AddPttBinding(binding);
            Settings.IsWaitingForPtt = false;
            StopPttCapture();
        }));
        _capture.Start();
        // macOS 缺输入监控权限、Linux 不在 input 组时，录不到任何按键，要告诉用户为什么
        if (InputHooks.Problem is { } problem) Settings.PttStatusText = problem;
    }

    private void StopPttCapture()
    {
        _capture?.Stop();
        _capture = null;
    }

    private async Task ExecuteAsync(string command, object? data = null)
    {
        try { await _bridge.SendAsync(command, data); }
        catch (Exception ex) { StatusText = ex.Message; }
    }

    [RelayCommand]
    private void OpenLogs()
    {
        try { AppLog.OpenFolder(); }
        catch (Exception ex) { StatusText = "无法打开日志文件夹：" + ex.Message; AppLog.Error(StatusText, ex); }
    }

    private void HandleMessage(JsonElement msg)
    {
        if (!msg.TryGetProperty("event", out var kind)) return;
        switch (kind.GetString())
        {
            case "connection": ConnectionStatus.UpdateVoice(msg); break;
            case "state":
            case "fatal":
                StatusText = msg.GetProperty("message").GetString() ?? "";
                var state = kind.GetString() == "fatal" ? "error" : msg.GetProperty("state").GetString() ?? "";
                ConnectionStatus.SetVoiceState(state);
                AppLog.Info($"语音状态 {state}：{StatusText}");
                // 重连后再推一次：引擎会保留快照，但协议要求 online 后重发，防止中途丢了
                if (state == "online" && IsConnected) _range.Resend();
                if (ConnectionStatusViewModel.IsTerminalVoiceState(state))
                {
                    _range.Clear();
                    _rangeUnavailable = null;
                    IsConnected = false; IsTransmitting = false; IsReceiving = false;
                    Stations.Clear(); _syncTimer.Stop();
                }
                break;
            case "rx":
                var frequency = (msg.GetProperty("khz").GetInt32() / 1000m).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
                var radio = Stations.FirstOrDefault(s => s.Frequency == frequency);
                var cid = msg.GetProperty("callsign").GetString() ?? "";
                var callsign = _directory.Roster.GetValueOrDefault(cid, cid);
                if (radio is not null) radio.ReceivingCallsign = msg.GetProperty("active").GetBoolean() ? callsign : null;
                IsReceiving = Stations.Any(s => s.IsReceiving);
                if (msg.GetProperty("active").GetBoolean()) { LastReceivedFrequency = frequency; LastReceivedCallsign = callsign; }
                break;
            case "tx": IsTransmitting = msg.GetProperty("active").GetBoolean(); break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _range.Clear();
        _feedTimer.Stop(); _syncTimer.Stop(); _directory.Dispose();
        StopPttCapture();
        _ptt.Dispose();
        await _bridge.DisposeAsync();
    }
}
