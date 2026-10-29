using Microsoft.Win32;

namespace PanDaF;

/// Windows açılınca başlatma: HKCU\...\Run altına kayıt. Yalnızca bu kullanıcıyı etkiler,
/// yönetici izni istemez. Uygulama her açılışta yolu tazeler (exe taşınmışsa kayıt düzelir).
public static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "PanDaF";
    public const string BackgroundArgument = "--arkaplan";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return;
            var exe = Environment.ProcessPath;
            if (enabled && !string.IsNullOrEmpty(exe)) key.SetValue(Name, $"\"{exe}\" {BackgroundArgument}", RegistryValueKind.String);
            else if (!enabled && key.GetValue(Name) is not null) key.DeleteValue(Name, false);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException) { App.Log(error); }
    }

    public static bool Registered
    {
        get
        {
            try { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue(Name) is string; }
            catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return false; }
        }
    }
}
