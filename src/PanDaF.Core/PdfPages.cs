// Adapted from Nashi's DocumentEngine (https://github.com/orkunisitmak/nashi-app),
// Copyright (c) 2026 Orkun Işıtmak and Nashi contributors, MIT License.
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PanDaF.Core;

public enum SplitMode
{
    /// Her sayfa ayrı bir PDF olur.
    EachPage,
    /// Her N sayfa bir PDF olur.
    EveryN,
    /// Seçilen sayfalar tek bir PDF olarak çıkarılır.
    Extract,
}

/// Birleştirme, bölme, sayfa silme / sıralama / döndürme. Asıl dosyalar hiç değiştirilmez.
public static class PdfPages
{
    public static int PageCount(string path)
    {
        using var pdf = Open(path);
        return pdf.PageCount;
    }

    public static string Merge(IReadOnlyList<string> inputs, string destination, bool allowLossy = false, CancellationToken token = default)
    {
        if (inputs.Count < 2) throw new UserFacingException("Birleştirmek için en az iki PDF seçin.");
        using var tx = new ExportTransaction(destination);
        using var merged = new PdfDocument();
        foreach (var file in inputs)
        {
            token.ThrowIfCancellationRequested();
            using var source = Open(file);
            if (!allowLossy) RequireRebuildable(source, file);
            foreach (var page in source.Pages) merged.AddPage(page);
        }
        var output = Path.Combine(tx.StagingDirectory, "Birleştirilmiş PDF.pdf");
        merged.Save(output);
        return tx.Publish([output], token)[0];
    }

    /// EachPage ve EveryN bir klasör ("Ad - Bölünmüş") döndürür; Extract tek bir PDF döndürür.
    public static string Split(string input, string destination, SplitMode mode, int pagesPerFile = 1, string ranges = "", bool allowLossy = false, CancellationToken token = default)
    {
        using var tx = new ExportTransaction(destination);
        using var source = Open(input);
        if (!allowLossy) RequireRebuildable(source, input);
        var stem = SafePaths.Stem(input);
        if (mode == SplitMode.Extract)
        {
            var selected = PageRange(ranges, source.PageCount, allowEmpty: false);
            using var part = new PdfDocument();
            foreach (var page in selected) part.AddPage(source.Pages[page - 1]);
            var file = Path.Combine(tx.StagingDirectory, $"{stem} - Seçilen sayfalar.pdf");
            part.Save(file);
            return tx.Publish([file], token)[0];
        }
        var size = mode == SplitMode.EachPage ? 1 : pagesPerFile;
        if (size < 1 || size > 1000) throw new UserFacingException("Her parçadaki sayfa sayısı 1 ile 1000 arasında olmalı.");
        if (size >= source.PageCount) throw new UserFacingException($"Bu PDF {source.PageCount} sayfa. Bölmek için parça başına daha az sayfa seçin.");
        var folder = Path.Combine(tx.StagingDirectory, $"{stem} - Bölünmüş");
        Directory.CreateDirectory(folder);
        var digits = source.PageCount.ToString().Length;
        for (var start = 1; start <= source.PageCount; start += size)
        {
            token.ThrowIfCancellationRequested();
            var end = Math.Min(source.PageCount, start + size - 1);
            using var part = new PdfDocument();
            for (var page = start; page <= end; page++) part.AddPage(source.Pages[page - 1]);
            var label = start == end ? $"Sayfa {start.ToString().PadLeft(digits, '0')}" : $"Sayfa {start.ToString().PadLeft(digits, '0')}-{end.ToString().PadLeft(digits, '0')}";
            part.Save(Path.Combine(folder, $"{stem} - {label}.pdf"));
        }
        return tx.Publish([folder], token)[0];
    }

