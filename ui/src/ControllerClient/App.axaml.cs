using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Controls;
using Avalonia.Platform;
using ControllerClient.ViewModels;
using ControllerClient.Views;

namespace ControllerClient;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            using var iconStream = AssetLoader.Open(new Uri("avares://ControllerClient/app.ico"));
            desktop.MainWindow = new MainWindow
            {
                Icon = new WindowIcon(iconStream),
                DataContext = new MainWindowViewModel()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
