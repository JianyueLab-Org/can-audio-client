namespace ControllerClient.Services.Ptt;

/// <summary>
/// 全局键盘 / 鼠标侧键监听，按系统选实现：Windows 低级钩子、macOS 事件 tap、Linux evdev。
/// 三者都只听不拦，回调参数相同：handler(按下?, 规范化键名或 "x1"/"x2")。
/// </summary>
internal static class InputHooks
{
    private static string? _problem;

    /// <summary>
    /// 装不上监听的原因（缺权限之类），给设置页显示；没出过问题是 null。
    /// 不会自动清掉：两种权限问题都要重启客户端（macOS）或重新登录（Linux 的 input 组）才能解决。
    /// </summary>
    public static string? Problem => Volatile.Read(ref _problem);

    internal static void Report(string problem)
    {
        if (Interlocked.Exchange(ref _problem, problem) != problem) AppLog.Write("client", "WARN", problem);
    }

    public static IDisposable Keyboard(Action<bool, string> handler) =>
        OperatingSystem.IsWindows() ? LowLevelHook.Keyboard(handler)
        : OperatingSystem.IsMacOS() ? MacEventTap.Keyboard(handler)
        : LinuxInput.Keyboard(handler);

    public static IDisposable Mouse(Action<bool, string> handler) =>
        OperatingSystem.IsWindows() ? LowLevelHook.Mouse(handler)
        : OperatingSystem.IsMacOS() ? MacEventTap.Mouse(handler)
        : LinuxInput.Mouse(handler);
}