    /// pages: çıktıdaki sırasıyla kaynak sayfa numaraları (1'den başlar). Listede olmayan sayfalar silinir.
    /// rotations: her çıktı sayfası için eklenecek döndürme (0, 90, 180, 270).
    public static string Organize(string input, string destination, IReadOnlyList<int> pages, IReadOnlyList<int> rotations, bool allowLossy = false, CancellationToken token = default)
    {
        using var tx = new ExportTransaction(destination);
        using var source = Open(input);
        if (!allowLossy) RequireRebuildable(source, input);
        if (pages.Count == 0) throw new UserFacingException("En az bir sayfa kalmalı.");
        if (pages.Any(p => p < 1 || p > source.PageCount) || rotations.Count != pages.Count) throw new ArgumentException("Geçersiz sayfa listesi.");
        using var result = new PdfDocument();
        for (var n = 0; n < pages.Count; n++)
        {
            token.ThrowIfCancellationRequested();
            var added = result.AddPage(source.Pages[pages[n] - 1]);
            added.Rotate = ((added.Rotate + rotations[n]) % 360 + 360) % 360;
        }
        var output = Path.Combine(tx.StagingDirectory, $"{SafePaths.Stem(input)} (düzenlendi).pdf");
        result.Save(output);
        return tx.Publish([output], token)[0];
    }

    /// "1-3, 5, 8-6" gibi metinleri sayfa listesine çevirir.
    public static int[] PageRange(string text, int count, bool allowEmpty = true)
    {
        text = (text ?? "").Replace('–', '-').Replace('—', '-').Replace(';', ',').Replace(" ", "");
        if (string.IsNullOrWhiteSpace(text))
        {
            if (allowEmpty) return Enumerable.Range(1, count).ToArray();
            throw new UserFacingException("Hangi sayfaları istediğinizi yazın. Örnek: 1-3, 5");
        }
        var pages = new List<int>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Split('-');
            if (p.Length is < 1 or > 2 || !int.TryParse(p[0], out var a) || (p.Length == 2 && !int.TryParse(p[1], out _)))
                throw new UserFacingException($"“{part}” anlaşılamadı. Örnek yazım: 1-3, 5");
            var b = p.Length == 2 ? int.Parse(p[1]) : a;
            if (a < 1 || a > count || b < 1 || b > count)
                throw new UserFacingException($"“{part}” bu PDF'in dışında. PDF {count} sayfa.");
            for (var n = a; ; n += a <= b ? 1 : -1)
            {
                if (pages.Count >= 10000) throw new UserFacingException("En fazla 10.000 sayfa seçebilirsiniz.");
                pages.Add(n);
                if (n == b) break;
            }
        }
        return pages.ToArray();
    }

    internal static PdfDocument Open(string path)
    {
        try { return PdfReader.Open(path, PdfDocumentOpenMode.Import); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new UserFacingException(Friendly(path, error), error);
        }
    }

    internal static string Friendly(string path, Exception error)
    {
        var name = Path.GetFileName(path);
        if (!File.Exists(path)) return $"“{name}” bulunamadı. Taşınmış veya silinmiş olabilir.";
        if (error.Message.Contains("password", StringComparison.OrdinalIgnoreCase) || error.Message.Contains("encrypt", StringComparison.OrdinalIgnoreCase))
            return $"“{name}” şifreli. Önce şifresini kaldırıp tekrar deneyin.";
        if (error is IOException && error.Message.Contains("being used", StringComparison.OrdinalIgnoreCase))
            return $"“{name}” başka bir programda açık. O programı kapatıp tekrar deneyin.";
        return $"“{name}” açılamadı. Dosya bozuk olabilir veya desteklenmeyen bir PDF olabilir.";
    }

    // Sayfaları yeni bir belgeye kopyalamak form alanlarını ve ekleri geride bırakır.
    // Sessizce kaybetmek yerine kullanıcıya sorulur (Nashi bu durumda işlemi reddeder).
    private static void RequireRebuildable(PdfDocument source, string path)
    {
        var catalog = source.Internals.Catalog.Elements;
        var widgets = source.Pages.Cast<PdfPage>().Any(p => p.Elements.GetArray("/Annots") is { } annotations &&
            annotations.Elements.Any(e => (e is PdfSharp.Pdf.Advanced.PdfReference reference ? reference.Value : e) is PdfDictionary annotation &&
                annotation.Elements.GetName("/Subtype") == "/Widget"));
        var attachments = catalog.GetDictionary("/Names")?.Elements.ContainsKey("/EmbeddedFiles") == true || catalog.ContainsKey("/AF") || catalog.ContainsKey("/Collection");
        var form = catalog.GetDictionary("/AcroForm");
        var fields = form is not null && (form.Elements.GetArray("/Fields") is { Elements.Count: > 0 } || form.Elements.ContainsKey("/XFA"));
        if (fields || widgets || attachments) throw new LossyPdfException(Path.GetFileName(path));
    }
}
