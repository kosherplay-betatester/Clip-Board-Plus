using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace ClipboardPlus;

public sealed class KeyRecorderWindow : Window
{
    private readonly TextBlock display = new() { Text = "Press your shortcut…", FontSize = 25, Margin = new(0, 20, 0, 20), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock hint = new() { Text = "Press one key, or hold up to two modifiers and press a key.\nExamples: F12 · ScrLk · Ctrl+D · Ctrl+Shift+V · Win+V", TextWrapping = TextWrapping.Wrap };
    private readonly Button accept = new() { Content = "Use this shortcut", IsEnabled = false, Margin = new(0, 20, 10, 0) };
    private readonly Native.HookProc callback;
    private readonly HashSet<int> swallowed = [];
    private IntPtr hook, handle;
    public string Shortcut { get; private set; } = "";
    public KeyRecorderWindow()
    {
        Style = (Style)FindResource(typeof(Window)); Title = "Record shortcut · Clipboard Plus"; Width = 500; Height = 310; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(28) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "Choose your shortcut", FontSize = 22, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(display); panel.Children.Add(hint);
        var actions = new WrapPanel(); panel.Children.Add(actions); actions.Children.Add(accept);
        var cancel = new Button { Content = "Cancel", Margin = new(0, 20, 0, 0) }; actions.Children.Add(cancel);
        accept.Click += (_, _) => { DialogResult = true; }; cancel.Click += (_, _) => Close();
        callback = Capture;
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            hook = Native.SetWindowsHookEx(13, callback, Native.GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) hint.Text = "Could not start the recorder. Close this window and try again.";
        };
        Closed += (_, _) => { if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook); };
    }
    internal static uint CurrentModifiers() => (Native.GetAsyncKeyState(0x11) < 0 ? 2u : 0) | (Native.GetAsyncKeyState(0x12) < 0 ? 1u : 0) | (Native.GetAsyncKeyState(0x10) < 0 ? 4u : 0) | (Native.GetAsyncKeyState(0x5B) < 0 || Native.GetAsyncKeyState(0x5C) < 0 ? 8u : 0);
    internal static string Format(uint modifiers, int key)
    {
        var parts = new List<string>();
        if ((modifiers & 2) != 0) parts.Add("Ctrl"); if ((modifiers & 1) != 0) parts.Add("Alt"); if ((modifiers & 4) != 0) parts.Add("Shift"); if ((modifiers & 8) != 0) parts.Add("Win");
        parts.Add(key == 0x91 ? "ScrLk" : key is >= 0x30 and <= 0x39 ? ((char)key).ToString() : KeyInterop.KeyFromVirtualKey(key).ToString());
        return string.Join('+', parts);
    }
    private IntPtr Capture(int code, IntPtr message, IntPtr data)
    {
        if (code < 0) return Native.CallNextHookEx(hook, code, message, data);
        int key = Marshal.ReadInt32(data); bool down = message.ToInt32() is 0x100 or 0x104;
        if (key == 0xE8) return Native.CallNextHookEx(hook, code, message, data);
        if (!down && swallowed.Remove(key)) return new IntPtr(1);
        if (Native.GetForegroundWindow() != handle || !down || key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5) return Native.CallNextHookEx(hook, code, message, data);
        uint modifiers = CurrentModifiers(); swallowed.Add(key);
        if ((modifiers & 8) != 0) Native.MaskWindowsMenu();
        string value = Format(modifiers, key);
        Dispatcher.BeginInvoke(() =>
        {
            try { HotkeySpec.Parse(value); Shortcut = value; display.Text = value.Replace("+", " + "); accept.IsEnabled = true; hint.Text = "Press a different shortcut to replace this one, or click Use this shortcut.\nThis key will open Clipboard Plus while the app is running."; }
            catch (ArgumentException error) { display.Text = value; hint.Text = error.Message; accept.IsEnabled = false; }
        });
        return new IntPtr(1);
    }
}
