using System.Windows;
using System.Windows.Controls;

namespace ClipboardPlus;

// Small code-built dialogs for the password-protected sensitive-content vault.
internal static class VaultDialogs
{
    internal sealed class Dialog : Window
    {
        public readonly StackPanel Body = new() { Margin = new(26) };
        public readonly TextBlock Error = new() { Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("AccentBrush"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 0) };
        public Dialog(Window? owner, string title, string heading, string? intro = null, double width = 470)
        {
            Style = (Style)FindResource(typeof(Window)); Title = title + " · Clipboard Plus"; Width = width; SizeToContent = SizeToContent.Height; MaxHeight = 700;
            ResizeMode = ResizeMode.NoResize; WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner; Owner = owner; Content = new ScrollViewer { Content = Body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Body.Children.Add(new TextBlock { Text = heading, FontSize = 21, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            if (intro is not null) Body.Children.Add(Muted(intro));
        }
        public static TextBlock Muted(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0), Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("MutedBrush") };
        public PasswordBox Field(string label)
        {
            Body.Children.Add(new TextBlock { Text = label, Margin = new(0, 14, 0, 4) });
            var box = new PasswordBox { Padding = new(10, 8, 10, 8) }; System.Windows.Automation.AutomationProperties.SetName(box, label); Body.Children.Add(box); return box;
        }
        public Button Add(WrapPanel row, string text, Action click, bool accent = false)
        {
            var button = new Button { Content = text, Margin = new(0, 0, 8, 8) };
            if (accent) button.Style = (Style)FindResource("AccentButton");
            button.Click += (_, _) => { try { click(); } catch (Exception e) { Error.Text = e.Message; } };
            row.Children.Add(button); return button;
        }
        public WrapPanel Row() { var row = new WrapPanel { Margin = new(0, 18, 0, 0) }; Body.Children.Add(Error); Body.Children.Add(row); return row; }
    }

    internal static bool Unlock(Window owner, Vault vault, HistoryStore store)
    {
        var dialog = new Dialog(owner, "Unlock", "Unlock sensitive items", "Enter your password to open locked items. They lock again automatically after 5 minutes.");
        var password = dialog.Field("Password"); var row = dialog.Row();
        dialog.Loaded += (_, _) => password.Focus();
        dialog.Add(row, "Unlock", () =>
        {
            if (vault.Unlock(password.Password)) dialog.DialogResult = true; else { dialog.Error.Text = "That password is not correct."; password.Clear(); password.Focus(); }
        }, true);
        dialog.Add(row, "Forgot password…", () => { if (Forgot(dialog, vault, store)) dialog.DialogResult = vault.IsUnlocked; else if (!vault.Exists) dialog.DialogResult = false; });
        dialog.Add(row, "Cancel", () => dialog.DialogResult = false);
        dialog.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; if (vault.Unlock(password.Password)) dialog.DialogResult = true; else { dialog.Error.Text = "That password is not correct."; password.Clear(); } } };
        return dialog.ShowDialog() == true;
    }

