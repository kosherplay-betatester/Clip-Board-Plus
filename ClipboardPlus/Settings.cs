namespace ClipboardPlus;

public sealed record AppSettings
{
    public int MaxItems { get; init; } = 10000;
    public int RetentionDays { get; init; } = 90;
    public int DiskBudgetMb { get; init; } = 512;
    public int CacheBudgetMb { get; init; } = 32;
    public int MaxItemMb { get; init; } = 16;
    public string Hotkey { get; init; } = "Ctrl+Shift+V";
    public bool ReplaceWinV { get; init; }
    public string WindowsHistoryMode { get; init; } = "Automatic";
    public bool WindowsHistoryManaged { get; init; }
    public bool StartWithWindows { get; init; }
    public bool HideAfterPaste { get; init; } = true;
    public string ExcludedApps { get; init; } = "1Password;Bitwarden;KeePass;KeePassXC;LastPass";
    public string Theme { get; init; } = "Dark";
    public void Validate()
    {
        if (WindowsHistoryMode is not ("Automatic" or "On" or "Off" or "Unchanged")) throw new ArgumentException("Choose a Windows history preference.");
        if (MaxItems is < 100 or > 1000000) throw new ArgumentException("History capacity must be between 100 and 1,000,000 items.");
        if (RetentionDays is < 1 or > 3650) throw new ArgumentException("Retention must be between 1 and 3,650 days.");
        if (DiskBudgetMb is < 32 or > 102400) throw new ArgumentException("Disk budget must be between 32 and 102,400 MB.");
        if (CacheBudgetMb is < 0 or > 512) throw new ArgumentException("Memory cache must be between 0 and 512 MB.");
        if (MaxItemMb is < 1 or > 64 || MaxItemMb > DiskBudgetMb) throw new ArgumentException("Individual clips must be limited to 1–64 MB, within the disk budget.");
        var shortcut = HotkeySpec.Parse(ReplaceWinV ? "Win+V" : Hotkey);
        if (!ReplaceWinV && shortcut == new HotkeySpec(8, 0x56)) throw new ArgumentException("To use Win+V, enable the dedicated replacement option above.");
    }
    public bool Excludes(string source) => ExcludedApps.Split([';', ',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(s => Path.GetFileName(s.Trim('"'))).Select(s => s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s)
        .Any(s => string.Equals(s, source, StringComparison.OrdinalIgnoreCase));
    public static AppSettings Load(string root)
    {
        var path = Path.Combine(root, "settings.json");
        if (!File.Exists(path)) return new();
        var result = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new();
        result.Validate();
        return result;
    }
    public void Save(string root)
    {
        Validate();
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}
