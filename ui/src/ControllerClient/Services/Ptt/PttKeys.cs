using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace ControllerClient.Services.Ptt;

/// <summary>
/// Windows 虚拟键码 → 设置文件里的键名。沿用旧 Python 引擎（pynput）的命名，
/// 老配置里的 "v"、"ctrl_r"、"space" 升级后照样能用：
/// 功能键取固定名字，可打印键取当前布局下不带修饰键的字符并转小写，
/// 两者都没有就退回 "vk数字"。
/// </summary>
public static class PttKeys
{
    private static readonly Dictionary<int, string> Special = BuildSpecial();

    private static Dictionary<int, string> BuildSpecial()
    {
        var keys = new Dictionary<int, string>
        {
            [0x12] = "alt", [0xA4] = "alt_l", [0xA5] = "alt_r", [0x08] = "backspace", [0x14] = "caps_lock",
            [0x5B] = "cmd", [0x5C] = "cmd_r", [0x11] = "ctrl", [0xA2] = "ctrl_l", [0xA3] = "ctrl_r",
            [0x2E] = "delete", [0x28] = "down", [0x23] = "end", [0x0D] = "enter", [0x1B] = "esc",
            [0x24] = "home", [0x25] = "left", [0x22] = "page_down", [0x21] = "page_up", [0x27] = "right",
            // pynput 里 shift 与 shift_l 同为左 Shift，配置里存的是 "shift"
            [0x10] = "shift", [0xA0] = "shift", [0xA1] = "shift_r", [0x20] = "space", [0x09] = "tab", [0x26] = "up",
            [0xB3] = "media_play_pause", [0xB2] = "media_stop", [0xAD] = "media_volume_mute",
            [0xAE] = "media_volume_down", [0xAF] = "media_volume_up", [0xB1] = "media_previous", [0xB0] = "media_next",
            [0x2D] = "insert", [0x5D] = "menu", [0x90] = "num_lock", [0x13] = "pause", [0x2C] = "print_screen",
            [0x91] = "scroll_lock"
        };
        for (var i = 0; i < 24; i++) keys[0x70 + i] = "f" + (i + 1);
        return keys;
    }

    /// <summary>纯函数部分：<paramref name="character"/> 是布局翻译出的字符（没有则为 null）。</summary>
    public static string Name(int virtualKey, string? character)
    {
        if (Special.TryGetValue(virtualKey, out var name)) return name;
        if (!string.IsNullOrEmpty(character) && !char.IsControl(character[0])) return character.ToLowerInvariant();
        return "vk" + virtualKey;
    }

    /// <summary>在钩子线程上调用：按前台窗口的键盘布局翻译。</summary>
    internal static string FromHook(int virtualKey, int scanCode) =>
        Special.TryGetValue(virtualKey, out var name) ? name : Name(virtualKey, Translate(virtualKey, scanCode));

