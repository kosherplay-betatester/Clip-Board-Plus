using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace ClipboardPlus;

// Inline password field shown inside a locked history row. The typed text is masked (with an eye to reveal it),
// and is erased as soon as the item has been copied/pasted, refused, or the row is recycled or unloaded.
public sealed class LockedRowEditor : UserControl
{
    internal static Func<ClipRow, string, bool, Task<bool>>? Submit;
    private readonly PasswordBox hidden = new() { Padding = new(6, 3, 6, 3), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox shown = new() { Padding = new(6, 3, 6, 3), Visibility = Visibility.Collapsed, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ToggleButton eye = new() { Content = "\U0001F441", MinWidth = 32, Margin = new(6, 0, 0, 0), ToolTip = "Show or hide what you type" };
    private readonly TextBlock message = new() { FontSize = 11, Margin = new(0, 3, 0, 0), Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0, 0x7A, 0x6A)) };
    public LockedRowEditor()
    {
        var grid = new Grid { Margin = new(0, 6, 0, 0) };
        for (int i = 0; i < 4; i++) grid.ColumnDefinitions.Add(new() { Width = i == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        var paste = new Button { Content = "Paste", Margin = new(6, 0, 0, 0), Padding = new(10, 3, 10, 3), MinHeight = 0 };
        var copy = new Button { Content = "Copy", Margin = new(6, 0, 0, 0), Padding = new(10, 3, 10, 3), MinHeight = 0 };
        eye.MinHeight = 0; eye.Padding = new(4, 2, 4, 2);
        eye.SetResourceReference(BackgroundProperty, "CardBrush"); eye.SetResourceReference(ForegroundProperty, "TextBrush"); eye.SetResourceReference(BorderBrushProperty, "BorderBrush");
        System.Windows.Automation.AutomationProperties.SetName(hidden, "Password for this locked item");
        System.Windows.Automation.AutomationProperties.SetName(shown, "Password for this locked item (visible)");
        System.Windows.Automation.AutomationProperties.SetName(eye, "Show or hide password");
        Grid.SetColumn(shown, 0); Grid.SetColumn(hidden, 0); Grid.SetColumn(eye, 1); Grid.SetColumn(paste, 2); Grid.SetColumn(copy, 3);
        grid.Children.Add(hidden); grid.Children.Add(shown); grid.Children.Add(eye); grid.Children.Add(paste); grid.Children.Add(copy);
        var panel = new StackPanel(); panel.Children.Add(grid); panel.Children.Add(message); Content = panel;
        eye.Checked += (_, _) => { shown.Text = hidden.Password; hidden.Password = ""; hidden.Visibility = Visibility.Collapsed; shown.Visibility = Visibility.Visible; shown.Focus(); shown.CaretIndex = shown.Text.Length; };
        eye.Unchecked += (_, _) => { hidden.Password = shown.Text; shown.Text = ""; shown.Visibility = Visibility.Collapsed; hidden.Visibility = Visibility.Visible; hidden.Focus(); };
        paste.Click += (_, _) => Run(true); copy.Click += (_, _) => Run(false);
        KeyEventHandler enter = (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Run(true); } };
        hidden.KeyDown += enter; shown.KeyDown += enter;
        DataContextChanged += (_, _) => Wipe(); Unloaded += (_, _) => Wipe();
    }
    internal void FocusPassword() { if (eye.IsChecked == true) shown.Focus(); else hidden.Focus(); }
    private void Wipe() { hidden.Password = ""; shown.Text = ""; message.Text = ""; if (eye.IsChecked == true) { eye.IsChecked = false; } shown.Visibility = Visibility.Collapsed; hidden.Visibility = Visibility.Visible; }
    private async void Run(bool paste)
    {
        if (DataContext is not ClipRow row || Submit is null) return;
        string typed = eye.IsChecked == true ? shown.Text : hidden.Password;
        if (typed.Length == 0) { message.Text = "Type the password first."; return; }
        IsEnabled = false; bool ok = false;
        try { ok = await Submit(row, typed, paste); }
        catch (Exception e) { message.Text = e.Message; }
        finally { typed = ""; Wipe(); IsEnabled = true; } // Never leave the password behind, right or wrong.
        if (!ok && message.Text.Length == 0) message.Text = "Incorrect password.";
    }
}
