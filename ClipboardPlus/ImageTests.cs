using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipboardPlus;

// Pixel-level regressions for the Telegram black-image defect: Qt publishes opaque RGB32 as PNG/DIB with alpha 0.
internal static class ImageTests
{
    internal static byte[] Bgra(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4]; converted.CopyPixels(pixels, frame.PixelWidth * 4, 0); return pixels;
    }
    internal static byte[] MakePng(int width, int height, Func<int, int, (byte B, byte G, byte R, byte A)> pixel)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) { var (b, g, r, a) = pixel(x, y); int i = (y * width + x) * 4; pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = a; }
        return ImageFidelity.Encode(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4));
    }
    internal static (byte B, byte G, byte R, byte A) Color(int x, int y) => ((byte)(x * 30 + 5), (byte)(y * 40 + 9), (byte)(x * 7 + y * 11 + 100), 0);
    private static bool SameColor(byte[] a, byte[] b) { for (int i = 0; i < a.Length; i += 4) if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2]) return false; return true; }
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        var telegram = MakePng(6, 5, Color);
        var original = Bgra(telegram);
        var fixedPng = ImageFidelity.NormalizePng(telegram); var fixedPixels = Bgra(fixedPng);
        check(original.Where((_, i) => i % 4 == 3).All(a => a == 0) && fixedPixels.Where((_, i) => i % 4 == 3).All(a => a == 255) && SameColor(original, fixedPixels), "Telegram-style PNG (colors present, alpha 0 everywhere) becomes opaque with identical RGB values");
        var transparent = MakePng(4, 4, (x, y) => x < 2 ? ((byte)200, (byte)100, (byte)50, (byte)0) : ((byte)10, (byte)20, (byte)30, (byte)255));
        check(ReferenceEquals(ImageFidelity.NormalizePng(transparent), transparent), "A genuinely transparent region is never made opaque");
        var halfAlpha = MakePng(3, 3, (x, y) => ((byte)(x * 60), (byte)(y * 60), 90, (byte)(x * 100)));
        check(ReferenceEquals(ImageFidelity.NormalizePng(halfAlpha), halfAlpha), "Graduated alpha is preserved exactly");
        var opaque = MakePng(3, 3, (x, y) => ((byte)(x * 60), (byte)(y * 60), 90, 255));
        check(ReferenceEquals(ImageFidelity.NormalizePng(opaque), opaque), "Opaque PNG bytes pass through unchanged");
        var empty = MakePng(3, 3, (_, _) => (0, 0, 0, 0));
        check(ReferenceEquals(ImageFidelity.NormalizePng(empty), empty), "A fully empty transparent image stays transparent");

        // CF_DIB: bottom-up 24 bpp rows are padded to 4 bytes. Row order and channel order must survive.
        var dib = new byte[40 + 8 * 2];
        BitConverter.GetBytes(40).CopyTo(dib, 0); BitConverter.GetBytes(2).CopyTo(dib, 4); BitConverter.GetBytes(2).CopyTo(dib, 8); BitConverter.GetBytes((short)1).CopyTo(dib, 12); BitConverter.GetBytes((short)24).CopyTo(dib, 14);
        byte[] bottomRow = [10, 20, 30, 40, 50, 60, 0, 0], topRow = [70, 80, 90, 100, 110, 120, 0, 0]; // BGR pixels
        bottomRow.CopyTo(dib, 40); topRow.CopyTo(dib, 48);
        var fromDib = ClipboardService.DibToBitmap(dib)!; var dibPixels = new byte[16]; new FormatConvertedBitmap(fromDib, PixelFormats.Bgra32, null, 0).CopyPixels(dibPixels, 8, 0);
        check(dibPixels[0] == 70 && dibPixels[1] == 80 && dibPixels[2] == 90 && dibPixels[12] == 40 && dibPixels[13] == 50 && dibPixels[14] == 60, "24-bit CF_DIB decodes with correct row direction and channel order");
        bool bombRejected = false; var bomb = new byte[44]; BitConverter.GetBytes(40).CopyTo(bomb, 0); BitConverter.GetBytes(60000).CopyTo(bomb, 4); BitConverter.GetBytes(60000).CopyTo(bomb, 8); BitConverter.GetBytes((short)1).CopyTo(bomb, 12); BitConverter.GetBytes((short)32).CopyTo(bomb, 14);
        try { ClipboardService.DibToBitmap(bomb, 1048576); } catch (InvalidOperationException) { bombRejected = true; }
        check(bombRejected, "A tiny DIB declaring enormous dimensions is rejected before any decoding");
        var dib32 = new byte[40 + 8];
        BitConverter.GetBytes(40).CopyTo(dib32, 0); BitConverter.GetBytes(2).CopyTo(dib32, 4); BitConverter.GetBytes(1).CopyTo(dib32, 8); BitConverter.GetBytes((short)1).CopyTo(dib32, 12); BitConverter.GetBytes((short)32).CopyTo(dib32, 14);
        new byte[] { 1, 2, 3, 0, 4, 5, 6, 0 }.CopyTo(dib32, 40);
        var data32 = new System.Windows.DataObject(); data32.SetData(DataFormats.Dib, new MemoryStream(dib32), false);
        var fromDib32 = ClipboardService.ReadPayload(data32, "Dib app", 1048576)!;
        var dib32Pixels = Bgra(fromDib32.Image!);
        check(fromDib32.Kind == ClipKind.Image && dib32Pixels[3] == 255 && dib32Pixels[7] == 255 && dib32Pixels[0] == 1 && dib32Pixels[6] == 6, "A DIB-only image with unused alpha bytes is captured opaque with its colors");

        var tg = new System.Windows.DataObject(); tg.SetData("PNG", new MemoryStream(telegram), false);
        var captured = ClipboardService.ReadPayload(tg, "Telegram", 4194304)!;
        var capturedPixels = Bgra(captured.Image!);
        check(capturedPixels[3] == 255 && SameColor(original, capturedPixels), "Capture of a Telegram-style PNG stores visible pixels equal to the source colors");
        var published = ClipboardService.CreateData(captured, false);
        var publishedPng = published.GetData("PNG") is MemoryStream ps ? ps.ToArray() : [];
        check(publishedPng.Length > 0 && SameColor(original, Bgra(publishedPng)) && Bgra(publishedPng)[3] == 255, "Published clipboard PNG matches the stored pixels");

        // Hebrew copied from Chrome: Unicode text missing, ANSI text degraded to '?', HTML intact.
        var chrome = new System.Windows.DataObject();
        chrome.SetData(DataFormats.Text, "?????? ??????", false);
        chrome.SetData(DataFormats.Html, new MemoryStream(Encoding.UTF8.GetBytes("Version:0.9 <html><body><!--StartFragment--><h1>גלריה מיילים</h1><!--EndFragment--></body></html>")), false);
        check(ClipboardService.ReadPayload(chrome, "chrome", 1048576)!.Text == "גלריה מיילים", "ANSI '?' fallback never replaces real Unicode text recoverable from HTML");
        // Existing history written before the fix is recoverable in place because the RGB channels were stored.
        string repairRoot = Path.Combine(root, "image-repair");
        using var store = new HistoryStore(repairRoot, new());
        long broken = await store.AddAsync(new() { Kind = ClipKind.Image, Image = telegram, Source = "Telegram" });
        long keep = await store.AddAsync(new() { Kind = ClipKind.Image, Image = transparent, Source = "Design tool" });
        while (await store.RepairImagesBatchAsync()) { }
        var repaired = await store.GetAsync(broken); var kept = await store.GetAsync(keep);
        check(Bgra(repaired!.Image!)[3] == 255 && SameColor(original, Bgra(repaired.Image!)) && kept!.Image!.SequenceEqual(transparent), "Stored invisible images are repaired in place; transparent images remain byte-identical");
        long garbled = await store.AddAsync(new() { Text = "?????? ??????", Html = "<html><body><!--StartFragment--><p>שלום עולם</p><!--EndFragment--></body></html>" });
        long literal = await store.AddAsync(new() { Text = "Is this right?? yes", Html = "<p>שלום</p>" });
        while (await store.RepairImagesBatchAsync()) { }
        while (await store.RepairGarbledTextBatchAsync()) { }
        check((await store.GetAsync(garbled))!.Text == "שלום עולם" && (await store.GetAsync(literal))!.Text == "Is this right?? yes", "Stored '?'-only text is recovered from its HTML; genuine question marks stay exactly as copied");
        var thumb = (await store.QueryAsync(new(Filter: "Image"))).Single(r => r.Id == broken).Summary.Thumbnail;
        check(thumb is not null && Bgra(thumb)[3] == 255, "Repaired image thumbnails are visible");
    }
}
