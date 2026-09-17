using System.Windows;
using System.Windows.Controls;

namespace ClipboardPlus;
public sealed class SnippetWindow : Window
{
    private readonly TextBox name = new() { Margin = new(0, 8, 0, 18), MaxLength = 180 };
    private readonly TextBox text = new() { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    public string SnippetName => name.Text.Trim().Length == 0 ? "Untitled snippet" : name.Text.Trim();
    public string SnippetText => text.Text;
    public SnippetWindow(string initial)
    {
        Style = (Style)FindResource(typeof(Window));
        Title = "New snippet · Clipboard Plus"; Width = 580; Height = 500; MinWidth = 450; MinHeight = 350; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new(24) }; Content = panel;
        var heading = new StackPanel(); heading.Children.Add(new TextBlock { Text = "Keep a good thought", FontSize = 24, FontWeight = FontWeights.SemiBold }); heading.Children.Add(new TextBlock { Text = "Name this snippet. It will be pinned for quick access.", Margin = new(0, 8, 0, 8) }); heading.Children.Add(name); DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var button = new Button { Content = "Save & pin", Style = (Style)FindResource("AccentButton"), Margin = new(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        button.Click += (_, _) => { if (string.IsNullOrWhiteSpace(text.Text)) { MessageBox.Show(this, "Enter some text for the snippet."); return; } DialogResult = true; };
        DockPanel.SetDock(button, Dock.Bottom); panel.Children.Add(button); text.Text = initial; panel.Children.Add(text);
        System.Windows.Automation.AutomationProperties.SetName(name, "Snippet name"); System.Windows.Automation.AutomationProperties.SetName(text, "Snippet text");
    }
}
