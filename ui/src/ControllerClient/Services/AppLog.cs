using System.Diagnostics;
using System.Text;

namespace ControllerClient.Services;

/// <summary>本地日期滚动；每个进程独立文件，避免多个实例互相占用。</summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly string Session = $"{DateTime.Now:HHmmss}-{Environment.ProcessId}";
    public static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "logs");
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
