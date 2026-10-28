using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClipboardPlus;

internal readonly record struct ClipboardReceipt(uint Sequence, byte[] Token);

public sealed class ClipboardService : IDisposable
{
    private readonly Thread thread;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<ClipPayload, Task> save;
    private readonly Action<string> status;
    private Dispatcher dispatcher = null!;
    private HwndSource source = null!;
    private AppSettings settings;
    private uint ignoredSequence;
    private uint lastCaptured;
    private int pending;
    private uint queuedSequence;
    private bool paused;
    private bool disposed;
    private readonly SemaphoreSlim writes = new(1, 1);
    public bool Paused { get => Volatile.Read(ref paused); set => Volatile.Write(ref paused, value); }
    public ClipboardService(AppSettings settings, Func<ClipPayload, Task> save, Action<string> status)
    {
        this.settings = settings; this.save = save; this.status = status;
        thread = new Thread(Run) { IsBackground = true, Name = "Clipboard Plus capture (STA)" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
    }
    public void Configure(AppSettings value) => Volatile.Write(ref settings, value);
    private void Run()
    {
        try
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            source = new HwndSource(new HwndSourceParameters("Clipboard Plus listener") { ParentWindow = new IntPtr(-3) });
            source.AddHook(WndProc);
            if (!Native.AddClipboardFormatListener(source.Handle)) throw new InvalidOperationException("Could not listen for clipboard changes.");
            ready.SetResult(); Dispatcher.Run();
        }
        catch (Exception e) { ready.TrySetException(e); status("Capture unavailable: " + e.Message); }
    }
    public Task Ready => ready.Task;
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled)
    {
        if (msg == 0x31D && !Paused && !disposed)
        {
            var sequence = Native.GetClipboardSequenceNumber();
            if (sequence != ignoredSequence && sequence != lastCaptured) _ = CaptureAsync(sequence);
        }
        return IntPtr.Zero;
    }
    private async Task CaptureAsync(uint sequence)
    {
        if (Interlocked.Increment(ref pending) > 1)
        {
            // Only the newest clipboard content can still be read; older sequences were already replaced.
            Interlocked.Decrement(ref pending);
            Interlocked.Exchange(ref queuedSequence, sequence); return;
        }
        try
        {
            // ~2.5 s of bounded retries for a clipboard another app holds open or a save that hits a busy database.
            const int attempts = 12;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (disposed || Paused || sequence == ignoredSequence || sequence == lastCaptured) return;
                // A newer change has its own event; capturing it here would race with that event.
                if (sequence != Native.GetClipboardSequenceNumber()) return;
                try
                {
                    var config = Volatile.Read(ref settings);
                    var owner = Native.ProcessName(Native.GetClipboardOwner());
                    if (config.Excludes(owner)) return;
                    var data = System.Windows.Clipboard.GetDataObject();
                    if (data is null || data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") || data.GetDataPresent("ClipboardPlus.Internal")) return;
                    if (ReadFlag(data.GetData("CanIncludeInClipboardHistory")) == 0) return;
                    var payload = ReadPayload(data, owner, config.MaxItemMb * 1048576L);
                    if (sequence != Native.GetClipboardSequenceNumber()) return;
                    if (payload is null) { lastCaptured = sequence; return; }
                    await save(payload);
                    lastCaptured = sequence; // Only a completed save counts as captured.
                    if (payload.CaptureNote is not null) status(payload.CaptureNote);
                    return;
                }
                catch (Exception e) when (attempt < attempts - 1 && (e is ExternalException || e is Microsoft.Data.Sqlite.SqliteException || e is IOException))
                { await Task.Delay(Math.Min(400, 25 * (attempt + 1))); }
            }
        }
        catch (Exception e) { lastCaptured = sequence; status("Clip not saved: " + e.Message); }
        finally
        {
            Interlocked.Decrement(ref pending);
            uint next = Interlocked.Exchange(ref queuedSequence, 0);
            if (next != 0 && !disposed) _ = CaptureAsync(next);
        }
    }
    private static int? ReadFlag(object? data) => data switch { MemoryStream m when m.Length >= 4 => BitConverter.ToInt32(m.ToArray()), byte[] b when b.Length >= 4 => BitConverter.ToInt32(b), int i => i, _ => null };
    internal static ClipPayload? ReadPayload(System.Windows.IDataObject data, string owner, long limit)
    {
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] paths)
            return new() { Kind = ClipKind.Files, Paths = paths, Source = owner };
        if (data.GetDataPresent("PNG"))
        {
            var raw = data.GetData("PNG");
            long length = raw is MemoryStream ms ? ms.Length : raw is byte[] buffer ? buffer.LongLength : 0;
            if (length > limit * 0.7) throw new InvalidOperationException("Image exceeds the item size limit.");
            byte[]? bytes = raw is MemoryStream memory ? memory.ToArray() : raw as byte[];
            if (bytes is { Length: > 0 })
            {
                using var stream = new MemoryStream(bytes);
                var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                if ((long)frame.PixelWidth * frame.PixelHeight * 4 > limit * 4) throw new InvalidOperationException("Image dimensions exceed the capture memory limit.");
                return new() { Kind = ClipKind.Image, Image = ImageFidelity.NormalizePng(bytes), Source = owner, Name = $"Image · {frame.PixelWidth} × {frame.PixelHeight}" };
            }
        }
        if ((data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent(DataFormats.Dib)) && ReadBitmap(data) is { } bitmap)
        {
            if ((long)bitmap.PixelWidth * bitmap.PixelHeight * 4 > limit * 4) throw new InvalidOperationException("Image dimensions exceed the capture memory limit.");
            var png = ImageFidelity.Encode(ImageFidelity.Normalize(bitmap, out _));
            if (png.Length > limit * 0.7) throw new InvalidOperationException("Image exceeds the item size limit.");
            return new() { Kind = ClipKind.Image, Image = png, Source = owner, Name = $"Image · {bitmap.PixelWidth} × {bitmap.PixelHeight}" };
        }
        string? text = ClipText.Read(data.GetData(DataFormats.UnicodeText), true);
        if (string.IsNullOrEmpty(text)) text = ClipText.Read(data.GetData(DataFormats.Text)) ?? text;
        string? html = ClipText.Read(data.GetData(DataFormats.Html)), rtf = ClipText.Read(data.GetData(DataFormats.Rtf));
        if (text is null && html is null && rtf is null) return null;
        text ??= "";
        string? note = null;
        if ((long)text.Length * 2 > limit) throw new InvalidOperationException("Text exceeds Maximum single clip in Settings. Nothing was truncated; the original clipboard is unchanged. Raise the limit (up to 64 MB) to save larger code clips.");
        if (((long)text.Length + (html?.Length ?? 0) + (rtf?.Length ?? 0)) * 2 > limit)
        {
            if (!ClipText.HasVisibleText(text)) throw new InvalidOperationException("Rich content exceeds the clip size limit. Increase Maximum single clip in Settings.");
            html = null; rtf = null;
            note = "Full text saved. Extra rich formatting exceeded the clip size limit and was omitted.";
        }
        var isLink = Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";
        return ClipText.Normalize(new() { Kind = isLink ? ClipKind.Link : ClipKind.Text, Text = text, Html = html, Rtf = rtf, Source = owner, CaptureNote = note });
    }
    private static BitmapSource? ReadBitmap(System.Windows.IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource direct) return direct;
        return data.GetDataPresent(DataFormats.Dib) && data.GetData(DataFormats.Dib) is MemoryStream dib ? DibToBitmap(dib.ToArray()) : null;
    }
    // CF_DIB is a BITMAPINFOHEADER-prefixed device-independent bitmap; wrap it as a BMP file to decode it.
    internal static BitmapSource? DibToBitmap(byte[] dib)
    {
        if (dib.Length < 40) return null;
        int header = BitConverter.ToInt32(dib, 0), bits = BitConverter.ToInt16(dib, 14), compression = BitConverter.ToInt32(dib, 16), colors = BitConverter.ToInt32(dib, 32);
        int masks = header == 40 && compression == 3 ? 12 : 0;
        int palette = bits <= 8 ? (colors == 0 ? 1 << bits : colors) * 4 : 0;
        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B'; file[1] = (byte)'M'; BitConverter.GetBytes(file.Length).CopyTo(file, 2); BitConverter.GetBytes(14 + header + masks + palette).CopyTo(file, 10);
        dib.CopyTo(file, 14);
        using var stream = new MemoryStream(file);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]; frame.Freeze(); return frame;
    }
    internal static System.Windows.DataObject CreateData(ClipPayload payload, bool plain)
    {
        var data = new System.Windows.DataObject();
        data.SetData("ClipboardPlus.Internal", new MemoryStream(Guid.NewGuid().ToByteArray()), false);
        if (payload.Kind == ClipKind.Files && !plain)
        {
            var missing = payload.Paths.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToArray();
            if (missing.Length > 0) throw new FileNotFoundException($"{missing.Length} source(s) are unavailable. Open Preview to inspect the saved paths.");
            var paths = new StringCollection(); paths.AddRange(payload.Paths); data.SetFileDropList(paths);
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1))); // Replay history as COPY, never a stale CUT.
        }
        else if (payload.Kind == ClipKind.Image && payload.Image is { } image)
        {
            using var stream = new MemoryStream(image);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            data.SetImage(bitmap); data.SetData("PNG", new MemoryStream(image));
        }
        else
        {
            data.SetText(payload.Kind == ClipKind.Files ? string.Join(Environment.NewLine, payload.Paths) : payload.Text);
            if (!plain)
            {
                if (payload.Html is { } html) data.SetData(DataFormats.Html, html);
                if (payload.Rtf is { } rtf) data.SetData(DataFormats.Rtf, rtf);
            }
        }
        return data;
    }
    internal static System.Windows.DataObject CreateManyData(IReadOnlyList<ClipPayload> payloads, string exportRoot)
    {
        if (payloads.Count == 0) throw new ArgumentException("Select at least one item.");
        if (payloads.Count == 1) return CreateData(payloads[0], false);
        var data = new System.Windows.DataObject();
        data.SetData("ClipboardPlus.Internal", new MemoryStream(Guid.NewGuid().ToByteArray()), false);
        var paths = payloads.SelectMany(p => p.Kind == ClipKind.Files ? p.Paths : []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Any(p => !File.Exists(p) && !Directory.Exists(p))) throw new FileNotFoundException("A selected source file or folder is unavailable. No items were copied.");
        var text = string.Join(Environment.NewLine, payloads.Where(p => p.Kind is ClipKind.Text or ClipKind.Link).Select(p => p.Text));
        if (text.Length > 0) data.SetText(text);
        var images = payloads.Where(p => p.Kind == ClipKind.Image && p.Image is not null).ToArray();
        if (images.Length > 0)
        {
            Directory.CreateDirectory(exportRoot);
            // Only our own PNG exports expire. Never touch original source files.
            foreach (var old in Directory.EnumerateFiles(exportRoot, "clip-*.png"))
                if (System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(old), @"^clip-[A-F0-9]{64}\.png$") && File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-7))
                    try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            var exports = images.Select(image => (Image: image, Path: Path.Combine(exportRoot, "clip-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image.Image!)) + ".png"))).DistinctBy(x => x.Path).ToArray();
            long existingBytes = Directory.EnumerateFiles(exportRoot, "clip-*.png").Sum(p => new FileInfo(p).Length);
            long addedBytes = exports.Where(x => !File.Exists(x.Path)).Sum(x => x.Image.Image!.LongLength);
            if (existingBytes + addedBytes > 256 * 1048576L) throw new InvalidOperationException("Temporary image copies reached 256 MB. Copy images individually, or remove old files from the CopyExports folder when you no longer need them.");
            foreach (var export in exports)
            {
                File.WriteAllBytes(export.Path, export.Image.Image!); paths.Add(export.Path);
            }
            if (images.Length == 1)
            {
                var imageData = CreateData(images[0], false);
                data.SetImage(imageData.GetImage()); data.SetData("PNG", new MemoryStream(images[0].Image!));
            }
        }
        if (paths.Count > 0)
        {
            var files = new StringCollection(); files.AddRange(paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            data.SetFileDropList(files); data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)));
        }
        return data;
    }
    public Task<uint> PutAsync(ClipPayload payload, bool plain = false) => PutDataAsync(() => CreateData(payload, plain));
    public Task<uint> PutManyAsync(IReadOnlyList<ClipPayload> payloads) => PutDataAsync(() => CreateManyData(payloads,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipboardPlus", "CopyExports")));
    internal Task PublishForPasteAsync(ClipPayload payload, bool plain, Func<ClipboardReceipt, Task> deliver)
        => PutDataAsync(() => CreateData(payload, plain), deliver);
    internal static string ExportRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipboardPlus", "CopyExports");
    internal Task PublishManyForPasteAsync(IReadOnlyList<ClipPayload> payloads, Func<ClipboardReceipt, Task> deliver)
        => PutDataAsync(() => CreateManyData(payloads, ExportRoot), deliver);
    internal async Task<bool> IsCurrentAsync(ClipboardReceipt receipt)
    {
        await Ready;
        return await dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (receipt.Sequence != Native.GetClipboardSequenceNumber()) return false;
                var value = System.Windows.Clipboard.GetData("ClipboardPlus.Internal");
                var token = value switch { MemoryStream m => m.ToArray(), byte[] b => b, _ => [] };
                return token.AsSpan().SequenceEqual(receipt.Token) && receipt.Sequence == Native.GetClipboardSequenceNumber();
            }
            catch (ExternalException) { return false; }
        });
    }
    private async Task<uint> PutDataAsync(Func<System.Windows.DataObject> create, Func<ClipboardReceipt, Task>? deliver = null)
    {
        await Ready;
        await writes.WaitAsync();
        try
        {
        var receipt = await dispatcher.InvokeAsync(async () =>
        {
            var data = create();
            var token = ((MemoryStream)data.GetData("ClipboardPlus.Internal")!).ToArray();
            for (int attempt = 0; ; attempt++)
            {
                try { System.Windows.Clipboard.SetDataObject(data, true); ignoredSequence = Native.GetClipboardSequenceNumber(); return new ClipboardReceipt(ignoredSequence, token); }
                catch (ExternalException) when (attempt < 5) { await Task.Delay(25 * (attempt + 1)); }
            }
        }).Task.Unwrap();
        // Keep every application writer serialized until the paste input has been delivered.
        if (deliver is not null) await deliver(receipt);
        return receipt.Sequence;
        }
        finally { writes.Release(); }
    }
    public void Dispose()
    {
        disposed = true;
        if (ready.Task.IsCompletedSuccessfully) dispatcher.BeginInvoke(() => { Native.RemoveClipboardFormatListener(source.Handle); source.Dispose(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); });
    }
}