    // Shows the recovery code once. The user must confirm they saved it before the dialog can close.
    internal static void ShowRecoveryCode(Window owner, string code)
    {
        var dialog = new Dialog(owner, "Recovery code", "Save your recovery code", "This code is the ONLY way to get back into your locked items if you forget your password. Clipboard Plus cannot recover it for you, and it will not be shown again.");
        var codeBox = new TextBox { Text = code, IsReadOnly = true, FontSize = 22, FontFamily = new("Consolas"), Padding = new(12), Margin = new(0, 14, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
        dialog.Body.Children.Add(codeBox);
        dialog.Body.Children.Add(Dialog.Muted("Store it somewhere important and safe: a password manager, or printed and kept with your private documents. Do not leave it in the clipboard history or an unprotected note."));
        var saved = new CheckBox { Content = "I saved my recovery code in a safe place", Margin = new(0, 14, 0, 0) }; dialog.Body.Children.Add(saved);
        var row = dialog.Row(); Button? done = null;
        dialog.Add(row, "Copy code", () => { System.Windows.Clipboard.SetText(code); dialog.Error.Text = "Copied. Paste it into your safe place now — then remove it from the clipboard history."; });
        done = dialog.Add(row, "Done", () => dialog.DialogResult = true, true); done.IsEnabled = false;
        saved.Checked += (_, _) => done.IsEnabled = true; saved.Unchecked += (_, _) => done.IsEnabled = false;
        dialog.Closing += (_, e) => { if (dialog.DialogResult != true) { e.Cancel = true; dialog.Error.Text = "Confirm that you saved the recovery code first."; } };
        dialog.ShowDialog();
    }

    internal static bool ChangePassword(Window owner, Vault vault)
    {
        var dialog = new Dialog(owner, "Change password", "Change password");
        var current = dialog.Field("Current password"); var first = dialog.Field("New password (at least 8 characters)"); var second = dialog.Field("Type the new password again"); var row = dialog.Row();
        dialog.Add(row, "Change password", () =>
        {
            if (!vault.ChangePassword(current.Password, first.Password, second.Password)) { dialog.Error.Text = "The current password is not correct."; return; }
            dialog.DialogResult = true;
        }, true);
        dialog.Add(row, "Cancel", () => dialog.DialogResult = false);
        return dialog.ShowDialog() == true;
    }

    // Two honest ways out: the recovery code, or an explicit reset that destroys the locked items.
    internal static bool Forgot(Window owner, Vault vault, HistoryStore store)
    {
        var dialog = new Dialog(owner, "Forgot password", "Forgot your password?", "Use the recovery code you saved when you created the password. It restores access without losing anything.");
        var code = dialog.Field("Recovery code (XXXXX-XXXXX-XXXXX-XXXXX)"); var first = dialog.Field("New password (at least 8 characters)"); var second = dialog.Field("Type the new password again");
        var row = dialog.Row();
        dialog.Add(row, "Reset with recovery code", () =>
        {
            if (!vault.ResetWithRecoveryCode(code.Password, first.Password, second.Password)) { dialog.Error.Text = "That recovery code is not correct."; return; }
            dialog.DialogResult = true;
        }, true);
        dialog.Add(row, "Cancel", () => dialog.DialogResult = false);
        dialog.Body.Children.Add(Dialog.Muted("No recovery code? You can reset the password, but every sensitive item protected by it will be permanently deleted. Nothing else in your history is touched."));
        var resetRow = new WrapPanel { Margin = new(0, 10, 0, 0) }; dialog.Body.Children.Add(resetRow);
        dialog.Add(resetRow, "Reset password and delete protected items…", () =>
        {
            if (!ConfirmDestructiveReset(dialog, store)) return;
            dialog.DialogResult = false;
        });
        return dialog.ShowDialog() == true;
    }

    private static bool ConfirmDestructiveReset(Window owner, HistoryStore store)
    {
        long count = store.LockedCountAsync().GetAwaiter().GetResult();
        var confirm = new Dialog(owner, "Confirm reset", "Delete all protected items?", $"This permanently deletes {count:N0} sensitive item(s) that are protected by your password, and removes the password. This cannot be undone and the items cannot be recovered by anyone.");
        var typed = new TextBox { Padding = new(10, 8, 10, 8), Margin = new(0, 14, 0, 0) }; System.Windows.Automation.AutomationProperties.SetName(typed, "Type DELETE to confirm");
        confirm.Body.Children.Add(Dialog.Muted("Type DELETE to confirm:")); confirm.Body.Children.Add(typed);
        var row = confirm.Row(); bool done = false;
        confirm.Add(row, "Yes, delete them and reset", () =>
        {
            if (!string.Equals(typed.Text.Trim(), "DELETE", StringComparison.Ordinal)) { confirm.Error.Text = "Type DELETE exactly to confirm."; return; }
            store.Vault.Destroy(); store.DeleteLockedAsync().GetAwaiter().GetResult(); done = true; confirm.DialogResult = true;
        }, true);
        confirm.Add(row, "Cancel — keep my items", () => confirm.DialogResult = false);
        confirm.ShowDialog(); return done;
    }
}
