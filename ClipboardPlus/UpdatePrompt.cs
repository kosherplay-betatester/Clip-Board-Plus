using System.Windows;
using System.Windows.Controls;

namespace ClipboardPlus;

internal static class UpdatePrompt
{
    // Returns whether to update now, and (when declining) whether to stay quiet until the next version.
    internal static (bool UpdateNow, bool DontRemind) Ask(Window? owner, UpdateInfo info, Version current)
    {
        var dialog = new VaultDialogs.Dialog(owner, "Update available", $"Clipboard Plus {info.Version} is available", $"You have version {current}. Updating downloads the installer, checks it, closes Clipboard Plus, installs, and opens it again. Your history, pins and settings are kept.", 500);
        string notes = info.Notes.Trim();
        if (notes.Length > 0) dialog.Body.Children.Add(VaultDialogs.Dialog.Muted(notes.Length > 700 ? notes[..700] + "…" : notes));
        var dontRemind = new CheckBox { Content = "Don't remind me again until the next version", Margin = new(0, 16, 0, 0) };
        dialog.Body.Children.Add(dontRemind);
        var row = dialog.Row(); bool update = false;
        dialog.Add(row, "Update now", () => { update = true; dialog.DialogResult = true; }, true);
        dialog.Add(row, "Later", () => dialog.DialogResult = false);
        dialog.ShowDialog();
        return (update, !update && dontRemind.IsChecked == true);
    }
}
