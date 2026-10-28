using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ClipboardPlus;

// A short, non-interactive confirmation that never takes focus (so a paste into another app is not disturbed)
// and closes itself.
internal static class Toast
{
    internal static TimeSpan Visible = TimeSpan.FromMilliseconds(2200);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    private const int ExStyle = -20, NoActivate = 0x08000000, ToolWindow = 0x00000080;
    private static Brush Resource(string key) => (Brush)System.Windows.Application.Current.FindResource(key);

    internal static Window Show(string text)
    {
        var label = new TextBlock { Text = text, FontSize = 14, TextWrapping = TextWrapping.Wrap, MaxWidth = 360, Foreground = Resource("TextBrush"), VerticalAlignment = VerticalAlignment.Center };
        var check = new TextBlock { Text = "✔", FontSize = 16, Margin = new(0, 0, 10, 0), Foreground = Resource("AccentBrush"), VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal }; row.Children.Add(check); row.Children.Add(label);
        var card = new Border
        {
            Background = Resource("CardBrush"), BorderBrush = Resource("AccentBrush"), BorderThickness = new(1), CornerRadius = new(10), Padding = new(16, 11, 18, 11), Child = row, Margin = new(14),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = .45 }
        };
        var window = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, ShowActivated = false, Topmost = true,
            SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Focusable = false, Content = card, Opacity = 0
        };
        System.Windows.Automation.AutomationProperties.SetName(window, text);
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            SetWindowLong(handle, ExStyle, GetWindowLong(handle, ExStyle) | NoActivate | ToolWindow);
        };
        window.Loaded += (_, _) =>
        {
            var dpi = VisualTreeHelper.GetDpi(window);
            var area = System.Windows.Forms.Screen.FromHandle(Native.GetForegroundWindow()).WorkingArea;
            window.Left = (area.Right / dpi.DpiScaleX) - window.ActualWidth - 8; window.Top = (area.Bottom / dpi.DpiScaleY) - window.ActualHeight - 8;
            window.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
            var timer = new DispatcherTimer { Interval = Visible };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(350)); fade.Completed += (_, _) => window.Close();
                window.BeginAnimation(UIElement.OpacityProperty, fade);
            };
            timer.Start();
        };
        window.Show();
        return window;
    }
}
