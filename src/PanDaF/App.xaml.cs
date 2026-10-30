using System.IO;
using System.Windows;
using PanDaF.Core;

namespace PanDaF;

public partial class App : Application
{
    public static string DataFolder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PanDaF");
    public static Converter Converter { get; } = new(Path.Combine(DataFolder, "araclar"));
    public static bool SmokeMode { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        WindowsFontResolver.Install();
        var smokeIndex = Array.IndexOf(e.Args, "--smoke");
        SmokeMode = smokeIndex >= 0;
        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            args.Handled = true;
            if (SmokeMode) { Console.Error.WriteLine(args.Exception); Shutdown(1); return; }
            MessageBox.Show("Beklenmeyen bir sorun oluştu:\n\n" + args.Exception.Message, "PanDaF", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        var files = e.Args.Where(File.Exists).ToArray();
        var background = e.Args.Contains(AutoStart.BackgroundArgument);

        // Tek kopya: PanDaF zaten çalışıyorsa (ör. tepside) onu öne getir ve bu kopyayı kapat.
        if (!SmokeMode)
        {
            instance = new Mutex(true, @"Local\PanDaF-tek-kopya", out var first);
            if (!first)
            {
                if (!background)
                {
                    try { Directory.CreateDirectory(DataFolder); File.WriteAllLines(Path.Combine(DataFolder, "acilacak.txt"), files); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    try { using var signal = EventWaitHandle.OpenExisting(ShowSignalName); signal.Set(); } catch (WaitHandleCannotBeOpenedException) { }
                }
                Shutdown();
                return;
            }
        }

        var window = new MainWindow();
        MainWindow = window;
        if (SmokeMode)
        {
            window.Show();
            var folder = smokeIndex + 1 < e.Args.Length ? e.Args[smokeIndex + 1] : Path.Combine(Path.GetTempPath(), "PanDaFSmoke");
            var code = await SmokeTest.RunAsync(window, folder);
            Shutdown(code);
            return;
        }

        window.InstallTray();
        // Her açılışta kayıt tazelenir: exe başka bir klasöre taşındıysa otomatik başlatma yine çalışır.
        AutoStart.Apply(window.Settings.StartWithWindows);
        ListenForSecondInstance(window);
        // Windows açılışında (--arkaplan) pencere gösterilmez; PanDaF tepside bekler.
        if (!background) window.Show();
        // Dosyalar uygulama simgesinin üzerine bırakıldıysa veya "Birlikte aç" ile açıldıysa.
        if (files.Length > 0) window.OpenFiles(files);
    }

    private const string ShowSignalName = @"Local\PanDaF-goster";
    private static Mutex? instance;

    private void ListenForSecondInstance(MainWindow window)
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        var thread = new Thread(() =>
        {
            while (signal.WaitOne())
            {
                Dispatcher.InvokeAsync(() =>
                {
                    var request = Path.Combine(DataFolder, "acilacak.txt");
                    string[] pending = [];
                    try { if (File.Exists(request)) { pending = File.ReadAllLines(request).Where(File.Exists).ToArray(); File.Delete(request); } }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                    window.BringToFront();
                    if (pending.Length > 0) window.OpenFiles(pending);
                });
            }
        }) { IsBackground = true, Name = "PanDaF ikinci kopya dinleyici" };
        thread.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { instance?.ReleaseMutex(); } catch (ApplicationException) { }
        base.OnExit(e);
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
