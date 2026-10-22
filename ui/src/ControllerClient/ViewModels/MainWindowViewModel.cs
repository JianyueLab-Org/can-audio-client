using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ControllerClient.Services;

namespace ControllerClient.ViewModels;

/// <summary>
/// 主界面状态，与 AudioForVATSIM MainForm（ATC 模式）一致：
/// 顶部为按钮、席位呼号、无线电 TX/RX 与收发信机信息；“+/-”展开下方的
/// 频率（交叉耦合）面板与内话 / 附加收发信机面板；“↑”切换为置顶小窗。
/// 语音服务器连接、接收与内话均为演示数据。
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    /// <summary>演示用台站数据库：按呼号添加频率时在这里查找。</summary>
    private static readonly Dictionary<string, (string Frequency, string? Alias)> KnownStations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ZBAA_DEL"] = ("121.950", null),
        ["ZBAA_GND"] = ("121.700", null),
        ["ZBAA_TWR"] = ("118.500", null),
        ["ZBAA_APP"] = ("119.000", null),
        ["ZBAA_ATIS"] = ("127.600", null),
        ["ZBAD_TWR"] = ("118.350", null),
        ["ZBPE_CTR"] = ("125.300", null),
        ["ZBPE_FSS"] = ("5.655", "131.050"),
        ["ZSSS_TWR"] = ("118.250", null),
        ["ZGGG_APP"] = ("120.400", null)
    };

    private static readonly string[] DemoTraffic = ["CCA1501", "CES5101", "CSN3101", "CHH7845", "CXA8302", "CPA112"];

    private readonly DispatcherTimer _rxTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly Random _random = new();

    public MainWindowViewModel()
    {
        Stations.CollectionChanged += (_, _) => RefreshStationPage();
        InitializeBackend();
    }

    public string VersionText => "CAN Audio · 0.0.1";

    // —— 连接 ——
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectButtonText), nameof(CallsignText),
        nameof(ShowMoreButton), nameof(ShowStationPanel), nameof(ShowSidePanel))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectButtonText))]
    private bool _isConnecting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CallsignText))]
    private string _callsign = "ZBAA_TWR";

    public string ConnectButtonText => IsConnecting ? "正在连接…" : IsConnected ? "断开" : "连接";
    public string CallsignText => IsConnected ? Callsign : "未连接";

    public async Task ConnectAsync(string callsign, string frequency)
    {
        await ConnectBackendAsync();
    }

    public void Disconnect()
    {
        _ = ExecuteAsync("disconnect");
        ConnectionStatus.SetVoiceState("stopped");
        AppLog.Info("用户断开语音连接");
        _rxTimer.Stop();
        IsConnected = false;
        Stations.Clear();
        TransceiverSource = "- - - -";
        TransceiverCount = null;
        LastReceivedFrequency = "无";
        LastReceivedCallsign = "无";
        IsAddingStation = false;
        IsTransmitting = false;
        IsReceiving = false;
        foreach (var line in Landlines)
            line.State = LandlineState.Idle;
        ActiveLine = null;
    }

    // —— 收发信机信息（AFV lblSource / lblTransCount / lblLastFreq / lblLastSender） ——
    [ObservableProperty] private string _transceiverSource = "- - - -";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransceiverCountText))]
    private int? _transceiverCount;

    public string TransceiverCountText => TransceiverCount?.ToString(CultureInfo.InvariantCulture) ?? "- - -";

    [ObservableProperty] private string _lastReceivedFrequency = "无";
    [ObservableProperty] private string _lastReceivedCallsign = "无";

    /// <summary>ATIS 来源（AFV lblLastUniATIS）。</summary>
    public string AtisSource => "CAN Mumble";

    /// <summary>该席位在数据库中有收发信机（AFV APIDataFound）：决定右侧显示内话还是附加收发信机。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAdditionalTransceivers))]
    private bool _hasApiData;

    public bool ShowAdditionalTransceivers => !HasApiData;

    // —— 无线电指示（AFV “Radio:” com1TxTile / com1RxTile / AudioSourceTile） ——
    /// <summary>正在发射：按住 PTT（演示为右 Ctrl）。</summary>
    [ObservableProperty] private bool _isTransmitting;

    [ObservableProperty] private bool _isReceiving;

    /// <summary>输出到扬声器而不是耳机（AFV AudioSourceTile）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioOutputText))]
    private bool _isSpeakerOutput;

    public string AudioOutputText => IsSpeakerOutput ? "扬声器" : "耳机";

    // —— 窗口模式 ——
    /// <summary>展开下方面板（AFV btnMore：“-”展开，“+”收起成只有顶部信息的窄条）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoreButtonText), nameof(ShowStationPanel), nameof(ShowSidePanel))]
    private bool _isExpanded = true;

    public string MoreButtonText => IsExpanded ? "-" : "+";

    /// <summary>置顶小窗（AFV btnOnTop：“↑”进入，“↓”退出）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OnTopButtonText), nameof(IsNormalLayout), nameof(ShowMoreButton),
        nameof(ShowStationPanel), nameof(ShowSidePanel))]
    private bool _isTopmost;

    public string OnTopButtonText => IsTopmost ? "↓" : "↑";
    public bool IsNormalLayout => !IsTopmost;

    /// <summary>置顶小窗下显示频率面板（AFV btnMiniVCCS）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MiniVccsButtonText), nameof(ShowStationPanel))]
    private bool _isMiniVccs;

    public string MiniVccsButtonText => IsMiniVccs ? "-" : "+";

    partial void OnIsTopmostChanged(bool value) => IsMiniVccs = false;

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void ToggleTopmost() => IsTopmost = !IsTopmost;

    [RelayCommand]
    private void ToggleMiniVccs() => IsMiniVccs = !IsMiniVccs;

    public bool ShowMoreButton => IsConnected && !IsTopmost;
    public bool ShowStationPanel => IsConnected && (IsTopmost ? IsMiniVccs : IsExpanded);
    public bool ShowSidePanel => IsConnected && !IsTopmost && IsExpanded;

    // —— 频率列表（AFV crossCouplePanel，每页 6 个台站） ——
    private const int StationsPerPage = 6;

    public ObservableCollection<StationViewModel> Stations { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PagedStations), nameof(StationPageText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousStationPageCommand), nameof(NextStationPageCommand))]
    private int _stationPage;

    private int StationPageCount => Math.Max(1, (Stations.Count + StationsPerPage - 1) / StationsPerPage);

    public IReadOnlyList<StationViewModel> PagedStations => Stations.Skip(StationPage * StationsPerPage).Take(StationsPerPage).ToList();
    public string StationPageText => $"第 {StationPage + 1}/{StationPageCount} 页";

    private bool CanPreviousStationPage() => StationPage > 0;
    private bool CanNextStationPage() => StationPage < StationPageCount - 1;

    [RelayCommand(CanExecute = nameof(CanPreviousStationPage))]
    private void PreviousStationPage() => StationPage--;

    [RelayCommand(CanExecute = nameof(CanNextStationPage))]
    private void NextStationPage() => StationPage++;

    private void RefreshStationPage()
    {
        if (StationPage > StationPageCount - 1)
            StationPage = StationPageCount - 1;
        OnPropertyChanged(nameof(PagedStations));
        OnPropertyChanged(nameof(StationPageText));
        PreviousStationPageCommand.NotifyCanExecuteChanged();
        NextStationPageCommand.NotifyCanExecuteChanged();
    }

    /// <summary>显示添加台站输入框（AFV ccAddButton “+”）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotAddingStation))]
    private bool _isAddingStation;

    public bool IsNotAddingStation => !IsAddingStation;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddStationCommand))]
    private string _newStationText = string.Empty;

    [RelayCommand]
    private void BeginAddStation()
    {
        NewStationText = string.Empty;
        IsAddingStation = true;
    }

    [RelayCommand]
    private void CancelAddStation() => IsAddingStation = false;

    private bool CanAddStation() => NewStationText.Trim().Length > 0 && !NewStationText.StartsWith('<');

    /// <summary>按呼号或频率添加台站（AFV BtnCCConfirm：找不到时在输入框里显示 &lt;未找到&gt;）。</summary>
    [RelayCommand(CanExecute = nameof(CanAddStation))]
    private void AddStation()
    {
        var text = NewStationText.Trim().ToUpperInvariant();

        if (Stations.Any(s => s.Name == text || s.Frequency == text))
        {
            NewStationText = "<已在列表中>";
            return;
        }

        try
        {
            var position = _directory.Positions.FirstOrDefault(p => p.Callsign.Equals(text, StringComparison.OrdinalIgnoreCase));
            AddRadio(position?.Callsign ?? text, position?.Frequency ?? text);
        }
        catch (FormatException)
        {
            NewStationText = "<未找到>";
            return;
        }

        NewStationText = string.Empty;
        IsAddingStation = false;
        StationPage = StationPageCount - 1;
    }

    [RelayCommand]
    private void RemoveStation(StationViewModel station)
    {
        if (station.CanRemove)
        {
            Stations.Remove(station);
            QueueSync();
        }
    }

    private void AddKnownStation(string name, bool rx)
    {
        var (frequency, alias) = KnownStations[name];
        Stations.Add(new StationViewModel(name.ToUpperInvariant(), frequency, alias) { IsRx = rx });
    }

    /// <summary>演示：随机让一个已打开 RX 的频率接收一段通话。</summary>
    private void SimulateReception()
    {
        // 演示：偶尔有内话来电
        if (HasApiData && ActiveLine is null && _random.Next(8) == 0)
            SimulateIncomingCall();

        foreach (var s in Stations)
            s.ReceivingCallsign = null;
        IsReceiving = false;

        var candidates = Stations.Where(s => s.IsRx).ToList();
        if (candidates.Count == 0 || _random.Next(3) == 0)
            return;

        IsReceiving = true;

        var station = candidates[_random.Next(candidates.Count)];
        var callsign = DemoTraffic[_random.Next(DemoTraffic.Length)];
        station.ReceivingCallsign = callsign;
        LastReceivedFrequency = station.Frequency;
        LastReceivedCallsign = callsign;
    }

    // —— 内话（AFV Land Line Calling，每页 10 个） ——
    private const int LandlinesPerPage = 10;

    public ObservableCollection<LandlineViewModel> Landlines { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PagedLandlines), nameof(LandlinePageText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousLandlinePageCommand), nameof(NextLandlinePageCommand))]
    private int _landlinePage;

    private int LandlinePageCount => Math.Max(1, (Landlines.Count + LandlinesPerPage - 1) / LandlinesPerPage);

    public IReadOnlyList<LandlineViewModel> PagedLandlines => Landlines.Skip(LandlinePage * LandlinesPerPage).Take(LandlinesPerPage).ToList();
    public string LandlinePageText => $"第 {LandlinePage + 1} 页";

    private bool CanPreviousLandlinePage() => LandlinePage > 0;
    private bool CanNextLandlinePage() => LandlinePage < LandlinePageCount - 1;

    [RelayCommand(CanExecute = nameof(CanPreviousLandlinePage))]
    private void PreviousLandlinePage() => LandlinePage--;

    [RelayCommand(CanExecute = nameof(CanNextLandlinePage))]
    private void NextLandlinePage() => LandlinePage++;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptCallCommand), nameof(RejectCallCommand), nameof(EndCallCommand))]
    private LandlineViewModel? _activeLine;

    /// <summary>点击内话按钮：空闲时发起呼叫，否则挂断（同 AFV HandleLLButtonPress）。</summary>
    [RelayCommand]
    private async Task PressLine(LandlineViewModel line)
    {
        if (!IsConnected)
            return;

        if (ActiveLine is not null)
        {
            if (line == ActiveLine)
                EndCall();
            return;
        }

        line.State = LandlineState.Calling;
        ActiveLine = line;

        // 演示：对方 2 秒后接听
        await Task.Delay(2000);
        if (ActiveLine == line && line.State == LandlineState.Calling)
            line.State = LandlineState.Connected;
        RefreshCallCommands();
    }

    private bool CanAcceptCall() => ActiveLine?.State == LandlineState.Incoming;
    private bool CanEndCall() => ActiveLine?.State is LandlineState.Calling or LandlineState.Connected;

    [RelayCommand(CanExecute = nameof(CanAcceptCall))]
    private void AcceptCall()
    {
        ActiveLine!.State = LandlineState.Connected;
        RefreshCallCommands();
    }

    [RelayCommand(CanExecute = nameof(CanAcceptCall))]
    private void RejectCall()
    {
        ActiveLine!.State = LandlineState.Idle;
        ActiveLine = null;
    }

    [RelayCommand(CanExecute = nameof(CanEndCall))]
    private void EndCall()
    {
        if (ActiveLine is not null)
            ActiveLine.State = LandlineState.Idle;
        ActiveLine = null;
    }

    /// <summary>演示：模拟一个来电。</summary>
    private void SimulateIncomingCall()
    {
        if (!IsConnected || ActiveLine is not null)
            return;

        var line = Landlines[_random.Next(Landlines.Count)];
        line.State = LandlineState.Incoming;
        ActiveLine = line;
    }

    private void RefreshCallCommands()
    {
        AcceptCallCommand.NotifyCanExecuteChanged();
        RejectCallCommand.NotifyCanExecuteChanged();
        EndCallCommand.NotifyCanExecuteChanged();
    }

    // —— 附加机场收发信机（AFV Additional Airport Transceivers） ——
    public ObservableCollection<string> AdditionalTransceivers { get; } = ["ZBAD"];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddTransceiverCommand))]
    private string _newTransceiverAirport = string.Empty;

    private bool CanAddTransceiver() => NewTransceiverAirport.Trim().Length == 4;

    [RelayCommand(CanExecute = nameof(CanAddTransceiver))]
    private void AddTransceiver()
    {
        var icao = NewTransceiverAirport.Trim().ToUpperInvariant();
        if (!AdditionalTransceivers.Contains(icao))
            AdditionalTransceivers.Add(icao);
        NewTransceiverAirport = string.Empty;
    }

    [RelayCommand]
    private void RemoveAllTransceivers() => AdditionalTransceivers.Clear();
}
