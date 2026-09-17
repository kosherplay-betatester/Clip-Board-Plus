using System.Windows;
using System.Windows.Controls;

namespace ClipboardPlus;

public partial class SettingsWindow : Window
{
    private readonly AppSettings original;
    private readonly HistoryStore store;
    private readonly Func<AppSettings, Task> save;
    private readonly Action<bool>? suspendShortcut;
    private readonly System.Collections.ObjectModel.ObservableCollection<string> excluded = [];
    private readonly CancellationTokenSource updatesCancellation = new();
    public SettingsWindow(AppSettings settings, HistoryStore store, Func<AppSettings, Task> save, Action<bool>? suspendShortcut = null)
    {
        original = settings; this.store = store; this.save = save; this.suspendShortcut = suspendShortcut; InitializeComponent();
        WinV.IsChecked = settings.ReplaceWinV; HotkeyBox.Text = settings.Hotkey;
        WindowsHistoryBox.SelectedIndex = settings.WindowsHistoryMode switch { "On" => 1, "Off" => 2, "Unchanged" => 3, _ => 0 };
        WindowsHistoryStatus.Text = WindowsHistorySettings.Status();
        VersionLabel.Text = $"Clipboard Plus {UpdateChecker.CurrentVersion} · build {typeof(App).Assembly.GetName().Version!.Revision} · Windows desktop";
        StartupBox.IsChecked = settings.StartWithWindows; HideBox.IsChecked = settings.HideAfterPaste;
        ItemsBox.Text = settings.MaxItems.ToString(); DaysBox.Text = settings.RetentionDays.ToString(); DiskBox.Text = settings.DiskBudgetMb.ToString(); CacheBox.Text = settings.CacheBudgetMb.ToString(); ItemBox.Text = settings.MaxItemMb.ToString();
        foreach (var name in settings.ExcludedApps.Split([';', ',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)) excluded.Add(name);
        ExcludedList.ItemsSource = excluded; ThemeBox.SelectedIndex = settings.Theme == "Light" ? 1 : 0;
        Loaded += async (_, _) => await UpdateUsage();
        Closed += (_, _) => updatesCancellation.Cancel();
    }
    private async Task UpdateUsage()
    {
        var stats = await store.StatsAsync();
        using var process = Process.GetCurrentProcess();
        UsageLabel.Text = $"{stats.Count:N0} clips · {stats.Pinned:N0} pins · {stats.DiskBytes.SizeLabel()} on disk\nCache: {store.CacheBytes.SizeLabel()} · Current process RAM: {process.WorkingSet64.SizeLabel()}\nInstalled RAM: {MemoryInfo.Read().Total / 1073741824d:0.#} GB · Suggested cache: {MemoryInfo.RecommendedCacheMb} MB";
    }
    private void Recommend_Click(object sender, RoutedEventArgs e) => CacheBox.Text = MemoryInfo.RecommendedCacheMb.ToString();
    private void WinV_Changed(object sender, RoutedEventArgs e) { if (HotkeyBox is not null) HotkeyBox.Opacity = WinV.IsChecked == true ? .5 : 1; }
    private void Record_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            suspendShortcut?.Invoke(true);
            var recorder = new KeyRecorderWindow { Owner = this };
            if (recorder.ShowDialog() == true)
            {
                bool winV = HotkeySpec.Parse(recorder.Shortcut) == new HotkeySpec(8, 0x56);
                WinV.IsChecked = winV;
                if (!winV) HotkeyBox.Text = recorder.Shortcut;
                ErrorLabel.Text = "Shortcut recorded. Choose Save settings to apply it.";
            }
        }
        catch (Exception error) { ErrorLabel.Text = error.Message; }
        finally { try { suspendShortcut?.Invoke(false); } catch (Exception error) { ErrorLabel.Text = error.Message; } }
    }
    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        var picker = new AppPickerWindow { Owner = this };
        if (picker.ShowDialog() == true && picker.SelectedProcess is { } name && !excluded.Contains(name, StringComparer.OrdinalIgnoreCase)) excluded.Add(name);
    }
    private void RemoveApp_Click(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is string name) excluded.Remove(name); }
    private void Releases_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(UpdateChecker.ReleasesUrl) { UseShellExecute = true });
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false; UpdateStatus.Text = "Checking GitHub Releases…";
        try { UpdateStatus.Text = await UpdateChecker.CheckAsync(updatesCancellation.Token); }
        catch (OperationCanceledException) { if (!updatesCancellation.IsCancellationRequested) UpdateStatus.Text = "The check timed out. Try again or open the releases page."; }
        catch (Exception) { UpdateStatus.Text = "Could not check for updates. Check your connection, or open the releases page."; }
        finally { UpdateButton.IsEnabled = true; }
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            int Read(TextBox box, string name) => int.TryParse(box.Text, out int value) ? value : throw new ArgumentException($"Enter a whole number for {name}.");
            var result = original with {
                ReplaceWinV = WinV.IsChecked == true, Hotkey = HotkeyBox.Text.Trim(), StartWithWindows = StartupBox.IsChecked == true, HideAfterPaste = HideBox.IsChecked == true,
                WindowsHistoryMode = WindowsHistoryBox.SelectedIndex switch { 1 => "On", 2 => "Off", 3 => "Unchanged", _ => "Automatic" }, WindowsHistoryManaged = true,
                MaxItems = Read(ItemsBox, "history capacity"), RetentionDays = Read(DaysBox, "retention"), DiskBudgetMb = Read(DiskBox, "disk budget"), CacheBudgetMb = Read(CacheBox, "cache budget"), MaxItemMb = Read(ItemBox, "clip size"),
                ExcludedApps = string.Join(';', excluded), Theme = ThemeBox.SelectedIndex == 1 ? "Light" : "Dark"
            };
            await save(result); DialogResult = true;
        }
        catch (Exception error) { ErrorLabel.Text = error.Message; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void WindowsSettings_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("ms-settings:clipboard") { UseShellExecute = true });
    private async Task Clear(bool all)
    {
        if (MessageBox.Show(this, all ? "Delete all saved clips, including pins and snippets?" : "Delete all unpinned clips? Pinned clips and snippets will stay.", "Clear history", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { await store.ClearAsync(all); await UpdateUsage(); ErrorLabel.Text = "History cleared. Original files are unchanged."; }
        catch (Exception e) { ErrorLabel.Text = e.Message; }
    }
    private async void ClearAll_Click(object sender, RoutedEventArgs e) => await Clear(true);
    private async void ClearUnpinned_Click(object sender, RoutedEventArgs e) => await Clear(false);
}
