using System.Windows;
using System.Windows.Controls;

namespace ClipboardPlus;

public sealed class AppPickerWindow : Window
{
    private sealed record Choice(string Name, string ProcessName) { public override string ToString() => $"{Name}  ({ProcessName}.exe)"; }
    private readonly ListBox list = new() { MinHeight = 150, Margin = new(0, 12, 0, 12) };
    private readonly TextBox search = new() { ToolTip = "Find an open app" };
    private List<Choice> apps = [];
    public string? SelectedProcess { get; private set; }
    public AppPickerWindow()
    {
        Style = (Style)FindResource(typeof(Window)); Title = "Choose an app · Clipboard Plus"; Width = 570; Height = 460; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new(24) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Pause capture for an app", FontSize = 23, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock { Text = "Choose an open app, or browse to its program file.", Margin = new(0, 8, 0, 14) }); top.Children.Add(search);
        var actions = new WrapPanel(); DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        var add = new Button { Content = "Add selected app", Margin = new(0, 0, 8, 0) }; actions.Children.Add(add);
        var browse = new Button { Content = "Browse…", Margin = new(0, 0, 8, 0) }; actions.Children.Add(browse);
        var cancel = new Button { Content = "Cancel", IsCancel = true }; actions.Children.Add(cancel); root.Children.Add(list);
        add.Click += (_, _) => { if (list.SelectedItem is Choice choice) { SelectedProcess = choice.ProcessName; DialogResult = true; } };
        browse.Click += (_, _) => { var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose an app to exclude", Filter = "Windows programs (*.exe)|*.exe", CheckFileExists = true }; if (dialog.ShowDialog(this) == true) { SelectedProcess = Path.GetFileNameWithoutExtension(dialog.FileName); DialogResult = true; } };
        search.TextChanged += (_, _) => Filter();
        Loaded += async (_, _) =>
        {
            apps = await Task.Run(() =>
            {
                var choices = new List<Choice>();
                foreach (var process in Process.GetProcesses())
                {
                    using (process) try
                    {
                        if (process.Id != Environment.ProcessId && process.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(process.MainWindowTitle)) choices.Add(new(process.MainWindowTitle.Truncate(65), process.ProcessName));
                    } catch (Exception) { /* Protected or exited app; Browse remains available. */ }
                }
                return choices.DistinctBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase).OrderBy(c => c.Name).ToList();
            });
            Filter(); search.Focus();
        };
    }
    private void Filter() => list.ItemsSource = apps.Where(a => a.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase) || a.ProcessName.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
}
