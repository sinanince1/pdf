using System.IO.Pipes;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PanDaF.Core;

namespace PanDaF;

public partial class App : Application
{
    public static string DataFolder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PanDaF");
    public static Converter Converter { get; } = new(Path.Combine(DataFolder, "araclar"));
    public static bool SmokeMode { get; private set; }
    public static Settings Settings { get; } = Settings.Load();
    public static MainWindow? Main { get; private set; }
    private static Mutex? instance;
    private const string PipeName = "PanDaF-goster";
    private TrayIcon? tray;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) { base.OnFrameworkInitializationCompleted(); return; }
        var args = desktop.Args ?? [];
        var smokeIndex = Array.IndexOf(args, "--smoke");
        SmokeMode = smokeIndex >= 0;
        WindowsFontResolver.Install();
        Platform.Install();
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var files = args.Where(File.Exists).ToArray();
        var background = args.Contains(AutoStart.BackgroundArgument);

        // Tek kopya: PanDaF zaten çalışıyorsa (ör. tepside) onu öne getir ve bu kopyayı kapat.
        if (!SmokeMode)
        {
            try { instance = new Mutex(true, "PanDaF-tek-kopya", out var first); if (!first) { SignalOther(files); desktop.Shutdown(); return; } }
            catch (Exception error) when (error is PlatformNotSupportedException or IOException or UnauthorizedAccessException) { Log(error); }
        }

        var window = new MainWindow();
        Main = window;
        desktop.MainWindow = window;
        if (SmokeMode)
        {
            window.Show();
            var folder = smokeIndex + 1 < args.Length ? args[smokeIndex + 1] : Path.Combine(Path.GetTempPath(), "PanDaFSmoke");
            _ = RunSmoke(window, folder, desktop);
            base.OnFrameworkInitializationCompleted();
            return;
        }

        InstallTray();
        AutoStart.Apply(Settings.StartWithWindows);
        ListenForSecondInstance();
        // Dosya "Birlikte aç" ile veya Dock simgesine bırakılarak açıldıysa (macOS).
        if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            activatable.Activated += (_, e) => { if (e is FileActivatedEventArgs opened) Dispatcher.UIThread.Post(() => OpenPaths(opened.Files.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Cast<string>().ToArray())); };
        if (!background || tray is null) window.Show();
        if (files.Length > 0) window.OpenFiles(files);
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task RunSmoke(MainWindow window, string folder, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var code = 1;
        try { code = await SmokeTest.RunAsync(window, folder); }
        catch (Exception error) { Console.Error.WriteLine(error); Log(error); }
        ExitApp(code);
    }

    private void OpenPaths(string[] paths)
    {
        if (paths.Length == 0 || Main is null) return;
        Main.BringToFront();
        Main.OpenFiles(paths);
    }

    private void InstallTray()
    {
        try
        {
            var menu = new NativeMenu();
            var open = new NativeMenuItem("PanDaF'ı aç"); open.Click += (_, _) => Main?.BringToFront();
            var shift = new NativeMenuItem("Shift paneli") { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = Settings.ShiftPanel, IsEnabled = OperatingSystem.IsWindows() };
            shift.Click += (_, _) => { Settings.ShiftPanel = shift.IsChecked; Settings.Save(); };
            var startup = new NativeMenuItem("Bilgisayar açılınca başlat") { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = Settings.StartWithWindows };
            startup.Click += (_, _) => { Settings.StartWithWindows = startup.IsChecked; Settings.Save(); AutoStart.Apply(startup.IsChecked); };
            var exit = new NativeMenuItem("PanDaF'tan çık"); exit.Click += (_, _) => ExitApp();
            menu.Items.Add(open); menu.Items.Add(new NativeMenuItemSeparator()); menu.Items.Add(shift); menu.Items.Add(startup); menu.Items.Add(new NativeMenuItemSeparator()); menu.Items.Add(exit);
            tray = new TrayIcon { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://PanDaF/Assets/PanDaF.ico"))), ToolTipText = "PanDaF", Menu = menu, IsVisible = true };
            tray.Clicked += (_, _) => Main?.BringToFront();
            TrayIcon.SetIcons(this, [tray]);
        }
        catch (Exception error) { Log(error); tray = null; }
    }

    public bool HasTray => tray is not null;

    private static bool shuttingDown;
    public static void ExitApp(int code = 0)
    {
        if (shuttingDown) return;
        shuttingDown = true;
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown(code);
    }

    // ----- İkinci kopya: dosyaları adlandırılmış boru ile çalışan kopyaya iletir -----

    private static void SignalOther(string[] files)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            using var writer = new StreamWriter(client);
            writer.Write(string.Join("\n", files));
        }
        catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException) { Log(error); }
    }

    private void ListenForSecondInstance()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server);
                    var text = reader.ReadToEnd();
                    var paths = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(File.Exists).ToArray();
                    Dispatcher.UIThread.Post(() => { Main?.BringToFront(); if (paths.Length > 0) Main?.OpenFiles(paths); });
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException) { Log(error); Thread.Sleep(1000); }
            }
        }) { IsBackground = true, Name = "PanDaF ikinci kopya dinleyici" };
        thread.Start();
    }

    public static void Log(Exception error)
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            var log = Path.Combine(DataFolder, "hata-gunlugu.txt");
            if (File.Exists(log) && new FileInfo(log).Length > 1_000_000) File.Delete(log);
            File.AppendAllText(log, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {error}\r\n\r\n");
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
    }
}
