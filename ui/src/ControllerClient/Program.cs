using Avalonia;
using ControllerClient.Services;

namespace ControllerClient;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppLog.Info("客户端启动");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Error("未处理异常", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => AppLog.Error("未观察的后台任务异常", e.Exception);
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception ex) { AppLog.Error("客户端异常退出", ex); throw; }
        finally { AppLog.Info("客户端退出"); }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
