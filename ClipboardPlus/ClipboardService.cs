using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClipboardPlus;

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
        if (Interlocked.Increment(ref pending) > 2)
        {
            Interlocked.Decrement(ref pending);
            if (queuedSequence == 0) status("Rapid copies queued. Intermediate changes may be skipped to keep memory bounded.");
            queuedSequence = sequence; return;
        }
        try
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (disposed || Paused || sequence != Native.GetClipboardSequenceNumber() || sequence == ignoredSequence || sequence == lastCaptured) return;
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
                    lastCaptured = sequence;
                    if (payload is not null) await save(payload);
                    return;
                }
                catch (ExternalException) when (attempt < 4) { await Task.Delay(20 * (attempt + 1)); }
            }
        }
        catch (Exception e) { status("Clip not saved: " + e.Message); }
        finally
        {
            Interlocked.Decrement(ref pending);
            if (queuedSequence != 0 && !disposed)
            {
                uint next = queuedSequence; queuedSequence = 0;
                _ = CaptureAsync(next);
            }
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
                return new() { Kind = ClipKind.Image, Image = bytes, Source = owner, Name = $"Image · {frame.PixelWidth} × {frame.PixelHeight}" };
            }
        }
        if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
        {
            if ((long)bitmap.PixelWidth * bitmap.PixelHeight * 4 > limit * 4) throw new InvalidOperationException("Image dimensions exceed the capture memory limit.");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream(); encoder.Save(stream);
            if (stream.Length > limit * 0.7) throw new InvalidOperationException("Image exceeds the item size limit.");
            return new() { Kind = ClipKind.Image, Image = stream.ToArray(), Source = owner, Name = $"Image · {bitmap.PixelWidth} × {bitmap.PixelHeight}" };
        }
        string? text = data.GetData(DataFormats.UnicodeText) as string ?? data.GetData(DataFormats.Text) as string;
        string? html = data.GetData(DataFormats.Html) as string, rtf = data.GetData(DataFormats.Rtf) as string;
        if (text is null && html is null && rtf is null) return null;
        text ??= "";
        if ((long)(text.Length + (html?.Length ?? 0) + (rtf?.Length ?? 0)) * 2 > limit) throw new InvalidOperationException("Text exceeds the item size limit.");
        var isLink = Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";
        return new() { Kind = isLink ? ClipKind.Link : ClipKind.Text, Text = text, Html = html, Rtf = rtf, Source = owner };
    }
    internal static System.Windows.DataObject CreateData(ClipPayload payload, bool plain)
    {
        var data = new System.Windows.DataObject();
        data.SetData("ClipboardPlus.Internal", new MemoryStream([1]), false);
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
    public async Task PutAsync(ClipPayload payload, bool plain = false)
    {
        await Ready;
        await dispatcher.InvokeAsync(async () =>
        {
            var data = CreateData(payload, plain);
            for (int attempt = 0; ; attempt++)
            {
                try { System.Windows.Clipboard.SetDataObject(data, true); ignoredSequence = Native.GetClipboardSequenceNumber(); return; }
                catch (ExternalException) when (attempt < 5) { await Task.Delay(25 * (attempt + 1)); }
            }
        }).Task.Unwrap();
    }
    public void Dispose()
    {
        disposed = true;
        if (ready.Task.IsCompletedSuccessfully) dispatcher.BeginInvoke(() => { Native.RemoveClipboardFormatListener(source.Handle); source.Dispose(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); });
    }
}
