using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace PanDaF.Core;

/// Sayfadaki bir metin satırı: UI için sol üst köşeden konum (nokta), motor için bölge.
public sealed record TextLine(string Text, double X, double Y, double Width, double Height, double FontSize, bool Bold,
    double RegionLeft, double RegionBottom, double RegionRight, double RegionTop, string FontName = "");

public sealed record RewriteResult(byte[] Document, bool UsedSubstituteFont, double? ShrunkToFontSize = null);

/// Sayfadaki bir paragraf (motorun bulduğu aday). Konum UI için sol üstten, nokta cinsinden.
public sealed record Paragraph(string Text, double X, double Y, double Width, double Height, string FontName, double FontSize, double Leading,
    double RegionLeft, double RegionBottom, double RegionRight, double RegionTop);

public sealed record ReplaceAllResult(byte[] Document, int ReplacedLines, int FailedLines, int PagesWithMatches);

/// Var olan yazıyı sayfa içeriğinde yerinde değiştirir (KillerPdf.Engine). Eski harfler aynı metin
/// nesnesinden çıkar, yenileri aynı yazı tipi, boyut ve renkle yerine yazılır; üstüne kapatma konmaz.
public static class TextRewriter
{
    private static readonly Lazy<TrueTypeFont> Regular = new(() => TrueTypeFont.Load(WindowsFontResolver.FontBytes(bold: false)));
    private static readonly Lazy<TrueTypeFont> BoldFont = new(() => TrueTypeFont.Load(WindowsFontResolver.FontBytes(bold: true)));

    /// Sayfadaki metin satırlarını çıkarır. Döndürülmüş sayfalarda boş liste döner (motor bölgeyi döndürülmemiş bekler).
    public static List<TextLine> Lines(byte[] document, int pageIndex)
    {
        var result = new List<TextLine>();
        using var pdf = PigDocument.Open(document);
        if (pageIndex < 0 || pageIndex >= pdf.NumberOfPages) return result;
        var page = pdf.GetPage(pageIndex + 1);
        if (page.Rotation.Value != 0) return result;
        var crop = page.CropBox.Bounds;
        var words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        var lines = new List<List<UglyToad.PdfPig.Content.Word>>();
        foreach (var word in words.OrderByDescending(w => w.BoundingBox.Bottom))
        {
            var height = Math.Max(2, word.BoundingBox.Height);
            var line = lines.LastOrDefault(l => Math.Abs(l[0].BoundingBox.Bottom - word.BoundingBox.Bottom) < height * .4);
            if (line is null) lines.Add([word]); else line.Add(word);
        }
        foreach (var line in lines)
        {
            // Aynı satırda birbirinden uzak sözcük grupları (tablo hücreleri, sütunlar) ayrı satır sayılır.
            var ordered = line.OrderBy(w => w.BoundingBox.Left).ToList();
            var group = new List<UglyToad.PdfPig.Content.Word> { ordered[0] };
            for (var i = 1; i <= ordered.Count; i++)
            {
                var split = i == ordered.Count || ordered[i].BoundingBox.Left - group[^1].BoundingBox.Right > Math.Max(12, group[^1].BoundingBox.Height * 2.5);
                if (!split) { group.Add(ordered[i]); continue; }
                result.Add(Build(group, crop));
                if (i < ordered.Count) group = [ordered[i]];
            }
        }
        return result;
    }

    private static TextLine Build(List<UglyToad.PdfPig.Content.Word> words, UglyToad.PdfPig.Core.PdfRectangle crop)
    {
        var left = words.Min(w => w.BoundingBox.Left); var right = words.Max(w => w.BoundingBox.Right);
        var bottom = words.Min(w => w.BoundingBox.Bottom); var top = words.Max(w => w.BoundingBox.Top);
        var letter = words[0].Letters.FirstOrDefault();
        var size = Math.Clamp(letter?.PointSize ?? (top - bottom) / 1.2, 1, 300);
        var font = letter?.FontName ?? "";
        var text = string.Join(" ", words.Select(w => w.Text));
        return new TextLine(text, left - crop.Left, crop.Top - top, right - left, top - bottom, size, font.Contains("Bold", StringComparison.OrdinalIgnoreCase),
            left - crop.Left - .5, bottom - crop.Bottom - .5, right - crop.Left + .5, top - crop.Bottom + .5, font);
    }

