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
    private PasteDestination pasteDestination;
    private bool clipboardAction;
    private bool queryPending;
    private long lastRowPress;
    private long? pressedRowId;
    private PasteDestination? pendingPanel;
    private readonly TaskCompletionSource loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? initialization;
    public MainWindow(HistoryStore store, AppSettings settings, bool demo)
    {
        this.store = store; this.settings = settings; this.demo = demo;
        InitializeComponent();
        ClipList.ItemsSource = rows;
        searchTimer.Tick += async (_, _) => { searchTimer.Stop(); await Safe(RefreshAsync); };
        maintenance.Tick += async (_, _) => await Safe(async () => { await store.MaintainAsync(); MarkHistoryChanged(); });
        Loaded += async (_, _) => { await InitializeAsync(); if (IsVisible) SearchBox.Focus(); };
        Closing += (_, e) => { if (!exiting) { e.Cancel = true; Hide(); } };
    }
    internal Task InitializeAsync() => initialization ??= InitializeCoreAsync();
    private async Task InitializeCoreAsync()
    {
        initialized = true;
        var handle = new WindowInteropHelper(this).EnsureHandle();
        clipboard = new(settings, async payload => { await store.AddAsync(payload); _ = Dispatcher.BeginInvoke(MarkHistoryChanged); }, text => Dispatcher.BeginInvoke(() => { SetStatus(text); if (!IsVisible && tray is not null && (text.StartsWith("Clip not saved:", StringComparison.Ordinal) || text.StartsWith("Full text saved.", StringComparison.Ordinal))) tray.ShowBalloonTip(5000, "Clipboard Plus", text, System.Windows.Forms.ToolTipIcon.Info); }));
        clipboard.Paused = demo;
        await clipboard.Ready;
        hotkeys = new(handle, ShowPanel);
        try { hotkeys.Configure(settings); } catch (Exception e) { SetStatus(e.Message); }
        CreateTray(); UpdateSettingsLabels(); maintenance.Start();
        await Safe(RefreshAsync);
        if (demo) SetStatus("Demo workspace · capture paused · sample clips only");
        else if (settings.WindowsHistoryManaged) { try { SetStatus(WindowsHistorySettings.Apply(settings)); } catch (Exception e) { SetStatus("Could not apply Windows history preference: " + e.Message); } }
        loaded.TrySetResult();
        _ = Safe(async () => { while (!exiting && await store.RepairTextBatchAsync()) await Task.Delay(50); if (!exiting && SearchBox.Text.Length > 0) MarkHistoryChanged(); });
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
        if (exiting) return;
        Cleanup();
        System.Windows.Application.Current.Shutdown();
    }
    internal async void ExitForInstaller()
    {
        for (int i = 0; i < 100 && clipboardAction; i++) await Task.Delay(50);
        if (!clipboardAction) Exit();
    }
    private void Cleanup() { exiting = true; maintenance.Stop(); searchTimer.Stop(); clipboard.Dispose(); hotkeys.Dispose(); tray.Dispose(); appIcon?.Dispose(); }
    public void ShowPanel(IntPtr target)
        => ShowPanel(Native.CaptureDestination(target));
    public void ShowPanel(PasteDestination target)
    {
        if (clipboardAction) { pendingPanel = target; return; }
        if (target.Window != new WindowInteropHelper(this).Handle && !OwnedWindows.Cast<Window>().Any(w => new WindowInteropHelper(w).Handle == target.Window)) pasteDestination = target;
        if (IsVisible && target.Window == new WindowInteropHelper(this).Handle) { Hide(); return; }
        Show(); WindowState = WindowState.Normal; Activate(); SearchBox.Focus(); SearchBox.SelectAll(); ScheduleRefresh();
    }
    private void SetStatus(string text)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => SetStatus(text)); return; }
        StatusLabel.Text = text;
        StatusLabel.ToolTip = text;
    }
    private void PasteNotice(string text)
    {
        SetStatus(text);
        if (!IsVisible && !demo) tray.ShowBalloonTip(3500, "Clipboard Plus", text, System.Windows.Forms.ToolTipIcon.Info);
    }
    private void FinishClipboardAction()
    {
        clipboardAction = false;
        if (pendingPanel is { } nextPanel) { pendingPanel = null; ShowPanel(nextPanel); }
    }
    private async Task Safe(Func<Task> action)
    {
        try { await action(); } catch (Exception e) { SetStatus(e.Message); }
    }
    private void MarkHistoryChanged() { if (!exiting) RefreshButton.Visibility = Visibility.Visible; }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Safe(RefreshAsync);
    private void ScheduleRefresh() { ++refreshVersion; queryPending = true; searchTimer.Stop(); searchTimer.Start(); }
    private DateTimeOffset? Since => DateFilter.SelectedIndex switch { 1 => new DateTimeOffset(DateTime.Today), 2 => DateTimeOffset.Now.AddDays(-7), 3 => DateTimeOffset.Now.AddDays(-30), _ => null };
    private async Task RefreshAsync()
    {
        long version = ++refreshVersion;
        queryPending = true;
        searchTimer.Stop();
        try
        {
        RefreshButton.Visibility = Visibility.Collapsed;
        var results = await store.QueryAsync(new(SearchBox.Text, filter, offset, 80, Since));
        // Never recycle a row between the two presses of a double-click.
        while (version == refreshVersion && IsVisible && (Mouse.LeftButton == MouseButtonState.Pressed || Environment.TickCount64 - lastRowPress < Native.GetDoubleClickTime())) await Task.Delay(25);
        if (version != refreshVersion) return;
        // Selection may have changed while the database query was in flight.
        var selected = ClipList.SelectedItems.Cast<ClipRow>().Select(r => r.Id).ToHashSet();
        var wanted = results.Select(r => r.Id).ToHashSet();
        for (int i = rows.Count - 1; i >= 0; i--) if (!wanted.Contains(rows[i].Id)) rows.RemoveAt(i);
        for (int i = 0; i < results.Count; i++)
        {
            var row = results[i];
            var existing = rows.FirstOrDefault(r => r.Id == row.Id);
            if (existing is null) rows.Insert(i, row);
            else
            {
                int current = rows.IndexOf(existing); if (current != i) rows.Move(current, i);
                if (existing.Updated != row.Updated || existing.Pinned != row.Pinned || existing.Title != row.Title || existing.Preview != row.Preview || existing.Summary.MediaSource != row.Summary.MediaSource) rows[i] = row;
            }
        }
        foreach (var row in rows.Where(r => selected.Contains(r.Id))) if (!ClipList.SelectedItems.Contains(row)) ClipList.SelectedItems.Add(row);
        if (ClipList.SelectedItems.Count == 0) ClipList.SelectedItem = rows.FirstOrDefault();
        PasteButton.IsEnabled = ClipList.SelectedItem is not null;
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
        finally { if (version == refreshVersion) queryPending = false; }
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
    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PasteButton is not null) PasteButton.IsEnabled = ClipList.SelectedItem is not null;
        if (CopySelectedButton is not null) { CopySelectedButton.IsEnabled = ClipList.SelectedItems.Count > 0; CopySelectedButton.Content = $"⧉  Copy ({ClipList.SelectedItems.Count})"; }
    }
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
    private async Task PasteAsync(bool plain = false, bool copyOnly = false, ClipPayload? supplied = null, long? clickedId = null, bool fromQueue = false)
    {
        if (clipboardAction) return;
        if (queryPending && supplied is null) { SetStatus("Updating the list. Choose your clip when the results are ready."); return; }
        clipboardAction = true;
        var destination = pasteDestination;
        long? selectedId = clickedId ?? (ClipList.SelectedItem as ClipRow)?.Id;
        try
        {
        long? queueId = fromQueue && pasteQueue.TryPeek(out long next) ? next : null;
        if (fromQueue && queueId is null) return;
        var payload = supplied ?? ((queueId ?? selectedId) is { } id ? await store.GetAsync(id) : null);
        if (payload is null)
        {
            if (queueId is not null) { pasteQueue.Dequeue(); UpdateQueue(); SetStatus("A queued clip was removed. Continue with the next item."); }
            return;
        }
        if (copyOnly) { await clipboard.PutAsync(payload, plain); SetStatus("Copied. Ready to paste anywhere."); return; }
        var pasteTarget = destination.Window;
        if (!Native.ValidDestination(destination) || pasteTarget == new WindowInteropHelper(this).Handle)
        {
            await clipboard.PutAsync(payload, plain);
            SetStatus("Copied. Open Clipboard Plus from your destination app using the shortcut to paste directly."); return;
        }
        if (!Native.IsOurWindow(Native.GetForegroundWindow()))
        {
            await clipboard.PutAsync(payload, plain);
            PasteNotice("Copied. Focus changed while preparing the clip; use Ctrl+V where you want to paste."); return;
        }
        Hide();
        if (Native.GetForegroundWindow() != pasteTarget) Native.SetForegroundWindow(pasteTarget);
        for (int i = 0; i < 10 && Native.GetForegroundWindow() != pasteTarget; i++) await Task.Delay(10);
        if (Native.GetForegroundWindow() != pasteTarget) { await clipboard.PutAsync(payload, plain); PasteNotice("Copied. Windows prevented focus switching; paste manually with Ctrl+V."); return; }
        // Never synthesize Ctrl+V while the user still holds the shortcut's modifier keys.
        for (int i = 0; i < 40 && new[] { 0x01, 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(k => Native.GetAsyncKeyState(k) < 0); i++) await Task.Delay(25);
        await clipboard.PublishForPasteAsync(payload, plain, async receipt =>
        {
            if (!await clipboard.IsCurrentAsync(receipt)) { PasteNotice("Another app changed the clipboard. Paste was cancelled; choose the clip again."); return; }
            if (!Native.RestoreDestinationFocus(destination) || Native.GetForegroundWindow() != pasteTarget || new[] { 0x01, 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(k => Native.GetAsyncKeyState(k) < 0) || Native.GetClipboardSequenceNumber() != receipt.Sequence || !Native.SendPaste())
            { PasteNotice("Copied, but automatic paste was blocked. Use Ctrl+V in the destination app."); return; }
            if (queueId is not null) { pasteQueue.Dequeue(); UpdateQueue(); }
            SetStatus("Paste sent to the previous app.");
            // No writer in this app can replace the clipboard during input delivery.
            await Task.Delay(300);
            if (!settings.HideAfterPaste && pendingPanel is null && Native.GetForegroundWindow() == pasteTarget) { Show(); Activate(); }
        });
        }
        finally { FinishClipboardAction(); }
    }
    private void UpdateQueue()
    {
        QueueLabel.Text = pasteQueue.Count == 0 ? "Enter to paste  ·  Ctrl+Enter for plain text  ·  Esc to hide" : $"Queue: {pasteQueue.Count} remaining · Enter still pastes your selected clip";
        PasteQueueButton.Visibility = pasteQueue.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        PasteQueueButton.Content = $"Paste next queued ({pasteQueue.Count})";
    }
    private async void PasteQueue_Click(object sender, RoutedEventArgs e) => await Safe(() => PasteAsync(fromQueue: true));
    private async void Paste_Click(object sender, RoutedEventArgs e) => await Safe(() => PasteAsync());
    private async void Copy_Click(object sender, RoutedEventArgs e) => await Safe(CopySelectionAsync);
    private async void RowCopy_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (((FrameworkElement)sender).Tag is ClipRow row) await Safe(() => PasteAsync(copyOnly: true, clickedId: row.Id));
    }
    private async Task CopySelectionAsync()
    {
        if (queryPending) { SetStatus("Wait for the search results before copying."); return; }
        var ids = rows.Where(r => ClipList.SelectedItems.Contains(r)).Select(r => r.Id).ToArray();
        if (ids.Length == 1) { await PasteAsync(copyOnly: true, clickedId: ids[0]); return; }
        if (ids.Length == 0 || clipboardAction) return;
        clipboardAction = true;
        try
        {
            var payloads = new List<ClipPayload>(); long bytes = 0;
            foreach (var id in ids)
            {
                var p = await store.GetAsync(id) ?? throw new InvalidOperationException("A selected clip was removed. Select the remaining items again.");
                bytes += (p.Image?.LongLength ?? 0) + (long)p.Text.Length * 2;
                if (bytes > 128 * 1048576L) throw new InvalidOperationException("Select fewer items: combined image and text data is limited to 128 MB.");
                payloads.Add(p);
            }
            await clipboard.PutManyAsync(payloads);
            SetStatus($"Copied {ids.Length} items. The receiving app chooses text or attachments; paste captions separately if needed.");
        }
        finally { FinishClipboardAction(); }
    }
    private static ClipRow? HitRow(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase or Slider or MediaControls) return null;
            if (d is ListBoxItem item) return item.DataContext as ClipRow;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }
    private void Clip_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) pressedRowId = HitRow(e.OriginalSource as DependencyObject)?.Id;
        lastRowPress = Environment.TickCount64;
    }
    private async void Clip_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        var clicked = HitRow(e.OriginalSource as DependencyObject);
        if (clicked is null) return;
        e.Handled = true;
        if (pressedRowId is { } pressed && Environment.TickCount64 - lastRowPress <= Native.GetDoubleClickTime() && pressed != clicked.Id) { SetStatus("Selection changed between clicks. Choose the item again."); return; }
        await Safe(() => PasteAsync(clickedId: clicked.Id));
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
        => OpenActionsMenu((Button)sender);
    private ContextMenu OpenActionsMenu(Button button)
    {
        var menu = BuildActionsMenu();
        var dpi = VisualTreeHelper.GetDpi(this);
        var work = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
        menu.MaxHeight = Math.Max(120, Math.Min(560, work.Height / dpi.DpiScaleY - 24));
        menu.MaxWidth = Math.Max(120, Math.Min(480, work.Width / dpi.DpiScaleX - 24));
        menu.MinWidth = Math.Min(320, menu.MaxWidth);
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.VerticalOffset = -6;
        menu.IsOpen = true;
        return menu;
    }
    private ContextMenu BuildActionsMenu()
    {
        var menu = new ContextMenu { Style = (Style)FindResource("ActionsMenuStyle"), ItemContainerStyle = (Style)FindResource("ActionMenuItemStyle") };
        void Add(string title, Func<Task> action, string shortcut = "") { var item = new MenuItem { Header = title, InputGestureText = shortcut }; item.Click += async (_, _) => await Safe(action); menu.Items.Add(item); }
        void Separate() => menu.Items.Add(new Separator { Style = (Style)FindResource("ActionSeparatorStyle") });
        Add("Paste exact text (no formatting)", () => PasteAsync(true), "Ctrl+Enter");
        Add("Copy exact text (no formatting)", () => PasteAsync(true, true));
        Add("Preview", () => { Preview_Click(this, new()); return Task.CompletedTask; }, "Space");
        Separate();
        Add("Queue selected clips (list order)", () => { foreach (var row in rows.Where(r => ClipList.SelectedItems.Contains(r))) pasteQueue.Enqueue(row.Id); UpdateQueue(); SetStatus($"{pasteQueue.Count} clips in the paste queue."); return Task.CompletedTask; });
        Add("Clear paste queue", () => { pasteQueue.Clear(); UpdateQueue(); return Task.CompletedTask; });
        Add("Copy selected text only (caption)", async () =>
        {
            var ids = rows.Where(r => ClipList.SelectedItems.Contains(r)).Select(r => r.Id).ToArray();
            var parts = new List<string>();
            foreach (var id in ids) if (await store.GetAsync(id) is { Kind: ClipKind.Text or ClipKind.Link } p) parts.Add(p.Text);
            if (parts.Count == 0) throw new InvalidOperationException("Select text or links to copy a caption.");
            await PasteAsync(copyOnly: true, supplied: new() { Text = string.Join(Environment.NewLine, parts) });
        });
        Add("Combine selected as lines", async () =>
        {
            var parts = new List<string>();
            foreach (var row in rows.Where(r => ClipList.SelectedItems.Contains(r))) if (await store.GetAsync(row.Id) is { } p) parts.Add(p.Kind == ClipKind.Files ? string.Join(Environment.NewLine, p.Paths) : p.Text);
            var payload = new ClipPayload { Text = string.Join(Environment.NewLine, parts.Where(s => s.Length > 0)), Source = "Clipboard Plus", Kind = ClipKind.Text };
            if (payload.Text.Length == 0) throw new InvalidOperationException("Select text, links, or file paths to combine.");
            await store.AddAsync(payload); await clipboard.PutAsync(payload); SetStatus("Combined text copied and saved."); await RefreshAsync();
        });
        Separate();
        Add("Edit as a new snippet", async () => { if (await SelectedPayloadAsync() is { } p) await EditSnippetAsync(p.Text); });
        Add("Copy trimmed text", () => TransformAsync(s => s.Trim()));
        Add("Copy UPPERCASE", () => TransformAsync(s => s.ToUpperInvariant()));
        Add("Copy lowercase", () => TransformAsync(s => s.ToLowerInvariant()));
        Add("Copy file paths", () => PasteAsync(true, true));
        Separate();
        Add("Delete selected clips", DeleteSelectedAsync, "Delete");
        return menu;
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
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && ClipList.IsKeyboardFocusWithin) { e.Handled = true; await Safe(CopySelectionAsync); }
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
        await RenderActionsMenuAsync(directory, "dark");
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
        await RenderActionsMenuAsync(directory, "light");
        var text = await PreviewWindow.ExtractTextAsync(payload.Image!);
        await File.WriteAllTextAsync(Path.Combine(directory, "ocr-result.txt"), text);
        Exit();
    }
    private async Task RenderActionsMenuAsync(string directory, string theme)
    {
        var menu = OpenActionsMenu(ActionsButton);
        try
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            menu.UpdateLayout();
            if (menu.ActualWidth <= 0 || menu.ActualHeight <= 0) throw new InvalidOperationException("Actions menu did not lay out.");
            void Render(string suffix, double scale)
            {
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth * scale), (int)Math.Ceiling(menu.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(menu);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(directory, $"actions-{theme}-{suffix}.png")); encoder.Save(file);
            }
            foreach (double scale in new[] { 1d, 1.75, 2d }) Render(((int)(scale * 100)).ToString(), scale);
            menu.MaxHeight = 220; menu.UpdateLayout();
            var scroll = (ScrollViewer)menu.Template.FindName("MenuScroll", menu);
            if (scroll.ScrollableHeight <= 0) throw new InvalidOperationException("A constrained Actions menu must scroll.");
            Render("compact-top", 1.75);
            scroll.ScrollToEnd(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); menu.UpdateLayout();
            if (scroll.VerticalOffset <= 0) throw new InvalidOperationException("Lower menu actions could not be reached.");
            Render("compact-bottom", 1.75);
        }
        finally { menu.IsOpen = false; }
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
            // Repeat against different clicked rows while a previous row and queue remain selected.
            var firstId = rows.Single().Id;
            for (int round = 0; round < 20; round++)
            {
                string nextText = $"Different clicked item {round}";
                long nextId = await store.AddAsync(new() { Text = nextText });
                destination.Clear(); target.Activate(); destination.Focus();
                await Task.Delay(60);
                ShowPanel(new WindowInteropHelper(target).Handle); await RefreshAsync();
                ClipList.SelectedItem = rows.Single(r => r.Id == firstId);
                pasteQueue.Enqueue(firstId);
                var clickedRow = rows.Single(r => r.Id == nextId);
                ClipList.ScrollIntoView(clickedRow); ClipList.UpdateLayout();
                var container = (ListBoxItem)ClipList.ItemContainerGenerator.ContainerFromItem(clickedRow);
                if (round % 2 == 0) ClipList.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent, Source = container });
                else { ClipList.SelectedItem = clickedRow; await PasteAsync(); }
                for (int i = 0; i < 80 && (destination.Text != nextText || clipboardAction); i++) await Task.Delay(25);
                if (destination.Text != nextText || pasteQueue.Count != round + 1) throw new InvalidOperationException($"Repeated double-click round={round}, text={destination.Text}, expected={nextText}, queue={pasteQueue.Count}, busy={clipboardAction}, clicked={container.DataContext}, status={StatusLabel.Text}");
            }
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
    internal static async Task<bool> VerifyClipboardRoutingAsync(HistoryStore store)
    {
        var window = new MainWindow(store, new() { Hotkey = "Ctrl+Alt+F9" }, true);
        try
        {
            await window.InitializeAsync();
            long oldId = await store.AddAsync(new() { Text = "Old queue fixture" });
            window.pasteQueue.Enqueue(oldId);
            for (int i = 0; i < 24; i++)
            {
                string expected = $"Requested row {i} — שלום 你好";
                long id = await store.AddAsync(new() { Text = expected });
                await window.RefreshAsync();
                window.ClipList.SelectedItem = window.rows.Single(r => r.Id == (i % 2 == 0 ? oldId : id));
                // With no valid destination, the full Paste flow must still copy the right item.
                await window.PasteAsync(clickedId: i % 2 == 0 ? id : null);
                if (System.Windows.Clipboard.GetText() != expected || window.pasteQueue.Count != 1) return false;
            }
            // Search changes invalidate a query immediately, before its debounce fires.
            var pending = window.RefreshAsync(); window.SearchBox.Text = "not-in-any-fixture";
            await pending;
            if (!window.queryPending) return false;
            await window.RefreshAsync();
            return window.rows.Count == 0;
        }
        finally { window.Cleanup(); window.Close(); }
    }
    internal static async Task<bool> VerifySelectionRefreshAsync(HistoryStore store)
    {
        var window = new MainWindow(store, new() { Hotkey = "Ctrl+Alt+F9" }, true);
        try
        {
            await window.InitializeAsync();
            var ids = window.rows.Take(2).Select(r => r.Id).ToArray();
            window.ClipList.SelectedItems.Add(window.rows.Single(r => r.Id == ids[1]));
            await window.RefreshAsync();
            if (window.ClipList.SelectedItems.Count != 2) return false;
            var pendingRefresh = window.RefreshAsync();
            window.ClipList.SelectedItems.Clear();
            window.ClipList.SelectedItem = window.rows.Single(r => r.Id == ids[1]);
            await pendingRefresh;
            if (window.ClipList.SelectedItems.Count != 1 || ((ClipRow)window.ClipList.SelectedItem).Id != ids[1]) return false;
            var before = window.rows.ToArray();
            await store.AddAsync(new() { Text = "New capture while choosing a row" });
            window.MarkHistoryChanged();
            await Task.Delay(200);
            return before.SequenceEqual(window.rows) && window.RefreshButton.Visibility == Visibility.Visible;
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
