using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ClipboardPlus;

public sealed class HistoryStore : IDisposable
{
    private readonly SqliteConnection db;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly byte[] key;
    private readonly PayloadCache cache;
    private AppSettings settings;
    private int writesSinceCheckpoint;
    private long repairCursor;
    private bool disposed;
    // Persist original data once; computed labels/search text belong to summaries/indexes.
    private static readonly JsonSerializerOptions payloadJson = new() { IgnoreReadOnlyProperties = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public string Root { get; }
    public long CacheBytes => cache.Bytes;
    public HistoryStore(string root, AppSettings settings)
    {
        Root = root; this.settings = settings;
        Directory.CreateDirectory(root);
        var keyPath = Path.Combine(root, "search.key");
        if (!File.Exists(keyPath)) File.WriteAllBytes(keyPath, Protect(RandomNumberGenerator.GetBytes(32)));
        key = Unprotect(File.ReadAllBytes(keyPath));
        cache = new(settings.CacheBudgetMb * 1048576L);
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "history.db"), Pooling = false }.ToString());
        db.Open();
        Execute("PRAGMA auto_vacuum=INCREMENTAL; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=3000; PRAGMA cache_size=-2048;");
        Execute("""
            CREATE TABLE IF NOT EXISTS clips (
                id INTEGER PRIMARY KEY AUTOINCREMENT, fingerprint BLOB NOT NULL UNIQUE, kind INTEGER NOT NULL,
                summary BLOB NOT NULL, payload BLOB NOT NULL, bytes INTEGER NOT NULL,
                created INTEGER NOT NULL, updated INTEGER NOT NULL, pinned INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS clips_recent ON clips(pinned DESC, updated DESC, id DESC);
            CREATE INDEX IF NOT EXISTS clips_kind ON clips(kind, updated DESC);
            CREATE TABLE IF NOT EXISTS tokens (token BLOB NOT NULL, clip_id INTEGER NOT NULL REFERENCES clips(id) ON DELETE CASCADE, PRIMARY KEY(token, clip_id)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS tokens_clip ON tokens(clip_id);
            """);
        // A stale row or queue entry must never resolve to a different clip after deletion.
        using var schema = Command("SELECT sql FROM sqlite_master WHERE name='clips'");
        if (!(schema.ExecuteScalar() as string)!.Contains("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase))
        {
            Execute("PRAGMA foreign_keys=OFF;");
            try
            {
                using var tx = db.BeginTransaction();
                using var migrate = Command("""
                    CREATE TABLE clips_v2 (
                        id INTEGER PRIMARY KEY AUTOINCREMENT, fingerprint BLOB NOT NULL UNIQUE, kind INTEGER NOT NULL,
                        summary BLOB NOT NULL, payload BLOB NOT NULL, bytes INTEGER NOT NULL,
                        created INTEGER NOT NULL, updated INTEGER NOT NULL, pinned INTEGER NOT NULL DEFAULT 0);
                    INSERT INTO clips_v2 SELECT * FROM clips;
                    DROP TABLE clips;
                    ALTER TABLE clips_v2 RENAME TO clips;
                    CREATE INDEX clips_recent ON clips(pinned DESC, updated DESC, id DESC);
                    CREATE INDEX clips_kind ON clips(kind, updated DESC);
                    PRAGMA user_version=2;
                    """);
                migrate.Transaction = tx; migrate.ExecuteNonQuery(); tx.Commit();
            }
            finally { Execute("PRAGMA foreign_keys=ON;"); }
        }
        Execute("PRAGMA user_version=2;");
    }
    private static byte[] Protect(byte[] bytes) => ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
    private static byte[] Unprotect(byte[] bytes) => ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
    private byte[] Hash(string value) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value))[..16];
    private SqliteCommand Command(string sql, params (string, object)[] args)
    {
        var c = db.CreateCommand(); c.CommandText = sql;
        foreach (var (name, value) in args) c.Parameters.AddWithValue(name, value);
        return c;
    }
    private void Execute(string sql) { using var c = Command(sql); c.ExecuteNonQuery(); }
    private async Task<T> Work<T>(Func<T> work)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(disposed, this); return await Task.Run(work).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    public Task<long> AddAsync(ClipPayload payload, bool pin = false) => Work(() => Add(payload, pin));
    private long Add(ClipPayload payload, bool pin)
    {
        payload = ClipText.Normalize(payload);
        ClipText.ValidateUnicode(payload.Text);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, payloadJson);
        if (bytes.Length > settings.MaxItemMb * 1048576L) throw new InvalidOperationException("Clip exceeds the individual item limit. Increase it in Settings if needed.");
        // Source application and a user-assigned label must not prevent content deduplication.
        // Keep the existing fingerprint representation for deduplication, but stream it instead of
        // allocating a second multi-megabyte serialized copy. Derived fields aren't persisted.
        using var hash = new HMACSHA256(key);
        using var hashStream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        JsonSerializer.Serialize(hashStream, payload with { Source = "", Name = null, CaptureNote = null });
        hashStream.FlushFinalBlock(); var fingerprint = hash.Hash!;
        bool rename = pin && payload.Name is not null;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var tx = db.BeginTransaction();
        using var cmd = Command("""
            INSERT INTO clips(fingerprint,kind,summary,payload,bytes,created,updated,pinned)
            VALUES($hash,$kind,$summary,$payload,$bytes,$now,$now,$pin)
            ON CONFLICT(fingerprint) DO UPDATE SET updated=$now, pinned=MAX(clips.pinned,$pin),
                summary=CASE WHEN $rename=1 THEN excluded.summary ELSE clips.summary END,
                payload=CASE WHEN $rename=1 THEN excluded.payload ELSE clips.payload END,
                bytes=CASE WHEN $rename=1 THEN excluded.bytes ELSE clips.bytes END
            RETURNING id;
            """, ("$hash", fingerprint), ("$kind", (int)payload.Kind),
            ("$summary", Protect(JsonSerializer.SerializeToUtf8Bytes(new ClipSummary(payload.Title, payload.Preview, payload.Source, MakeThumbnail(payload.Image), MediaSourceInfo.From(payload), true, 1, payload.CaptureNote)))),
            ("$payload", Protect(bytes)), ("$bytes", bytes.LongLength), ("$now", now), ("$pin", pin ? 1 : 0), ("$rename", rename ? 1 : 0));
        cmd.Transaction = tx;
        var id = (long)cmd.ExecuteScalar()!;
        if (rename)
        {
            using var removeTokens = Command("DELETE FROM tokens WHERE clip_id=$id", ("$id", id)); removeTokens.Transaction = tx; removeTokens.ExecuteNonQuery(); cache.Remove(id);
        }
        using var check = Command("SELECT COUNT(*) FROM tokens WHERE clip_id=$id", ("$id", id)); check.Transaction = tx;
        if ((long)check.ExecuteScalar()! == 0)
        {
            using var insert = Command("INSERT OR IGNORE INTO tokens(token,clip_id) VALUES($token,$id)", ("$token", Array.Empty<byte>()), ("$id", id));
            insert.Transaction = tx; insert.Prepare();
            foreach (var token in IndexTokens(IndexableText(payload)))
            {
                insert.Parameters["$token"].Value = Hash(token); insert.ExecuteNonQuery();
            }
        }
        tx.Commit();
        Prune();
        using var exists = Command("SELECT COUNT(*) FROM clips WHERE id=$id", ("$id", id));
        if ((long)exists.ExecuteScalar()! == 0) throw new InvalidOperationException("History budget is full. Remove some pins or increase the disk budget.");
        return id;
    }
    private static byte[]? MakeThumbnail(byte[]? image)
    {
        if (image is null) return null;
        using var stream = new MemoryStream(image);
        var frame = System.Windows.Media.Imaging.BitmapDecoder.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation, System.Windows.Media.Imaging.BitmapCacheOption.None).Frames[0];
        int width = frame.PixelWidth, height = frame.PixelHeight; stream.Position = 0;
        var bitmap = new System.Windows.Media.Imaging.BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        if (width >= height) bitmap.DecodePixelWidth = Math.Min(width, 128); else bitmap.DecodePixelHeight = Math.Min(height, 128);
        bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var result = new MemoryStream(); encoder.Save(result); return result.ToArray();
    }
    internal static IEnumerable<string> Words(string text) => Regex.Matches(text.Normalize().ToLowerInvariant(), @"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant)
        .Select(m => m.Value).Distinct(StringComparer.Ordinal);
    private static IEnumerable<string> IndexTokens(string text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var word in Words(text))
        {
            for (int i = 1; i <= Math.Min(word.Length, 24); i++) if (seen.Add(word[..i])) { yield return word[..i]; if (seen.Count >= 16384) yield break; }
            if (word.Length > 24 && seen.Add(word)) { yield return word; if (seen.Count >= 16384) yield break; }
        }
    }
    private static string IndexableText(ClipPayload payload) => string.Join(' ', payload.Name, payload.Source, string.Join(' ', payload.Paths), payload.Text[..Math.Min(payload.Text.Length, 262144)]);
    public Task<List<ClipRow>> QueryAsync(HistoryQuery query) => Work(() =>
    {
        var sql = new StringBuilder("SELECT id,kind,summary,bytes,created,updated,pinned FROM clips WHERE 1=1");
        var args = new List<(string, object)>();
        if (query.Filter == "Pinned") sql.Append(" AND pinned=1");
        else if (Enum.TryParse<ClipKind>(query.Filter, out var kind)) { sql.Append(" AND kind=$kind"); args.Add(("$kind", (int)kind)); }
        if (query.Since is { } since) { sql.Append(" AND updated >= $since"); args.Add(("$since", since.ToUnixTimeMilliseconds())); }
        int n = 0;
        foreach (var word in Words(query.Search).Take(16))
        {
            var name = "$t" + n++;
            sql.Append($" AND id IN (SELECT clip_id FROM tokens WHERE token={name})"); args.Add((name, Hash(word)));
        }
        sql.Append(" ORDER BY pinned DESC,updated DESC,id DESC LIMIT $limit OFFSET $offset");
        args.Add(("$limit", Math.Clamp(query.Limit, 1, 200))); args.Add(("$offset", Math.Max(0, query.Offset)));
        using var c = Command(sql.ToString(), args.ToArray()); using var r = c.ExecuteReader();
        var rows = new List<ClipRow>();
        while (r.Read()) rows.Add(new(r.GetInt64(0), (ClipKind)r.GetInt32(1), JsonSerializer.Deserialize<ClipSummary>(Unprotect((byte[])r[2]))!, r.GetInt64(3), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(5)), r.GetBoolean(6)));
        r.Close();
        // Upgrade visible legacy file/link rows on demand; never load image payloads for list rendering.
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Kind is ClipKind.Text or ClipKind.Link && row.Summary.TextVersion < 1)
            {
                var summary = RepairText(row.Id, row.Summary);
                rows[i] = row = row with { Summary = summary };
            }
            if (row.Kind is not (ClipKind.Files or ClipKind.Link) || row.Summary.MediaChecked) continue;
            using var get = Command("SELECT payload FROM clips WHERE id=$id", ("$id", row.Id));
            if (get.ExecuteScalar() is byte[] raw && JsonSerializer.Deserialize<ClipPayload>(Unprotect(raw)) is { } p)
            {
                var summary = row.Summary with { MediaSource = MediaSourceInfo.From(p), MediaChecked = true }; rows[i] = row with { Summary = summary };
                using var update = Command("UPDATE clips SET summary=$summary WHERE id=$id", ("$summary", Protect(JsonSerializer.SerializeToUtf8Bytes(summary))), ("$id", row.Id)); update.ExecuteNonQuery();
            }
        }
        return rows;
    });
    public Task<ClipPayload?> GetAsync(long id) => Work(() =>
    {
        bool lowMemory = MemoryInfo.UnderPressure;
        if (lowMemory) cache.Clear();
        var bytes = cache.Get(id);
        if (bytes is null)
        {
            using var c = Command("SELECT payload FROM clips WHERE id=$id", ("$id", id));
            if (c.ExecuteScalar() is not byte[] encrypted) return null;
            bytes = Unprotect(encrypted); if (!lowMemory) cache.Add(id, bytes);
        }
        return ClipText.Normalize(JsonSerializer.Deserialize<ClipPayload>(bytes)!);
    });
    // Small batches yield the database between repairs so paste/search stay responsive.
    public Task<bool> RepairTextBatchAsync() => Work(() =>
    {
        var batch = new List<(long Id, ClipSummary Summary)>();
        using (var c = Command("SELECT id,summary FROM clips WHERE id>$id AND kind IN (0,1) ORDER BY id LIMIT 16", ("$id", repairCursor)))
        using (var r = c.ExecuteReader())
            while (r.Read()) batch.Add((r.GetInt64(0), JsonSerializer.Deserialize<ClipSummary>(Unprotect((byte[])r[1]))!));
        foreach (var (id, summary) in batch)
        {
            if (summary.TextVersion < 1) RepairText(id, summary);
            repairCursor = id;
        }
        return batch.Count == 16;
    });
    private ClipSummary RepairText(long id, ClipSummary summary)
    {
        using var get = Command("SELECT payload FROM clips WHERE id=$id", ("$id", id));
        if (get.ExecuteScalar() is not byte[] raw) return summary;
        var payload = ClipText.Normalize(JsonSerializer.Deserialize<ClipPayload>(Unprotect(raw))!);
        summary = summary with { Title = payload.Title, Preview = payload.Preview, TextVersion = 1 };
        // Keep the original rich clipboard data and fingerprint intact. Plain text is recovered on read.
        using var tx = db.BeginTransaction();
        using var update = Command("UPDATE clips SET summary=$summary WHERE id=$id", ("$summary", Protect(JsonSerializer.SerializeToUtf8Bytes(summary))), ("$id", id));
        update.Transaction = tx; update.ExecuteNonQuery();
        using var insert = Command("INSERT OR IGNORE INTO tokens(token,clip_id) VALUES($token,$id)", ("$token", Array.Empty<byte>()), ("$id", id));
        insert.Transaction = tx; insert.Prepare();
        foreach (var token in IndexTokens(IndexableText(payload))) { insert.Parameters["$token"].Value = Hash(token); insert.ExecuteNonQuery(); }
        tx.Commit(); return summary;
    }
    public Task SetPinAsync(long id, bool pin) => Work(() => { using var c = Command("UPDATE clips SET pinned=$pin WHERE id=$id", ("$pin", pin ? 1 : 0), ("$id", id)); c.ExecuteNonQuery(); Prune(); return true; });
    public Task DeleteAsync(IEnumerable<long> ids) => Work(() =>
    {
        using var tx = db.BeginTransaction();
        foreach (var id in ids) { using var c = Command("DELETE FROM clips WHERE id=$id", ("$id", id)); c.Transaction = tx; c.ExecuteNonQuery(); cache.Remove(id); }
        tx.Commit(); Compact(); return true;
    });
    public Task ClearAsync(bool includePinned) => Work(() => { Execute(includePinned ? "DELETE FROM clips" : "DELETE FROM clips WHERE pinned=0"); cache.Clear(); Compact(); return true; });
    public Task ConfigureAsync(AppSettings value) => Work(() => { value.Validate(); settings = value; cache.SetBudget(value.CacheBudgetMb * 1048576L); Prune(); return true; });
    public Task MaintainAsync() => Work(() => { Prune(); return true; });
    private void Prune()
    {
        int removed = 0;
        using (var c = Command("DELETE FROM clips WHERE pinned=0 AND updated < $cut", ("$cut", DateTimeOffset.UtcNow.AddDays(-settings.RetentionDays).ToUnixTimeMilliseconds()))) removed += c.ExecuteNonQuery();
        using (var c = Command("DELETE FROM clips WHERE id IN (SELECT id FROM clips WHERE pinned=0 ORDER BY updated DESC,id DESC LIMIT -1 OFFSET MAX(0,$max-(SELECT COUNT(*) FROM clips WHERE pinned=1)))", ("$max", settings.MaxItems))) removed += c.ExecuteNonQuery();
        // Include the search index and database overhead in the storage budget, not just clip payloads.
        while (UsedPages() > settings.DiskBudgetMb * 1048576L)
        {
            using var c = Command("DELETE FROM clips WHERE id IN (SELECT id FROM clips WHERE pinned=0 ORDER BY updated,id LIMIT 1)");
            int count = c.ExecuteNonQuery();
            if (count == 0) break; // Pins are never silently destroyed.
            removed += count;
        }
        if (removed > 0) { cache.Clear(); Compact(); }
        else if (++writesSinceCheckpoint >= 128) { writesSinceCheckpoint = 0; Execute("PRAGMA wal_checkpoint(PASSIVE);"); }
    }
    private long UsedPages()
    {
        using var c = Command("SELECT (page_count-freelist_count)*page_size FROM pragma_page_count(),pragma_freelist_count(),pragma_page_size()");
        return (long)c.ExecuteScalar()!;
    }
    private void Compact() => Execute("PRAGMA incremental_vacuum; PRAGMA wal_checkpoint(TRUNCATE);");
    public Task<StoreStats> StatsAsync() => Work(() =>
    {
        using var c = Command("SELECT COUNT(*),COALESCE(SUM(pinned),0),COALESCE(SUM(bytes),0) FROM clips"); using var r = c.ExecuteReader(); r.Read();
        return new StoreStats(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), Directory.EnumerateFiles(Root, "history.db*").Sum(p => new FileInfo(p).Length));
    });
    public void Dispose()
    {
        gate.Wait();
        try { if (disposed) return; disposed = true; db.Dispose(); cache.Clear(); CryptographicOperations.ZeroMemory(key); }
        finally { gate.Release(); }
    }
}
