using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace ClipboardPlus;

internal static class WindowsHistorySettings
{
    private const string PreferencePath = @"Software\Microsoft\Clipboard";
    private const string PolicyPath = @"Software\Policies\Microsoft\Windows\System";
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wparam, string lparam, uint flags, uint timeout, out UIntPtr result);
    internal static bool? Desired(AppSettings settings) => settings.WindowsHistoryMode switch
    {
        "Automatic" => !settings.ReplaceWinV,
        "On" => true,
        "Off" => false,
        _ => null
    };
    internal static bool PolicyBlocked
    {
        get
        {
            using var machine = Registry.LocalMachine.OpenSubKey(PolicyPath);
            using var user = Registry.CurrentUser.OpenSubKey(PolicyPath);
            return machine?.GetValue("AllowClipboardHistory") is int m && m == 0 || user?.GetValue("AllowClipboardHistory") is int u && u == 0;
        }
    }
    internal static string Status()
    {
        try
        {
            if (PolicyBlocked) return "Windows history is disabled by policy. If Shutup10++ set this policy, undo its clipboard-history restriction there before enabling Windows history. Clipboard Plus can still work.";
            bool enabled = Windows.ApplicationModel.DataTransfer.Clipboard.IsHistoryEnabled();
            return enabled ? "Windows clipboard history is currently on." : "Windows clipboard history is currently off.";
        }
        catch (Exception e) { return "Windows history status is unavailable: " + e.Message; }
    }
    internal static void WritePreference(RegistryKey key, bool enabled) => key.SetValue("EnableClipboardHistory", enabled ? 1 : 0, RegistryValueKind.DWord);
    internal static string Apply(AppSettings settings)
    {
        if (Desired(settings) is not { } enabled) return Status();
        if (enabled && PolicyBlocked) return Status();
        using var key = Registry.CurrentUser.CreateSubKey(PreferencePath);
        WritePreference(key, enabled);
        SendMessageTimeout(new IntPtr(0xFFFF), 0x1A, UIntPtr.Zero, PreferencePath, 2, 200, out _);
        bool effective = Windows.ApplicationModel.DataTransfer.Clipboard.IsHistoryEnabled();
        return effective == enabled ? $"Windows clipboard history is {(enabled ? "on" : "off")}." : $"Windows history preference saved as {(enabled ? "on" : "off")}. Windows has not applied it yet; open Windows clipboard settings or sign in again. {Status()}";
    }
}
