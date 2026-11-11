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
    public byte[]? ImagePng { get; set; }           // resim (PNG veya JPEG baytları)
    public string FontFamily { get; set; } = "";    // yazı: boşsa Arial
    public bool Italic { get; set; }
    /// Sayfadan alınıp taşınan bir öğenin asıl yerini örten kutu: seçilemez, taşınamaz.
    public bool Cover { get; set; }

    public Mark Clone() => new()
    {
        Kind = Kind, Page = Page, X = X, Y = Y, Width = Width, Height = Height, Text = Text, FontSize = FontSize, Bold = Bold,
        Color = Color, StrokeWidth = StrokeWidth, Points = [.. Points], ImagePng = ImagePng, FontFamily = FontFamily, Italic = Italic, Cover = Cover,
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
                var font = FontOf(mark.FontFamily, mark.FontSize, mark.Bold, mark.Italic);
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

    private static XFont FontOf(string family, double size, bool bold, bool italic) =>
        new(string.IsNullOrWhiteSpace(family) ? "Arial" : family, Math.Max(1, size), (bold, italic) switch { (true, true) => XFontStyleEx.BoldItalic, (true, false) => XFontStyleEx.Bold, (false, true) => XFontStyleEx.Italic, _ => XFontStyleEx.Regular });

    /// Yazı kutusunun üst kenarından ilk satırın taban çizgisine uzaklık ve satır yüksekliği (nokta).
    /// Sayfadan alınan bir satırı aynı taban çizgisine oturtmak ve önizlemeyi kayıtla hizalamak için.
    public static (double Ascent, double LineHeight) TextMetrics(string family, double size, bool bold, bool italic)
    {
        WindowsFontResolver.Install();
        try
        {
            var font = FontOf(family, size, bold, italic);
            var line = font.GetHeight();
            return (line * font.CellAscent / Math.Max(1, font.CellSpace), line);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or UserFacingException or IOException) { return (size * .93, size * 1.15); }
    }

    public static XColor ParseColor(string hex)
    {
        hex = (hex ?? "").TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var value)) return XColors.Black;
        return XColor.FromArgb((value >> 16) & 255, (value >> 8) & 255, value & 255);
    }
}

