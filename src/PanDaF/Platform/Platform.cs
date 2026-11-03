using System.Diagnostics;
using PanDaF.Core;

namespace PanDaF;

/// Platforma özgü parçalar tek yerden bağlanır: yazı tanıma (Windows), dosya açma, klasörde gösterme.
internal static class Platform
{
    public static void Install()
    {
#if WINDOWS
        if (WindowsOcr.Available) Converter.OcrProvider = WindowsOcr.Recognize;
#endif
    }

    public static bool SupportsShiftPanel => OperatingSystem.IsWindows();

    public static void Open(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", [path]);
            else Process.Start("xdg-open", [path]);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { App.Log(error); }
    }

    public static void Reveal(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", ["-R", path]);
            else Process.Start("xdg-open", [Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path]);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { App.Log(error); }
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { App.Log(error); }
    }
}
