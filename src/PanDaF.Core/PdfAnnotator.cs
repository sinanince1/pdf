using PdfSharp.Drawing;
using PdfSharp.Drawing.Layout;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PanDaF.Core;

public enum MarkKind { Text, Whiteout, Highlight, Ink, Image }

/// PDF üzerine eklenen bir öğe. Konumlar PDF noktası (1/72 inç) cinsinden, sayfanın sol üst köşesinden.
public sealed class Mark
{
    public MarkKind Kind { get; set; }
    public int Page { get; set; }                 // 0'dan başlar
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string Text { get; set; } = "";
    public double FontSize { get; set; } = 14;
    public bool Bold { get; set; }
    public string Color { get; set; } = "#000000"; // yazı, kalem; vurgu rengi
    public double StrokeWidth { get; set; } = 2;
    public List<(double X, double Y)> Points { get; set; } = [];  // kalem
    public byte[]? ImagePng { get; set; }

    public Mark Clone() => new()
    {
        Kind = Kind, Page = Page, X = X, Y = Y, Width = Width, Height = Height, Text = Text, FontSize = FontSize, Bold = Bold,
        Color = Color, StrokeWidth = StrokeWidth, Points = [.. Points], ImagePng = ImagePng,
    };
}

/// İşaretleri PDF'ye işler: asıl sayfa içeriği olduğu gibi kalır, eklenenler üstüne çizilir.
public static class PdfAnnotator
{
    /// Düzenlenen PDF'yi hedef klasöre "Ad (düzenlendi).pdf" olarak yazar; yarım dosya bırakmaz.
    public static string SaveNextTo(string input, string destination, IReadOnlyList<Mark> marks, CancellationToken token = default, string? originalName = null)
    {
        using var tx = new ExportTransaction(destination);
        var staged = Path.Combine(tx.StagingDirectory, SafePaths.CleanName(SafePaths.Stem(originalName ?? input)) + " (düzenlendi).pdf");
        Save(input, staged, marks, token);
        return tx.Publish([staged], token)[0];
    }

    public static void Save(string input, string output, IReadOnlyList<Mark> marks, CancellationToken token = default)
    {
        WindowsFontResolver.Install();
        if (marks.Count == 0) { File.Copy(input, output, true); return; }
        PdfDocument document;
        try { document = PdfReader.Open(input, PdfDocumentOpenMode.Modify); }
        catch (Exception error) when (error is not OperationCanceledException) { throw new UserFacingException(PdfPages.Friendly(input, error), error); }
        using (document)
        {
            var keep = new List<IDisposable>();
            try
            {
                foreach (var group in marks.GroupBy(m => m.Page))
                {
                    token.ThrowIfCancellationRequested();
                    if (group.Key < 0 || group.Key >= document.PageCount) continue;
                    var page = document.Pages[group.Key];
                    using var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append, XGraphicsUnit.Point, XPageDirection.Downwards);
                    foreach (var mark in group) Draw(graphics, mark, keep);
                }
                document.Save(output);
            }
            finally { foreach (var item in keep) item.Dispose(); }
        }
    }

    private static void Draw(XGraphics g, Mark mark, List<IDisposable> keep)
    {
        var color = ParseColor(mark.Color);
        switch (mark.Kind)
        {
            case MarkKind.Whiteout:
                g.DrawRectangle(new XSolidBrush(string.IsNullOrEmpty(mark.Color) || mark.Color == "#000000" ? XColors.White : color), mark.X, mark.Y, mark.Width, mark.Height);
                break;
            case MarkKind.Highlight:
                g.DrawRectangle(new XSolidBrush(XColor.FromArgb(90, color.R, color.G, color.B)), mark.X, mark.Y, mark.Width, mark.Height);
                break;
            case MarkKind.Ink:
                if (mark.Points.Count == 1) mark.Points.Add(mark.Points[0]);
                if (mark.Points.Count >= 2)
                {
                    var pen = new XPen(color, mark.StrokeWidth) { LineCap = XLineCap.Round, LineJoin = XLineJoin.Round };
                    g.DrawLines(pen, mark.Points.Select(p => new XPoint(mark.X + p.X, mark.Y + p.Y)).ToArray());
                }
                break;
            case MarkKind.Text:
                if (string.IsNullOrWhiteSpace(mark.Text)) break;
                var font = new XFont("Arial", mark.FontSize, mark.Bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);
                var formatter = new XTextFormatter(g) { Alignment = XParagraphAlignment.Left };
                // Metin kutusu kısaysa taşan satırlar kesilmesin: yükseklik gerektiği kadar uzar.
                var rect = new XRect(mark.X + 2, mark.Y + 2, Math.Max(10, mark.Width - 4), Math.Max(mark.Height, mark.FontSize * 1.3 * (mark.Text.Count(c => c == '\n') + 1) + 4));
                formatter.DrawString(mark.Text, font, new XSolidBrush(color), rect, XStringFormats.TopLeft);
                break;
            case MarkKind.Image:
                if (mark.ImagePng is null) break;
                var stream = new MemoryStream(mark.ImagePng); keep.Add(stream);
                var image = XImage.FromStream(stream); keep.Add(image);
                g.DrawImage(image, mark.X, mark.Y, mark.Width, mark.Height);
                break;
        }
    }

    public static XColor ParseColor(string hex)
    {
        hex = (hex ?? "").TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var value)) return XColors.Black;
        return XColor.FromArgb((value >> 16) & 255, (value >> 8) & 255, value & 255);
    }
}

/// PDFsharp'ın Core sürümü yazı tiplerini kendisi bulamaz; Windows'un Fonts klasöründen Arial
/// (yoksa Segoe UI) okunur. Türkçe karakterler her ikisinde de vardır.
public sealed class WindowsFontResolver : IFontResolver
{
    private static readonly object gate = new();
    private static bool installed;
    private static readonly string Fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    public static void Install()
    {
        lock (gate)
        {
            if (installed) return;
            try { GlobalFontSettings.FontResolver ??= new WindowsFontResolver(); }
            catch (InvalidOperationException) { } // bir yazı tipi zaten kullanıldıysa değiştirilemez; mevcut çözümleyici kalır
            installed = true;
        }
    }

    /// Arial (yoksa Segoe UI) dosyasının baytları; yerinde yazı değiştirmede yedek yazı tipi olarak kullanılır.
    public static byte[] FontBytes(bool bold) => new WindowsFontResolver().GetFont(bold ? "pandaf-b" : "pandaf-r")!;

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        var face = (bold, italic) switch { (true, true) => "bi", (true, false) => "b", (false, true) => "i", _ => "r" };
        return new FontResolverInfo("pandaf-" + face);
    }

    public byte[]? GetFont(string faceName)
    {
        var face = faceName.Replace("pandaf-", "");
        string[] candidates = face switch
        {
            "bi" => ["arialbi.ttf", "segoeuiz.ttf", "arialbd.ttf", "arial.ttf", "segoeui.ttf"],
            "b" => ["arialbd.ttf", "segoeuib.ttf", "arial.ttf", "segoeui.ttf"],
            "i" => ["ariali.ttf", "segoeuii.ttf", "arial.ttf", "segoeui.ttf"],
            _ => ["arial.ttf", "segoeui.ttf", "calibri.ttf", "tahoma.ttf"],
        };
        foreach (var file in candidates)
        {
            var path = Path.Combine(Fonts, file);
            if (File.Exists(path)) return File.ReadAllBytes(path);
        }
        throw new UserFacingException("Yazı tipi bulunamadı: Windows'ta Arial veya Segoe UI yüklü olmalı.");
    }
}
