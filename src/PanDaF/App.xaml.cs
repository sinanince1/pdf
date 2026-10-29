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
        var smokeIndex = Array.IndexOf(e.Args, "--smoke");
        SmokeMode = smokeIndex >= 0;
        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            args.Handled = true;
            if (SmokeMode) { Console.Error.WriteLine(args.Exception); Shutdown(1); return; }
            MessageBox.Show("Beklenmeyen bir sorun oluştu:\n\n" + args.Exception.Message, "PanDaF", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (SmokeMode)
        {
            var folder = smokeIndex + 1 < e.Args.Length ? e.Args[smokeIndex + 1] : Path.Combine(Path.GetTempPath(), "PanDaFSmoke");
            var code = await SmokeTest.RunAsync(window, folder);
            Shutdown(code);
            return;
        }
        // Dosyalar uygulama simgesinin üzerine bırakıldıysa veya "Birlikte aç" ile açıldıysa.
        var files = e.Args.Where(File.Exists).ToArray();
        if (files.Length > 0) window.OpenFiles(files);
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
