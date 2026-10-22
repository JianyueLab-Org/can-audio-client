using CommunityToolkit.Mvvm.ComponentModel;

namespace ControllerClient.ViewModels;

/// <summary>“连接到语音服务器”对话框：席位呼号与主频率。</summary>
public sealed partial class ConnectViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    private string _callsign = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    private string _frequency = string.Empty;

    public bool IsValid => Callsign.Trim().Length >= 3
                           && decimal.TryParse(Frequency.Trim(), out var f) && f is >= 118m and < 137m;
}
