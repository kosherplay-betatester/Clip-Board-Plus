using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace ClipboardPlus;

public sealed class PreviewWindow : Window
{
    private readonly ClipPayload payload;
    private readonly Func<string, Task> copyOcr;
    private readonly DockPanel root = new() { Margin = new(24) };
    private readonly Grid body = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 0) };
    private readonly CancellationTokenSource cancellation = new();
    private readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private MediaElement? media;
    private byte[]? imageBytes;
    private string? currentSource;
    private bool closed, fullscreen;
    private WindowState previousState;
    private const long MaxPreviewBytes = 32 * 1048576;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal ClipPayload PreviewPayload => payload;
    internal TimeSpan MediaDuration => media?.NaturalDuration.HasTimeSpan == true ? media.NaturalDuration.TimeSpan : TimeSpan.Zero;
    internal TimeSpan MediaPosition { get => media?.Position ?? TimeSpan.Zero; set { if (media is not null) media.Position = value; } }
    internal void PlayMedia() => media?.Play();
    internal void PauseMedia() => media?.Pause();
    internal void StopMedia() => media?.Stop();
    internal async Task PlaySourceAsync(string source)
    {
        await ready.Task;
        if (closed) return;
        await ShowSourceAsync(source);
        if (media is not null) { media.Source = new Uri(source); media.Play(); }
    }
    public PreviewWindow(ClipPayload payload, Func<string, Task> copyOcr)
    {
        this.payload = payload; this.copyOcr = copyOcr;
        Style = (Style)FindResource(typeof(Window));
        Title = "Preview · Clipboard Plus"; Width = 880; Height = 690; MinWidth = 520; MinHeight = 400; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = root;
        var heading = new TextBlock { Text = payload.Title, FontSize = 21, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 0, 0, 16) };
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status); root.Children.Add(body);
        status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Closed += (_, _) => { closed = true; ready.TrySetResult(); cancellation.Cancel(); ReleasePlayer(); imageBytes = null; body.Children.Clear(); cancellation.Dispose(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.F11) { ToggleFullscreen(); e.Handled = true; } else if (e.Key == Key.Escape) { if (fullscreen) ToggleFullscreen(); else Close(); e.Handled = true; } };
        Loaded += async (_, _) => { await Guard(async () =>
        {
            switch (payload.Kind)
            {
                case ClipKind.Image: imageBytes = payload.Image; ShowImage(imageBytes!); break;
                case ClipKind.Files: if (payload.Paths.Length == 1 && MediaSourceInfo.IsMedia(payload.Paths[0])) await ShowSourceAsync(payload.Paths[0]); else ShowFiles(); break;
                case ClipKind.Link: await ShowSourceAsync(payload.Text.Trim()); break;
                default: ShowText(payload.Text); break;
            }
        }); ready.TrySetResult(); };
    }
    private async Task Guard(Func<Task> action) { try { await action(); } catch (OperationCanceledException) { } catch (Exception e) { if (!closed) status.Text = "Preview unavailable: " + e.Message; } }
    private Button Button(string label, Func<Task> action)
    {
        var b = new Button { Content = label, Margin = new(0, 0, 8, 8) }; b.Click += async (_, _) => await Guard(action); return b;
    }
    private void ShowText(string text)
    {
        body.Children.Clear();
        var box = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Cascadia Code, Consolas"), FontSize = 14 };
        body.Children.Add(box); status.Text = $"{text.Length:N0} characters · Select any part to copy";
    }
    private void ShowFiles()
    {
        body.Children.Clear();
        var dock = new DockPanel(); body.Children.Add(dock);
        var note = new TextBlock { Text = "References only — original files stay in their current locations.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 14) };
        DockPanel.SetDock(note, Dock.Top); dock.Children.Add(note);
        var actions = new WrapPanel(); DockPanel.SetDock(actions, Dock.Bottom); dock.Children.Add(actions);
        var list = new ListBox { ItemsSource = payload.Paths, Margin = new(0, 0, 0, 16) }; dock.Children.Add(list);
        actions.Children.Add(Button("Preview selected", async () => { if (list.SelectedItem is string path) await ShowSourceAsync(path); }));
        actions.Children.Add(Button("Open containing folder", () => { if (list.SelectedItem is string path) OpenFolder(path); return Task.CompletedTask; }));
        list.SelectionChanged += async (_, _) =>
        {
            if (list.SelectedItem is string path)
            {
                var exists = await Task.Run(() => File.Exists(path) || Directory.Exists(path));
                if (!closed) status.Text = exists ? path : "Source unavailable: " + path;
            }
        };
        if (payload.Paths.Length > 0) list.SelectedIndex = 0;
    }
    private static void OpenFolder(string path)
    {
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        info.Arguments = Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"";
        Process.Start(info);
    }
    private async Task ShowSourceAsync(string source)
    {
        ReleasePlayer(); body.Children.Clear(); currentSource = source;
        bool remote = Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";
        if (!remote && !await Task.Run(() => File.Exists(source) || Directory.Exists(source))) throw new FileNotFoundException("The original source was moved, deleted, or is offline.");
        if (closed) return;
        var extension = Path.GetExtension(remote ? uri!.AbsolutePath : source).ToLowerInvariant();
        var dock = new DockPanel(); body.Children.Add(dock);
        var buttons = new WrapPanel(); DockPanel.SetDock(buttons, Dock.Bottom); dock.Children.Add(buttons);
        if (payload.Kind == ClipKind.Files) buttons.Children.Add(Button("← File list", () => { ReleasePlayer(); ShowFiles(); return Task.CompletedTask; }));
        buttons.Children.Add(Button(remote ? "Open in browser ↗" : "Open original ↗", () => { Process.Start(new ProcessStartInfo(source) { UseShellExecute = true }); return Task.CompletedTask; }));
        if (!remote) buttons.Children.Add(Button("Containing folder", () => { OpenFolder(source); return Task.CompletedTask; }));
        status.Text = source;
        if (new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" }.Contains(extension))
        {
            if (remote)
            {
                dock.Children.Add(new TextBlock { Text = "Load this image from its original address when you’re ready.", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
                buttons.Children.Add(Button("Load image", async () => { imageBytes = await ReadRemoteImage(source); if (!closed) ShowImage(imageBytes); }));
            }
            else { imageBytes = await ReadLocalImage(source); if (!closed) ShowImage(imageBytes); }
        }
        else if (new[] { ".mp4", ".m4v", ".wmv", ".avi", ".mov", ".mp3", ".wav", ".wma", ".m4a", ".aac", ".flac", ".webm", ".mkv", ".ogg" }.Contains(extension))
        {
            var player = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform, Volume = 0.6 };
            media = player; dock.Children.Add(player);
            var seek = new Slider { Minimum = 0, Maximum = 1, Width = 170, Margin = new(8, 0, 8, 8), VerticalAlignment = VerticalAlignment.Center };
            seek.ToolTip = "Seek";
            var time = new TextBlock { Margin = new(4, 6, 8, 0) };
            buttons.Children.Add(Button("▶ Play", () => { if (player.Source is null) player.Source = new Uri(source); player.Play(); return Task.CompletedTask; }));
            buttons.Children.Add(Button("Ⅱ Pause", () => { player.Pause(); return Task.CompletedTask; }));
            buttons.Children.Add(Button("■ Stop", () => { player.Stop(); return Task.CompletedTask; }));
            buttons.Children.Add(Button("−10s", () => { player.Position = TimeSpan.FromSeconds(Math.Max(0, player.Position.TotalSeconds - 10)); return Task.CompletedTask; }));
            buttons.Children.Add(Button("+10s", () => { if (player.NaturalDuration.HasTimeSpan) player.Position = TimeSpan.FromSeconds(Math.Min(player.NaturalDuration.TimeSpan.TotalSeconds, player.Position.TotalSeconds + 10)); return Task.CompletedTask; }));
            buttons.Children.Add(Button("Full screen", () => { ToggleFullscreen(); return Task.CompletedTask; }));
            buttons.Children.Add(seek); buttons.Children.Add(time);
            var volume = new Slider { Minimum = 0, Maximum = 1, Value = .6, Width = 70, ToolTip = "Volume", Margin = new(8, 0, 8, 8) };
            volume.ValueChanged += (_, _) => player.Volume = volume.Value; buttons.Children.Add(volume);
            bool updatingSeek = false;
            seek.ValueChanged += (_, _) => { if (!updatingSeek && !seek.IsMouseCaptureWithin && player.NaturalDuration.HasTimeSpan) player.Position = TimeSpan.FromSeconds(seek.Value * player.NaturalDuration.TimeSpan.TotalSeconds); };
            seek.PreviewMouseLeftButtonUp += (_, _) => { if (player.NaturalDuration.HasTimeSpan) player.Position = TimeSpan.FromSeconds(seek.Value * player.NaturalDuration.TimeSpan.TotalSeconds); };
            player.MediaFailed += (_, e) => { status.Text = "This source or codec cannot be played here. Use Open original / Open in browser. " + e.ErrorException.Message; player.Close(); player.Source = null; };
            ticker.Tick += Tick;
            void Tick(object? _, EventArgs __)
            {
                if (player.NaturalDuration.HasTimeSpan && player.NaturalDuration.TimeSpan.TotalSeconds > 0)
                {
                    updatingSeek = true;
                    if (!seek.IsMouseCaptureWithin) seek.Value = player.Position.TotalSeconds / player.NaturalDuration.TimeSpan.TotalSeconds;
                    updatingSeek = false;
                    time.Text = $"{player.Position:mm\\:ss} / {player.NaturalDuration.TimeSpan:mm\\:ss}";
                }
            }
            cleanupTick = () => ticker.Tick -= Tick; ticker.Start();
            status.Text = "Press Play to read from the original source. Playback support depends on installed codecs.\n" + source;
        }
        else dock.Children.Add(new TextBlock { Text = remote ? "This is a webpage, not a directly playable media address.\nOpen it in your browser to view its content." : "This item can be opened with its associated Windows app.", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
    }
    private Action? cleanupTick;
    private void ReleasePlayer() { ticker.Stop(); cleanupTick?.Invoke(); cleanupTick = null; if (media is not null) { media.Stop(); media.Close(); media.Source = null; media = null; } }
    private async Task<byte[]> ReadLocalImage(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (stream.Length > MaxPreviewBytes) throw new InvalidOperationException("This image exceeds the 32 MB preview limit. Open the original file instead.");
        using var output = new MemoryStream(); await stream.CopyToAsync(output, cancellation.Token); return output.ToArray();
    }
    private async Task<byte[]> ReadRemoteImage(string uri)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token); response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxPreviewBytes) throw new InvalidOperationException("Image exceeds the 32 MB preview limit.");
        using var input = await response.Content.ReadAsStreamAsync(timeout.Token); using var output = new MemoryStream();
        var buffer = new byte[65536]; int count;
        while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (output.Length + count > MaxPreviewBytes) throw new InvalidOperationException("Image exceeds the 32 MB preview limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
        }
        return output.ToArray();
    }
    private void ShowImage(byte[] bytes)
    {
        body.Children.Clear();
        using var stream = new MemoryStream(bytes);
        var dimensions = System.Windows.Media.Imaging.BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        int width = dimensions.PixelWidth, height = dimensions.PixelHeight; stream.Position = 0;
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (width >= height) bitmap.DecodePixelWidth = Math.Min(width, 2400); else bitmap.DecodePixelHeight = Math.Min(height, 2400);
        bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        var dock = new DockPanel(); body.Children.Add(dock);
        var buttons = new WrapPanel(); DockPanel.SetDock(buttons, Dock.Bottom); dock.Children.Add(buttons);
        var zoom = new Slider { Minimum = .1, Maximum = 4, Value = 1, Width = 140, Margin = new(10, 0, 16, 8), ToolTip = "Zoom" };
        var transform = new ScaleTransform(1, 1);
        var image = new System.Windows.Controls.Image { Source = bitmap, Stretch = Stretch.Uniform, Width = Math.Min(bitmap.PixelWidth, 740), LayoutTransform = transform };
        var scroll = new ScrollViewer { Content = image, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        dock.Children.Add(scroll);
        zoom.ValueChanged += (_, _) => transform.ScaleX = transform.ScaleY = zoom.Value;
        buttons.Children.Add(zoom);
        buttons.Children.Add(Button("Fit", () => { zoom.Value = 1; image.Width = Math.Max(100, scroll.ViewportWidth - 20); return Task.CompletedTask; }));
        buttons.Children.Add(Button("Full screen · F11", () => { ToggleFullscreen(); return Task.CompletedTask; }));
        buttons.Children.Add(Button("Extract text", async () =>
        {
            status.Text = "Reading text on this device…";
            var text = await ExtractTextAsync(bytes);
            if (closed) return;
            if (string.IsNullOrWhiteSpace(text)) { status.Text = "No readable text found in this image."; return; }
            await copyOcr(text); status.Text = "Text extracted, copied, and saved to history.";
        }));
        if (payload.Kind == ClipKind.Files) buttons.Children.Add(Button("← File list", () => { imageBytes = null; ShowFiles(); return Task.CompletedTask; }));
        if (currentSource is not null) buttons.Children.Add(Button("Open original ↗", () => { Process.Start(new ProcessStartInfo(currentSource) { UseShellExecute = true }); return Task.CompletedTask; }));
        Point? drag = null; double horizontal = 0, vertical = 0;
        image.MouseLeftButtonDown += (_, e) => { drag = e.GetPosition(scroll); horizontal = scroll.HorizontalOffset; vertical = scroll.VerticalOffset; image.CaptureMouse(); };
        image.MouseMove += (_, e) => { if (drag is { } start) { var current = e.GetPosition(scroll); scroll.ScrollToHorizontalOffset(horizontal + start.X - current.X); scroll.ScrollToVerticalOffset(vertical + start.Y - current.Y); } };
        image.MouseLeftButtonUp += (_, _) => { drag = null; image.ReleaseMouseCapture(); };
        status.Text = "Drag to pan · Use the slider to zoom · F11 for full screen · Preview decoded at up to 2,400 px wide";
    }
    internal static async Task<string> ExtractTextAsync(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes); using var random = stream.AsRandomAccessStream();
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(random);
        double scale = Math.Min(1, (double)Math.Min(3200, OcrEngine.MaxImageDimension) / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform { ScaledWidth = Math.Max(1, (uint)(decoder.PixelWidth * scale)), ScaledHeight = Math.Max(1, (uint)(decoder.PixelHeight * scale)) };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        var engine = OcrEngine.TryCreateFromUserProfileLanguages() ?? throw new InvalidOperationException("Install an OCR-capable Windows language pack to extract text.");
        var result = await engine.RecognizeAsync(bitmap); return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text));
    }
    private void ToggleFullscreen()
    {
        if (!fullscreen) { previousState = WindowState; WindowStyle = WindowStyle.None; WindowState = WindowState.Maximized; }
        else { WindowStyle = WindowStyle.SingleBorderWindow; WindowState = previousState; }
        fullscreen = !fullscreen;
    }
    internal static async Task<bool> VerifyMediaAsync(string path)
    {
        var window = new PreviewWindow(new() { Kind = ClipKind.Files, Paths = [path] }, _ => Task.CompletedTask);
        try
        {
            window.Show(); await Task.Delay(100); await window.ShowSourceAsync(path);
            var player = window.media ?? throw new InvalidOperationException("Preview did not create a media player.");
            if (player.Source is not null) throw new InvalidOperationException("Media was loaded without a play request.");
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            player.MediaOpened += (_, _) => opened.TrySetResult();
            player.MediaFailed += (_, e) => opened.TrySetException(e.ErrorException);
            player.Volume = 0; player.Source = new Uri(path); player.Play();
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(250);
            if (!player.NaturalDuration.HasTimeSpan || player.NaturalDuration.TimeSpan.TotalSeconds <= 0) throw new InvalidOperationException("Media duration was not decoded.");
            bool advanced = player.Position > TimeSpan.Zero;
            window.Close();
            return advanced && player.Source is null && window.media is null && !window.ticker.IsEnabled;
        }
        finally { if (!window.closed) window.Close(); }
    }
}
