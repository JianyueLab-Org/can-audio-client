using CommunityToolkit.Mvvm.ComponentModel;

namespace ControllerClient.ViewModels;

/// <summary>内话状态，对应 AFV LandLineStation.callStatus 的几种颜色。</summary>
public enum LandlineState
{
    Idle,
    Calling,
    Incoming,
    Connected,
    Rejected
}

/// <summary>内话（地对地电话）对象，对应 AFV 的 LandLineStation。</summary>
public sealed partial class LandlineViewModel(string callsign) : ViewModelBase
{
    public string Callsign { get; } = callsign;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCalling), nameof(IsIncoming), nameof(IsConnected), nameof(IsRejected), nameof(StateText))]
    private LandlineState _state;

    public bool IsCalling => State == LandlineState.Calling;
    public bool IsIncoming => State == LandlineState.Incoming;
    public bool IsConnected => State == LandlineState.Connected;
    public bool IsRejected => State == LandlineState.Rejected;

    public string StateText => State switch
    {
        LandlineState.Calling => "呼叫中",
        LandlineState.Incoming => "来电",
        LandlineState.Connected => "通话中",
        LandlineState.Rejected => "已拒绝",
        _ => "空闲"
    };
}
