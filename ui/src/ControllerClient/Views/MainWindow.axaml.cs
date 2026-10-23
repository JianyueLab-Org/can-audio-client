using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using ControllerClient.ViewModels;

namespace ControllerClient.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closed += async (_, _) => await Vm.DisposeAsync();
    }

    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;

    /// <summary>
    /// 在窗口内的遮罩层上显示模态对话框，返回用户点击的按钮。
    /// 窗口随模式缩到很小（未连接时只有顶部一行），显示对话框期间临时撑大到能容纳对话框。
    /// </summary>
    public async Task<DialogResult> ShowDialogAsync(DialogView dialog)
    {
        DialogLayer.Children.Add(dialog);
        DialogLayer.IsVisible = true;
        dialog.Measure(Size.Infinity);
        Root.MinWidth = dialog.DesiredSize.Width + 40;
        Root.MinHeight = dialog.DesiredSize.Height + 40;
        dialog.Focus();
        try
        {
            return await dialog.Result;
        }
        finally
        {
            DialogLayer.Children.Remove(dialog);
            DialogLayer.IsVisible = DialogLayer.Children.Count > 0;
            Root.MinWidth = 0;
            Root.MinHeight = 0;
        }
    }

    // 无系统标题栏：在顶部空白处按下左键即可拖动窗口
    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.IsConnected)
        {
            Vm.Disconnect();
            return;
        }

        try
        {
            await Vm.ConnectAsync("", "");
        }
        catch (Exception ex)
        {
            await ShowDialogAsync(new DialogView("连接失败", new TextBlock { Text = ex.Message, MaxWidth = 360, TextWrapping = TextWrapping.Wrap }, null, closeText: "确定"));
        }
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        // 模态设置窗口打开期间，主窗口蒙上遮罩，明确区分当前操作的窗口
        DialogLayer.IsVisible = true;
        try
        {
            var settings = await Vm.OpenSettingsAsync();
            await new SettingsWindow { DataContext = settings }.ShowDialog(this);
        }
        finally
        {
            DialogLayer.IsVisible = DialogLayer.Children.Count > 0;
            await Vm.CloseSettingsAsync();
        }
    }

    private void OnNewStationKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Vm.CancelAddStationCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter || !Vm.AddStationCommand.CanExecute(null))
            return;
        Vm.AddStationCommand.Execute(null);
        e.Handled = true;
    }

    // 输入框里显示的是 <未找到> 之类的提示时，重新聚焦即清空（同 AFV tbCCEntry）
    private void OnNewStationGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (Vm.NewStationText.StartsWith('<'))
            Vm.NewStationText = string.Empty;
    }

    private void OnNewTransceiverKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !Vm.AddTransceiverCommand.CanExecute(null))
            return;
        Vm.AddTransceiverCommand.Execute(null);
        e.Handled = true;
    }

    private async void OnMapClick(object? sender, RoutedEventArgs e)
    {
        var body = new TextBlock
        {
            Text = "界面原型：地图将在浏览器中显示当前收发信机覆盖范围。",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 280
        };
        await ShowDialogAsync(new DialogView("地图", body, null, closeText: "确定"));
    }
}
