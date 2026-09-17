using System.Net.Http;
using System.Reflection;

namespace ClipboardPlus;

internal static class UpdateChecker
{
    internal const string ReleasesUrl = "https://github.com/kosherplay-betatester/Clip-Board-Plus/releases";
    internal static Version CurrentVersion { get { var v = Assembly.GetExecutingAssembly().GetName().Version!; return new(v.Major, v.Minor, Math.Max(0, v.Build)); } }
    internal static string Describe(string json, Version current)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("tag_name", out var name)) throw new InvalidOperationException("GitHub did not return a release version.");
        string tag = name.GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var remote)) return $"Latest release: {tag}. Open the releases page for details.";
        remote = new(remote.Major, remote.Minor, Math.Max(0, remote.Build));
        current = new(current.Major, current.Minor, Math.Max(0, current.Build));
        return remote > current ? $"Version {remote} is available. You have {current}. Open releases to download the installer." : $"You’re up to date — Clipboard Plus {current}.";
    }
    internal static async Task<string> CheckAsync(CancellationToken cancellation = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"ClipboardPlus/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.GetAsync("https://api.github.com/repos/kosherplay-betatester/Clip-Board-Plus/releases/latest", cancellation);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return "There is no public release yet. Check the releases page later.";
        response.EnsureSuccessStatusCode();
        return Describe(await response.Content.ReadAsStringAsync(cancellation), CurrentVersion);
    }
}
