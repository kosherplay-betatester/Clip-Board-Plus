using System.Windows;
using System.Windows.Threading;

namespace ClipboardPlus;

public partial class App : System.Windows.Application
{
    private Mutex? mutex;
    public static string DataRoot { get; private set; } = "";
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        int installerArtIndex = Array.IndexOf(e.Args, "--write-installer-art");
        if (installerArtIndex >= 0 && installerArtIndex + 1 < e.Args.Length) { IconFactory.WriteInstallerArt(e.Args[installerArtIndex + 1]); Shutdown(); return; }
        int iconIndex = Array.IndexOf(e.Args, "--write-icon");
        if (iconIndex >= 0 && iconIndex + 1 < e.Args.Length)
        {
            IconFactory.Write(e.Args[iconIndex + 1]); Shutdown(); return;
        }
        if (e.Args.Contains("--self-test"))
        {
            int result = await SelfTests.RunAsync(e.Args); Shutdown(result); return;
        }
        bool demo = e.Args.Contains("--demo");
        DataRoot = demo ? Path.Combine(Path.GetTempPath(), "ClipboardPlus-Demo") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipboardPlus");
        if (e.Args.Contains("--render-proof")) DataRoot = Path.Combine(Path.GetTempPath(), "ClipboardPlus-Proof-" + Guid.NewGuid().ToString("N"));
        mutex = new Mutex(true, "Local\\ClipboardPlus-" + Environment.UserName + (demo ? "-demo" : ""), out bool first);
        if (!first) { MessageBox.Show("Clipboard Plus is already running. Use your shortcut or the tray icon to open it.", "Clipboard Plus"); Shutdown(); return; }
        DispatcherUnhandledException += OnUnhandled;
        try
        {
            var settings = AppSettings.Load(DataRoot);
            if (!demo)
            {
                using var startup = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                settings = settings with { StartWithWindows = startup?.GetValue("ClipboardPlus") is string value && !string.IsNullOrWhiteSpace(value) };
            }
            var store = await Task.Run(() => new HistoryStore(DataRoot, settings));
            await store.MaintainAsync();
            if (demo) await DemoData.SeedAsync(store);
            ApplyTheme(settings.Theme);
            var window = new MainWindow(store, settings, demo);
            MainWindow = window;
            await window.InitializeAsync();
            if (!e.Args.Contains("--background")) window.Show();
            int proofIndex = Array.IndexOf(e.Args, "--render-proof");
            int fixtureIndex = Array.IndexOf(e.Args, "--media-fixtures");
            if (proofIndex >= 0 && proofIndex + 1 < e.Args.Length) await window.RenderProofAsync(e.Args[proofIndex + 1], fixtureIndex >= 0 && fixtureIndex + 1 < e.Args.Length ? e.Args[fixtureIndex + 1] : null);
        }
        catch (Exception error) { MessageBox.Show("Clipboard Plus could not start. Your saved data has not been reset.\n\n" + error.Message, "Clipboard Plus"); Shutdown(1); }
    }
    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        MessageBox.Show("The operation could not finish.\n\n" + e.Exception.Message, "Clipboard Plus");
    }
    public static void ApplyTheme(string theme)
    {
        var light = theme == "Light";
        var colors = new Dictionary<string, string> {
            ["BackgroundBrush"] = light ? "#F5F7FA" : "#10151D", ["SidebarBrush"] = light ? "#EDF1F5" : "#141B25",
            ["CardBrush"] = light ? "#FFFFFF" : "#1A2330", ["HoverBrush"] = light ? "#E1EEE7" : "#263446",
            ["BorderBrush"] = light ? "#D2DBE4" : "#2B3747", ["TextBrush"] = light ? "#172B3A" : "#F0F4F8",
            ["MutedBrush"] = light ? "#53687C" : "#9CAEC3", ["AccentBrush"] = light ? "#2B7950" : "#A8EAC6",
            ["AccentTextBrush"] = light ? "#FFFFFF" : "#11281C"
        };
        foreach (var (key, color) in colors) Current.Resources[key] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
    }
    protected override void OnExit(ExitEventArgs e) { mutex?.Dispose(); base.OnExit(e); }
}
