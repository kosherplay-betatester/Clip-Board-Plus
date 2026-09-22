using System.Security.Cryptography;
using System.Windows;
using Microsoft.Data.Sqlite;

namespace ClipboardPlus;

internal static class ReliabilityTests
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        var htmlOnly = new System.Windows.DataObject();
        const string html = "Version:1.0\r\nStartHTML:00000000\r\n<html><body><!--StartFragment--><p>Hello &amp; שלום</p><p>Second line <b>bold</b></p><script>unwanted</script><!--EndFragment--></body></html>";
        htmlOnly.SetData(DataFormats.Html, new MemoryStream(Encoding.UTF8.GetBytes(html)), false);
        var captured = ClipboardService.ReadPayload(htmlOnly, "HTML editor", 1048576)!;
        check(captured.Text.Contains("Hello & שלום") && captured.Text.Contains("Second line bold") && !captured.Text.Contains("unwanted") && captured.Html == html, "HTML-only stream recovers readable Unicode text while preserving original rich data");
        var rtfOnly = new System.Windows.DataObject();
        rtfOnly.SetData(DataFormats.Rtf, @"{\rtf1\ansi Rich \b words\b0\par Next paragraph}", false);
        var rtf = ClipboardService.ReadPayload(rtfOnly, "RTF editor", 1048576)!;
        check(rtf.Text.Contains("Rich words") && rtf.Text.Contains("Next paragraph") && rtf.Title != "Empty text", "RTF-only capture has readable list and plain-paste text");
        check(ClipText.Normalize(new() { Text = "\r\n\u200B", Html = "<div>Other representation</div>" }).Text == "\r\n\u200B", "An explicit invisible or whitespace-only plain string is preserved exactly");
        check(new ClipPayload { Text = "\t\n " }.Title == "Whitespace text" && ClipText.Display("\uFEFF\n你好\tשלום\0") == "你好 שלום", "Whitespace-only clips have honest labels and Unicode previews remain readable");
        check(ClipText.Normalize(new() { Text = "  keep\r\nspacing  ", Html = "<b>different</b>" }).Text == "  keep\r\nspacing  ", "Preview normalization never changes nonempty original plain text");
        check(ClipText.FromHtml("<div hidden>hidden</div><style>hidden</style><p>Visible<br>line</p>").Contains("Visible") && !ClipText.FromHtml("<script>secret</script>").Contains("secret"), "Preview extraction excludes scripts, styles, and explicitly hidden content");
        check(ClipText.Normalize(new() { Html = string.Concat(Enumerable.Repeat("<div>", 300)) }).Title == "Formatted text", "Malformed or excessive markup retains a useful fallback instead of failing capture");

        string migrationRoot = Path.Combine(root, "reliability-migration");
        long original;
        using (var seed = new HistoryStore(migrationRoot, new())) original = await seed.AddAsync(new() { Text = "Migration fixture", Source = "Test" }, true);
        static byte[] Encrypt<T>(T value) => ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(value), null, DataProtectionScope.CurrentUser);
        using (var db = new SqliteConnection($"Data Source={Path.Combine(migrationRoot, "history.db")};Pooling=False"))
        {
            db.Open(); using var cmd = db.CreateCommand();
            // Recreate exactly the old identity schema with an encrypted blank summary.
            cmd.CommandText = """
                PRAGMA foreign_keys=OFF;
                BEGIN;
                CREATE TABLE old_clips(id INTEGER PRIMARY KEY, fingerprint BLOB NOT NULL UNIQUE, kind INTEGER NOT NULL, summary BLOB NOT NULL, payload BLOB NOT NULL, bytes INTEGER NOT NULL, created INTEGER NOT NULL, updated INTEGER NOT NULL, pinned INTEGER NOT NULL DEFAULT 0);
                INSERT INTO old_clips SELECT * FROM clips;
                DROP TABLE clips;
                ALTER TABLE old_clips RENAME TO clips;
                UPDATE clips SET summary=$summary,payload=$payload;
                PRAGMA user_version=1;
                COMMIT;
                """;
            cmd.Parameters.AddWithValue("$summary", Encrypt(new ClipSummary("Empty text", "", "Test")));
            cmd.Parameters.AddWithValue("$payload", Encrypt(new ClipPayload { Html = "<p>Recovered legacy content שלום</p>", Source = "Test" }));
            cmd.ExecuteNonQuery();
        }
        using (var migrated = new HistoryStore(migrationRoot, new()))
        {
            var row = (await migrated.QueryAsync(new())).Single();
            check(row.Id == original && row.Pinned && row.Title == "Recovered legacy content שלום", "Upgrade preserves IDs and pins and repairs encrypted legacy blank summaries");
            check((await migrated.QueryAsync(new("recovered legacy"))).Single().Id == original && (await migrated.GetAsync(original))!.Html == "<p>Recovered legacy content שלום</p>", "Legacy recovery updates search and retains original rich payload");
            for (int i = 0; i < 20; i++)
            {
                await migrated.ClearAsync(true);
                long next = await migrated.AddAsync(new() { Text = $"Replacement {i}" });
                if (next <= original || await migrated.GetAsync(original) is not null) throw new InvalidOperationException("A deleted ID was reused");
                original = next;
            }
            check(true, "Twenty clear-and-add cycles never alias a removed row or queued clip ID");
            await migrated.DeleteAsync([original]);
            check(await migrated.AddAsync(new() { Text = "After highest deletion" }) > original, "Deleting the highest ID never reuses that identity");
        }
        using (var db = new SqliteConnection($"Data Source={Path.Combine(migrationRoot, "history.db")};Pooling=False"))
        {
            db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA foreign_key_check";
            check(cmd.ExecuteScalar() is null, "Schema migration and subsequent deletion retain valid search-index foreign keys");
        }
        using var textStore = new HistoryStore(Path.Combine(root, "reliability-text"), new());
        long richId = await textStore.AddAsync(rtf);
        check((await textStore.QueryAsync(new("paragraph"))).Single().Id == richId && (await textStore.GetAsync(richId))!.Text.Contains("Rich words"), "Recovered text survives storage, lookup, and search");
    }
}
