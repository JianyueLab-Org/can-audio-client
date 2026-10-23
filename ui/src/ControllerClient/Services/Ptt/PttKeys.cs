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

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint virtualKey, uint scanCode, byte[] keyState, StringBuilder buffer,
        int bufferSize, uint flags, IntPtr layout);
}
