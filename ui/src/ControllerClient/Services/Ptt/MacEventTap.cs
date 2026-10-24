using System.Runtime.InteropServices;

namespace ControllerClient.Services.Ptt;

/// <summary>
/// macOS 全局键盘 / 鼠标侧键：CGEventTap，只听不拦（ListenOnly），跑在自己的 CFRunLoop 线程上。
/// 需要「隐私与安全性 → 输入监控」权限；没有权限时 tap 建不起来，第一次会弹系统授权提示。
/// 回调太慢或用户输入期间系统会临时停用 tap，收到停用通知就立刻重新启用。
/// </summary>
internal sealed class MacEventTap : IDisposable
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint KeyDown = 10, KeyUp = 11, FlagsChanged = 12, OtherMouseDown = 25, OtherMouseUp = 26;
    private const uint TapDisabledByTimeout = 0xFFFFFFFE, TapDisabledByUserInput = 0xFFFFFFFF;
    private const int KeyboardEventKeycode = 9, MouseEventButtonNumber = 3;
    private const uint SessionEventTap = 1, HeadInsertEventTap = 0, ListenOnly = 1;
    private static readonly Lazy<(IntPtr Common, IntPtr Default)> Modes = new(LoadModes);

    // flagsChanged 事件只说"某个修饰键变了"，按下还是松开要看设备相关的那一位
    private static readonly Dictionary<int, ulong> ModifierMasks = new()
    {
        [59] = 0x1, [56] = 0x2, [60] = 0x4, [55] = 0x8, [54] = 0x10, [58] = 0x20, [61] = 0x40, [62] = 0x2000,
        [57] = 0x10000, [63] = 0x800000
    };

    private readonly bool _keyboard;
    private readonly Action<bool, string> _handler;
    private readonly TapCallback _callback;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private volatile bool _stopping;
    private IntPtr _runLoop, _tap;

    private MacEventTap(bool keyboard, Action<bool, string> handler)
    {
        _keyboard = keyboard;
        _handler = handler;
        _callback = Callback; // 字段持有委托，避免被 GC 回收后回调到野指针
        _thread = new Thread(Run) { IsBackground = true, Name = keyboard ? "ptt-keyboard" : "ptt-mouse" };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    public static MacEventTap Keyboard(Action<bool, string> handler) => new(true, handler);
    public static MacEventTap Mouse(Action<bool, string> handler) => new(false, handler);

    private void Run()
    {
        IntPtr source = IntPtr.Zero;
        try
        {
            _runLoop = CFRunLoopGetCurrent();
            var mask = _keyboard
                ? (1UL << (int)KeyDown) | (1UL << (int)KeyUp) | (1UL << (int)FlagsChanged)
                : (1UL << (int)OtherMouseDown) | (1UL << (int)OtherMouseUp);
            _tap = CGEventTapCreate(SessionEventTap, HeadInsertEventTap, ListenOnly, mask,
                Marshal.GetFunctionPointerForDelegate(_callback), IntPtr.Zero);
            if (_tap == IntPtr.Zero)
            {
                // 弹出系统的输入监控授权；用户授权后要重启客户端才生效
                try { CGRequestListenEventAccess(); } catch (EntryPointNotFoundException) { }
                InputHooks.Report("macOS 没有授予输入监控权限，键盘 / 鼠标 PTT 不可用：请在「系统设置 → 隐私与安全性 → 输入监控」中允许 CAN Audio，然后重启客户端。");
                return;
            }
            source = CFMachPortCreateRunLoopSource(IntPtr.Zero, _tap, 0);
            CFRunLoopAddSource(_runLoop, source, Modes.Value.Common);
            CGEventTapEnable(_tap, true);
            _ready.Set();
            while (!_stopping) CFRunLoopRunInMode(Modes.Value.Default, 0.5, false);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            InputHooks.Report("无法加载 macOS 事件接口，键盘 / 鼠标 PTT 不可用：" + ex.Message);
        }
        finally
        {
            _ready.Set();
            if (_tap != IntPtr.Zero)
            {
                CGEventTapEnable(_tap, false);
                CFMachPortInvalidate(_tap);
                CFRelease(_tap);
            }
            if (source != IntPtr.Zero) CFRelease(source);
        }
    }

    private IntPtr Callback(IntPtr proxy, uint type, IntPtr cgEvent, IntPtr userInfo)
    {
        try
        {
            if (type is TapDisabledByTimeout or TapDisabledByUserInput)
            {
                if (_tap != IntPtr.Zero && !_stopping) CGEventTapEnable(_tap, true);
            }
            else if (type is KeyDown or KeyUp)
            {
                _handler(type == KeyDown, PttKeys.FromMac((int)CGEventGetIntegerValueField(cgEvent, KeyboardEventKeycode)));
            }
            else if (type == FlagsChanged)
            {
                var code = (int)CGEventGetIntegerValueField(cgEvent, KeyboardEventKeycode);
                if (ModifierMasks.TryGetValue(code, out var bit))
                    _handler((CGEventGetFlags(cgEvent) & bit) != 0, PttKeys.FromMac(code));
            }
            else if (type is OtherMouseDown or OtherMouseUp)
            {
                // 按钮号从 0 开始：2 是中键，3 / 4 是后退 / 前进侧键
                var button = CGEventGetIntegerValueField(cgEvent, MouseEventButtonNumber);
                if (button is 3 or 4) _handler(type == OtherMouseDown, button == 3 ? "x1" : "x2");
            }
        }
        catch (Exception ex) { AppLog.Write("client", "WARN", "PTT 事件回调异常：" + ex.Message); }
        return cgEvent;
    }

    private static (IntPtr, IntPtr) LoadModes()
    {
        var library = NativeLibrary.Load(CoreFoundation);
        return (Marshal.ReadIntPtr(NativeLibrary.GetExport(library, "kCFRunLoopCommonModes")),
                Marshal.ReadIntPtr(NativeLibrary.GetExport(library, "kCFRunLoopDefaultMode")));
    }

    public void Dispose()
    {
        _stopping = true;
        if (_runLoop != IntPtr.Zero) CFRunLoopStop(_runLoop);
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(1));
        _ready.Dispose();
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr TapCallback(IntPtr proxy, uint type, IntPtr cgEvent, IntPtr userInfo);

    [DllImport(CoreGraphics)]
    private static extern IntPtr CGEventTapCreate(uint tap, uint place, uint options, ulong eventsOfInterest, IntPtr callback, IntPtr userInfo);
    [DllImport(CoreGraphics)] private static extern void CGEventTapEnable(IntPtr tap, [MarshalAs(UnmanagedType.U1)] bool enable);
    [DllImport(CoreGraphics)] private static extern long CGEventGetIntegerValueField(IntPtr cgEvent, int field);
    [DllImport(CoreGraphics)] private static extern ulong CGEventGetFlags(IntPtr cgEvent);
    [DllImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool CGRequestListenEventAccess();
    [DllImport(CoreFoundation)] private static extern IntPtr CFMachPortCreateRunLoopSource(IntPtr allocator, IntPtr port, nint order);
    [DllImport(CoreFoundation)] private static extern void CFMachPortInvalidate(IntPtr port);
    [DllImport(CoreFoundation)] private static extern IntPtr CFRunLoopGetCurrent();
    [DllImport(CoreFoundation)] private static extern void CFRunLoopAddSource(IntPtr runLoop, IntPtr source, IntPtr mode);
    [DllImport(CoreFoundation)] private static extern int CFRunLoopRunInMode(IntPtr mode, double seconds, [MarshalAs(UnmanagedType.U1)] bool returnAfterSourceHandled);
    [DllImport(CoreFoundation)] private static extern void CFRunLoopStop(IntPtr runLoop);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr value);
}
