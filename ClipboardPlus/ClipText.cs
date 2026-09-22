using System.Net;
using System.Text.RegularExpressions;
using System.Windows.Documents;
using System.Windows.Threading;
using HtmlAgilityPack;

namespace ClipboardPlus;

// Parses clipboard markup locally. Never renders HTML, executes scripts, or fetches resources.
internal static class ClipText
{
    private static readonly Lazy<Dispatcher> rtfDispatcher = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() => { ready.SetResult(Dispatcher.CurrentDispatcher); Dispatcher.Run(); }) { IsBackground = true, Name = "Clipboard Plus text conversion" };
        worker.SetApartmentState(ApartmentState.STA); worker.Start(); return ready.Task.GetAwaiter().GetResult();
    });
    internal static string Display(string text) => Regex.Replace(text.Replace("\0", "").Replace("\uFEFF", "").Replace("\u200B", ""), @"\s+", " ").Trim();
    internal static ClipPayload Normalize(ClipPayload payload)
    {
        if (payload.Kind is not (ClipKind.Text or ClipKind.Link) || Display(payload.Text).Length > 0) return payload;
        string plain = FromHtml(payload.Html);
        if (Display(plain).Length == 0) plain = FromRtf(payload.Rtf);
        return Display(plain).Length > 0 ? payload with { Text = plain } : payload;
    }
    internal static string FromHtml(string? html)
    {
        if (string.IsNullOrEmpty(html) || html.Length > 8 * 1048576) return "";
        try
        {
            int start = html.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase), end = html.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
            if (start >= 0 && end > start) html = html[(start + 20)..end];
            else { int firstTag = html.IndexOf('<'); if (firstTag > 0) html = html[firstTag..]; }
            var document = new HtmlDocument { OptionMaxNestedChildNodes = 256 };
            document.LoadHtml(html);
            var output = new StringBuilder();
            void Walk(HtmlNode node)
            {
                if (node.NodeType == HtmlNodeType.Comment || node.Name is "script" or "style" or "head" or "template" or "noscript") return;
                if (node.Attributes["hidden"] is not null) return;
                if (node.NodeType == HtmlNodeType.Text) { output.Append(WebUtility.HtmlDecode(node.InnerText)); return; }
                bool block = node.Name is "p" or "div" or "li" or "tr" or "section" or "article" or "h1" or "h2" or "h3" or "pre";
                if ((block || node.Name == "br") && output.Length > 0 && output[^1] != '\n') output.AppendLine();
                foreach (var child in node.ChildNodes) Walk(child);
                if (node.Name is "td" or "th") output.Append('\t');
                if (block && output.Length > 0 && output[^1] != '\n') output.AppendLine();
            }
            Walk(document.DocumentNode);
            return output.ToString().Trim();
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return ""; }
    }
    internal static string FromRtf(string? rtf)
    {
        if (string.IsNullOrEmpty(rtf) || rtf.Length > 8 * 1048576) return "";
        try
        {
            return rtfDispatcher.Value.Invoke(() =>
            {
                var document = new FlowDocument();
                var range = new TextRange(document.ContentStart, document.ContentEnd);
                using var input = new MemoryStream(Encoding.UTF8.GetBytes(rtf));
                range.Load(input, System.Windows.DataFormats.Rtf);
                return range.Text.TrimEnd('\r', '\n');
            });
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return ""; }
    }
    internal static string? Read(object? value, bool unicode = false)
    {
        if (value is string text) return text.TrimEnd('\0');
        byte[]? bytes = value switch { MemoryStream stream => stream.ToArray(), byte[] array => array, _ => null };
        if (bytes is null) return null;
        return (unicode ? Encoding.Unicode : Encoding.UTF8).GetString(bytes).TrimEnd('\0', '\uFEFF');
    }
}
