using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipboardPlus;

// Some apps (e.g. Telegram/Qt) publish opaque RGB32 pixels as PNG/DIB with every alpha byte at 0.
// Taken literally that is a fully invisible image; the color channels are still correct.
internal static class ImageFidelity
{
    internal static bool IsInvisibleButColored(byte[] bgra)
    {
        bool anyColor = false;
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            if (bgra[i + 3] != 0) return false;
            if (!anyColor && (bgra[i] | bgra[i + 1] | bgra[i + 2]) != 0) anyColor = true;
        }
        return anyColor;
    }
    internal static BitmapSource Normalize(BitmapSource source, out bool repaired)
    {
        repaired = false;
        var bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4; var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        if (!IsInvisibleButColored(pixels)) return source;
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        repaired = true;
        var fixedBitmap = BitmapSource.Create(bgra.PixelWidth, bgra.PixelHeight, source.DpiX, source.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        fixedBitmap.Freeze(); return fixedBitmap;
    }
    // Returns the original bytes untouched unless the image is invisible yet carries color data.
    internal static byte[] NormalizePng(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var result = Normalize(frame, out bool repaired);
        if (!repaired) return png;
        return Encode(result);
    }
    internal static byte[] Encode(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
    }
}
