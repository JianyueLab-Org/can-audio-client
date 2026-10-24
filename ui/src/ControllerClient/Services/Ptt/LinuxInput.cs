using System.Runtime.InteropServices;

namespace ControllerClient.Services.Ptt;

/// <summary>
/// Linux 全局键盘 / 鼠标侧键：直接读 /dev/input/event*（evdev），X11 和 Wayland 下都一样能用。
/// 只读不抢（不 EVIOCGRAB），别的程序照常收到按键。读这些设备要求用户在 input 组里；
/// 没有权限时键盘退回 X11 的 XQueryKeymap 轮询（Wayland 下只在 X11 程序有焦点时看得到按键），
/// 鼠标侧键则不可用。所有设备在一条线程上 poll，每 3 秒重扫一次以接住热插拔。
/// </summary>
internal sealed class LinuxInput : IDisposable
{
    private const string Libc = "libc.so.6", X11 = "libX11.so.6";
    private const int ReadOnlyNonBlocking = 0x800 | 0x80000; // O_RDONLY | O_NONBLOCK | O_CLOEXEC
    private const short PollIn = 0x1, PollError = 0x8 | 0x10 | 0x20; // POLLERR | POLLHUP | POLLNVAL
    private const int EvKey = 1, BtnMisc = 0x100, EAgain = 11, EPerm = 1, EAccess = 13;
    private const int PollMilliseconds = 200, X11PollMilliseconds = 15;
    private static readonly TimeSpan Rescan = TimeSpan.FromSeconds(3);
    // struct input_event：timeval（两个 long）+ u16 type + u16 code + s32 value
    private static readonly int TimeSize = IntPtr.Size * 2;
    private static readonly int EventSize = TimeSize + 8;

    private readonly bool _keyboard;
    private readonly Action<bool, string> _handler;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(), _stop = new();
    private readonly Dictionary<string, int> _devices = [];
    private bool _denied;

    private LinuxInput(bool keyboard, Action<bool, string> handler)
    {
        _keyboard = keyboard;
        _handler = handler;
        _thread = new Thread(Run) { IsBackground = true, Name = keyboard ? "ptt-keyboard" : "ptt-mouse" };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    public static LinuxInput Keyboard(Action<bool, string> handler) => new(true, handler);
    public static LinuxInput Mouse(Action<bool, string> handler) => new(false, handler);

    private void Run()
    {
        try
        {
            Scan();
            if (_devices.Count == 0)
            {
                var why = _denied
                    ? "当前用户没有读取 /dev/input 的权限"
                    : "找不到可读的输入设备";
                if (_keyboard && Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 })
                {
                    InputHooks.Report($"{why}，键盘 PTT 暂用 X11 读取（Wayland 下只在 X11 程序有焦点时有效），鼠标侧键 PTT 不可用。" +
                                      "执行 sudo usermod -aG input $USER 后重新登录即可在任何窗口使用。");
                    _ready.Set();
                    RunX11();
                    return;
                }
                InputHooks.Report($"{why}，{(_keyboard ? "键盘" : "鼠标侧键")} PTT 不可用：执行 sudo usermod -aG input $USER 后重新登录。");
            }
            _ready.Set();
            RunEvdev();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            InputHooks.Report("无法加载系统输入接口，PTT 不可用：" + ex.Message);
        }
        finally
        {
            _ready.Set();
            foreach (var fd in _devices.Values) close(fd);
            _devices.Clear();
        }
    }

