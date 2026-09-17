using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ClipboardPlus;

public partial class MainWindow : Window
{
    private readonly HistoryStore store;
    private AppSettings settings;
    private readonly bool demo;
    private readonly ObservableCollection<ClipRow> rows = [];
    private ClipboardService clipboard = null!;
    private HotkeyService hotkeys = null!;
    private System.Windows.Forms.NotifyIcon tray = null!;
    private System.Drawing.Icon? appIcon;
    private readonly DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer maintenance = new() { Interval = TimeSpan.FromMinutes(10) };
    private readonly Queue<long> pasteQueue = [];
    private string filter = "All";
    private int offset;
    private long refreshVersion;
    private bool exiting, initialized;
    private IntPtr pasteTarget;
    private readonly TaskCompletionSource loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? initialization;
    public MainWindow(HistoryStore store, AppSettings settings, bool demo)
    {
        this.store = store; this.settings = settings; this.demo = demo;
        InitializeComponent();
        ClipList.ItemsSource = rows;
        searchTimer.Tick += async (_, _) => { searchTimer.Stop(); await Safe(RefreshAsync); };
        maintenance.Tick += async (_, _) => await Safe(async () => { await store.MaintainAsync(); if (IsVisible) await RefreshAsync(); });
        Loaded += async (_, _) => { await InitializeAsync(); if (IsVisible) SearchBox.Focus(); };
        Closing += (_, e) => { if (!exiting) { e.Cancel = true; Hide(); } };
    }
    internal Task InitializeAsync() => initialization ??= InitializeCoreAsync();
    private async Task InitializeCoreAsync()
    {
        initialized = true;
        var handle = new WindowInteropHelper(this).EnsureHandle();
        clipboard = new(settings, async payload => { await store.AddAsync(payload); _ = Dispatcher.BeginInvoke(() => { if (IsVisible) ScheduleRefresh(); }); }, SetStatus);
        clipboard.Paused = demo;
        await clipboard.Ready;
        hotkeys = new(handle, ShowPanel);
        try { hotkeys.Configure(settings); } catch (Exception e) { SetStatus(e.Message); }
        CreateTray(); UpdateSettingsLabels(); maintenance.Start();
        await Safe(RefreshAsync);
        if (demo) SetStatus("Demo workspace · capture paused · sample clips only");
        else if (settings.WindowsHistoryManaged) { try { SetStatus(WindowsHistorySettings.Apply(settings)); } catch (Exception e) { SetStatus("Could not apply Windows history preference: " + e.Message); } }
        loaded.TrySetResult();
    }
    private void CreateTray()
    {
        appIcon = IconFactory.Create();
        Icon = Imaging.CreateBitmapSourceFromHIcon(appIcon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
        tray = new() { Icon = appIcon, Text = "Clipboard Plus", Visible = true };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Clipboard Plus", null, (_, _) => Dispatcher.Invoke(() => ShowPanel(Native.GetForegroundWindow())));
        menu.Items.Add("Pause / resume capture", null, (_, _) => Dispatcher.Invoke(() => Pause_Click(this, new())));
        menu.Items.Add("Settings", null, (_, _) => Dispatcher.Invoke(() => { ShowPanel(Native.GetForegroundWindow()); Settings_Click(this, new()); }));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit Clipboard Plus", null, (_, _) => Dispatcher.Invoke(Exit));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => ShowPanel(Native.GetForegroundWindow()));
    }
    private void Exit()
    {
        Cleanup();
        System.Windows.Application.Current.Shutdown();
    }
    private void Cleanup() { exiting = true; maintenance.Stop(); searchTimer.Stop(); clipboard.Dispose(); hotkeys.Dispose(); tray.Dispose(); appIcon?.Dispose(); }
    public void ShowPanel(IntPtr target)
    {
        if (target != new WindowInteropHelper(this).Handle) pasteTarget = target;
        if (IsVisible && IsActive) { Hide(); return; }
        Show(); WindowState = WindowState.Normal; Activate(); SearchBox.Focus(); SearchBox.SelectAll(); ScheduleRefresh();
    }
    private void SetStatus(string text)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => SetStatus(text)); return; }
        StatusLabel.Text = text;
        StatusLabel.ToolTip = text;
    }
    private async Task Safe(Func<Task> action)
    {
        try { await action(); } catch (Exception e) { SetStatus(e.Message); }
    }
    private void ScheduleRefresh() { searchTimer.Stop(); searchTimer.Start(); }
    private DateTimeOffset? Since => DateFilter.SelectedIndex switch { 1 => new DateTimeOffset(DateTime.Today), 2 => DateTimeOffset.Now.AddDays(-7), 3 => DateTimeOffset.Now.AddDays(-30), _ => null };
    private async Task RefreshAsync()
    {
        long version = ++refreshVersion;
        long? selected = (ClipList.SelectedItem as ClipRow)?.Id;
        var results = await store.QueryAsync(new(SearchBox.Text, filter, offset, 80, Since));
        if (version != refreshVersion) return;
        rows.Clear(); foreach (var row in results) rows.Add(row);
        ClipList.SelectedItem = rows.FirstOrDefault(r => r.Id == selected) ?? rows.FirstOrDefault();
        PasteButton.IsEnabled = ClipList.SelectedItem is not null || pasteQueue.Count > 0;
        EmptyPanel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = SearchBox.Text.Length > 0 ? "No matching clips" : "Your next idea starts here";
        EmptyText.Text = SearchBox.Text.Length > 0 ? "Try another word, a filename, or an app name." : "Copy text, an image, or files. We’ll keep them within reach.";
        PreviousButton.IsEnabled = offset > 0; NextButton.IsEnabled = rows.Count == 80;
        PageLabel.Text = rows.Count == 0 ? "No items" : $"{offset + 1}–{offset + rows.Count}";
        ResultLabel.Text = SearchBox.Text.Length > 0 ? "SEARCH RESULTS" : filter == "Pinned" ? "SAVED FOR LATER" : "RECENT CLIPS";
        var stats = await store.StatsAsync();
        if (version != refreshVersion) return;
        StorageLabel.Text = $"{stats.Count:N0} clips  ·  {stats.DiskBytes.SizeLabel()} on disk";
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (SearchHint is not null) SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; offset = 0; if (initialized) ScheduleRefresh(); }
    private void Date_Changed(object sender, SelectionChangedEventArgs e) { offset = 0; if (initialized) ScheduleRefresh(); }
    private async void Filter_Click(object sender, RoutedEventArgs e)
    {
        filter = (string)((Button)sender).Tag; offset = 0;
        Heading.Text = filter switch { "All" => "All history", "Image" => "Images", "Text" => "Text & code", "Link" => "Links", "Files" => "Files & folders", _ => filter };
        await Safe(RefreshAsync);
    }
    private async void Previous_Click(object sender, RoutedEventArgs e) { offset = Math.Max(0, offset - 80); await Safe(RefreshAsync); }
    private async void Next_Click(object sender, RoutedEventArgs e) { offset += 80; await Safe(RefreshAsync); }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e) { if (PasteButton is not null) PasteButton.IsEnabled = ClipList.SelectedItem is not null || pasteQueue.Count > 0; }
    private async void Pin_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (((Button)sender).Tag is ClipRow row) await Safe(async () => { await store.SetPinAsync(row.Id, !row.Pinned); await RefreshAsync(); });
    }
    private async Task<ClipPayload?> SelectedPayloadAsync()
    {
        if (ClipList.SelectedItem is not ClipRow row) { SetStatus("Select a clip first."); return null; }
        var payload = await store.GetAsync(row.Id);
        if (payload is null) { SetStatus("This clip has expired or was removed."); await RefreshAsync(); }
        return payload;
    }
    private async Task PasteAsync(bool plain = false, bool copyOnly = false, ClipPayload? supplied = null)
    {
        long? queueId = !copyOnly && pasteQueue.TryPeek(out long next) ? next : null;
        var payload = supplied ?? (queueId is { } id ? await store.GetAsync(id) : await SelectedPayloadAsync());
        if (payload is null)
        {
            if (queueId is not null) { pasteQueue.Dequeue(); UpdateQueue(); SetStatus("A queued clip was removed. Continue with the next item."); }
            return;
        }
        await clipboard.PutAsync(payload, plain);
        if (copyOnly) { SetStatus("Copied. Ready to paste anywhere."); return; }
        if (pasteTarget == IntPtr.Zero || !Native.IsWindow(pasteTarget) || pasteTarget == new WindowInteropHelper(this).Handle)
        {
            SetStatus("Copied. Open Clipboard Plus from your destination app using the shortcut to paste directly."); return;
        }
        Hide();
        if (Native.GetForegroundWindow() != pasteTarget) Native.SetForegroundWindow(pasteTarget);
        for (int i = 0; i < 10 && Native.GetForegroundWindow() != pasteTarget; i++) await Task.Delay(10);
        if (Native.GetForegroundWindow() != pasteTarget) { Show(); Activate(); SetStatus("Copied. Windows prevented focus switching; paste manually with Ctrl+V."); return; }
        // Never synthesize Ctrl+V while the user still holds the shortcut's modifier keys.
        for (int i = 0; i < 40 && new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(k => Native.GetAsyncKeyState(k) < 0); i++) await Task.Delay(25);
        await Task.Delay(45);
        if (Native.GetForegroundWindow() != pasteTarget || new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(k => Native.GetAsyncKeyState(k) < 0) || !Native.SendPaste())
        { Show(); Activate(); SetStatus("Copied, but automatic paste was blocked. Use Ctrl+V in the destination app."); return; }
        if (queueId is not null) { pasteQueue.Dequeue(); UpdateQueue(); }
        SetStatus("Pasted to the previous app.");
        if (!settings.HideAfterPaste) { Show(); Activate(); }
    }
    private void UpdateQueue() => QueueLabel.Text = pasteQueue.Count == 0 ? "Enter to paste  ·  Ctrl+Enter for plain text  ·  Esc to hide" : $"Paste queue: {pasteQueue.Count} remaining · Enter pastes the next clip";
    private async void Paste_Click(object sender, RoutedEventArgs e) => await Safe(() => PasteAsync());
    private async void Copy_Click(object sender, RoutedEventArgs e) => await Safe(() => PasteAsync(copyOnly: true));
    private async void Clip_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d)
        {
            while (d is not null) { if (d is System.Windows.Controls.Primitives.ButtonBase or Slider or MediaControls) return; d = VisualTreeHelper.GetParent(d); }
        }
        await Safe(() => PasteAsync());
    }
    private async void Preview_Click(object sender, RoutedEventArgs e) => await Safe(async () =>
    {
        if (ClipList.SelectedItem is ClipRow row) await OpenPreviewAsync(row);
    });
    internal async Task<PreviewWindow?> OpenPreviewAsync(ClipRow row)
    {
        var payload = await store.GetAsync(row.Id);
        if (payload is null) { SetStatus("This clip is no longer in history."); return null; }
        var preview = new PreviewWindow(payload, async text => { var p = new ClipPayload { Kind = ClipKind.Text, Text = text, Source = "Clipboard Plus OCR" }; await store.AddAsync(p); await clipboard.PutAsync(p); SetStatus("Extracted text copied and saved."); }) { Owner = this };
        preview.Show(); return preview;
    }
    private async void Thumbnail_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (((FrameworkElement)sender).Tag is not ClipRow row) return;
        await Safe(async () => { await OpenPreviewAsync(row); });
    }
    private void Actions_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        void Add(string title, Func<Task> action) { var item = new MenuItem { Header = title }; item.Click += async (_, _) => await Safe(action); menu.Items.Add(item); }
        Add("Paste as plain text    Ctrl+Enter", () => PasteAsync(true));
        Add("Preview    Space", () => { Preview_Click(this, new()); return Task.CompletedTask; });
        menu.Items.Add(new Separator());
        Add("Queue selected clips (list order)", () => { foreach (var row in rows.Where(r => ClipList.SelectedItems.Contains(r))) pasteQueue.Enqueue(row.Id); UpdateQueue(); SetStatus($"{pasteQueue.Count} clips in the paste queue."); return Task.CompletedTask; });
        Add("Clear paste queue", () => { pasteQueue.Clear(); UpdateQueue(); return Task.CompletedTask; });
        Add("Combine selected as lines", async () =>
        {
            var parts = new List<string>();
            foreach (var row in rows.Where(r => ClipList.SelectedItems.Contains(r))) if (await store.GetAsync(row.Id) is { } p) parts.Add(p.Kind == ClipKind.Files ? string.Join(Environment.NewLine, p.Paths) : p.Text);
            var payload = new ClipPayload { Text = string.Join(Environment.NewLine, parts.Where(s => s.Length > 0)), Source = "Clipboard Plus", Kind = ClipKind.Text };
            if (payload.Text.Length == 0) throw new InvalidOperationException("Select text, links, or file paths to combine.");
            await store.AddAsync(payload); await clipboard.PutAsync(payload); SetStatus("Combined text copied and saved."); await RefreshAsync();
        });
        menu.Items.Add(new Separator());
        Add("Edit as a new snippet", async () => { if (await SelectedPayloadAsync() is { } p) await EditSnippetAsync(p.Text); });
        Add("Copy trimmed text", () => TransformAsync(s => s.Trim()));
        Add("Copy UPPERCASE", () => TransformAsync(s => s.ToUpperInvariant()));
        Add("Copy lowercase", () => TransformAsync(s => s.ToLowerInvariant()));
        Add("Copy file paths", () => PasteAsync(true, true));
        menu.Items.Add(new Separator());
        Add("Delete selected clips    Delete", DeleteSelectedAsync);
        menu.PlacementTarget = (Button)sender; menu.IsOpen = true;
    }
    private async Task TransformAsync(Func<string, string> transform)
    {
        if (await SelectedPayloadAsync() is not { } p || p.Kind is not (ClipKind.Text or ClipKind.Link)) { SetStatus("Select a text clip or link."); return; }
        await clipboard.PutAsync(new() { Kind = ClipKind.Text, Text = transform(p.Text), Source = "Clipboard Plus" }); SetStatus("Transformed text copied.");
    }
    private async Task DeleteSelectedAsync()
    {
        var selected = ClipList.SelectedItems.Cast<ClipRow>().ToArray(); if (selected.Length == 0) return;
        if (MessageBox.Show(this, $"Delete {selected.Length} selected clip(s) from history? Original files will not be deleted.", "Delete clips", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await store.DeleteAsync(selected.Select(r => r.Id)); await RefreshAsync(); SetStatus("Selected clips deleted.");
    }
    private async Task EditSnippetAsync(string text = "")
    {
        var dialog = new SnippetWindow(text) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await store.AddAsync(new() { Kind = ClipKind.Text, Text = dialog.SnippetText, Name = dialog.SnippetName, Source = "Snippet" }, true);
        offset = 0; await RefreshAsync(); SetStatus("Snippet saved and pinned.");
    }
    private async void Snippet_Click(object sender, RoutedEventArgs e) => await Safe(() => EditSnippetAsync());
    private void Pause_Click(object sender, RoutedEventArgs e) { clipboard.Paused = !clipboard.Paused; UpdateSettingsLabels(); SetStatus(clipboard.Paused ? "Capture paused. Existing history is still available." : "●  Ready to capture"); }
    private void UpdateSettingsLabels() { ShortcutLabel.Text = settings.ReplaceWinV ? "Win + V" : settings.Hotkey.Replace("+", " + "); PauseButton.Content = clipboard.Paused ? "▶   Resume capture" : "Ⅱ   Pause capture"; }
    private async void Settings_Click(object sender, RoutedEventArgs e) => await Safe(async () =>
    {
        var dialog = new SettingsWindow(settings, store, async value =>
        {
            value.Validate(); hotkeys.Configure(value);
            try
            {
                if (!demo)
                {
                    using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                    if (value.StartWithWindows) run.SetValue("ClipboardPlus", $"\"{Environment.ProcessPath}\" --background"); else run.DeleteValue("ClipboardPlus", false);
                }
                value.Save(store.Root);
            }
            catch { hotkeys.Configure(settings); throw; }
            settings = value; clipboard.Configure(value); await store.ConfigureAsync(value); App.ApplyTheme(value.Theme); UpdateSettingsLabels();
            if (!demo) { try { SetStatus(WindowsHistorySettings.Apply(value)); } catch (Exception e) { SetStatus("App settings saved. Windows history could not be changed: " + e.Message); } }
        }, hotkeys.SetSuspended) { Owner = this };
        dialog.ShowDialog(); await RefreshAsync();
    });
    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.Enter && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Control && (SearchBox.IsKeyboardFocusWithin || ClipList.IsKeyboardFocusWithin) && Keyboard.FocusedElement is not System.Windows.Controls.Primitives.ButtonBase) { e.Handled = true; await Safe(() => PasteAsync(Keyboard.Modifiers == ModifierKeys.Control)); }
        else if (e.Key == Key.Down && SearchBox.IsKeyboardFocusWithin) { ClipList.Focus(); if (ClipList.SelectedIndex < 0 && rows.Count > 0) ClipList.SelectedIndex = 0; e.Handled = true; }
        else if (e.Key == Key.Space && ClipList.IsKeyboardFocusWithin && Keyboard.FocusedElement is not System.Windows.Controls.Primitives.ButtonBase) { Preview_Click(this, new()); e.Handled = true; }
        else if (e.Key == Key.Delete && ClipList.IsKeyboardFocusWithin) { e.Handled = true; await Safe(DeleteSelectedAsync); }
    }
    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    internal async Task RenderProofAsync(string directory, string? mediaFixtureRoot = null)
    {
        await loaded.Task;
        Directory.CreateDirectory(directory);
        await Task.Delay(250); UpdateLayout();
        RenderWindow(this, Path.Combine(directory, "history-dark.png"));
        if (mediaFixtureRoot is not null)
        {
            foreach (string name in new[] { "preview.mp3", "preview.mp4" }) await store.AddAsync(new() { Kind = ClipKind.Files, Paths = [Path.GetFullPath(Path.Combine(mediaFixtureRoot, name))], Source = "Explorer" });
            filter = "Files"; Heading.Text = "Files & media"; Height = 900;
            await RefreshAsync(); await Task.Delay(200); UpdateLayout(); RenderWindow(this, Path.Combine(directory, "media-controls.png"));
            filter = "All"; Heading.Text = "All history"; Height = 760; await RefreshAsync();
        }
        var firstImage = (await store.QueryAsync(new(Filter: "Image"))).First();
        var payload = (await store.GetAsync(firstImage.Id))!;
        var preview = new PreviewWindow(payload, _ => Task.CompletedTask) { Owner = this };
        preview.Show(); await Task.Delay(300); preview.UpdateLayout();
        RenderWindow(preview, Path.Combine(directory, "image-preview.png")); preview.Close();
        var options = new SettingsWindow(settings, store, _ => Task.CompletedTask) { Owner = this };
        options.Show(); await Task.Delay(250); options.UpdateLayout(); RenderWindow(options, Path.Combine(directory, "settings.png")); options.Close();
        App.ApplyTheme("Light"); await Task.Delay(100); UpdateLayout(); RenderWindow(this, Path.Combine(directory, "history-light.png"));
        var text = await PreviewWindow.ExtractTextAsync(payload.Image!);
        await File.WriteAllTextAsync(Path.Combine(directory, "ocr-result.txt"), text);
        Exit();
    }
    private static void RenderWindow(Window window, string path)
    {
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    internal async Task<bool> VerifyPasteAsync(Window target, System.Windows.Controls.TextBox destination, string expected)
    {
        await loaded.Task;
        try
        {
            target.Show(); target.Activate(); destination.Focus();
            await Task.Delay(100);
            ShowPanel(new WindowInteropHelper(target).Handle);
            await RefreshAsync();
            await PasteAsync();
            for (int i = 0; i < 40 && destination.Text != expected; i++) await Task.Delay(25);
            if (destination.Text != expected) throw new InvalidOperationException($"Paste verification: {StatusLabel.Text}; length={destination.Text.Length}; focus={destination.IsKeyboardFocused}; focusedType={Keyboard.FocusedElement?.GetType().Name}; targetActive={target.IsActive}; clipboardMatches={System.Windows.Clipboard.GetText() == expected}");
            return !IsVisible;
        }
        finally { Cleanup(); Close(); }
    }
    internal static async Task<bool> VerifyBackgroundAsync(HistoryStore store)
    {
        var window = new MainWindow(store, new() { Hotkey = "Ctrl+Alt+F9" }, true);
        int visibleTransitions = 0;
        window.IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) visibleTransitions++; };
        try
        {
            await window.InitializeAsync();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            bool conflict = Native.RegisterHotKey(IntPtr.Zero, 739, 3, 0x78);
            if (conflict) Native.UnregisterHotKey(IntPtr.Zero, 739);
            return visibleTransitions == 0 && !window.IsVisible && !window.IsActive && window.tray.Visible && window.clipboard.Ready.IsCompletedSuccessfully && !conflict;
        }
        finally { window.Cleanup(); window.Close(); }
    }
    internal static async Task<bool> VerifyPreviewRoutingAsync(HistoryStore store)
    {
        var window = new MainWindow(store, new() { Hotkey = "Ctrl+Alt+F9" }, true);
        PreviewWindow? first = null, second = null;
        try
        {
            await window.InitializeAsync();
            var images = window.rows.Where(r => r.Kind == ClipKind.Image).Take(2).ToArray();
            window.ClipList.SelectedItem = images[0];
            window.ClipList.SelectedItems.Add(images[1]);
            first = await window.OpenPreviewAsync(images[0]);
            second = await window.OpenPreviewAsync(images[1]);
            var expected = await store.GetAsync(images[1].Id);
            return first is not null && second is not null && !first.PreviewPayload.Image!.SequenceEqual(second.PreviewPayload.Image!) && second.PreviewPayload.Image!.SequenceEqual(expected!.Image!);
        }
        finally { first?.Close(); second?.Close(); window.Cleanup(); window.Close(); }
    }
}
