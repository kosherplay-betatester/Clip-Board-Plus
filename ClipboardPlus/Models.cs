namespace ClipboardPlus;

public enum ClipKind { Text, Link, Image, Files }
public sealed record ClipPayload
{
    public ClipKind Kind { get; init; }
    public string Text { get; init; } = "";
    public string? Html { get; init; }
    public string? Rtf { get; init; }
    public string[] Paths { get; init; } = [];
    public byte[]? Image { get; init; }
    public string Source { get; init; } = "Unknown app";
    public string? Name { get; init; }
    public string SearchText => string.Join(' ', Name, Text, Source, string.Join(' ', Paths));
    public string Title => Name ?? (Kind switch {
        ClipKind.Image => "Copied image",
        ClipKind.Files => Paths.Length == 1 ? Path.GetFileName(Paths[0].TrimEnd('\\')) : $"{Paths.Length} files and folders",
        _ => ClipText.Display(Text) is { Length: > 0 } s ? s.Truncate(180) : Html is not null || Rtf is not null ? "Formatted text" : Text.Length > 0 ? "Whitespace text" : "Empty text"
    });
    public string Preview => Kind switch {
        ClipKind.Image => "Image saved locally · Open to zoom or extract text",
        ClipKind.Files => string.Join("  ·  ", Paths.Select(p => Path.GetFileName(p.TrimEnd('\\')))).Truncate(300),
        _ => ClipText.Display(Text).Truncate(300)
    };
}
public sealed record ClipSummary(string Title, string Preview, string Source, byte[]? Thumbnail = null, string? MediaSource = null, bool MediaChecked = false, int TextVersion = 0);
public sealed record ClipRow(long Id, ClipKind Kind, ClipSummary Summary, long Bytes, DateTimeOffset Created, DateTimeOffset Updated, bool Pinned)
{
    public string Title => Summary.Title;
    public string Preview => Summary.Preview;
    public string Source => Summary.Source;
    public string KindLabel => Kind.ToString().ToUpperInvariant();
    public string Icon => Kind switch { ClipKind.Text => "≡", ClipKind.Link => "↗", ClipKind.Image => "▧", _ => "▱" };
    public string Meta => $"{Source}  ·  {Updated.LocalDateTime:MMM d, HH:mm}  ·  {Bytes.SizeLabel()}";
    public string PinLabel => Pinned ? "★" : "☆";
    public override string ToString() => Title;
    private System.Windows.Media.Imaging.BitmapSource? thumbnail;
    public System.Windows.Media.Imaging.BitmapSource? Thumbnail
    {
        get
        {
            if (thumbnail is not null || Summary.Thumbnail is null) return thumbnail;
            using var stream = new MemoryStream(Summary.Thumbnail);
            var bitmap = new System.Windows.Media.Imaging.BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            return thumbnail = bitmap;
        }
    }
}
public sealed record HistoryQuery(string Search = "", string Filter = "All", int Offset = 0, int Limit = 80, DateTimeOffset? Since = null);
public sealed record StoreStats(long Count, long Pinned, long PayloadBytes, long DiskBytes);
public static class TextExtensions
{
    public static string Truncate(this string s, int length) => s.Length <= length ? s : s[..length] + "…";
    public static string SizeLabel(this long size) => size >= 1024 * 1024 ? $"{size / 1048576d:0.#} MB" : size >= 1024 ? $"{size / 1024d:0.#} KB" : $"{size} B";
}
