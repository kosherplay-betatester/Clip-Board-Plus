using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipboardPlus;
internal static class DemoData
{
    public static async Task SeedAsync(HistoryStore store)
    {
        if ((await store.StatsAsync()).Count != 0) return;
        await store.AddAsync(new() { Kind = ClipKind.Text, Name = "A reply worth keeping", Text = "Thanks for sharing this. I’ll take a look and get back to you with a thoughtful answer.", Source = "Snippet" }, true);
        await store.AddAsync(new() { Kind = ClipKind.Text, Text = "Good tools get out of your way. Great tools give you your time back.", Source = "Notepad" });
        await store.AddAsync(new() { Kind = ClipKind.Link, Text = "https://learn.microsoft.com/dotnet/", Source = "Edge" });
        await store.AddAsync(new() { Kind = ClipKind.Text, Text = "const ideas = clipboard.filter(item => item.worthKeeping);\nconst next = ideas.at(0);", Source = "Code" });
        await store.AddAsync(new() { Kind = ClipKind.Files, Paths = [Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)], Source = "Explorer" });
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(24, 40, 48)), null, new Rect(0, 0, 960, 540));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(168, 234, 198)), null, new Point(750, 135), 190, 190);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(74, 122, 103)), null, new Point(690, 445), 280, 200);
            dc.DrawText(new FormattedText("MAKE ROOM\nFOR GOOD IDEAS.", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 48, Brushes.White, 1), new Point(70, 160));
        }
        var image = new RenderTargetBitmap(960, 540, 96, 96, PixelFormats.Pbgra32); image.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = new MemoryStream(); encoder.Save(stream);
        await store.AddAsync(new() { Kind = ClipKind.Image, Image = stream.ToArray(), Name = "A little inspiration · 960 × 540", Source = "SnippingTool" });
        await store.AddAsync(new() { Kind = ClipKind.Text, Text = "Launch checklist\n• Keep the interface simple\n• Make every interaction feel instant\n• Leave room for the next big idea", Source = "Notepad" });
    }
}
