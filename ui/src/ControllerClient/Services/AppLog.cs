using System.Diagnostics;
using System.Text;

namespace ControllerClient.Services;

/// <summary>
/// 本地日期滚动；每个进程独立文件，避免多个实例互相占用。
/// 日志放在程序目录的 logs/；macOS 的 .app 包里不能写（会破坏签名），放 ~/Library/Logs/CAN/AudioClient；
/// Linux 装在 /opt 之类不可写的地方时放 $XDG_STATE_HOME/can-audio-client/logs。
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly string Session = $"{DateTime.Now:HHmmss}-{Environment.ProcessId}";
    public static string DirectoryPath { get; } = ChooseDirectory();

    private static string ChooseDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS()) return Path.Combine(home, "Library", "Logs", "CAN", "AudioClient");
        var local = Path.Combine(AppContext.BaseDirectory, "logs");
        if (OperatingSystem.IsWindows() || IsWritable(local)) return local;
        var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (string.IsNullOrWhiteSpace(state)) state = Path.Combine(home, ".local", "state");
        return Path.Combine(state, "can-audio-client", "logs");
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".probe-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    public static string FilePath(DateTime date, string source) =>
        Path.Combine(DirectoryPath, $"{source}-{date:yyyy-MM-dd}-{Session}.log");

    public static void Info(string message) => Write("client", "INFO", message);
    public static void Error(string message, Exception? error = null) => Write("client", "ERROR", message + (error is null ? "" : Environment.NewLine + error));
    public static void Write(string source, string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var now = DateTime.Now;
                Directory.CreateDirectory(DirectoryPath);
                File.AppendAllText(FilePath(now, source), $"{now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Trace.WriteLine($"Log write failed: {ex.Message}"); }
    }

    public static void OpenFolder()
    {
        Directory.CreateDirectory(DirectoryPath);
        Process.Start(new ProcessStartInfo(DirectoryPath) { UseShellExecute = true });
    }
}
