using Avalonia.Controls;
using Avalonia.Interactivity;
using ControllerClient.ViewModels;
using ControllerClient.Views.Settings;

namespace ControllerClient.Views;

public partial class SettingsWindow : Window
{
    private readonly Dictionary<string, Control> _panels = [];

    public SettingsWindow()
    {
        InitializeComponent();
        Menu.SelectedIndex = 0;
    }

    private SettingsViewModel Vm => (SettingsViewModel)DataContext!;

    private void OnMenuSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Menu.SelectedItem is not ListBoxItem { Tag: string tag })
            return;

        if (!_panels.TryGetValue(tag, out var panel))
        {
            panel = CreatePanel(tag);
            _panels[tag] = panel;
        }

        PanelHost.Content = panel;
    }

    private static Control CreatePanel(string tag) => tag switch
    {
        "User" => new UserPage(),
        "Audio" => new AudioPage(),
        "Ptt" => new PttPage(),
        "UniAtis" => new UniAtisPage(),
        "StreamDeck" => new StreamDeckPage(),
        _ => throw new ArgumentOutOfRangeException(nameof(tag), tag, null)
    };

    // 界面原型：设置尚未持久化，仅重置“应用”按钮状态
    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        if (Save()) Close(true);
    }

    private bool Save()
    {
        try { Services.SettingsStore.Save(Vm); Vm.Apply?.Invoke(); return true; }
        catch (Exception ex) { Vm.StatusText = ex.Message; return false; }
    }
    private void OnApplyClick(object? sender, RoutedEventArgs e) => Save();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
