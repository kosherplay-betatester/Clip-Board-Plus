using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ClipboardPlus;

internal sealed record UpdateInfo(Version Version, string Tag, string Notes, string? InstallerUrl, string? ChecksumUrl);

internal static class UpdateChecker
{
    internal const string ReleasesUrl = "https://github.com/kosherplay-betatester/Clip-Board-Plus/releases";
    private const string LatestApi = "https://api.github.com/repos/kosherplay-betatester/Clip-Board-Plus/releases/latest";
    internal static Version CurrentVersion { get { var v = Assembly.GetExecutingAssembly().GetName().Version!; return new(v.Major, v.Minor, Math.Max(0, v.Build)); } }
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
    internal static string Describe(string json, Version current)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("tag_name", out var name)) throw new InvalidOperationException("GitHub did not return a release version.");
        string tag = name.GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var remote)) return $"Latest release: {tag}. Open the releases page for details.";
        remote = Normalize(remote); current = Normalize(current);
        return remote > current ? $"Version {remote} is available. You have {current}. Open releases to download the installer." : $"You’re up to date — Clipboard Plus {current}.";
    }
    internal static UpdateInfo ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string tag = root.TryGetProperty("tag_name", out var name) ? name.GetString() ?? "" : "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) throw new InvalidOperationException("GitHub did not return a usable release version.");
        string? installer = null, sums = null;
        if (root.TryGetProperty("assets", out var assets))
            foreach (var asset in assets.EnumerateArray())
            {
                string file = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                if (Regex.IsMatch(file, @"^ClipboardPlus-Setup-[0-9.]+-win-x64\.exe$")) installer = url;
                else if (file.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) sums = url;
            }
        string notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        return new(Normalize(version), tag, notes, installer, sums);
    }
    // Offer an update only for a newer version the user has not asked to be left alone about, and only when the
    // installer and its checksum are both published (the download is verified before it is run).
    internal static bool ShouldPrompt(UpdateInfo info, Version current, string skippedVersion)
        => info.Version > Normalize(current) && !string.Equals(skippedVersion, info.Version.ToString(), StringComparison.Ordinal) && info.InstallerUrl is not null && info.ChecksumUrl is not null;
    internal static bool TrustedUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && (uri.Host == "github.com" || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));
    private static HttpClient NewClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"ClipboardPlus/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
    internal static async Task<string> CheckAsync(CancellationToken cancellation = default)
    {
        using var client = NewClient(TimeSpan.FromSeconds(15));
        using var response = await client.GetAsync(LatestApi, cancellation);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return "There is no public release yet. Check the releases page later.";
        response.EnsureSuccessStatusCode();
        return Describe(await response.Content.ReadAsStringAsync(cancellation), CurrentVersion);
    }
    internal static async Task<UpdateInfo> GetLatestAsync(CancellationToken cancellation = default)
    {
        using var client = NewClient(TimeSpan.FromSeconds(15));
        using var response = await client.GetAsync(LatestApi, cancellation);
        response.EnsureSuccessStatusCode();
        return ParseRelease(await response.Content.ReadAsStringAsync(cancellation));
    }
    internal static bool VerifyChecksum(string path, string sums, string fileName)
    {
        foreach (var line in sums.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*') == fileName)
            {
                using var stream = File.OpenRead(path);
                return string.Equals(Convert.ToHexString(SHA256.HashData(stream)), parts[0], StringComparison.OrdinalIgnoreCase);
            }
        }
        return false;
    }
    // Downloads the installer, then refuses to continue unless its SHA-256 matches the checksum file of the same release.
    internal static async Task<string> DownloadAsync(UpdateInfo info, CancellationToken cancellation)
    {
        if (info.InstallerUrl is null || info.ChecksumUrl is null || !TrustedUrl(info.InstallerUrl) || !TrustedUrl(info.ChecksumUrl)) throw new InvalidOperationException("This release has no verifiable installer. Open the releases page to update manually.");
        string fileName = Path.GetFileName(new Uri(info.InstallerUrl).LocalPath);
        if (!Regex.IsMatch(fileName, @"^ClipboardPlus-Setup-[0-9.]+-win-x64\.exe$")) throw new InvalidOperationException("Unexpected installer name.");
        string directory = Path.Combine(Path.GetTempPath(), "ClipboardPlus-Update");
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, fileName);
        using var client = NewClient(TimeSpan.FromMinutes(10));
        string sums = await client.GetStringAsync(info.ChecksumUrl, cancellation);
        using (var response = await client.GetAsync(info.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellation))
        {
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(target); await response.Content.CopyToAsync(file, cancellation);
        }
        if (!VerifyChecksum(target, sums, fileName)) { File.Delete(target); throw new InvalidOperationException("The downloaded installer did not match its published checksum, so it was discarded."); }
        return target;
    }
    // The app exits first (it was closed on the user's request), then the installer runs silently and reopens Clipboard Plus.
    internal static void LaunchInstallerAfterExit(string installer)
    {
        Process.Start(new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, Arguments = $"/c ping -n 4 127.0.0.1 >nul & \"{installer}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH" });
    }
}
