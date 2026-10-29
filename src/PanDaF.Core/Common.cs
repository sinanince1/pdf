// Parts of this file are adapted from Nashi (https://github.com/orkunisitmak/nashi-app),
// Copyright (c) 2026 Orkun Işıtmak and Nashi contributors, MIT License.
namespace PanDaF.Core;

/// İlerleme bildirimi: Fraction 0..1 (bilinmiyorsa null) ve kullanıcıya gösterilecek metin.
public sealed record OperationProgress(double? Fraction, string Label);

/// Kullanıcıya olduğu gibi gösterilebilecek, Türkçe hata.
public class UserFacingException(string message, Exception? inner = null) : Exception(message, inner);

/// PDF'de form alanı veya ekli dosya var; işlem bunları koruyamaz. Kullanıcı onaylarsa
/// aynı işlem allowLossy:true ile tekrar çalıştırılır.
public sealed class LossyPdfException(string file) : UserFacingException(
    $"“{file}” içinde doldurulabilir form alanları veya ekli dosyalar var. " +
    "Bu işlemden sonra form alanları çalışmayabilir ve ekler kaybolabilir.");

public static class FileKinds
{
    public static readonly string[] Images = ["jpg", "jpeg", "png", "bmp", "gif", "tif", "tiff", "webp", "heic", "jfif"];
    public static readonly string[] WordDocs = ["doc", "docx", "odt", "rtf"];
    public static readonly string[] Slides = ["ppt", "pptx", "pps", "ppsx", "odp"];
    public static readonly string[] Sheets = ["xls", "xlsx", "ods"];

    public static string Extension(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
    public static bool IsPdf(string path) => Extension(path) == "pdf";
    public static bool IsImage(string path) => Images.Contains(Extension(path));
    public static bool IsOfficeDocument(string path) =>
        WordDocs.Contains(Extension(path)) || Slides.Contains(Extension(path)) || Sheets.Contains(Extension(path));
    public static bool CanConvertToPdf(string path) => IsImage(path) || IsOfficeDocument(path);
}

public static class SafePaths
{
    private static readonly HashSet<string> Devices = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    public static string ArchiveEntry(string root, string entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entry);
        var name = entry.Replace('\\', '/');
        var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (name.StartsWith('/') || parts.Length == 0 || parts.Any(p => p is "." or ".." ||
            p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(['<', '>', ':', '"', '|', '?', '*', '\0']) >= 0 ||
            Devices.Contains(p.Split('.')[0])))
            throw new InvalidDataException("Paket güvenli olmayan bir dosya yolu içeriyor.");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var destination = Path.GetFullPath(Path.Combine(fullRoot, Path.Combine(parts)));
        if (!destination.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Paket içindeki bir dosya hedef klasörün dışına çıkıyor.");
        return destination;
    }

    /// Aynı adda dosya varsa "Ad 2.pdf", "Ad 3.pdf"… döndürür. Hiçbir dosyanın üzerine yazılmaz.
    public static string Unique(string directory, string filename)
    {
        filename = CleanName(filename);
        var stem = Path.GetFileNameWithoutExtension(filename); var extension = Path.GetExtension(filename);
        for (var number = 1; number < 100_000; number++)
        {
            var candidate = Path.Combine(directory, number == 1 ? filename : $"{stem} {number}{extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("Bu adı kullanan çok fazla dosya var.");
    }

    public static string CleanName(string filename)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var clean = new string(filename.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(clean) ? "PDF" : clean;
    }

    public static string Stem(string path) => Path.GetFileNameWithoutExtension(path);
}

/// Çıktılar önce hedef klasörde gizli bir geçici klasöre yazılır; iş başarılı olursa taşınır.
/// İptal edilen veya hata veren işler yarım dosya bırakmaz; asıl dosyalar asla değiştirilmez.
public sealed class ExportTransaction : IDisposable
{
    public string StagingDirectory { get; }
    public string Destination { get; }
    private readonly List<string> published = [];
    private bool committed;

    public ExportTransaction(string destination)
    {
        Destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(Destination);
        StagingDirectory = Path.Combine(Destination, ".pandaf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(StagingDirectory);
        if ((File.GetAttributes(StagingDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Geçici klasör bir kısayol olamaz.");
        File.SetAttributes(StagingDirectory, File.GetAttributes(StagingDirectory) | FileAttributes.Hidden);
    }

    public IReadOnlyList<string> Publish(IEnumerable<string> outputs, CancellationToken token)
    {
        var files = outputs.ToList();
        if (files.Count == 0) throw new UserFacingException("Hiç çıktı oluşmadı.");
        foreach (var item in files)
        {
            token.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(item);
            if (!full.StartsWith(StagingDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("Bir çıktı geçici klasörün dışında.");
            if (!Directory.Exists(full) && (!File.Exists(full) || new FileInfo(full).Length == 0))
                throw new UserFacingException("Boş bir çıktı oluştu; işlem tamamlanamadı.");
        }
        foreach (var item in files)
        {
            token.ThrowIfCancellationRequested();
            var destination = SafePaths.Unique(Destination, Path.GetFileName(item));
            if (Directory.Exists(item)) Directory.Move(item, destination);
            else File.Move(item, destination, overwrite: false);
            published.Add(destination);
        }
        token.ThrowIfCancellationRequested();
        committed = true;
        return published.ToArray();
    }

    public void Dispose()
    {
        if (!committed) foreach (var path in published) TryDelete(path);
        if (TryDelete(StagingDirectory)) return;
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 40; attempt++)
            {
                await Task.Delay(250);
                if (TryDelete(StagingDirectory)) return;
            }
        });
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) Directory.Delete(path, recursive: true);
            else File.Delete(path);
            return true;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// Çıktının kaydedileceği klasör: kaynak dosyanın klasörü yazılabilirse orası,
/// değilse Belgeler\PanDaF.
public static class OutputFolder
{
    public static string For(string firstInput)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(firstInput));
        // E-posta ekleri gibi geçici klasörlerden açılan dosyaların çıktısı kaybolmasın.
        var temporary = (folder ?? "").TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (folder is not null && !temporary.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) && Writable(folder))
            return folder;
        return Fallback;
    }

    public static string Fallback
    {
        get
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PanDaF");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    private static bool Writable(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, ".pandaf-yazma-testi-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(probe, [1]);
            File.Delete(probe);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }
}
