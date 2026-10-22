using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ControllerClient.Services;

namespace ControllerClient.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly VoiceBridge _bridge = new();
    private readonly NetworkDirectory _directory = new();
    private readonly DispatcherTimer _feedTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _syncTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Dictionary<string, int> _inputs = [];
    private readonly Dictionary<string, int> _outputs = [];
    private bool _refreshing;
    private bool _applyingDuty;
    private bool _allowed;
    private bool _settingsOpen;
    private string _sessionCid = "";
    public SettingsViewModel Settings { get; private set; } = new();
    public ConnectionStatusViewModel ConnectionStatus { get; } = new();
    public ObservableCollection<Position> OnlinePositions { get; } = [];
    [ObservableProperty] private Position? _selectedPosition;
    [ObservableProperty] private string _statusText = "请在设置中填写 CAN 账号";

    private void InitializeBackend()
    {
        ConfigureSettings(Settings);
        _bridge.Message += msg => Dispatcher.UIThread.Post(() => HandleMessage(msg));
        _feedTimer.Tick += async (_, _) => await RefreshFeedAsync();
        _feedTimer.Start();
        _syncTimer.Tick += async (_, _) => { _syncTimer.Stop(); await SyncAsync(); };
    }

    private void ConfigureSettings(SettingsViewModel model)
    {
        model.BridgeCommand = async command => { await _bridge.SendAsync(command); };
        model.Apply = () => { Settings = model; ConfigureSettings(model); _ = ApplyAudioAsync(); };
    }

    public async Task<SettingsViewModel> OpenSettingsAsync()
    {
        _settingsOpen = true;
        await ExecuteAsync("ptt", new { active = false });
        var model = new SettingsViewModel { Password = Settings.Password };
        Settings = model;
        ConfigureSettings(model);
        try
        {
            var devices = await _bridge.SendAsync("devices");
            _inputs.Clear(); _outputs.Clear();
            foreach (var device in devices.EnumerateArray())
            {
                var name = device.GetProperty("name").GetString()!;
                var index = device.GetProperty("index").GetInt32();
                var label = name;
                if (device.GetProperty("input").GetBoolean()) { _inputs[label] = index; model.InputDevices.Add(label); }
                if (device.GetProperty("output").GetBoolean()) { _outputs[label] = index; model.OutputDevices.Add(label); model.SpeakerDevices.Add(label); }
            }
        }
        catch (Exception ex) { model.StatusText = ex.Message; }
        model.MarkSaved();
        return model;
    }

    public async Task CloseSettingsAsync()
    {
        await ExecuteAsync("cancel_capture");
        Settings = new SettingsViewModel { Password = Settings.Password };
        ConfigureSettings(Settings);
        _settingsOpen = false;
        await ApplyAudioAsync();
        await RefreshFeedAsync();
    }

    private int? Device(Dictionary<string, int> devices, string? name) => name is not null && devices.TryGetValue(name, out var index) ? index : null;

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
            var devices = await _bridge.SendAsync("devices");
            _inputs.Clear(); _outputs.Clear();
            foreach (var device in devices.EnumerateArray())
            {
                var name = device.GetProperty("name").GetString()!;
                var index = device.GetProperty("index").GetInt32();
                if (device.GetProperty("input").GetBoolean()) _inputs[name] = index;
                if (device.GetProperty("output").GetBoolean()) _outputs[name] = index;
            }
            var result = await _bridge.SendAsync("connect", new { host = Settings.ServerHost, port = Settings.ServerPort,
                cid = _sessionCid, password = Settings.Password,
                input = Device(_inputs, Settings.InputDeviceName), output = Device(_outputs, Settings.HeadsetDeviceName) });
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
        await ExecuteAsync("watch", new { bindings = Settings.PttBindings });
        if (!IsConnected) return;
        await ExecuteAsync("audio", new { mic = Math.Min(200, 100 * Math.Pow(10, Settings.InputVolume / 20)),
            speaker = Math.Min(200, 100 * Math.Pow(10, Settings.OutputVolume / 20)), denoise = !Settings.DisableAudioEffects,
            input = Device(_inputs, Settings.InputDeviceName),
            output = Device(_outputs, IsSpeakerOutput ? Settings.SpeakerDeviceName : Settings.HeadsetDeviceName) });
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
            var previousFsd = ConnectionStatus.FsdText;
            ConnectionStatus.UpdateFsd(_directory.Roster.ContainsKey(cid), _directory.LastUpdatedAt!.Value, _directory.RequestMilliseconds);
            if (previousFsd != ConnectionStatus.FsdText) AppLog.Info(ConnectionStatus.FsdText);
            OnlinePositions.Clear();
            foreach (var position in _directory.Positions) OnlinePositions.Add(position);
            if (IsConnected) ApplyDuty(_directory.Positions.Where(p => p.Cid == _sessionCid).ToList());
        }
        catch (Exception ex)
        {
            ConnectionStatus.FeedFailed();
            AppLog.Error("席位数据更新失败", ex);
            if (IsConnected) ApplyDuty([]);
            StatusText = "席位数据读取失败，暂时仅收听：" + ex.Message;
        }
        finally { _refreshing = false; }
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
        var radios = Stations.Select(s => new { frequency = NetworkDirectory.ParseFrequency(s.Frequency), callsign = s.Name,
            rx = s.IsRx, tx = _allowed && s.IsTx, xc = _allowed && s.IsCrossCoupled, volume = 100, muted = false }).ToArray();
        var primary = Stations.FirstOrDefault(s => s.IsPrimary);
        await ExecuteAsync("sync", new { radios, allowed = _allowed,
            locked = Stations.Where(s => s.IsPrimary).Select(s => NetworkDirectory.ParseFrequency(s.Frequency)).ToArray(),
            selected = primary is null ? (int?)null : NetworkDirectory.ParseFrequency(primary.Frequency) });
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
                if (kind.GetString() == "fatal" || msg.GetProperty("state").GetString() is "error" or "stopped")
                {
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
            case "ptt":
                if (!_settingsOpen && IsConnected) _ = ExecuteAsync("ptt", new { active = _allowed && msg.GetProperty("active").GetBoolean() });
                break;
            case "captured":
                if (!_settingsOpen || !Settings.IsWaitingForPtt) break;
                Settings.AddPttBinding(msg.GetProperty("binding"));
                Settings.IsWaitingForPtt = false;
                _ = ExecuteAsync("cancel_capture");
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _feedTimer.Stop(); _syncTimer.Stop(); _directory.Dispose();
        await _bridge.DisposeAsync();
    }
}
