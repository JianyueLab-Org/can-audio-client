using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text;

namespace ControllerClient.Services;

/// <summary>通过标准输入输出调用现有 Mumble 引擎，凭据不进入命令行。</summary>
public sealed class VoiceBridge : IAsyncDisposable
{
    private Process? _process;
    private readonly SemaphoreSlim _write = new(1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private int _sequence;
    private bool _stopping;
    public event Action<JsonElement>? Message;
    public static string Root { get; } = FindRoot();
    private static string FindRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "controller", "bridge.py")) ||
                    File.Exists(Path.Combine(dir.FullName, "controller", "can-voice.exe"))) return dir.FullName;
        return AppContext.BaseDirectory;
    }

    public async Task StartAsync()
    {
        if (_process is { HasExited: false }) return;
        var configured = Environment.GetEnvironmentVariable("CAN_PYTHON");
        var local = Path.Combine(Root, ".venv", "Scripts", "python.exe");
        if (!File.Exists(local))
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, ".venv", "Scripts", "python.exe");
                if (File.Exists(candidate)) { local = candidate; break; }
            }
        var bundled = Path.Combine(Root, "controller", "can-voice.exe");
        var useBundled = configured is null && File.Exists(bundled);
        var info = new ProcessStartInfo(useBundled ? bundled : configured ?? (File.Exists(local) ? local : "python"))
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Root
        };
        if (!useBundled)
        {
            info.ArgumentList.Add("-u");
            info.ArgumentList.Add(Path.Combine(Root, "controller", "bridge.py"));
        }
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        _stopping = false;
        _process = Process.Start(info) ?? throw new IOException("无法启动 Python 语音引擎");
        AppLog.Info($"语音引擎启动，PID {_process.Id}");
        var process = _process;
        _ = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line) AppLog.Write("voice", "PYTHON", line);
        });
        _ = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    using var doc = JsonDocument.Parse(line);
                    var msg = doc.RootElement.Clone();
                    if (msg.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && _pending.TryRemove(id.GetInt32(), out var completion))
                    {
                        if (msg.TryGetProperty("error", out var error)) completion.TrySetException(new IOException(error.GetString()));
                        else completion.TrySetResult(msg.GetProperty("result").Clone());
                    }
                    else Message?.Invoke(msg);
                }
            }
            catch (Exception ex) { AppLog.Error("语音桥接读取失败", ex); FailPending(ex); }
            finally
            {
                AppLog.Info("语音引擎输出已关闭");
                FailPending(new IOException("Python 语音引擎已退出，请检查 logs 文件夹和 Python 依赖。"));
                if (!_stopping) Message?.Invoke(JsonSerializer.SerializeToElement(new { @event = "fatal", message = "语音引擎已退出，请检查 logs 文件夹。" }));
            }
        });
    }

    private void FailPending(Exception error)
    {
        foreach (var pair in _pending)
            if (_pending.TryRemove(pair.Key, out var completion)) completion.TrySetException(error);
    }

    public async Task<JsonElement> SendAsync(string command, object? data = null)
    {
        await StartAsync();
        var id = Interlocked.Increment(ref _sequence);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await _write.WaitAsync();
            try { await _process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, command, data })); }
            finally { _write.Release(); }
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(90));
        }
        catch (Exception ex) { AppLog.Error($"语音命令失败：{command}", ex); throw; }
        finally { _pending.TryRemove(id, out _); }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping = true;
        if (_process is { HasExited: false } process)
        {
            process.StandardInput.Close();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { process.Kill(entireProcessTree: true); }
        }
        _process?.Dispose();
    }
}
