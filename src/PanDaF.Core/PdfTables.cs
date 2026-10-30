using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace PanDaF.Core;

/// PDF sayfalarındaki tablo benzeri düzeni sözcük konumlarından çıkarır ve Excel (.xlsx) yazar.
/// Her sayfa bir çalışma sayfası olur. Ek program gerektirmez. Sayılar sayı olarak yazılır.
public static class PdfTables
{
    private sealed record Cell(double Left, double Right, string Text);

    public static void ToXlsx(string input, string output, CancellationToken token)
    {
        List<List<List<string>>> sheets;
        try
        {
            using var pdf = PdfDocument.Open(input);
            sheets = [];
            foreach (var page in pdf.GetPages())
            {
                token.ThrowIfCancellationRequested();
                sheets.Add(ExtractRows(page));
                if (sheets.Count >= 200) break;
            }
        }
        catch (Exception error) when (error is not (OperationCanceledException or UserFacingException))
        {
            throw new UserFacingException(PdfPages.Friendly(input, error), error);
        }
        if (sheets.All(s => s.Count == 0))
            throw new UserFacingException("Bu PDF'de seçilebilir metin bulunamadı. Taranmış bir belgeyse önce “Word'e çevir” ile yazıyı tanıtıp sonra Excel'e aktarabilirsiniz.");
        WriteXlsx(output, sheets, token);
    }

    /// Satırlar: aynı taban çizgisine yakın sözcükler. Sütunlar: sayfa genelinde sözcüklerin hiç bulunmadığı
    /// dikey boşluk şeritleri sınır alınır; böylece hücreler satırdan satıra aynı sütuna düşer.
    internal static List<List<string>> ExtractRows(Page page)
    {
        var words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        if (words.Count == 0) return [];
        var lines = new List<List<Word>>();
        foreach (var word in words.OrderByDescending(w => w.BoundingBox.Bottom))
        {
            var height = Math.Max(2, word.BoundingBox.Height);
            var line = lines.LastOrDefault(l => Math.Abs(l[0].BoundingBox.Bottom - word.BoundingBox.Bottom) < height * .5);
            if (line is null) lines.Add([word]); else line.Add(word);
        }
        // Satır içinde yakın sözcükleri birleştir (boşluk ≤ 1 karakter genişliği).
        var rows = new List<List<Cell>>();
        foreach (var line in lines)
        {
            var cells = new List<Cell>();
            foreach (var word in line.OrderBy(w => w.BoundingBox.Left))
            {
                var box = word.BoundingBox;
                var average = word.Letters.Count > 0 ? box.Width / word.Letters.Count : 5;
                if (cells.Count > 0 && box.Left - cells[^1].Right <= Math.Max(3, average * 1.6))
                    cells[^1] = new Cell(cells[^1].Left, box.Right, cells[^1].Text + " " + word.Text);
                else cells.Add(new Cell(box.Left, box.Right, word.Text));
            }
            rows.Add(cells);
        }
        // Sütun sınırları: yatay eksende hiçbir hücrenin kaplamadığı boşluklar.
        var spans = rows.SelectMany(r => r).Select(c => (c.Left, c.Right)).OrderBy(s => s.Left).ToList();
        var boundaries = new List<double>();
        var reach = spans[0].Right;
        foreach (var span in spans.Skip(1))
        {
            if (span.Left > reach + 4) boundaries.Add((reach + span.Left) / 2);
            reach = Math.Max(reach, span.Right);
        }
        var columns = boundaries.Count + 1;
        var result = new List<List<string>>();
        foreach (var row in rows)
        {
            var values = new string[columns];
            foreach (var cell in row)
            {
                var index = boundaries.Count(b => b < (cell.Left + cell.Right) / 2);
                values[index] = string.IsNullOrEmpty(values[index]) ? cell.Text : values[index] + " " + cell.Text;
            }
            result.Add(values.Select(v => v ?? "").ToList());
        }
        // Tamamen boş sütunları at.
        for (var c = columns - 1; c >= 0; c--)
            if (result.All(r => r[c].Length == 0)) foreach (var r in result) r.RemoveAt(c);
        return result;
    }