    private void RunEvdev()
    {
        var lastScan = DateTime.UtcNow;
        var buffer = new byte[EventSize * 64];
        while (!_stop.IsSet)
        {
            if (DateTime.UtcNow - lastScan >= Rescan) { lastScan = DateTime.UtcNow; Scan(); }
            if (_devices.Count == 0) { _stop.Wait(PollMilliseconds); continue; }
            var paths = _devices.Keys.ToArray();
            var fds = paths.Select(path => new PollFd { Fd = _devices[path], Events = PollIn }).ToArray();
            if (poll(fds, (nuint)fds.Length, PollMilliseconds) <= 0) continue;
            for (var i = 0; i < fds.Length; i++)
            {
                if ((fds[i].Revents & PollIn) != 0 && Drain(fds[i].Fd, buffer)) continue;
                if ((fds[i].Revents & (PollIn | PollError)) == 0) continue;
                // 拔掉的设备：读出错或 POLLHUP，关掉等重扫时再看它回不回来
                close(fds[i].Fd);
                _devices.Remove(paths[i]);
            }
        }
    }

    /// <summary>读空一个设备。返回 false 表示设备已失效。</summary>
    private bool Drain(int fd, byte[] buffer)
    {
        while (true)
        {
            var count = read(fd, buffer, buffer.Length);
            if (count < 0) return Marshal.GetLastPInvokeError() == EAgain;
            if (count == 0) return false;
            for (var offset = 0; offset + EventSize <= count; offset += EventSize)
            {
                if (BitConverter.ToUInt16(buffer, offset + TimeSize) != EvKey) continue;
                var code = BitConverter.ToUInt16(buffer, offset + TimeSize + 2);
                var pressed = BitConverter.ToInt32(buffer, offset + TimeSize + 4) != 0; // 2 是自动重复，也算按着
                if (_keyboard)
                {
                    // 0x100 以上是鼠标、摇杆按钮；摇杆走 SDL，不在这里
                    if (code < BtnMisc) _handler(pressed, PttKeys.FromEvdev(code));
                }
                else if (code is 0x113 or 0x116) _handler(pressed, "x1"); // BTN_SIDE / BTN_BACK
                else if (code is 0x114 or 0x115) _handler(pressed, "x2"); // BTN_EXTRA / BTN_FORWARD
            }
        }
    }

    private void Scan()
    {
        string[] paths;
        try { paths = Directory.GetFiles("/dev/input", "event*"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        foreach (var path in paths)
        {
            if (_devices.ContainsKey(path)) continue;
            var fd = open(path, ReadOnlyNonBlocking);
            if (fd >= 0) _devices[path] = fd;
            else if (Marshal.GetLastPInvokeError() is EAccess or EPerm) _denied = true;
        }
    }

    /// <summary>X11 退路：XQueryKeymap 给出全部按键的位图，X11 键码 = evdev 键码 + 8。</summary>
    private void RunX11()
    {
        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
        {
            InputHooks.Report("无法连接 X11，键盘 PTT 不可用：执行 sudo usermod -aG input $USER 后重新登录。");
            return;
        }
        try
        {
            var previous = new byte[32];
            var current = new byte[32];
            XQueryKeymap(display, previous);
            while (!_stop.Wait(X11PollMilliseconds))
            {
                XQueryKeymap(display, current);
                for (var keycode = 8; keycode < 256; keycode++)
                {
                    var bit = 1 << (keycode & 7);
                    var now = (current[keycode >> 3] & bit) != 0;
                    if (now != ((previous[keycode >> 3] & bit) != 0)) _handler(now, PttKeys.FromEvdev(keycode - 8));
                }
                (previous, current) = (current, previous);
            }
        }
        finally { XCloseDisplay(display); }
    }

    public void Dispose()
    {
        _stop.Set();
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(1));
        _ready.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events, Revents;
    }

    [DllImport(Libc, SetLastError = true)] private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport(Libc, SetLastError = true)] private static extern nint read(int fd, byte[] buffer, nint count);
    [DllImport(Libc)] private static extern int close(int fd);
    [DllImport(Libc, SetLastError = true)] private static extern int poll([In, Out] PollFd[] fds, nuint count, int timeout);
    [DllImport(X11)] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(X11)] private static extern int XQueryKeymap(IntPtr display, byte[] keys);
    [DllImport(X11)] private static extern int XCloseDisplay(IntPtr display);
}
