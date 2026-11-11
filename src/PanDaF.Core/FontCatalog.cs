using SkiaSharp;

namespace PanDaF.Core;

/// Bu bilgisayarda kurulu, PDF'ye yazılabilen (TrueType) yazı tiplerinin dizini: aile adı → dosyalar.
/// İlk kullanımda yazı tipi klasörleri bir kez taranır; düzenleyici açılırken arka planda hazırlanır.
public static class FontCatalog
{
    public sealed record Face(string Family, string Path, bool Bold, bool Italic);

    private sealed record Index(List<string> Families, Dictionary<string, List<Face>> ByName, Dictionary<string, string> ByNormalized);
    private static readonly Lazy<Index> index = new(Scan, LazyThreadSafetyMode.ExecutionAndPublication);

    // Harf yerine simge çizen yazı tipleri listede gösterilmez.
    private static readonly string[] Symbols = ["Wingdings", "Webdings", "Symbol", "Marlett", "MDL2", "Fluent Icons", "MT Extra", "Bookshelf Symbol", "Reference Specialty", "MS Outlook", "Emoji", "Dingbats", "LastResort", "Apple Symbols", "Font Awesome"];

    /// Kurulu aileler, alfabetik.
    public static IReadOnlyList<string> Families => index.Value.Families;
    public static bool Ready => index.IsValueCreated;
    public static Task Preload() => Task.Run(() => { _ = index.Value; });

    /// Ailenin istenen biçimdeki dosyası. Tam biçim yoksa ailenin düz (o da yoksa ilk) dosyası döner; Exact false olur.
    public static (Face Face, bool Exact)? Find(string family, bool bold, bool italic)
    {
        if (string.IsNullOrWhiteSpace(family) || !index.Value.ByName.TryGetValue(family.Trim(), out var faces) || faces.Count == 0) return null;
        var exact = faces.FirstOrDefault(f => f.Bold == bold && f.Italic == italic);
        if (exact is not null) return (exact, true);
        return (faces.FirstOrDefault(f => !f.Bold && !f.Italic) ?? faces.FirstOrDefault(f => f.Italic == italic) ?? faces[0], false);
    }

    /// PDF'deki yazı tipi adını ("ABCDEF+CalistoMT-Bold") kurulu bir aileye eşler; yoksa null.
    public static string? Match(string pdfFontName)
    {
        var name = pdfFontName ?? "";
        var plus = name.IndexOf('+'); if (plus is >= 0 and <= 7) name = name[(plus + 1)..];
        var map = index.Value.ByNormalized;
        foreach (var candidate in new[] { name, name.Split(['-', ','], 2)[0], TextRewriter.Parse(pdfFontName ?? "", false).Family })
            if (map.TryGetValue(Normalize(candidate), out var family)) return family;
        return null;
    }

    private static string Normalize(string name) => new string([.. name.Where(char.IsLetterOrDigit)]).ToLowerInvariant();

    private static Index Scan()
    {
        var byName = new Dictionary<string, List<Face>>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in WindowsFontResolver.FontFolders)
        {
            IEnumerable<string> files;
            try { files = Directory.Exists(folder) ? Directory.EnumerateFiles(folder).ToList() : []; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            foreach (var path in files)
            {
                var extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension is not (".ttf" or ".ttc") || !seen.Add(Path.GetFileName(path))) continue;
                try
                {
                    if (!TrueTypeOutlines(path)) continue;
                    using var typeface = SKTypeface.FromFile(path);
                    var family = typeface?.FamilyName;
                    if (typeface is null || string.IsNullOrWhiteSpace(family) || family.StartsWith('.')) continue;
                    if (Symbols.Any(s => family.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
                    if (typeface.GetGlyph('a') == 0 || typeface.GetGlyph('A') == 0) continue;
                    if (!byName.TryGetValue(family, out var faces)) byName[family] = faces = [];
                    var face = new Face(family, path, typeface.IsBold, typeface.IsItalic);
                    if (!faces.Any(f => f.Bold == face.Bold && f.Italic == face.Italic)) faces.Add(face);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
            }
        }
        var families = byName.Keys.OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList();
        var normalized = new Dictionary<string, string>();
        foreach (var family in families) normalized.TryAdd(Normalize(family), family);
        return new Index(families, byName, normalized);
    }

    /// Dosya TrueType çizgileri mi taşıyor? (PDFsharp CFF/PostScript çizgili OpenType dosyalarını okuyamaz.)
    private static bool TrueTypeOutlines(string path)
    {
        Span<byte> head = stackalloc byte[16];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Read(head) < 16) return false;
        if (head[0] == 't' && head[1] == 't' && head[2] == 'c' && head[3] == 'f')
        {
            var first = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(head[12..]);
            stream.Position = first;
            if (stream.Read(head[..4]) < 4) return false;
        }
        return (head[0] == 0 && head[1] == 1 && head[2] == 0 && head[3] == 0) || (head[0] == 't' && head[1] == 'r' && head[2] == 'u' && head[3] == 'e');
    }
}