    private static void WriteXlsx(string output, List<List<List<string>>> sheets, CancellationToken token)
    {
        using var zip = ZipFile.Open(output, ZipArchiveMode.Create);
        void Add(string name, string content)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        var types = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
        var workbook = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
        var rels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rStyles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
        var number = 0;
        for (var s = 0; s < sheets.Count; s++)
        {
            token.ThrowIfCancellationRequested();
            if (sheets[s].Count == 0 && sheets.Count > 1) continue;
            number++;
            types.Append($"<Override PartName=\"/xl/worksheets/sheet{number}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            workbook.Append($"<sheet name=\"Sayfa {s + 1}\" sheetId=\"{number}\" r:id=\"rId{number}\"/>");
            rels.Append($"<Relationship Id=\"rId{number}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{number}.xml\"/>");
            Add($"xl/worksheets/sheet{number}.xml", Sheet(sheets[s]));
        }
        types.Append("</Types>"); workbook.Append("</sheets></workbook>"); rels.Append("</Relationships>");
        Add("[Content_Types].xml", types.ToString());
        Add("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        Add("xl/workbook.xml", workbook.ToString());
        Add("xl/_rels/workbook.xml.rels", rels.ToString());
        Add("xl/styles.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs></styleSheet>");
    }

    private static string Sheet(List<List<string>> rows)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        var columns = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        if (columns > 0)
        {
            sb.Append("<cols>");
            for (var c = 0; c < columns; c++)
            {
                var width = Math.Clamp(rows.Max(r => c < r.Count ? r[c].Length : 0) * 1.1 + 2, 8, 60);
                sb.Append($"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{width.ToString("0.#", CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
            }
            sb.Append("</cols>");
        }
        sb.Append("<sheetData>");
        for (var r = 0; r < rows.Count; r++)
        {
            sb.Append($"<row r=\"{r + 1}\">");
            for (var c = 0; c < rows[r].Count; c++)
            {
                var text = rows[r][c];
                if (text.Length == 0) continue;
                var reference = Column(c) + (r + 1);
                if (TryNumber(text, out var value))
                    sb.Append($"<c r=\"{reference}\"><v>{value.ToString(CultureInfo.InvariantCulture)}</v></c>");
                else
                    sb.Append($"<c r=\"{reference}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{WebUtility.HtmlEncode(Clean(text))}</t></is></c>");
            }
            sb.Append("</row>");
        }
        return sb.Append("</sheetData></worksheet>").ToString();
    }

    // "1.234,56" ve "1,234.56" gibi yazımları sayıya çevirir; tarih/numara benzeri metinleri (01.05.2026, 0312…) bırakır.
    private static bool TryNumber(string text, out double value)
    {
        value = 0;
        var t = text.Trim().Replace("%", "").Replace("₺", "").Replace("TL", "").Trim();
        if (t.Length == 0 || t.Length > 15 || t.Count(ch => ch == '.' || ch == ',') > 2) return false;
        if (t.Count(ch => ch == '.') >= 2 && !t.Contains(',')) return false; // tarih: 01.05.2026
        if (t.StartsWith('0') && t.Length > 1 && char.IsDigit(t[1])) return false; // telefon, okul no
        if (double.TryParse(t, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.GetCultureInfo("tr-TR"), out value)) return double.IsFinite(value);
        return double.TryParse(t, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    private static string Clean(string text) => new(text.Where(ch => ch >= ' ' || ch == '\t').ToArray());

    private static string Column(int index)
    {
        var name = "";
        for (var n = index; n >= 0; n = n / 26 - 1) name = (char)('A' + n % 26) + name;
        return name;
    }
}