    /// Satır yerinde değiştirilebilir mi? Değilse Türkçe gerekçe döner.
    public static string? Blocker(byte[] document, int pageIndex, TextLine line)
    {
        try
        {
            var inspection = PdfTextLineEditor.Inspect(KillerPdf.Engine.Documents.PdfDocument.Open(document), pageIndex, Region(line));
            return inspection.Blocker switch
            {
                PdfTextLineBlocker.None => null,
                PdfTextLineBlocker.NoText => "Burada değiştirilebilir yazı yok.",
                PdfTextLineBlocker.InvisibleText => "Bu yazı görünmez bir metin katmanı (taranmış sayfa). Burada yazıyı değiştirmek yerine “Kapat” ile örtüp üstüne yazabilirsiniz.",
                PdfTextLineBlocker.ZeroSizeText => "Bu yazının boyutu sıfır; değiştirilemiyor.",
                _ => "Bu yazının yazı tipi çözülemedi; değiştirilemiyor. “Kapat” ile örtüp üstüne yazabilirsiniz.",
            };
        }
        catch (Exception error) when (error is FormatException or InvalidOperationException or NotSupportedException or ArgumentException or IOException)
        {
            return "Bu PDF'de yazı yerinde değiştirilemiyor: " + error.Message;
        }
    }

    public static RewriteResult Replace(byte[] document, int pageIndex, TextLine line, string newText, double? fontSize = null)
    {
        try
        {
            var edit = new PdfTextLineEdit(pageIndex, Region(line), newText) { Substitute = SubstituteFor(line.FontName, line.Bold), FontSize = fontSize };
            var result = PdfTextLineEditor.Replace(KillerPdf.Engine.Documents.PdfDocument.Open(document), edit);
            return new RewriteResult(result.Document, result.Font == PdfTextLineFont.Substitute);
        }
        catch (NotSupportedException error) { throw new UserFacingException("Bu satır yerinde değiştirilemedi: " + error.Message, error); }
        catch (Exception error) when (error is FormatException or InvalidOperationException or ArgumentException)
        { throw new UserFacingException("Yazı değiştirilemedi: " + error.Message, error); }
    }

    // ----- Yedek yazı tipi: önce aynı ailenin Windows'taki tam hâli, yoksa Arial -----

    private static readonly Dictionary<string, TrueTypeFont> substitutes = new(StringComparer.OrdinalIgnoreCase);

    /// "ABCDEF+Calibri-Bold" gibi bir PDF yazı tipi adı için Windows'ta kurulu en yakın dosyayı yükler.
    internal static TrueTypeFont SubstituteFor(string pdfFontName, bool boldHint)
    {
        var (family, bold, italic) = Parse(pdfFontName, boldHint);
        var key = $"{family}|{bold}|{italic}";
        lock (substitutes)
        {
            if (substitutes.TryGetValue(key, out var cached)) return cached;
            TrueTypeFont font;
            var file = FindFontFile(family, bold, italic);
            try { font = file is null ? (bold ? BoldFont.Value : Regular.Value) : TrueTypeFont.Load(File.ReadAllBytes(file)); }
            catch (Exception error) when (error is IOException or FormatException or InvalidOperationException or ArgumentException) { font = bold ? BoldFont.Value : Regular.Value; }
            return substitutes[key] = font;
        }
    }

    internal static (string Family, bool Bold, bool Italic) Parse(string name, bool boldHint)
    {
        name = name ?? "";
        var plus = name.IndexOf('+'); if (plus is >= 0 and <= 7) name = name[(plus + 1)..];
        var lower = name.ToLowerInvariant();
        var bold = boldHint || lower.Contains("bold") || lower.Contains("black") || lower.Contains("heavy") || lower.Contains("semibold");
        var italic = lower.Contains("italic") || lower.Contains("oblique");
        var family = name.Split(['-', ','], 2)[0];
        foreach (var token in new[] { "Bold", "Italic", "Oblique", "Regular", "MT", "PS", "PSMT", "Light", "Black" }) family = family.Replace(token, "", StringComparison.OrdinalIgnoreCase);
        family = family.Trim(' ', '-', ',');
        // Yaygın PostScript adları → Windows aileleri
        family = family.ToLowerInvariant() switch
        {
            "helvetica" or "helveticaneue" or "arialmt" => "Arial",
            "times" or "timesnewroman" or "timesnewromanpsmt" or "timesnewromanps" => "Times New Roman",
            "courier" or "couriernew" or "couriernewpsmt" => "Courier New",
            "symbol" => "Arial",
            _ => family,
        };
        return (family, bold, italic);
    }

