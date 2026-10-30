using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace PanDaF.Core;

/// Sayfadaki bir metin satırı: UI için sol üst köşeden konum (nokta), motor için bölge.
public sealed record TextLine(string Text, double X, double Y, double Width, double Height, double FontSize, bool Bold,
    double RegionLeft, double RegionBottom, double RegionRight, double RegionTop);

public sealed record RewriteResult(byte[] Document, bool UsedSubstituteFont);

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
            left - crop.Left - .5, bottom - crop.Bottom - .5, right - crop.Left + .5, top - crop.Bottom + .5);
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
            var edit = new PdfTextLineEdit(pageIndex, Region(line), newText) { Substitute = line.Bold ? BoldFont.Value : Regular.Value, FontSize = fontSize };
            var result = PdfTextLineEditor.Replace(KillerPdf.Engine.Documents.PdfDocument.Open(document), edit);
            return new RewriteResult(result.Document, result.Font == PdfTextLineFont.Substitute);
        }
        catch (NotSupportedException error) { throw new UserFacingException("Bu satır yerinde değiştirilemedi: " + error.Message, error); }
        catch (Exception error) when (error is FormatException or InvalidOperationException or ArgumentException)
        { throw new UserFacingException("Yazı değiştirilemedi: " + error.Message, error); }
    }

    private static PdfContentBounds Region(TextLine line) => new(line.RegionLeft, line.RegionBottom, line.RegionRight, line.RegionTop);
}
