using System.Runtime.InteropServices;

namespace ControllerClient.Services.Ptt;

/// <summary>
/// 全局键盘 / 鼠标低级钩子，跑在自己带消息循环的线程上。
/// 绝不吞事件：PTT 键在模拟器里往往另有用途，抢走它比没有 PTT 更糟。
/// 回调要快——超过系统的 LowLevelHooksTimeout，Windows 会悄悄摘掉钩子。
/// </summary>
internal sealed class LowLevelHook : IDisposable
{
    private const int WhKeyboardLl = 13, WhMouseLl = 14;
    private const int WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmSysKeyDown = 0x0104, WmSysKeyUp = 0x0105;
    private const int WmXButtonDown = 0x020B, WmXButtonUp = 0x020C, WmQuit = 0x0012;

    private readonly int _kind;
    private readonly Action<bool, string> _handler;
    private readonly HookProc _proc;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;

    private LowLevelHook(int kind, Action<bool, string> handler)
    {
        _kind = kind;
        _handler = handler;
        _proc = Callback; // 字段持有委托，避免被 GC 回收后钩子回调到野指针
        _thread = new Thread(Run) { IsBackground = true, Name = kind == WhKeyboardLl ? "ptt-keyboard" : "ptt-mouse" };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    /// <summary>handler(按下?, 规范化键名)。</summary>
    public static LowLevelHook Keyboard(Action<bool, string> handler) => new(WhKeyboardLl, handler);

    /// <summary>handler(按下?, "x1"/"x2")。左 / 中 / 右键根本不上报。</summary>
    public static LowLevelHook Mouse(Action<bool, string> handler) => new(WhMouseLl, handler);

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        var hook = SetWindowsHookEx(_kind, _proc, GetModuleHandle(null), 0);
        _ready.Set();
        if (hook == IntPtr.Zero)
        {
            AppLog.Write("client", "WARN", $"无法安装 PTT 钩子（{_thread.Name}），错误 {Marshal.GetLastWin32Error()}");
            return;
        }
        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        finally { UnhookWindowsHookEx(hook); }
    }

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            try
            {
                var message = (int)wParam;
                if (_kind == WhKeyboardLl && message is WmKeyDown or WmSysKeyDown or WmKeyUp or WmSysKeyUp)
                {
                    var name = PttKeys.FromHook(Marshal.ReadInt32(lParam), Marshal.ReadInt32(lParam, 4));
                    _handler(message is WmKeyDown or WmSysKeyDown, name);
                }
                else if (_kind == WhMouseLl && message is WmXButtonDown or WmXButtonUp)
                {
                    var button = (Marshal.ReadInt32(lParam, 8) >> 16) & 0xFFFF;
                    if (button is 1 or 2) _handler(message == WmXButtonDown, button == 1 ? "x1" : "x2");
                }
            }
            catch (Exception ex) { AppLog.Write("client", "WARN", "PTT 钩子回调异常：" + ex.Message); }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(1));
        _ready.Dispose();
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hook, HookProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out NativeMessage msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref NativeMessage msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref NativeMessage msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMessage
{
    public IntPtr Hwnd;
    public uint Message;
    public IntPtr WParam, LParam;
    public uint Time;
    public int X, Y;
}
