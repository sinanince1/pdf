using Microsoft.Win32;

namespace PanDaF;

/// Bilgisayar açılınca başlatma. Windows: HKCU Run kaydı. macOS: ~/Library/LaunchAgents plist.
/// Linux: ~/.config/autostart .desktop. Yalnızca bu kullanıcıyı etkiler, yönetici izni istemez.
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "PanDaF";
    public const string BackgroundArgument = "--arkaplan";

    public static void Apply(bool enabled)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                if (key is null) return;
                if (enabled) key.SetValue(Name, $"\"{exe}\" {BackgroundArgument}", RegistryValueKind.String);
                else if (key.GetValue(Name) is not null) key.DeleteValue(Name, false);
            }
            else if (OperatingSystem.IsMacOS())
            {
                var agents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents");
                var plist = Path.Combine(agents, "com.sinanince.pandaf.plist");
                if (!enabled) { if (File.Exists(plist)) File.Delete(plist); return; }
                Directory.CreateDirectory(agents);
                // .app paketi içinden çalışıyorsa paketi aç; aksi hâlde doğrudan çalıştırılabilir dosyayı.
                var app = AppBundle(exe);
                var program = app is null ? $"<string>{Escape(exe)}</string><string>{BackgroundArgument}</string>" : $"<string>/usr/bin/open</string><string>-a</string><string>{Escape(app)}</string><string>--args</string><string>{BackgroundArgument}</string>";
                File.WriteAllText(plist, $"""
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>Label</key><string>com.sinanince.pandaf</string>
<key>ProgramArguments</key><array>{program}</array>
<key>RunAtLoad</key><true/>
</dict></plist>
""");
            }
            else
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "autostart");
                var desktop = Path.Combine(dir, "pandaf.desktop");
                if (!enabled) { if (File.Exists(desktop)) File.Delete(desktop); return; }
                Directory.CreateDirectory(dir);
                File.WriteAllText(desktop, $"[Desktop Entry]\nType=Application\nName=PanDaF\nExec=\"{exe}\" {BackgroundArgument}\nX-GNOME-Autostart-enabled=true\n");
            }
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException) { App.Log(error); }
    }

    private static string? AppBundle(string exe)
    {
        var index = exe.IndexOf(".app/Contents/MacOS/", StringComparison.Ordinal);
        return index > 0 ? exe[..(index + 4)] : null;
    }

    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
