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
    double RegionLeft, double RegionBottom, double RegionRight, double RegionTop)
{
    public List<TextLine> Lines { get; init; } = [];
}

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

    // ----- Paragraf düzenleme: satırları gruplayıp yeni metni yeniden akıt, her satırı satır motoruyla değiştir -----

    /// Aynı sol kenar, aynı yazı tipi/boyut ve düzenli satır aralığı olan ardışık satırlar bir paragraf sayılır (en az 2 satır).
    public static List<Paragraph> Paragraphs(byte[] document, int pageIndex, double pageHeight)
    {
        var all = Lines(document, pageIndex).OrderBy(l => l.Y).ToList();
        var result = new List<Paragraph>();
        var group = new List<TextLine>();
        void Flush()
        {
            if (group.Count >= 2)
            {
                var left = group.Min(l => l.X); var top = group.Min(l => l.Y); var right = group.Max(l => l.X + l.Width); var bottom = group.Max(l => l.Y + l.Height);
                var first = group[0];
                var leading = group.Count > 1 ? (group[^1].Y - group[0].Y) / (group.Count - 1) : first.FontSize * 1.2;
                result.Add(new Paragraph(string.Join(" ", group.Select(l => l.Text)), left, top, right - left, bottom - top, first.FontName, first.FontSize, leading,
                    left, 0, right, 0) { Lines = [.. group] });
            }
            group.Clear();
        }
        foreach (var line in all)
        {
            if (group.Count > 0)
            {
                var previous = group[^1];
                var gap = line.Y - previous.Y;
                // Yazı tipi aile düzeyinde karşılaştırılır: alt küme öneki (ABCDEF+) ve yedek yazı tipi adı farkı paragrafı bölmesin.
                if (Math.Abs(previous.X - line.X) > 2 || gap < previous.FontSize * .8 || gap > previous.FontSize * 1.9
                    || !string.Equals(Parse(previous.FontName, false).Family, Parse(line.FontName, false).Family, StringComparison.OrdinalIgnoreCase)
                    || Math.Abs(previous.FontSize - line.FontSize) > .6) Flush();
            }
            group.Add(line);
        }
        Flush();
        return result;
    }

    /// Yeni metni paragrafın genişliğine göre satırlara böler ve her satırı yerinde değiştirir.
    /// Satır sayısı yetmezse yazıyı kademeli küçültür (en çok %30); yine sığmazsa hata verir.
    public static RewriteResult ReplaceParagraph(byte[] document, int pageIndex, Paragraph paragraph, string newText)
    {
        if (paragraph.Lines.Count == 0) throw new UserFacingException("Paragraf satırları bulunamadı.");
        var (family, bold, italic) = Parse(paragraph.FontName, false);
        var measure = Measurer.For(FindFontFile(family, bold, italic) ?? FindFontFile("Arial", bold, italic));
        var width = Math.Max(paragraph.Lines.Max(l => l.Width), 20) * 1.01;
        var words = newText.Replace("\r\n", "\n").Replace('\n', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var scale = 1.0; scale >= 0.69; scale -= 0.05)
        {
            var size = paragraph.FontSize * scale;
            var wrapped = Wrap(words, w => measure(w, size), measure(" ", size), width);
            if (wrapped.Count > paragraph.Lines.Count) continue;
            var current = document; var substituted = false;
            for (var i = 0; i < paragraph.Lines.Count; i++)
            {
                var text = i < wrapped.Count ? wrapped[i] : " ";
                var result = Replace(current, pageIndex, paragraph.Lines[i], text, scale < 1 ? size : null);
                current = result.Document; substituted |= result.UsedSubstituteFont;
            }
            return new RewriteResult(current, substituted, scale < 1 ? size : null);
        }
        throw new UserFacingException($"Yeni metin paragrafın {paragraph.Lines.Count} satırına sığmıyor (yazı %30 küçültülse bile). Metni kısaltın.");
    }

    private static List<string> Wrap(string[] words, Func<string, double> widthOf, double space, double maxWidth)
    {
        var lines = new List<string>(); var line = ""; var lineWidth = 0.0;
        foreach (var word in words)
        {
            var w = widthOf(word);
            if (line.Length == 0) { line = word; lineWidth = w; continue; }
            if (lineWidth + space + w <= maxWidth) { line += " " + word; lineWidth += space + w; }
            else { lines.Add(line); line = word; lineWidth = w; }
        }
        if (line.Length > 0) lines.Add(line);
        return lines;
    }

    /// Yazı tipi dosyasından harf genişliği ölçer (WPF GlyphTypeface). Dosya yoksa ortalama genişlik kullanır.
    private static class Measurer
    {
        private static readonly Dictionary<string, Func<string, double, double>> cache = new(StringComparer.OrdinalIgnoreCase);
        public static Func<string, double, double> For(string? fontFile)
        {
            var key = fontFile ?? "";
            lock (cache)
            {
                if (cache.TryGetValue(key, out var found)) return found;
                Func<string, double, double> fn;
                try
                {
                    if (fontFile is null) throw new FileNotFoundException();
                    var typeface = new System.Windows.Media.GlyphTypeface(new Uri(fontFile));
                    fn = (text, size) =>
                    {
                        var total = 0.0;
                        foreach (var ch in text) total += typeface.CharacterToGlyphMap.TryGetValue(ch, out var glyph) ? typeface.AdvanceWidths[glyph] : .55;
                        return total * size;
                    };
                }
                catch (Exception error) when (error is IOException or FileNotFoundException or ArgumentException or UriFormatException or NotSupportedException)
                { fn = (text, size) => text.Length * .52 * size; }
                return cache[key] = fn;
            }
        }
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
