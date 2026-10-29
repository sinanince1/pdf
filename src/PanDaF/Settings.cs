using System.Text.Json;

namespace PanDaF;

/// Kullanıcı ayarları: %LOCALAPPDATA%\PanDaF\ayarlar.json
public sealed class Settings
{
    /// Dosya sürüklerken Shift'e basınca PanDaF paneli açılsın mı?
    public bool ShiftPanel { get; set; } = true;

    /// Windows açılınca PanDaF arka planda (sistem tepsisinde) başlasın mı?
    public bool StartWithWindows { get; set; } = true;

    /// "Pencereyi kapatınca arka planda çalışmaya devam ediyor" bilgisi bir kez gösterildi mi?
    public bool TrayHintShown { get; set; }

    private static string File => Path.Combine(App.DataFolder, "ayarlar.json");

    public static Settings Load()
    {
        try
        {
            if (System.IO.File.Exists(File)) return JsonSerializer.Deserialize<Settings>(System.IO.File.ReadAllText(File)) ?? new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(App.DataFolder);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
