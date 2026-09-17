using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;

namespace ClipboardPlus;

internal static class SelfTests
{
    public static async Task<int> RunAsync(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "ClipboardPlus-Test-" + Guid.NewGuid().ToString("N"));
        var report = new List<object>(); int passed = 0;
        var timer = Stopwatch.StartNew();
        void Check(bool result, string name) { if (!result) throw new InvalidOperationException("FAILED: " + name); passed++; report.Add(new { test = name, result = "passed" }); }
        try
        {
            var config = new AppSettings { MaxItems = 100, CacheBudgetMb = 1 };
            using (var store = new HistoryStore(root, config))
            {
                string marker = "ConfidentialSecret_" + Guid.NewGuid().ToString("N");
                var id = await store.AddAsync(new() { Kind = ClipKind.Text, Text = marker + "\nAlpha beta world", Html = "<b>Alpha</b>", Rtf = @"{\rtf1 Alpha}", Source = "TestEditor" });
                var duplicate = await store.AddAsync(new() { Kind = ClipKind.Text, Text = marker + "\nAlpha beta world", Html = "<b>Alpha</b>", Rtf = @"{\rtf1 Alpha}", Source = "OtherApp" });
                Check(id == duplicate && (await store.StatsAsync()).Count == 1, "Identical content deduplicates across source apps");
                var payload = await store.GetAsync(id);
                Check(payload?.Html == "<b>Alpha</b>" && payload.Rtf == @"{\rtf1 Alpha}", "Rich formats survive encrypted storage");
                Check((await store.QueryAsync(new("alp wor"))).Single().Id == id, "Indexed prefix search ANDs multiple words");
                Check((await store.QueryAsync(new("testedit"))).Count == 1, "Source app is searchable");
                Check((await store.QueryAsync(new("unfindable"))).Count == 0, "Unmatched search returns no entries");
                Check((await store.QueryAsync(new(Filter: "Image"))).Count == 0, "Content filter excludes unrelated kinds");
                Check((await store.QueryAsync(new(Since: DateTimeOffset.UtcNow.AddDays(1)))).Count == 0, "Date filter applies to history");
                await store.SetPinAsync(id, true);
                Check((await store.QueryAsync(new(Filter: "Pinned"))).Single().Id == id, "Pins are queryable");
                var named = await store.AddAsync(payload! with { Name = "Reusable greeting", Source = "Snippet" }, true);
                Check(named == id && (await store.QueryAsync(new("reusable"))).Single().Title == "Reusable greeting" && (await store.GetAsync(id))!.Name == "Reusable greeting", "Turning existing content into a named snippet updates summary, payload cache, and search index");
                for (int i = 0; i < 105; i++) await store.AddAsync(new() { Text = $"Sample {i:D5}", Source = "Test" });
                Check((await store.StatsAsync()).Count == 100 && await store.GetAsync(id) is not null, "Capacity eviction preserves pins and enforces item count");
                var first = await store.QueryAsync(new(Limit: 80)); var second = await store.QueryAsync(new(Offset: 80, Limit: 80));
                Check(first.Count == 80 && second.Count == 20 && !first.Select(r => r.Id).Intersect(second.Select(r => r.Id)).Any(), "History paging has no gaps or overlapping entries");
                byte[] data;
                using (var dbFile = new FileStream(Path.Combine(root, "history.db"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                { using var copy = new MemoryStream(); await dbFile.CopyToAsync(copy); data = copy.ToArray(); }
                Check(!Encoding.UTF8.GetString(data).Contains(marker) && !Encoding.UTF8.GetString(data).Contains("TestEditor"), "Database does not contain plaintext payload or summary");
                await store.ClearAsync(false);
                Check((await store.StatsAsync()).Count == 1, "Clear unpinned keeps pinned content");
                bool rejected = false;
                try { await store.ConfigureAsync(config with { MaxItemMb = 1 }); await store.AddAsync(new() { Text = new string('x', 1100000) }); }
                catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "Oversized item is rejected before persistence");
                await store.ConfigureAsync(config);
                await store.DeleteAsync([id]);
                Check(await store.GetAsync(id) is null && (await store.QueryAsync(new("alpha"))).Count == 0, "Deletion removes payload, cache, and index entries");
                await store.AddAsync(new() { Text = "Persistent after reopening", Source = "Test" });
            }
            using (var reopened = new HistoryStore(root, new())) Check((await reopened.QueryAsync(new("persistent"))).Count == 1, "History and key survive process-style reopen");
            var cache = new PayloadCache(8); cache.Add(1, new byte[4]); cache.Add(2, new byte[4]); cache.Get(1); cache.Add(3, new byte[4]);
            Check(cache.Get(2) is null && cache.Get(1) is not null && cache.Bytes == 8, "LRU cache evicts the least recently used payload");
            cache.SetBudget(0); Check(cache.Bytes == 0 && cache.Get(1) is null, "Zero cache budget releases all cached payloads");
            Check(HotkeySpec.Parse("Ctrl+Shift+V") == new HotkeySpec(6, 0x56), "Custom hotkey parsing");
            Check(HotkeySpec.Parse("Win+V") == new HotkeySpec(8, 0x56), "Win+V replacement specification");
            Check(HotkeySpec.Parse("Ctrl+1") == new HotkeySpec(2, 0x31), "Numeric shortcut maps to the digit virtual key");
            var winState = new WinVState(); int opens = 0;
            Check(winState.Handle(true, true, false, () => opens++) && winState.Handle(true, true, false, () => opens++) && opens == 1 && winState.Handle(false, false, false, () => opens++), "Win+V state suppresses both edges and avoids repeated opens");
            Check(!winState.Handle(true, true, true, () => opens++) && !winState.Handle(true, false, false, () => opens++), "Win+V state leaves unrelated modifier combinations untouched");
            using (var hotkeys = new HotkeyService(IntPtr.Zero, _ => { }))
            {
                hotkeys.Configure(new() { Hotkey = "Ctrl+Alt+F9" });
                bool conflict = Native.RegisterHotKey(IntPtr.Zero, 739, 3, 0x78);
                if (conflict) Native.UnregisterHotKey(IntPtr.Zero, 739);
                Check(!conflict, "Custom shortcut registers globally on the dedicated worker");
                hotkeys.Configure(new() { ReplaceWinV = true });
                Check(hotkeys.IsWinVActive, "Windows accepts the Win+V replacement keyboard hook");
                hotkeys.Configure(new() { Hotkey = "Ctrl+Alt+F9" });
                Check(!hotkeys.IsWinVActive, "Returning to custom shortcut removes the Windows-key hook");
                hotkeys.Configure(new() { Hotkey = "F12" }); Check(hotkeys.IsHookActive, "F12 uses a native hook rather than reserved RegisterHotKey");
                hotkeys.SetSuspended(true); Check(!hotkeys.IsHookActive, "Shortcut is suspended while recording a new key");
                hotkeys.SetSuspended(false); Check(hotkeys.IsHookActive, "Canceling recorder restores the previous shortcut");
                hotkeys.Configure(new() { Hotkey = "ScrLk" }); Check(hotkeys.IsHookActive, "Single Scroll Lock key is supported");
            }
            Check(HotkeySpec.Parse("F12") == new HotkeySpec(0, 0x7B) && HotkeySpec.Parse("ScrLk") == new HotkeySpec(0, 0x91), "Single F12 and Scroll Lock shortcuts parse");
            bool invalidKey = false; try { HotkeySpec.Parse("Ctrl+Alt+Shift+V"); } catch (ArgumentException) { invalidKey = true; } Check(invalidKey, "More than three shortcut keys are rejected");
            using (var backgroundStore = new HistoryStore(Path.Combine(root, "background"), new()))
                Check(await MainWindow.VerifyBackgroundAsync(backgroundStore), "Background startup initializes capture, tray, and shortcut without showing or activating the panel");
            Check(new AppSettings().Excludes("Bitwarden") && !new AppSettings().Excludes("Notepad"), "Excluded app names match exact process names");
            Check(KeyRecorderWindow.Format(6, 0x56) == "Ctrl+Shift+V" && KeyRecorderWindow.Format(0, 0x91) == "ScrLk", "Recorder formats captured key combinations and Scroll Lock");
            Check(UpdateChecker.Describe("{\"tag_name\":\"v1.2.0\"}", new Version(1, 1, 0)).Contains("is available") && UpdateChecker.Describe("{\"tag_name\":\"v1.1.0\"}", new Version(1, 1, 0, 0)).Contains("up to date"), "Manual update check identifies newer and current release versions");
            Check(WindowsHistorySettings.Desired(new() { ReplaceWinV = true }) == false && WindowsHistorySettings.Desired(new()) == true && WindowsHistorySettings.Desired(new() { ReplaceWinV = true, WindowsHistoryMode = "On" }) == true && WindowsHistorySettings.Desired(new() { WindowsHistoryMode = "Off" }) == false && WindowsHistorySettings.Desired(new() { WindowsHistoryMode = "Unchanged" }) is null, "Windows history automatic and independent overrides map to the requested preference");
            string preferenceFixture = @"Software\ClipboardPlusTests\" + Guid.NewGuid().ToString("N");
            try
            {
                using var preference = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(preferenceFixture);
                WindowsHistorySettings.WritePreference(preference, false); Check((int)preference.GetValue("EnableClipboardHistory")! == 0, "Windows history preference writes disabled to an isolated test key");
                WindowsHistorySettings.WritePreference(preference, true); Check((int)preference.GetValue("EnableClipboardHistory")! == 1, "Windows history preference writes enabled to an isolated test key");
            }
            finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(preferenceFixture, false); }
            var rich = new System.Windows.DataObject(); rich.SetText("hello"); rich.SetData(DataFormats.Html, "<b>hello</b>"); rich.SetData(DataFormats.Rtf, @"{\rtf1 hello}");
            var captured = ClipboardService.ReadPayload(rich, "Test", 1024 * 1024)!;
            Check(captured.Text == "hello" && captured.Html == "<b>hello</b>", "Clipboard capture preserves text and HTML");
            var replay = ClipboardService.CreateData(captured, false); var plain = ClipboardService.CreateData(captured, true);
            Check(replay.GetDataPresent(DataFormats.Rtf) && !plain.GetDataPresent(DataFormats.Rtf) && plain.GetText() == "hello", "Plain-text paste strips rich formats");
            string fixture = Path.Combine(root, "fixture.txt"); await File.WriteAllTextAsync(fixture, "fixture");
            var filePayload = new ClipPayload { Kind = ClipKind.Files, Paths = [fixture, root] };
            var files = ClipboardService.CreateData(filePayload, false);
            Check(files.GetFileDropList().Count == 2 && ClipboardService.ReadPayload(files, "Test", 10000)!.Paths.SequenceEqual(filePayload.Paths), "Mixed file and folder selections round-trip by reference");
            Check(BitConverter.ToInt32(((MemoryStream)files.GetData("Preferred DropEffect")!).ToArray()) == 1, "Historical file replay copies instead of repeating stale moves");
            bool missing = false; try { ClipboardService.CreateData(filePayload with { Paths = [Path.Combine(root, "missing")] }, false); } catch (FileNotFoundException) { missing = true; } Check(missing, "Missing original file is reported before paste");
            var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8); var imageData = new System.Windows.DataObject(); imageData.SetImage(bitmap);
            var image = ClipboardService.ReadPayload(imageData, "Test", 100000)!;
            Check(image.Kind == ClipKind.Image && ClipboardService.CreateData(image, false).ContainsImage(), "Direct bitmap capture and replay");
            var batchRoot = Path.Combine(root, "copy-exports");
            var batch = ClipboardService.CreateManyData([new() { Text = "Caption one" }, filePayload, image, new() { Text = "Caption two" }, filePayload], batchRoot);
            Check(batch.GetText() == "Caption one" + Environment.NewLine + "Caption two" && batch.GetFileDropList().Count == 3 && batch.ContainsImage(), "Mixed selection includes ordered text, deduplicated file references and image formats");
            Check(File.ReadAllBytes(batch.GetFileDropList()[2]!).SequenceEqual(image.Image!) && File.ReadAllText(fixture) == "fixture", "Batch image export preserves original pixels without copying source files");
            Check(ClipboardService.CreateManyData([captured], batchRoot).GetDataPresent(DataFormats.Rtf), "Single-item batch preserves rich text formats");
            Check(ClipboardService.CreateManyData([new() { Text = "One" }, new() { Text = "Two" }], batchRoot).GetText() == "One" + Environment.NewLine + "Two", "Multiple text selections combine in supplied order");
            bool missingBatch = false; try { ClipboardService.CreateManyData([filePayload with { Paths = [Path.Combine(root, "absent.mp4")] }, image], batchRoot); } catch (FileNotFoundException) { missingBatch = true; }
            Check(missingBatch, "Missing source aborts whole batch rather than silently copying partial selection");
            var pngOnly = new System.Windows.DataObject(); pngOnly.SetData("PNG", new MemoryStream(image.Image!));
            Check(ClipboardService.ReadPayload(pngOnly, "Test", 100000)!.Image!.SequenceEqual(image.Image!), "PNG-only clipboard image preserves its original encoded bytes");
            using (var imageStore = new HistoryStore(Path.Combine(root, "images"), new()))
            {
                await imageStore.AddAsync(image);
                var row = (await imageStore.QueryAsync(new())).Single();
                Check(row.Thumbnail is not null && row.Thumbnail.PixelWidth <= 128 && row.Thumbnail.PixelHeight <= 128, "Image list loads a bounded thumbnail rather than the full-resolution bitmap");
                var otherData = new System.Windows.DataObject(); otherData.SetImage(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)255, 16).ToArray(), 8));
                await imageStore.AddAsync(ClipboardService.ReadPayload(otherData, "Other image", 100000)!);
                Check(await MainWindow.VerifyPreviewRoutingAsync(imageStore), "Second image preview opens its own payload even with the first image selected and open");
                Check(await MainWindow.VerifySelectionRefreshAsync(imageStore), "Refresh preserves multiple selection and never restores selection captured before an asynchronous query");
            }
            using (var bounded = new HistoryStore(Path.Combine(root, "bounded"), new AppSettings { DiskBudgetMb = 32, MaxItemMb = 4 }))
            {
                for (int i = 0; i < 20; i++) await bounded.AddAsync(new() { Text = new string('x', 1800000) + i, Source = "BudgetTest" });
                var stats = await bounded.StatsAsync();
                Check(stats.Count > 0 && stats.Count < 20 && stats.PayloadBytes < 32 * 1048576L, "Disk budget evicts old large clips and retains the newest");
                Check((await bounded.QueryAsync(new())).First().Preview.StartsWith("xxx"), "Newest large clip remains readable after quota eviction");
            }
            string expiryRoot = Path.Combine(root, "expiry");
            using (var expiry = new HistoryStore(expiryRoot, new()))
            {
                await expiry.AddAsync(new() { Text = "Expired unpinned" }); await expiry.AddAsync(new() { Text = "Expired but pinned" }, true);
            }
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(expiryRoot, "history.db")};Pooling=False"))
            {
                connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "UPDATE clips SET updated=1"; command.ExecuteNonQuery();
            }
            using (var expiry = new HistoryStore(expiryRoot, new()))
            {
                await expiry.MaintainAsync(); Check((await expiry.StatsAsync()).Count == 1 && (await expiry.QueryAsync(new())).Single().Pinned, "Time-based expiry removes old clips but preserves pins");
            }
            if (args.Contains("--clipboard-test")) await ClipboardIntegrationAsync(Check);
            if (args.Contains("--input-test"))
            {
                System.Windows.IDataObject? previous;
                for (int attempt = 0; ; attempt++)
                {
                    try { previous = System.Windows.Clipboard.GetDataObject(); break; }
                    catch (System.Runtime.InteropServices.ExternalException) when (attempt < 30) { await Task.Delay(100); }
                }
                try
                {
                    using var pasteStore = new HistoryStore(Path.Combine(root, "paste"), new());
                    const string expected = "Clipboard Plus end-to-end paste fixture";
                    await pasteStore.AddAsync(new() { Text = expected, Source = "Test" });
                    var destination = new System.Windows.Controls.TextBox { AcceptsReturn = true, Margin = new Thickness(24) };
                    var panel = new System.Windows.Controls.DockPanel();
                    var start = new System.Windows.Controls.Button { Content = "Start paste check", Margin = new Thickness(24) };
                    System.Windows.Controls.DockPanel.SetDock(start, System.Windows.Controls.Dock.Bottom); panel.Children.Add(start); panel.Children.Add(destination);
                    var target = new Window { Title = "Clipboard Plus paste verification", Width = 500, Height = 300, Content = panel };
                    var clicked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    start.Click += (_, _) => clicked.TrySetResult();
                    try
                    {
                        target.Show();
                        await clicked.Task.WaitAsync(TimeSpan.FromMinutes(2));
                        var main = new MainWindow(pasteStore, new(), true); main.Show();
                        Check(await main.VerifyPasteAsync(target, destination, expected), "Native paste repeats with three different double-clicked rows, stale selection and nonempty queue");
                    }
                    finally { target.Close(); }
                }
                finally
                {
                    for (int attempt = 0; ; attempt++)
                    {
                        try { if (previous is not null) System.Windows.Clipboard.SetDataObject(previous, true); else System.Windows.Clipboard.Clear(); break; }
                        catch (System.Runtime.InteropServices.ExternalException) when (attempt < 30) { await Task.Delay(100); }
                    }
                }
            }
            int mediaIndex = Array.IndexOf(args, "--media-test");
            if (mediaIndex >= 0 && mediaIndex + 1 < args.Length)
            {
                string fixtures = Path.GetFullPath(args[mediaIndex + 1]);
                Check(await PreviewWindow.VerifyMediaAsync(Path.Combine(fixtures, "preview.wav")), "Local audio preview starts on request, advances, and releases player on close");
                Check(await PreviewWindow.VerifyMediaAsync(Path.Combine(fixtures, "preview.mp4")), "Local H.264 video preview starts on request, advances, and releases player on close");
                Check(await MediaControls.VerifyPlaybackAsync(Path.Combine(fixtures, "preview.mp3")), "Inline MP3 controls play, pause, seek, stop, and release their decoder");
                Check(await MediaControls.VerifyPlaybackAsync(Path.Combine(fixtures, "preview.mp4")), "Video row controls launch player, pause, seek, stop, and release it");
                using var mediaStore = new HistoryStore(Path.Combine(root, "media-rows"), new());
                await mediaStore.AddAsync(new() { Kind = ClipKind.Files, Paths = [Path.Combine(fixtures, "preview.mp3")] });
                Check((await mediaStore.QueryAsync(new())).Single().Summary.MediaSource == Path.Combine(fixtures, "preview.mp3"), "Media row exposes a source reference without importing file content");
                using (var legacy = new SqliteConnection($"Data Source={Path.Combine(mediaStore.Root, "history.db")};Pooling=False"))
                {
                    legacy.Open(); using var update = legacy.CreateCommand(); update.CommandText = "UPDATE clips SET summary=$summary";
                    update.Parameters.AddWithValue("$summary", System.Security.Cryptography.ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(new ClipSummary("preview.mp3", "Audio", "Explorer")), null, System.Security.Cryptography.DataProtectionScope.CurrentUser)); update.ExecuteNonQuery();
                }
                var upgraded = (await mediaStore.QueryAsync(new())).Single();
                Check(upgraded.Summary.MediaChecked && upgraded.Summary.MediaSource == Path.Combine(fixtures, "preview.mp3"), "Existing media history gains inline playback without recopying the source");
            }
            int mediaUrlIndex = Array.IndexOf(args, "--media-url");
            if (mediaUrlIndex >= 0 && mediaUrlIndex + 1 < args.Length)
                Check(await PreviewWindow.VerifyMediaAsync(args[mediaUrlIndex + 1]), "HTTP media preview streams from its original URL and releases playback on close");
            if (args.Contains("--update-test")) Check((await UpdateChecker.CheckAsync()).Contains("up to date"), "Manual update check reads the published GitHub release");
            if (args.Contains("--benchmark")) await BenchmarkAsync(root, report);
            report.Add(new { summary = "passed", passed, elapsedMs = timer.ElapsedMilliseconds, temporaryData = root });
            await WriteReport(args, report); return 0;
        }
        catch (Exception e)
        {
            report.Add(new { summary = "failed", passed, error = e.ToString(), temporaryData = root }); await WriteReport(args, report); return 1;
        }
    }
    private static async Task ClipboardIntegrationAsync(Action<bool, string> check)
    {
        // This opt-in integration suite changes the real Windows clipboard, then restores its previous data object.
        System.Windows.IDataObject? previous = null;
        for (int attempt = 0; ; attempt++)
        {
            try { previous = System.Windows.Clipboard.GetDataObject(); break; }
            catch (System.Runtime.InteropServices.ExternalException) when (attempt < 30) { await Task.Delay(100); }
        }
        var received = new System.Collections.Concurrent.ConcurrentQueue<ClipPayload>();
        var issues = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var service = new ClipboardService(new(), p => { received.Enqueue(p); return Task.CompletedTask; }, issues.Enqueue);
        await service.Ready;
        try
        {
            var data = new System.Windows.DataObject(); data.SetText("Clipboard Plus integration fixture");
            System.Windows.Clipboard.SetDataObject(data, true);
            for (int i = 0; i < 40 && received.IsEmpty; i++) await Task.Delay(25);
            check(received.TryDequeue(out var clip) && clip.Text == "Clipboard Plus integration fixture", "Real WM_CLIPBOARDUPDATE capture on dedicated STA thread");
            await service.PutAsync(new() { Text = "Clipboard Plus replay fixture" });
            await Task.Delay(150);
            bool replayMatches = System.Windows.Clipboard.GetText() == "Clipboard Plus replay fixture";
            if (!replayMatches || !received.IsEmpty) throw new InvalidOperationException($"Replay mismatch: matches={replayMatches}, captures={received.Count}, owner={Native.ProcessName(Native.GetClipboardOwner())}, sequence={Native.GetClipboardSequenceNumber()}");
            check(true, "Real clipboard replay succeeds without capture feedback loop");
            for (int i = 0; i < 5; i++)
            {
                uint sequence = await service.PutAsync(new() { Text = $"Fresh replay {i}" });
                check(System.Windows.Clipboard.GetText() == $"Fresh replay {i}" && Native.GetClipboardSequenceNumber() == sequence, $"Clipboard write {i + 1} completes with fresh content before paste");
            }
            var privateData = new System.Windows.DataObject(); privateData.SetText("Do not retain this synthetic fixture"); privateData.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream([1]));
            System.Windows.Clipboard.SetDataObject(privateData, true); await Task.Delay(150);
            check(received.IsEmpty, "Real capture respects history exclusion format");
            var noHistory = new System.Windows.DataObject(); noHistory.SetText("No history synthetic fixture"); noHistory.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
            System.Windows.Clipboard.SetDataObject(noHistory, true); await Task.Delay(150);
            check(received.IsEmpty, "Real capture respects CanIncludeInClipboardHistory false");
            service.Paused = true; System.Windows.Clipboard.SetText("Paused synthetic fixture"); await Task.Delay(150);
            check(received.IsEmpty, "Paused capture does not retain new clipboard data");
            service.Paused = false; service.Configure(new() { ExcludedApps = "ClipboardPlus" }); System.Windows.Clipboard.SetText("Excluded process synthetic fixture"); await Task.Delay(150);
            check(received.IsEmpty, "Excluded source process is not captured");
            check(issues.IsEmpty, "Live clipboard integration completes without service errors");
        }
        finally
        {
            service.Paused = true;
            for (int attempt = 0; ; attempt++)
            {
                try { if (previous is not null) System.Windows.Clipboard.SetDataObject(previous, true); else System.Windows.Clipboard.Clear(); break; }
                catch (System.Runtime.InteropServices.ExternalException) when (attempt < 30) { await Task.Delay(100); }
            }
        }
    }
    private static async Task BenchmarkAsync(string root, List<object> report)
    {
        using var store = new HistoryStore(Path.Combine(root, "benchmark"), new AppSettings { MaxItems = 20000, DiskBudgetMb = 512 });
        var capture = Stopwatch.StartNew();
        for (int i = 0; i < 10000; i++) await store.AddAsync(new() { Text = $"Project planning task {i:D6} reference needle{i % 100:D3} â€” useful clipboard text for search and paste.", Source = "Benchmark" });
        capture.Stop();
        var latencies = new List<double>();
        for (int i = 0; i < 100; i++) { var t = Stopwatch.StartNew(); await store.QueryAsync(new($"needle{i:D3}")); latencies.Add(t.Elapsed.TotalMilliseconds); }
        latencies.Sort();
        var stats = await store.StatsAsync();
        report.Add(new { benchmark = "10000 text clips", captureTotalMs = capture.ElapsedMilliseconds, searchP50Ms = latencies[50], searchP95Ms = latencies[95], stats.Count, stats.DiskBytes, cacheBytes = store.CacheBytes, processWorkingSet = Process.GetCurrentProcess().WorkingSet64 });
    }
    private static Task WriteReport(string[] args, List<object> report)
    {
        int index = Array.IndexOf(args, "--report");
        string path = index >= 0 && index + 1 < args.Length ? args[index + 1] : Path.Combine(Environment.CurrentDirectory, "self-test-results.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
