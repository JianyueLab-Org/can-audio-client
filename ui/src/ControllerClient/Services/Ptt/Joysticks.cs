using System.Runtime.InteropServices;

namespace ControllerClient.Services.Ptt;

internal sealed record JoystickState(int Index, string Name, bool[] Buttons);

/// <summary>
/// 摇杆只能轮询。SDL 整个进程只在一条常驻线程上初始化并轮询：
/// SDL_Init 给 DirectInput 建的隐藏辅助窗口属于建它的线程，线程一退出窗口就没了，
/// 之后每次开摇杆都以 E_HANDLE 失败，直到重启客户端。所以这条线程和进程同寿。
/// 设备序号、按钮序号与旧 Python 引擎（pygame，同为 SDL2）一致，老绑定照样能用。
/// </summary>
internal static class Joysticks
{
    // 目标是一帧音频（20 ms）以内；Windows 默认计时器粒度 15.6 ms，Sleep(20) 实际会睡到 31 ms
    private const int PollMilliseconds = 15;
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(3); // 热插拔在飞行摇杆上很常见
    private static readonly object Gate = new();
    private static readonly List<Subscription> Subscribers = [];
    private static readonly AutoResetEvent Wake = new(false);
    private static Thread? _thread;
    private static int _version;

    /// <param name="wanted">要打开的设备序号；返回 null 表示全部（录制用）。</param>
    /// <param name="tick">每轮在轮询线程上回调一次，参数是已打开设备的按钮状态。</param>
    public static IDisposable Subscribe(Func<IReadOnlyCollection<int>?> wanted, Action<IReadOnlyList<JoystickState>> tick)
    {
        var subscription = new Subscription(wanted, tick);
        lock (Gate)
        {
            Subscribers.Add(subscription);
            _version++;
            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "ptt-joystick" };
                _thread.Start();
            }
        }
        Wake.Set();
        return subscription;
    }

    private sealed class Subscription(Func<IReadOnlyCollection<int>?> wanted, Action<IReadOnlyList<JoystickState>> tick) : IDisposable
    {
        public Func<IReadOnlyCollection<int>?> Wanted { get; } = wanted;
        public Action<IReadOnlyList<JoystickState>> Tick { get; } = tick;
        public void Dispose() { lock (Gate) { Subscribers.Remove(this); _version++; } }
    }

    private static void Run()
    {
        if (!Initialize()) return;
        var sticks = new Dictionary<int, (IntPtr Handle, string Name)>();
        var reported = new HashSet<int>(); // 打不开只报一次，否则一晚上的日志全是同一行
        var lastOpen = DateTime.MinValue;
        var seenVersion = -1;
        while (true)
        {
            Subscription[] subscribers;
            int version;
            lock (Gate) { subscribers = [.. Subscribers]; version = _version; }
            if (subscribers.Length == 0)
            {
                foreach (var stick in sticks.Values) SDL_JoystickClose(stick.Handle);
                sticks.Clear();
                Wake.WaitOne();
                continue;
            }
            if (version != seenVersion) { seenVersion = version; lastOpen = DateTime.MinValue; }
            try
            {
                PumpWindowMessages();
                SDL_JoystickUpdate();
                var count = SDL_NumJoysticks();
                var wanted = new HashSet<int>();
                foreach (var subscriber in subscribers)
                {
                    var indexes = subscriber.Wanted();
                    if (indexes is null) wanted.UnionWith(Enumerable.Range(0, Math.Max(0, count)));
                    else wanted.UnionWith(indexes);
                }
                foreach (var (index, stick) in sticks.ToList())
                {
                    if (wanted.Contains(index) && SDL_JoystickGetAttached(stick.Handle) != 0) continue;
                    SDL_JoystickClose(stick.Handle);
                    sticks.Remove(index);
                }
                if (wanted.Any(index => !sticks.ContainsKey(index)) && DateTime.UtcNow - lastOpen >= Retry)
                {
                    lastOpen = DateTime.UtcNow;
                    foreach (var index in wanted.Where(index => !sticks.ContainsKey(index)).Order())
                    {
                        var handle = index < count ? SDL_JoystickOpen(index) : IntPtr.Zero;
                        if (handle == IntPtr.Zero)
                        {
                            if (reported.Add(index))
                                AppLog.Write("client", "WARN", $"无法打开摇杆 {index}：{(index < count ? Error() : "设备不存在")}");
                            continue;
                        }
                        reported.Remove(index);
                        var name = Marshal.PtrToStringUTF8(SDL_JoystickName(handle)) ?? "";
                        sticks[index] = (handle, name);
                        AppLog.Info($"摇杆 {index} 已打开：{name}（{SDL_JoystickNumButtons(handle)} 个按钮）");
                    }
                }
                var states = sticks.Select(pair =>
                {
                    var buttons = new bool[Math.Max(0, SDL_JoystickNumButtons(pair.Value.Handle))];
                    for (var i = 0; i < buttons.Length; i++) buttons[i] = SDL_JoystickGetButton(pair.Value.Handle, i) != 0;
                    return new JoystickState(pair.Key, pair.Value.Name, buttons);
                }).ToList();
                foreach (var subscriber in subscribers)
                {
                    try { subscriber.Tick(states); }
                    catch (Exception ex) { AppLog.Write("client", "WARN", "摇杆回调异常：" + ex.Message); }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("client", "WARN", "读取摇杆失败，稍后重新打开：" + ex.Message);
                foreach (var stick in sticks.Values) SDL_JoystickClose(stick.Handle);
                sticks.Clear();
            }
            Thread.Sleep(PollMilliseconds);
        }
    }

    private static bool Initialize()
    {
        try
        {
            // 窗口不在前台时照样读：管制员的焦点通常在雷达软件上
            SDL_SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
            if (SDL_Init(0x200) != 0) // SDL_INIT_JOYSTICK
            {
                AppLog.Write("client", "WARN", "SDL 摇杆子系统初始化失败，摇杆 PTT 不可用：" + Error());
                return false;
            }
            SDL_JoystickEventState(0); // 只读状态，不往 SDL 事件队列里堆事件
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            AppLog.Write("client", "WARN", "找不到 SDL2.dll，摇杆 PTT 不可用：" + ex.Message);
            return false;
        }
    }

    private static void PumpWindowMessages()
    {
        while (PeekMessage(out var message, IntPtr.Zero, 0, 0, 1))
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    private static string Error() => Marshal.PtrToStringUTF8(SDL_GetError()) ?? "";

    private const string Sdl = "SDL2";
    [DllImport(Sdl)] private static extern int SDL_SetHint([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Sdl)] private static extern int SDL_Init(uint flags);
    [DllImport(Sdl)] private static extern IntPtr SDL_GetError();
    [DllImport(Sdl)] private static extern int SDL_JoystickEventState(int state);
    [DllImport(Sdl)] private static extern void SDL_JoystickUpdate();
    [DllImport(Sdl)] private static extern int SDL_NumJoysticks();
    [DllImport(Sdl)] private static extern IntPtr SDL_JoystickOpen(int index);
    [DllImport(Sdl)] private static extern void SDL_JoystickClose(IntPtr joystick);
    [DllImport(Sdl)] private static extern IntPtr SDL_JoystickName(IntPtr joystick);
    [DllImport(Sdl)] private static extern int SDL_JoystickGetAttached(IntPtr joystick);
    [DllImport(Sdl)] private static extern int SDL_JoystickNumButtons(IntPtr joystick);
    [DllImport(Sdl)] private static extern byte SDL_JoystickGetButton(IntPtr joystick, int button);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out NativeMessage msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref NativeMessage msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref NativeMessage msg);
}