/// PDFsharp'ın Core sürümü yazı tiplerini kendisi bulamaz; işletim sisteminin yazı tipi klasörlerinden
/// Arial (yoksa Helvetica/Liberation Sans/DejaVu Sans) okunur. Türkçe karakterler hepsinde vardır.
public sealed class WindowsFontResolver : IFontResolver
{
    private static readonly object gate = new();
    private static bool installed;
    /// Platformun yazı tipi klasörleri (sistem + kullanıcı).
    public static readonly string[] FontFolders = Folders();
    private static string[] Folders()
    {
        var list = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            list.Add(Environment.GetFolderPath(Environment.SpecialFolder.Fonts));
            list.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            list.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Fonts"));
            list.Add("/Library/Fonts"); list.Add("/Library/Fonts/Microsoft"); list.Add("/System/Library/Fonts/Supplemental"); list.Add("/System/Library/Fonts");
            list.Add("/Applications/Microsoft Word.app/Contents/Resources/DFonts"); list.Add("/Applications/LibreOffice.app/Contents/Resources/fonts/truetype");
        }
        else
        {
            list.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fonts"));
            list.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "fonts"));
            foreach (var root in new[] { "/usr/share/fonts", "/usr/local/share/fonts" })
                if (Directory.Exists(root)) { list.Add(root); try { list.AddRange(Directory.GetDirectories(root, "*", SearchOption.AllDirectories)); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
        }
        return list.Where(Directory.Exists).Distinct().ToArray();
    }

    /// Bir yazı tipi dosyasını klasörlerde arar (adlar sırayla denenir).
    public static string? Locate(params string[] fileNames)
    {
        foreach (var name in fileNames)
            foreach (var folder in FontFolders)
            {
                var path = Path.Combine(folder, name);
                if (File.Exists(path)) return path;
            }
        return null;
    }

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

    private const string FilePrefix = "pandaf-dosya:";

    /// Arial dışındaki bir aile istenirse ve bilgisayarda kuruluysa o dosya kullanılır; yoksa Arial (ve yedekleri).
    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        var family = TextRewriter.Parse(familyName, false).Family;
        if (family.Length > 0 && !family.Equals("Arial", StringComparison.OrdinalIgnoreCase))
        {
            var file = TextRewriter.FindFontFile(family, bold, italic);
            if (file is not null)
            {
                // Kalın/eğik dosyası yoksa düz dosya döner; o durumda PDFsharp kalını/eğiği kendisi üretir.
                var regular = bold || italic ? TextRewriter.FindFontFile(family, false, false) : null;
                var same = regular is not null && string.Equals(regular, file, StringComparison.OrdinalIgnoreCase);
                return new FontResolverInfo(FilePrefix + file, same && bold, same && italic);
            }
        }
        var face = (bold, italic) switch { (true, true) => "bi", (true, false) => "b", (false, true) => "i", _ => "r" };
        return new FontResolverInfo("pandaf-" + face);
    }

    public byte[]? GetFont(string faceName)
    {
        if (faceName.StartsWith(FilePrefix, StringComparison.Ordinal))
        {
            try { return FontFiles.Read(faceName[FilePrefix.Length..]); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return GetFont("pandaf-r"); }
        }
        var face = faceName.Replace("pandaf-", "");
        string[] candidates = face switch
        {
            "bi" => ["arialbi.ttf", "Arial Bold Italic.ttf", "LiberationSans-BoldItalic.ttf", "DejaVuSans-BoldOblique.ttf", "segoeuiz.ttf", "arialbd.ttf", "Arial Bold.ttf", "arial.ttf", "Arial.ttf"],
            "b" => ["arialbd.ttf", "Arial Bold.ttf", "LiberationSans-Bold.ttf", "DejaVuSans-Bold.ttf", "segoeuib.ttf", "arial.ttf", "Arial.ttf"],
            "i" => ["ariali.ttf", "Arial Italic.ttf", "LiberationSans-Italic.ttf", "DejaVuSans-Oblique.ttf", "segoeuii.ttf", "arial.ttf", "Arial.ttf"],
            _ => ["arial.ttf", "Arial.ttf", "LiberationSans-Regular.ttf", "DejaVuSans.ttf", "segoeui.ttf", "Helvetica.ttf", "calibri.ttf", "tahoma.ttf"],
        };
        var found = Locate(candidates);
        if (found is not null) return File.ReadAllBytes(found);
        throw new UserFacingException("Yazı tipi bulunamadı: bilgisayarda Arial (veya Liberation Sans / DejaVu Sans) yüklü olmalı.");
    }
}

/// Yazı tipi dosyası okur. Koleksiyon (.ttc; ör. Windows'taki Cambria) içindeki yüz, PDFsharp'ın ve
/// yerinde düzenleme motorunun okuyabildiği tek başına bir TrueType dosyasına çevrilir.
public static class FontFiles
{
    public static byte[] Read(string path, int face = 0)
    {
        var data = File.ReadAllBytes(path);
        return IsCollection(data) ? Extract(data, face) : data;
    }

    public static bool IsCollection(byte[] data) => data.Length > 16 && data[0] == (byte)'t' && data[1] == (byte)'t' && data[2] == (byte)'c' && data[3] == (byte)'f';

    public static byte[] Extract(byte[] collection, int face)
    {
        uint U32(int at) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(collection.AsSpan(at, 4));
        int U16(int at) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(collection.AsSpan(at, 2));
        var count = (int)U32(8);
        if (count < 1) throw new FormatException("Yazı tipi koleksiyonu boş.");
        if (face < 0 || face >= count) face = 0;
        var start = checked((int)U32(12 + face * 4));
        var tables = U16(start + 4);
        var result = new MemoryStream();
        result.Write(collection, start, 12);
        var offset = 12 + tables * 16;
        var records = new List<(int Offset, int Length)>();
        Span<byte> number = stackalloc byte[4];
        for (var i = 0; i < tables; i++)
        {
            var record = start + 12 + i * 16;
            var length = checked((int)U32(record + 12));
            records.Add((checked((int)U32(record + 8)), length));
            result.Write(collection, record, 8);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(number, (uint)offset); result.Write(number);
            result.Write(collection, record + 12, 4);
            offset += (length + 3) & ~3;
        }
        foreach (var (at, length) in records)
        {
            result.Write(collection, at, length);
            for (var pad = (4 - length % 4) % 4; pad > 0; pad--) result.WriteByte(0);
        }
        return result.ToArray();
    }
}
