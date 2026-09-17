using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClipboardPlus;

internal static class MediaSourceInfo
{
    public static bool IsVideo(string source) => new[] { ".mp4", ".m4v", ".wmv", ".avi", ".mov", ".webm", ".mkv" }.Contains(Extension(source));
    public static bool IsMedia(string source) => IsVideo(source) || new[] { ".mp3", ".wav", ".wma", ".m4a", ".aac", ".flac", ".ogg" }.Contains(Extension(source));
    private static string Extension(string source) => Path.GetExtension(Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsolutePath : source).ToLowerInvariant();
    public static string? From(ClipPayload p) => p.Kind == ClipKind.Link && IsMedia(p.Text.Trim()) ? p.Text.Trim() : p.Kind == ClipKind.Files && p.Paths.Length == 1 && IsMedia(p.Paths[0]) ? p.Paths[0] : null;
}

// A recycled row owns no decoder until Play. Only one row may play at a time.
public sealed class MediaControls : UserControl
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(string), typeof(MediaControls), new PropertyMetadata(null, (d, _) => ((MediaControls)d).SourceChanged()));
    public string? Source { get => (string?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    private static WeakReference<MediaControls>? active;
    private MediaPlayer? audio;
    private PreviewWindow? video;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Slider seek = new() { Minimum = 0, Maximum = 1, MinWidth = 90, ToolTip = "Playback position" };
    private readonly TextBlock clock = new() { Text = "0:00 / 0:00", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 0, 0) };
    private readonly TextBlock error = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private bool ticking;
    private int generation;
    public MediaControls()
    {
        var root = new StackPanel { Margin = new(0, 8, 0, 2) }; Content = root;
        var buttons = new WrapPanel(); root.Children.Add(buttons);
        void Add(string text, string tooltip, Action action)
        {
            var b = new Button { Content = text, ToolTip = tooltip, Padding = new(8, 4, 8, 4), Margin = new(0, 0, 5, 5) };
            b.Click += (_, e) => { e.Handled = true; try { action(); } catch (Exception ex) { error.Text = ex.Message; } }; buttons.Children.Add(b);
        }
        Add("▶", "Play preview", () => _ = PlayAsync()); Add("Ⅱ", "Pause", Pause); Add("■", "Stop", Stop);
        Add("−10s", "Skip back 10 seconds", () => Position -= TimeSpan.FromSeconds(10)); Add("+10s", "Skip forward 10 seconds", () => Position += TimeSpan.FromSeconds(10));
        var track = new DockPanel(); DockPanel.SetDock(clock, Dock.Right); track.Children.Add(clock); track.Children.Add(seek); root.Children.Add(track); root.Children.Add(error);
        seek.ValueChanged += (_, _) => { if (!ticking && !seek.IsMouseCaptureWithin) Position = TimeSpan.FromSeconds(seek.Value); };
        seek.PreviewMouseLeftButtonUp += (_, _) => Position = TimeSpan.FromSeconds(seek.Value);
        timer.Tick += (_, _) => Update();
        Unloaded += (_, _) => Release();
        Visibility = Visibility.Collapsed;
    }
    private TimeSpan Duration => video?.MediaDuration ?? (audio?.NaturalDuration.HasTimeSpan == true ? audio.NaturalDuration.TimeSpan : TimeSpan.Zero);
    private TimeSpan Position
    {
        get => video?.MediaPosition ?? audio?.Position ?? TimeSpan.Zero;
        set { var position = TimeSpan.FromSeconds(Math.Clamp(value.TotalSeconds, 0, Math.Max(0, Duration.TotalSeconds))); if (video is not null) video.MediaPosition = position; else if (audio is not null) audio.Position = position; Update(); }
    }
    private void SourceChanged() { Release(); Visibility = string.IsNullOrEmpty(Source) ? Visibility.Collapsed : Visibility.Visible; }
    private async Task PlayAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(Source)) return;
            if (active?.TryGetTarget(out var previous) == true && previous != this) previous.Release();
            active = new(this); error.Text = "";
            int playGeneration = generation;
            if (MediaSourceInfo.IsVideo(Source))
            {
                if (video is null)
                {
                    video = new PreviewWindow(new() { Kind = Uri.TryCreate(Source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? ClipKind.Link : ClipKind.Files, Text = Source, Paths = [Source] }, _ => Task.CompletedTask);
                    video.Closed += (_, _) => { video = null; timer.Stop(); Update(); };
                    video.Show();
                    await video.PlaySourceAsync(Source);
                }
                else { video.PlayMedia(); video.Activate(); }
            }
            else
            {
                if (audio is null)
                {
                    audio = new MediaPlayer(); audio.MediaOpened += (_, _) => Update();
                    audio.MediaFailed += (_, e) => { error.Text = "Cannot play this source: " + e.ErrorException.Message; Release(); };
                    audio.MediaEnded += (_, _) => { timer.Stop(); Update(); };
                    audio.Open(new Uri(Source));
                }
                audio.Play();
            }
            if (playGeneration != generation) return;
            timer.Start();
        }
        catch (Exception ex) { error.Text = "Preview unavailable: " + ex.Message; Release(); }
    }
    private void Pause() { audio?.Pause(); video?.PauseMedia(); timer.Stop(); Update(); }
    private void Stop() { audio?.Stop(); video?.StopMedia(); timer.Stop(); Update(); }
    private void Update()
    {
        ticking = true;
        try { seek.Maximum = Math.Max(1, Duration.TotalSeconds); if (!seek.IsMouseCaptureWithin) seek.Value = Position.TotalSeconds; clock.Text = $"{Position:mm\\:ss} / {Duration:mm\\:ss}"; }
        finally { ticking = false; }
    }
    private void Release() { generation++; timer.Stop(); audio?.Close(); audio = null; var oldVideo = video; video = null; oldVideo?.Close(); Update(); }
    internal static async Task<bool> VerifyPlaybackAsync(string source)
    {
        var control = new MediaControls { Source = source };
        var window = new Window { Content = control, Width = 470, Height = 180 };
        try
        {
            window.Show();
            await control.PlayAsync();
            for (int i = 0; i < 100 && (control.Duration <= TimeSpan.Zero || control.Position <= TimeSpan.FromMilliseconds(100)); i++) await Task.Delay(50);
            if (control.Duration <= TimeSpan.Zero || control.Position <= TimeSpan.Zero) throw new InvalidOperationException("Inline playback did not advance: " + control.error.Text);
            control.Pause(); var paused = control.Position; await Task.Delay(100);
            if (Math.Abs((control.Position - paused).TotalMilliseconds) > 80) throw new InvalidOperationException("Pause did not hold playback position.");
            control.Position = TimeSpan.FromSeconds(control.Duration.TotalSeconds / 2);
            if (Math.Abs(control.Position.TotalSeconds - control.Duration.TotalSeconds / 2) > .2) throw new InvalidOperationException("Inline seek failed.");
            control.Stop();
            if (control.Position.TotalMilliseconds > 100) throw new InvalidOperationException("Stop did not reset playback.");
            control.Release(); return control.audio is null && control.video is null && !control.timer.IsEnabled;
        }
        finally { control.Release(); window.Close(); }
    }
}
