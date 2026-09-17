using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ClipboardPlus;

public sealed class HotkeyService : IDisposable
{
    private HwndSource source = null!;
    private readonly Action<PasteDestination> show;
    private readonly Native.HookProc hookProc;
    private readonly Dispatcher callbackDispatcher;
    private Dispatcher workerDispatcher = null!;
    private readonly Thread thread;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IntPtr hook;
    private readonly WinVState winVState = new();
    private HotkeySpec? active;
    private HotkeySpec? hooked;
    private AppSettings? desired;
    private bool suspended, hookKeyDown;
    private int activeId = 701;
    internal bool IsWinVActive => hook != IntPtr.Zero && hooked == new HotkeySpec(8, 0x56);
    internal bool IsHookActive => hook != IntPtr.Zero;
    public HotkeyService(IntPtr hwnd, Action<PasteDestination> show)
    {
        this.show = show; hookProc = KeyboardHook; callbackDispatcher = Dispatcher.CurrentDispatcher;
        thread = new Thread(() =>
        {
            try
            {
                workerDispatcher = Dispatcher.CurrentDispatcher;
                source = new HwndSource(new HwndSourceParameters("Clipboard Plus shortcut worker") { ParentWindow = new IntPtr(-3) });
                source.AddHook(WndProc); ready.SetResult(); Dispatcher.Run();
            }
            catch (Exception error) { ready.TrySetException(error); }
        }) { IsBackground = true, Name = "Clipboard Plus shortcuts" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); ready.Task.GetAwaiter().GetResult();
    }
    public void Configure(AppSettings settings) => workerDispatcher.Invoke(() => { ConfigureCore(settings); desired = settings; });
    public void SetSuspended(bool value) => workerDispatcher.Invoke(() =>
    {
        if (suspended == value) return;
        if (value)
        {
            Native.UnregisterHotKey(source.Handle, activeId); active = null;
            if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero; hooked = null; hookKeyDown = false;
        }
        suspended = value;
        if (!value && desired is not null) ConfigureCore(desired);
    });
    private void ConfigureCore(AppSettings settings)
    {
        var spec = HotkeySpec.Parse(settings.ReplaceWinV ? "Win+V" : settings.Hotkey);
        bool useHook = spec.Modifiers == 0 || spec.Key == 0x7B || (spec.Modifiers & 8) != 0;
        if (useHook)
        {
            if (hook == IntPtr.Zero)
            {
                hook = Native.SetWindowsHookEx(13, hookProc, Native.GetModuleHandle(null), 0);
                if (hook == IntPtr.Zero) throw new InvalidOperationException("Windows did not allow this keyboard shortcut. Choose another key.");
            }
            hooked = spec; hookKeyDown = false;
            Native.UnregisterHotKey(source.Handle, activeId); active = null;
        }
        else
        {
            if (active != spec)
            {
                int nextId = activeId == 701 ? 702 : 701;
                if (!Native.RegisterHotKey(source.Handle, nextId, spec.Modifiers | 0x4000, spec.Key))
                    throw new InvalidOperationException("That shortcut is already in use. Choose another shortcut.");
                Native.UnregisterHotKey(source.Handle, activeId); activeId = nextId; active = spec;
            }
            if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; hooked = null; hookKeyDown = false; winVState.Reset(); }
        }
    }
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled)
    {
        if (msg == 0x312 && wp.ToInt32() == activeId) { var target = Native.CaptureDestination(Native.GetForegroundWindow()); callbackDispatcher.BeginInvoke(() => show(target)); handled = true; }
        return IntPtr.Zero;
    }
    private IntPtr KeyboardHook(int code, IntPtr wp, IntPtr lp)
    {
        if (code >= 0 && hooked is { } spec && Marshal.ReadInt32(lp) == spec.Key)
        {
            int message = wp.ToInt32(); bool down = message is 0x100 or 0x104;
            uint modifiers = KeyRecorderWindow.CurrentModifiers();
            if (hookKeyDown || (down && modifiers == spec.Modifiers))
            {
                if (down && !hookKeyDown)
                {
                    var target = Native.CaptureDestination(Native.GetForegroundWindow());
                    if ((spec.Modifiers & 8) != 0) Native.MaskWindowsMenu();
                    callbackDispatcher.BeginInvoke(() => show(target));
                }
                hookKeyDown = down;
                return new IntPtr(1);
            }
        }
        return Native.CallNextHookEx(hook, code, wp, lp);
    }
    public void Dispose() => workerDispatcher.Invoke(() => { Native.UnregisterHotKey(source.Handle, activeId); if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook); source.RemoveHook(WndProc); source.Dispose(); workerDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); });
}

internal sealed class WinVState
{
    private bool down;
    internal void Reset() => down = false;
    internal bool Handle(bool isDown, bool win, bool other, Action activate)
    {
        if (!(isDown && win && !other) && !down) return false;
        if (isDown && !down) activate();
        down = isDown; return true;
    }
}
