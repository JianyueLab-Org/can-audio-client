using CommunityToolkit.Mvvm.ComponentModel;

namespace ControllerClient.ViewModels;

/// <summary>
/// 频率列表中的一个台站，对应 AFV 的 CrossCoupleConfiguration：
/// 台站名、频率（可带别名频率）、TX / RX / XC 三个开关。
/// </summary>
public sealed partial class StationViewModel : ViewModelBase
{
    public StationViewModel(string name, string frequency, string? aliasFrequency = null, bool isPrimary = false)
    {
        Name = name;
        Frequency = frequency;
        AliasFrequency = aliasFrequency;
        IsPrimary = isPrimary;
    }

    public string Name { get; }
    public string Frequency { get; }

    /// <summary>别名频率（例如 HF 对应的 VHF 频率），显示在主频率下方括号里。</summary>
    public string? AliasFrequency { get; }

    public bool HasAlias => AliasFrequency is not null;

    /// <summary>自己的主频率，不能删除（同 AFV 第一行不显示删除按钮）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    private bool _isPrimary;

    public bool CanRemove => !IsPrimary;

    /// <summary>ATIS 频率不能发射（同 AFV HandleCCTXButtonPress）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTransmit))]
    private bool _transmitAllowed;
    public bool CanTransmit => TransmitAllowed && !Name.EndsWith("_ATIS", StringComparison.Ordinal);

    // TX 打开时自动打开 RX；RX 关闭时 TX、XC 一并关闭；XC 需要 TX
    [ObservableProperty] private bool _isTx;
    [ObservableProperty] private bool _isRx;
    [ObservableProperty] private bool _isCrossCoupled;

    partial void OnIsTxChanged(bool value)
    {
        if (value && !CanTransmit) { IsTx = false; return; }
        if (value)
            IsRx = true;
        else
            IsCrossCoupled = false;
    }

    partial void OnIsRxChanged(bool value)
    {
        if (value)
            return;
        IsTx = false;
        ReceivingCallsign = null;
    }

    partial void OnIsCrossCoupledChanged(bool value)
    {
        if (value && !CanTransmit) { IsCrossCoupled = false; return; }
        if (value)
            IsTx = true;
    }

    /// <summary>当前正在此频率上发射的呼号，为空表示无人发射。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReceiving))]
    private string? _receivingCallsign;

    public bool IsReceiving => ReceivingCallsign is not null;
}