    private static string? Translate(int virtualKey, int scanCode)
    {
        try
        {
            var layout = GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), out _));
            var buffer = new StringBuilder(8);
            // 不带修饰键：Shift+V 与 V 是同一个 PTT。标志 4 = 不改动内核里的死键状态，
            // 否则用户在别的程序里打带声调的字符会被吃掉。
            var count = ToUnicodeEx((uint)virtualKey, (uint)scanCode, new byte[256], buffer, buffer.Capacity, 4, layout);
            return count == 0 ? null : buffer.ToString(0, Math.Min(Math.Abs(count), buffer.Length));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    // macOS / Linux 的键码是物理位置，没有可靠的后台线程布局翻译，按美式布局取名。
    // 修饰键、功能键与 Windows 同名，同一份设置文件在三个系统上指同一个键。
    private const string Row1 = "1234567890-=", RowQ = "qwertyuiop[]", RowA = "asdfghjkl;'`", RowZ = "\\zxcvbnm,./";
    private static readonly Dictionary<int, string> Evdev = BuildEvdev();
    private static readonly Dictionary<int, string> Mac = BuildMac();

    private static Dictionary<int, string> BuildEvdev()
    {
        var keys = new Dictionary<int, string>
        {
            [1] = "esc", [14] = "backspace", [15] = "tab", [28] = "enter", [29] = "ctrl_l", [42] = "shift",
            [54] = "shift_r", [55] = "*", [56] = "alt_l", [57] = "space", [58] = "caps_lock", [69] = "num_lock",
            [70] = "scroll_lock", [71] = "7", [72] = "8", [73] = "9", [74] = "-", [75] = "4", [76] = "5", [77] = "6",
            [78] = "+", [79] = "1", [80] = "2", [81] = "3", [82] = "0", [83] = ".", [87] = "f11", [88] = "f12",
            [96] = "enter", [97] = "ctrl_r", [98] = "/", [99] = "print_screen", [100] = "alt_r", [102] = "home",
            [103] = "up", [104] = "page_up", [105] = "left", [106] = "right", [107] = "end", [108] = "down",
            [109] = "page_down", [110] = "insert", [111] = "delete", [113] = "media_volume_mute",
            [114] = "media_volume_down", [115] = "media_volume_up", [119] = "pause", [125] = "cmd", [126] = "cmd_r",
            [127] = "menu", [163] = "media_next", [164] = "media_play_pause", [165] = "media_previous", [166] = "media_stop"
        };
        for (var i = 0; i < Row1.Length; i++) keys[2 + i] = Row1[i].ToString();
        for (var i = 0; i < RowQ.Length; i++) keys[16 + i] = RowQ[i].ToString();
        for (var i = 0; i < RowA.Length; i++) keys[30 + i] = RowA[i].ToString();
        for (var i = 0; i < RowZ.Length; i++) keys[43 + i] = RowZ[i].ToString();
        for (var i = 0; i < 10; i++) keys[59 + i] = "f" + (i + 1);
        for (var i = 0; i < 12; i++) keys[183 + i] = "f" + (i + 13);
        return keys;
    }

    private static Dictionary<int, string> BuildMac()
    {
        var keys = new Dictionary<int, string>
        {
            [36] = "enter", [48] = "tab", [49] = "space", [51] = "backspace", [53] = "esc", [54] = "cmd_r", [55] = "cmd",
            [56] = "shift", [57] = "caps_lock", [58] = "alt_l", [59] = "ctrl_l", [60] = "shift_r", [61] = "alt_r",
            [62] = "ctrl_r", [63] = "fn", [65] = ".", [67] = "*", [69] = "+", [71] = "num_lock", [72] = "media_volume_up",
            [73] = "media_volume_down", [74] = "media_volume_mute", [75] = "/", [76] = "enter", [78] = "-", [81] = "=",
            [114] = "insert", [115] = "home", [116] = "page_up", [117] = "delete", [119] = "end", [121] = "page_down",
            [123] = "left", [124] = "right", [125] = "down", [126] = "up",
            [122] = "f1", [120] = "f2", [99] = "f3", [118] = "f4", [96] = "f5", [97] = "f6", [98] = "f7", [100] = "f8",
            [101] = "f9", [109] = "f10", [103] = "f11", [111] = "f12", [105] = "f13", [107] = "f14", [113] = "f15",
            [106] = "f16", [64] = "f17", [79] = "f18", [80] = "f19", [90] = "f20"
        };
        // ANSI 键位表：下标是 macOS 虚拟键码，空格是没有字符的位置
        const string ansi = "asdfhgzxcv bqweryt123465=97-80]ou[ip lj'k;\\,/nm.";
        for (var i = 0; i < ansi.Length; i++) if (ansi[i] != ' ') keys[i] = ansi[i].ToString();
        keys[50] = "`";
        int[] keypad = [82, 83, 84, 85, 86, 87, 88, 89, 91, 92];
        for (var i = 0; i < keypad.Length; i++) keys[keypad[i]] = i.ToString(CultureInfo.InvariantCulture);
        return keys;
    }

    /// <summary>Linux evdev 键码（X11 键码减 8）→ 键名。</summary>
    public static string FromEvdev(int code) => Evdev.TryGetValue(code, out var name) ? name : "evdev" + code;

    /// <summary>macOS 虚拟键码（kCGKeyboardEventKeycode）→ 键名。</summary>
    public static string FromMac(int keyCode) => Mac.TryGetValue(keyCode, out var name) ? name : "mac" + keyCode;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint virtualKey, uint scanCode, byte[] keyState, StringBuilder buffer,
        int bufferSize, uint flags, IntPtr layout);
}
