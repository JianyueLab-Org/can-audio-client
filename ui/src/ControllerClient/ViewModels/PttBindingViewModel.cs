using System.Text.Json;
using ControllerClient.Services.Ptt;

namespace ControllerClient.ViewModels;

/// <summary>设置列表里的一条 PTT 绑定。</summary>
public sealed class PttBindingViewModel
{
    public PttBindingViewModel(PttBinding binding)
    {
        Model = binding;
        Binding = binding.ToJson();
        Label = binding.Kind switch
        {
            PttKind.Keyboard => "键盘 · " + (binding.Key switch
            {
                "ctrl_l" => "左 Ctrl", "ctrl_r" => "右 Ctrl",
                "shift" or "shift_l" => "左 Shift", "shift_r" => "右 Shift",
                "alt_l" => "左 Alt", "alt_r" => "右 Alt", "space" => "空格",
                _ => binding.Key.ToUpperInvariant()
            }),
            PttKind.Mouse => "鼠标 · 侧键 " + (binding.Key == "x1" ? "1" : "2"),
            _ => $"{(binding.DeviceName.Length > 0 ? binding.DeviceName : "摇杆 " + binding.Device)} · 按钮 {binding.Button + 1}"
        };
    }

    public PttBinding Model { get; }
    public JsonElement Binding { get; }
    public string Identity => Model.Identity;
    public string Label { get; }
}