    // Aile adı → Windows dosya adı kökleri. Her ailenin dosyaları: kök, kök+b/bd, kök+i, kök+z/bi.
    private static readonly Dictionary<string, string[]> Files = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Arial"] = ["arial", "arialbd", "ariali", "arialbi"], ["Arial Narrow"] = ["arialn", "arialnb", "arialni", "arialnbi"],
        ["Calibri"] = ["calibri", "calibrib", "calibrii", "calibriz"], ["Calibri Light"] = ["calibril", "calibrib", "calibrili", "calibriz"],
        ["Times New Roman"] = ["times", "timesbd", "timesi", "timesbi"], ["Verdana"] = ["verdana", "verdanab", "verdanai", "verdanaz"],
        ["Tahoma"] = ["tahoma", "tahomabd", "tahoma", "tahomabd"], ["Segoe UI"] = ["segoeui", "segoeuib", "segoeuii", "segoeuiz"],
        ["Comic Sans MS"] = ["comic", "comicbd", "comici", "comicz"], ["Georgia"] = ["georgia", "georgiab", "georgiai", "georgiaz"],
        ["Trebuchet MS"] = ["trebuc", "trebucbd", "trebucit", "trebucbi"], ["Courier New"] = ["cour", "courbd", "couri", "courbi"],
        ["Candara"] = ["Candara", "Candarab", "Candarai", "Candaraz"], ["Corbel"] = ["corbel", "corbelb", "corbeli", "corbelz"],
        ["Century Gothic"] = ["GOTHIC", "GOTHICB", "GOTHICI", "GOTHICBI"], ["Book Antiqua"] = ["BKANT", "ANTQUAB", "ANTQUAI", "ANTQUABI"],
        ["Garamond"] = ["GARA", "GARABD", "GARAIT", "GARABD"], ["Impact"] = ["impact", "impact", "impact", "impact"],
        ["Franklin Gothic"] = ["FRAMD", "FRADM", "FRAMDIT", "FRADMIT"], ["Palatino Linotype"] = ["pala", "palab", "palai", "palabi"],
        ["Lucida Console"] = ["lucon", "lucon", "lucon", "lucon"], ["Consolas"] = ["consola", "consolab", "consolai", "consolaz"],
        ["Bahnschrift"] = ["bahnschrift", "bahnschrift", "bahnschrift", "bahnschrift"], ["Cambria"] = ["cambria", "cambriab", "cambriai", "cambriaz"],
    };

    private static readonly string[] FontFolders =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts"),
    ];

    internal static string? FindFontFile(string family, bool bold, bool italic)
    {
        if (string.IsNullOrWhiteSpace(family)) return null;
        var index = (bold, italic) switch { (true, true) => 3, (true, false) => 1, (false, true) => 2, _ => 0 };
        var known = Files.FirstOrDefault(k => k.Key.Replace(" ", "").Equals(family.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)).Value;
        if (known is not null)
        {
            foreach (var candidate in new[] { known[index], known[0] })
                foreach (var folder in FontFolders)
                {
                    var path = Path.Combine(folder, candidate + ".ttf");
                    if (File.Exists(path)) return path;
                }
        }
        // Bilinmeyen aile: dosya adı aileyle başlayan .ttf'ler arasında stile göre seç.
        var stem = family.Replace(" ", "").ToLowerInvariant();
        foreach (var folder in FontFolders)
        {
            if (!Directory.Exists(folder)) continue;
            string[] files;
            try { files = Directory.GetFiles(folder, "*.ttf"); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            var matches = files.Where(f => Path.GetFileNameWithoutExtension(f).Replace(" ", "").Replace("-", "").ToLowerInvariant().StartsWith(stem)).ToList();
            if (matches.Count == 0) continue;
            string Style(string f) => Path.GetFileNameWithoutExtension(f).Replace(" ", "").Replace("-", "").ToLowerInvariant()[stem.Length..];
            bool IsBold(string st) => st.Contains("bold") || st is "b" or "bd" or "z" or "bi"; bool IsItalic(string st) => st.Contains("italic") || st.Contains("oblique") || st is "i" or "it" or "z" or "bi";
            return matches.FirstOrDefault(f => IsBold(Style(f)) == bold && IsItalic(Style(f)) == italic) ?? matches.FirstOrDefault(f => Style(f) is "" or "regular") ?? matches[0];
        }
        return null;
    }

    private static PdfContentBounds Region(TextLine line) => new(line.RegionLeft, line.RegionBottom, line.RegionRight, line.RegionTop);

    // ----- Paragraf düzenleme -----

    public static List<Paragraph> Paragraphs(byte[] document, int pageIndex, double pageHeight)
    {
        try
        {
            var candidates = PdfParagraphEditor.FindParagraphs(KillerPdf.Engine.Documents.PdfDocument.Open(document), pageIndex);
            return candidates.Select(c => new Paragraph(c.Text, c.SourceRegion.Left, pageHeight - c.SourceRegion.Top, c.SourceRegion.Right - c.SourceRegion.Left, c.SourceRegion.Top - c.SourceRegion.Bottom,
                c.FontName, c.FontSize, c.Leading, c.SourceRegion.Left, c.SourceRegion.Bottom, c.SourceRegion.Right, c.SourceRegion.Top)).ToList();
        }
        catch (Exception error) when (error is FormatException or InvalidOperationException or NotSupportedException or ArgumentException or IOException) { return []; }
    }

    /// Paragrafı yeni metinle, aynı kutuda yeniden akıtır. Sığmazsa yazı boyutunu kademeli küçültür (en çok %30).
    public static RewriteResult ReplaceParagraph(byte[] document, int pageIndex, Paragraph paragraph, string newText)
    {
        var (family, bold, italic) = Parse(paragraph.FontName, false);
        var font = SubstituteFor(paragraph.FontName, bold);
        var region = new PdfContentBounds(paragraph.RegionLeft, paragraph.RegionBottom, paragraph.RegionRight, paragraph.RegionTop);
        var leadingRatio = paragraph.FontSize > 0 ? paragraph.Leading / paragraph.FontSize : 1.2;
        string? overflow = null;
        try
        {
            for (var scale = 1.0; scale >= 0.69; scale -= 0.05)
            {
                var size = paragraph.FontSize * scale;
                var pdf = KillerPdf.Engine.Documents.PdfDocument.Open(document);
                var plan = PdfParagraphEditor.Plan(pdf, pageIndex, region, region, newText, font, size, Math.Max(size * leadingRatio, size * 1.05), PdfParagraphAlignment.Left);
                if (plan.HasOverflow) { overflow = plan.OverflowReason; continue; }
                return new RewriteResult(PdfParagraphEditor.Apply(pdf, plan), true, scale < 1 ? size : null);
            }
            throw new UserFacingException("Yeni metin paragrafın kutusuna sığmıyor (yazı %30 küçültülse bile). Metni kısaltın." + (overflow is null ? "" : "\n" + overflow));
        }
        catch (NotSupportedException error) { throw new UserFacingException("Bu paragraf yerinde değiştirilemedi: " + error.Message, error); }
        catch (Exception error) when (error is FormatException or InvalidOperationException or ArgumentException) { throw new UserFacingException("Paragraf değiştirilemedi: " + error.Message, error); }
    }

    // ----- Tüm belgede bul ve değiştir -----

    public static ReplaceAllResult ReplaceAll(byte[] document, string find, string replace, bool caseSensitive, IProgress<OperationProgress>? progress, CancellationToken token)
    {
        if (string.IsNullOrEmpty(find)) throw new UserFacingException("Aranacak yazıyı girin.");
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.CurrentCultureIgnoreCase;
        int pageCount;
        using (var pdf = PigDocument.Open(document)) pageCount = pdf.NumberOfPages;
        var current = document; int replaced = 0, failed = 0, pagesHit = 0;
        for (var page = 0; page < pageCount; page++)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(new((double)page / pageCount, $"Sayfa {page + 1}/{pageCount} taranıyor…"));
            var lines = Lines(current, page).Where(l => l.Text.Contains(find, comparison)).ToList();
            if (lines.Count == 0) continue;
            pagesHit++;
            foreach (var line in lines)
            {
                token.ThrowIfCancellationRequested();
                var text = line.Text.Replace(find, replace, comparison);
                if (text == line.Text) continue;
                try { current = Replace(current, page, line, text).Document; replaced++; }
                catch (UserFacingException) { failed++; }
            }
        }
        return new ReplaceAllResult(current, replaced, failed, pagesHit);
    }
}
