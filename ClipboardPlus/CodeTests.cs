using System.Windows;

namespace ClipboardPlus;

internal static class CodeTests
{
    internal static readonly string[] Fixtures = [
        "\r\n\tpublic static string Quote()\r\n\t{\r\n\t\treturn \"\\t \\n \\\\ \\\" ${value} <>& // שלום 你好 🚀\";  \r\n\t}\r\n\r\n",
        "\n#!/usr/bin/env python3\ndef example():\n\ttext = r'\\path\\file [a-z]+ \\u1234'\n\treturn f\"{text!r} # café e\u0301\"    \n\n",
        "\uFEFF// mixed line endings\r\nconst x = `line1\n${'\\0'}\nline3`;\r// CR only\n// ZWJ 👩‍💻 ZWSP:\u200B LRM:\u200E NBSP:\u00A0\uFEFF",
        "\t  \r\n\n  \t\r",
        "[]{}()<>!=?:;&|^%$#@~`'\"\\/\t" + "\n" + "العربية שלום Ελληνικά 日本語"
    ];
    internal static async Task CoreAsync(string root, Action<bool, string> check)
    {
        using var store = new HistoryStore(Path.Combine(root, "code"), new() { CacheBudgetMb = 1 });
        for (int i = 0; i < Fixtures.Length; i++)
        {
            string original = Fixtures[i];
            var data = new System.Windows.DataObject(); data.SetText(original); data.SetData(DataFormats.Html, "<b>Different formatted representation</b>");
            var captured = ClipboardService.ReadPayload(data, "Code editor", 16 * 1048576)!;
            long id = await store.AddAsync(captured);
            var replay = ClipboardService.CreateData((await store.GetAsync(id))!, true);
            check(string.Equals(captured.Text, original, StringComparison.Ordinal) && string.Equals(replay.GetText(), original, StringComparison.Ordinal) && !replay.GetDataPresent(DataFormats.Html), $"Code fixture {i + 1}: capture, encrypted storage and exact-text replay preserve every UTF-16 code unit");
        }
        // Rich format overhead must not cause a complete, fitting code string to be lost.
        var largeRich = new System.Windows.DataObject(); largeRich.SetText(Fixtures[0]); largeRich.SetData(DataFormats.Html, new string('x', 10000));
        var plainFallback = ClipboardService.ReadPayload(largeRich, "Code editor", 2048)!;
        check(plainFallback.Text == Fixtures[0] && plainFallback.Html is null && plainFallback.CaptureNote is not null, "Oversized rich formatting keeps the entire exact plain text with a visible capture note");
        bool rejected = false;
        try { var oversize = new System.Windows.DataObject(); oversize.SetText(new string('z', 10001)); ClipboardService.ReadPayload(oversize, "Code editor", 20000); }
        catch (InvalidOperationException e) { rejected = e.Message.Contains("Nothing was truncated"); }
        check(rejected, "Oversized code is rejected explicitly instead of truncating the text");
        var unicodeStream = new MemoryStream(Encoding.Unicode.GetBytes("\uFEFFexact\uFEFF\0"));
        check(ClipText.Read(unicodeStream, true) == "\uFEFFexact\uFEFF", "UTF-16 stream decoding preserves leading and trailing BOM characters");
        bool invalidUnicode = false;
        try { await store.AddAsync(new() { Text = "Incomplete emoji: \uD83D" }); }
        catch (InvalidOperationException e) { invalidUnicode = e.Message.Contains("cannot be saved losslessly"); }
        check(invalidUnicode, "Malformed Unicode is rejected instead of silently replacing a broken character");
        var legacy = new ClipPayload { Text = Fixtures[0], Source = "Legacy code editor" };
        long legacyId = await store.AddAsync(legacy);
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(store.Root, "history.db")};Pooling=False"))
        {
            db.Open();
            var key = System.Security.Cryptography.ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(store.Root, "search.key")), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            var legacyFingerprint = System.Security.Cryptography.HMACSHA256.HashData(key, JsonSerializer.SerializeToUtf8Bytes(legacy with { Source = "", Name = null }));
            using var command = db.CreateCommand(); command.CommandText = "SELECT fingerprint FROM clips WHERE id=$id"; command.Parameters.AddWithValue("$id", legacyId);
            check(((byte[])command.ExecuteScalar()!).SequenceEqual(legacyFingerprint) && await store.AddAsync(legacy with { Source = "Other editor" }) == legacyId, "Streaming fingerprints remain compatible with 1.1.1 deduplication across source editors");
        }
    }
    internal static async Task LargeAsync(string root, ClipboardService service, Action<bool, string> check)
    {
        // 16 million characters / 32 MiB UTF-16, including tabs, CRLF and literal escapes.
        const string line = "\tif (value != null) { Console.WriteLine(\"\\t \\n \\\\ <>& ${value}\"); }  \r\n";
        var text = new StringBuilder(16 * 1048576 + 128);
        while (text.Length < 16 * 1048576) text.Append(line);
        text.Append("// FINAL SENTINEL — שלום 你好 🚀\n\t  \n");
        string original = text.ToString(); text.Clear();
        var data = new System.Windows.DataObject(); data.SetText(original);
        var payload = ClipboardService.ReadPayload(data, "Code fixture", 64 * 1048576)!;
        var settings = new AppSettings { MaxItemMb = 64, CacheBudgetMb = 1 };
        long id;
        var timer = Stopwatch.StartNew();
        using (var store = new HistoryStore(Path.Combine(root, "large-code"), settings))
        {
            id = await store.AddAsync(payload);
            var saved = (await store.GetAsync(id))!;
            check(saved.Text.Equals(original, StringComparison.Ordinal), "32 MiB code clip survives encrypted storage without changing order, tabs, spaces, escapes, Unicode or final sentinel");
            var stats = await store.StatsAsync();
            check(stats.PayloadBytes < original.Length * 1.5 && store.CacheBytes == 0, "Large code is stored once without duplicate search text and bypasses a smaller memory cache");
        }
        using (var reopened = new HistoryStore(Path.Combine(root, "large-code"), settings))
        {
            var restored = (await reopened.GetAsync(id))!;
            await service.PutAsync(restored, true);
            check(System.Windows.Clipboard.GetText().Equals(original, StringComparison.Ordinal), "32 MiB code clip reopens from disk and returns to the real Windows clipboard with exact character equality");
            check((await reopened.QueryAsync(new("ConsoleWriteLine"))).Count == 0 && (await reopened.QueryAsync(new("console value"))).Single().Id == id, "Large code remains searchable through its bounded keyword index");
        }
        check(timer.Elapsed < TimeSpan.FromSeconds(30), $"32 MiB encrypted save/reopen/clipboard round trip finishes in {timer.Elapsed.TotalSeconds:0.00}s on the test machine");
    }
}
