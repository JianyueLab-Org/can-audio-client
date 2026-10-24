using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text;

namespace ControllerClient.Services;

/// <summary>
/// 通过标准输入输出调用 Python 语音引擎，凭据不进入命令行。
/// 引擎不在本仓库：发行包里是打包好的 controller/can-voice(.exe)；从源码跑时用
/// JianyueLab-Org/can-audio（new-client-sys 分支）的 controller/bridge.py，
/// 默认找仓库里的 can-audio/（sync-voice 脚本拉取），或用 CAN_AUDIO_DIR 指向别处的检出。
/// </summary>
public sealed class VoiceBridge : IAsyncDisposable
{
    private Process? _process;
    private readonly SemaphoreSlim _write = new(1);
    private readonly object _lifecycle = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private int _sequence;
    private bool _stopping;
    public event Action<JsonElement>? Message;
    /// <summary>打包好的语音引擎文件名：Windows 带 .exe，macOS / Linux 没有扩展名。</summary>
    public static string EngineName { get; } = OperatingSystem.IsWindows() ? "can-voice.exe" : "can-voice";
    /// <summary>发行包目录（含 controller/can-voice），从源码跑时是本仓库根目录。</summary>
    public static string Root { get; } = FindRoot();
    /// <summary>
    /// can-audio 检出目录；没有时为 null。每次启动引擎时重新读 CAN_AUDIO_DIR：做成静态初始化的话，
    /// JIT 可以在用到这个类型的方法一进入时就初始化它，比调用方设置环境变量还早。
    /// </summary>
    public static string? VoiceRepo => FindVoiceRepo();

    private static IEnumerable<DirectoryInfo> Ancestors()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                yield return dir;
    }

    private static string FindRoot() =>
        Ancestors().FirstOrDefault(dir => File.Exists(Path.Combine(dir.FullName, "controller", EngineName)) ||
                                          File.Exists(Path.Combine(dir.FullName, "can-audio", "controller", "bridge.py")))
            ?.FullName ?? AppContext.BaseDirectory;

    private static string? FindVoiceRepo()
    {
        var configured = Environment.GetEnvironmentVariable("CAN_AUDIO_DIR");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        return Ancestors().Select(dir => Path.Combine(dir.FullName, "can-audio"))
            .FirstOrDefault(path => File.Exists(Path.Combine(path, "controller", "bridge.py")));
    }

    public Task StartAsync()
    {
        StartProcess();
        return Task.CompletedTask;
    }

    /// <summary>确保引擎在跑，并在同一把锁里取出这一次要写的进程，免得并发重启把它换掉。</summary>
    private Process StartProcess()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            StartCore();
            return _process!;
        }
    }

    private void StartCore()
    {
        if (_process is { HasExited: false }) return;
        // 旧读取任务可能尚未退出；其请求不能遗留到新进程的生命周期中。
        if (_process is not null) FailPending(new IOException("Python 语音引擎已退出。"));
        var configured = Environment.GetEnvironmentVariable("CAN_PYTHON");
        var bundled = Path.Combine(Root, "controller", EngineName);
        var useBundled = configured is null && File.Exists(bundled);
        var repo = VoiceRepo;
        var bridge = repo is null ? null : Path.Combine(repo, "controller", "bridge.py");
        if (!useBundled && (bridge is null || !File.Exists(bridge)))
            throw new FileNotFoundException($"找不到语音引擎：发行包缺少 controller/{EngineName}；从源码运行请先执行 sync-voice 脚本拉取 can-audio，或设置 CAN_AUDIO_DIR。");
        var venvPython = OperatingSystem.IsWindows() ? Path.Combine(".venv", "Scripts", "python.exe") : Path.Combine(".venv", "bin", "python");
        var python = configured ?? new[] { repo, Root }.Where(dir => dir is not null)
            .Select(dir => Path.Combine(dir!, venvPython)).FirstOrDefault(File.Exists)
            ?? (OperatingSystem.IsWindows() ? "python" : "python3");
        var info = new ProcessStartInfo(useBundled ? bundled : python)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = useBundled ? Root : Path.GetDirectoryName(bridge)!
        };
        if (!useBundled)
        {
            info.ArgumentList.Add("-u");
            info.ArgumentList.Add(bridge!);
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
            catch (Exception ex) { AppLog.Error("语音桥接读取失败", ex); }
            finally
            {
                AppLog.Info("语音引擎输出已关闭");
                lock (_lifecycle)
                {
                    if (ReferenceEquals(_process, process))
                    {
                        FailPending(new IOException("Python 语音引擎已退出，请检查 logs 文件夹和 Python 依赖。"));
                        if (!_stopping) Message?.Invoke(JsonSerializer.SerializeToElement(new { @event = "fatal", message = "语音引擎已退出，请检查 logs 文件夹。" }));
                    }
                }
            }
        });
    }

    private void FailPending(Exception error)
    {
        foreach (var pair in _pending)
            if (_pending.TryRemove(pair.Key, out var completion)) completion.TrySetException(error);
    }

    /// <param name="boundWrite">
    /// true（默认）：超时也覆盖写入管道，写不进去就结束引擎——半条 JSON 留在管道里会让之后每条命令都读错。
    /// false：写入只在关闭时取消，超时只管回执。给可以晚到、但绝不能为它丢掉整条语音连接的命令用
    /// （距离静音策略）：写不进去说明引擎此刻不读 stdin，这时任何命令都写不进去，等它读就是了。
    /// </param>
    public async Task<JsonElement> SendAsync(string command, object? data = null, bool logFailure = true,
        TimeSpan? timeout = null, bool boundWrite = true)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(90));
        var writeToken = boundWrite ? deadline.Token : _shutdown.Token;
        var process = StartProcess();
        var id = Interlocked.Increment(ref _sequence);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await _write.WaitAsync(writeToken);
            try
            {
                writeToken.ThrowIfCancellationRequested();
                // 写入超时可能留下半条 JSON；结束此进程并等写入退出，再释放写锁。
                var write = process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, command, data }));
                try { await write.WaitAsync(writeToken); }
                catch
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    try { await write; } catch { }
                    throw;
                }
            }
            finally { _write.Release(); }
            return await completion.Task.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested)
        {
            var error = new TimeoutException($"语音命令超时：{command}");
            if (logFailure) AppLog.Error(error.Message, error);
            throw error;
        }
        catch (Exception ex) { if (logFailure) AppLog.Error($"语音命令失败：{command}", ex); throw; }
        finally { _pending.TryRemove(id, out _); }
    }

    public async ValueTask DisposeAsync()
    {
        Process? process;
        lock (_lifecycle)
        {
            if (_stopping) return;
            _stopping = true;
            process = _process;
        }
        _shutdown.Cancel();
        await _write.WaitAsync();
        try
        {
            if (process is { HasExited: false })
            {
                process.StandardInput.Close();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            }
        }
        finally { _write.Release(); }
        // 读取任务仍可能查询 HasExited，Process 随桥接对象一起回收。
    }
}
