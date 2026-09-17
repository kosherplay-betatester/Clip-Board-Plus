using System.Runtime.InteropServices;
using System.Windows.Input;

namespace ClipboardPlus;

public readonly record struct HotkeySpec(uint Modifiers, uint Key)
{
    public static HotkeySpec Parse(string text)
    {
        uint modifiers = 0, key = 0;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL": case "CONTROL": modifiers |= 2; break;
                case "ALT": modifiers |= 1; break;
                case "SHIFT": modifiers |= 4; break;
                case "WIN": case "WINDOWS": modifiers |= 8; break;
                default:
                    if (part.Length == 1 && char.IsAsciiDigit(part[0]))
                    {
                        if (key != 0) throw new ArgumentException("Choose only one key in addition to modifiers.");
                        key = (uint)part[0]; break;
                    }
                    if (int.TryParse(part, out _)) throw new ArgumentException("Choose a letter, digit, or named key such as F8.");
                    var name = part.ToUpperInvariant() is "SCRLK" or "SCROLLLOCK" ? "Scroll" : part;
                    if (key != 0 || !Enum.TryParse<Key>(name, true, out var k) || k is System.Windows.Input.Key.None or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin or System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift)
                        throw new ArgumentException("Choose a key such as F12, ScrLk, or Ctrl+Shift+V.");
                    key = (uint)KeyInterop.VirtualKeyFromKey(k); break;
            }
        }
        if (key == 0) throw new ArgumentException("Press a key, optionally with Ctrl, Alt, Shift or Win.");
        if (System.Numerics.BitOperations.PopCount(modifiers) > 2) throw new ArgumentException("Use one key with at most two modifier keys (three keys total).");
        if ((modifiers & 3) == 3 && key == 0x2E) throw new ArgumentException("Ctrl+Alt+Delete is reserved by Windows. Choose another shortcut.");
        return new(modifiers, key);
    }
}
internal static class MemoryInfo
{
    [StructLayout(LayoutKind.Sequential)] private struct MEMORYSTATUSEX
    {
        public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);
    public static (long Total, long Available) Read()
    {
        var status = new MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? ((long)status.TotalPhysical, (long)status.AvailablePhysical) : (0, long.MaxValue);
    }
    public static int RecommendedCacheMb { get { var total = Read().Total; return total <= 8L * 1024 * 1048576 ? 16 : total <= 16L * 1024 * 1048576 ? 32 : 64; } }
    public static bool UnderPressure { get { var memory = Read(); return memory.Total > 0 && memory.Available < Math.Max(256 * 1048576L, memory.Total / 10); } }
}
internal static class Native
{
    [DllImport("user32.dll")] internal static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] internal static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    internal delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wp, IntPtr lp);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string? module);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public INPUTUNION u; }
    [StructLayout(LayoutKind.Explicit)] private struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT key; [FieldOffset(0)] public MOUSEINPUT mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public UIntPtr extra; }
    internal static bool SendPaste()
    {
        var inputs = new[] { Key(0x11), Key(0x56), Key(0x56, 2), Key(0x11, 2) };
        return SendInput(4, inputs, Marshal.SizeOf<INPUT>()) == 4;
    }
    // An otherwise unused key prevents the shell interpreting a consumed Win+V as a bare Win press.
    internal static void MaskWindowsMenu() => SendInput(2, [Key(0xE8), Key(0xE8, 2)], Marshal.SizeOf<INPUT>());
    private static INPUT Key(ushort key, uint flags = 0) => new() { type = 1, u = new INPUTUNION { key = new KEYBDINPUT { vk = key, flags = flags } } };
    internal static string ProcessName(IntPtr hwnd)
    {
        try { GetWindowThreadProcessId(hwnd, out var pid); using var p = Process.GetProcessById((int)pid); return p.ProcessName; }
        catch { return "Unknown app"; }
    }
    internal static bool IsOurWindow(IntPtr hwnd) { GetWindowThreadProcessId(hwnd, out var pid); return pid == Environment.ProcessId; }
}
