using Avalonia;
using Avalonia.Media;
using ControllerClient.Services;

namespace ControllerClient;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppLog.Info($"客户端启动 {AppVersion.Full}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Error("未处理异常", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => AppLog.Error("未观察的后台任务异常", e.Exception);
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception ex) { AppLog.Error("客户端异常退出", ex); throw; }
        finally { AppLog.Info("客户端退出"); }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions { WmClass = "can-controller-client", EnableSessionManagement = false })
            .With(new FontManagerOptions { FontFallbacks = FontFallbacks })
            .LogToTrace();

    private static readonly FontFallback[] FontFallbacks =
    [
        new() { FontFamily = new FontFamily("Microsoft YaHei UI") },
        new() { FontFamily = new FontFamily("PingFang SC") },
        new() { FontFamily = new FontFamily("Hiragino Sans GB") },
        new() { FontFamily = new FontFamily("Noto Sans CJK SC") },
        new() { FontFamily = new FontFamily("Noto Sans SC") },
        new() { FontFamily = new FontFamily("Source Han Sans SC") },
        new() { FontFamily = new FontFamily("WenQuanYi Micro Hei") },
        new() { FontFamily = new FontFamily("Segoe UI Symbol") },
        new() { FontFamily = new FontFamily("Apple Symbols") },
        new() { FontFamily = new FontFamily("DejaVu Sans") },
        new() { FontFamily = new FontFamily("Noto Sans Symbols 2") },
        new() { FontFamily = new FontFamily("Noto Sans Symbols") },
    ];
}
