// PDF → metin/görüntü kısımları Nashi'den uyarlanmıştır (https://github.com/orkunisitmak/nashi-app),
// Copyright (c) 2026 Orkun Işıtmak and Nashi contributors, MIT License.
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using global::Windows.Graphics.Imaging;
using global::Windows.Media.Ocr;
using global::Windows.Storage.Streams;

namespace PanDaF.Core;

public enum PdfTarget { Word, Jpg, Png, Text }

public sealed record ImageToPdfOptions(bool FitA4 = true, bool Combine = true, string CombinedName = "Resimler");

/// PDF'ye ve PDF'den dönüştürme. Office belgeleri için önce Microsoft Office, yoksa LibreOffice kullanılır.
public sealed class Converter(string toolsRoot)
{
    public LibreOffice LibreOffice { get; } = new(Path.Combine(toolsRoot, "libreoffice"));

    /// Seçilen dosyalar için LibreOffice indirmek gerekiyor mu?
    public bool NeedsLibreOfficeToPdf(IEnumerable<string> inputs) =>
        !LibreOffice.Ready && inputs.Any(f => FileKinds.IsOfficeDocument(f) && !MicrosoftOffice.CanConvert(f));

    public bool NeedsLibreOfficeForWord => !LibreOffice.Ready && !MicrosoftOffice.HasWord;

    public async Task<IReadOnlyList<string>> ToPdfAsync(IReadOnlyList<string> inputs, string destination, ImageToPdfOptions imageOptions,
        IProgress<OperationProgress>? progress, CancellationToken token)
    {
        var unsupported = inputs.FirstOrDefault(f => !FileKinds.CanConvertToPdf(f));
        if (unsupported is not null) throw new UserFacingException($"“{Path.GetFileName(unsupported)}” PDF'ye çevrilemez. Word, PowerPoint, Excel veya resim dosyası seçin.");
        var documents = inputs.Where(FileKinds.IsOfficeDocument).ToList();
        var images = inputs.Where(FileKinds.IsImage).ToList();
        var outputs = new List<string>();
        if (documents.Count > 0)
        {
            using var tx = new ExportTransaction(destination);
            var staged = new List<string>();
            for (var i = 0; i < documents.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var input = documents[i];
                progress?.Report(new((double)i / inputs.Count, $"Dönüştürülüyor: {Path.GetFileName(input)} ({i + 1}/{documents.Count})"));
                staged.Add(await DocumentToPdf(input, tx.StagingDirectory, token));
            }
            outputs.AddRange(tx.Publish(staged, token));
        }
        if (images.Count > 0)
        {
            var offset = (double)documents.Count / inputs.Count;
            var scaled = new Progress<OperationProgress>(p => progress?.Report(new(offset + (p.Fraction ?? 0) * (1 - offset), p.Label)));
            outputs.AddRange(await Task.Run(() => Images.ToPdf(images, destination, imageOptions.FitA4, imageOptions.Combine, imageOptions.CombinedName, scaled, token), token));
        }
        return outputs;
    }

    private async Task<string> DocumentToPdf(string input, string staging, CancellationToken token)
    {
        if (MicrosoftOffice.CanConvert(input))
        {
            var output = SafePaths.Unique(staging, SafePaths.Stem(input) + ".pdf");
            try { await MicrosoftOffice.ToPdfAsync(Path.GetFullPath(input), output, token); return output; }
            catch (Exception error) when (error is not OperationCanceledException && LibreOffice.Ready)
            {
                // Office başaramazsa LibreOffice ile denenir.
                if (File.Exists(output)) File.Delete(output);
            }
        }
        return await LibreOffice.ConvertAsync(input, "pdf", staging, token);
    }

    public async Task<IReadOnlyList<string>> FromPdfAsync(IReadOnlyList<string> inputs, string destination, PdfTarget target, int dpi,
        IProgress<OperationProgress>? progress, CancellationToken token)
    {
        using var tx = new ExportTransaction(destination);
        var staged = new List<string>();
        for (var i = 0; i < inputs.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var input = inputs[i];
            if (!FileKinds.IsPdf(input)) throw new UserFacingException($"“{Path.GetFileName(input)}” bir PDF değil.");
            var label = inputs.Count > 1 ? $" ({i + 1}/{inputs.Count})" : "";
            var stem = SafePaths.Stem(input);
            switch (target)
            {
                case PdfTarget.Jpg or PdfTarget.Png:
                {
                    var ext = target == PdfTarget.Jpg ? "jpg" : "png";
                    await Task.Run(() =>
                    {
                        using var pdf = PdfRasterDocument.Open(input);
                        var digits = pdf.PageCount.ToString().Length;
                        var folder = pdf.PageCount == 1 ? tx.StagingDirectory : Path.Combine(tx.StagingDirectory, SafePaths.CleanName($"{stem} - Resimler"));
                        Directory.CreateDirectory(folder);
                        var last = "";
                        for (var page = 0; page < pdf.PageCount; page++)
                        {
                            token.ThrowIfCancellationRequested();
                            progress?.Report(new((i + (double)page / pdf.PageCount) / inputs.Count, $"Sayfa {page + 1}/{pdf.PageCount} resme çevriliyor{label}"));
                            var bitmap = pdf.RenderBitmap(page, dpi, token);
                            var bytes = ext == "jpg" ? Images.Jpeg(bitmap, 90) : Images.Png(bitmap);
                            var name = pdf.PageCount == 1 ? $"{stem}.{ext}" : $"{stem} - Sayfa {(page + 1).ToString().PadLeft(digits, '0')}.{ext}";
                            last = SafePaths.Unique(folder, name);
                            File.WriteAllBytes(last, bytes);
                        }
                        staged.Add(pdf.PageCount == 1 ? last : folder);
                    }, token);
                    break;
                }
                case PdfTarget.Text:
                {
                    progress?.Report(new((double)i / inputs.Count, $"Metin çıkarılıyor{label}…"));
                    var text = await PdfText(input, token);
                    var output = SafePaths.Unique(tx.StagingDirectory, stem + ".txt");
                    await File.WriteAllTextAsync(output, text, new UTF8Encoding(true), token);
                    staged.Add(output);
                    break;
                }
                case PdfTarget.Word:
                {
                    progress?.Report(new((double)i / inputs.Count, $"Word belgesine çevriliyor{label}… Bu birkaç dakika sürebilir."));
                    staged.Add(await PdfToWord(input, tx.StagingDirectory, dpi, token));
                    break;
                }
            }
        }
        progress?.Report(new(.99, "Kaydediliyor…"));
        return tx.Publish(staged, token);
    }

    private async Task<string> PdfToWord(string input, string staging, int dpi, CancellationToken token)
    {
        var stem = SafePaths.Stem(input);
        if (MicrosoftOffice.HasWord)
        {
            var output = SafePaths.Unique(staging, stem + ".docx");
            try { await MicrosoftOffice.PdfToDocxAsync(Path.GetFullPath(input), output, token); return output; }
            catch (Exception error) when (error is not OperationCanceledException && LibreOffice.Ready)
            {
                if (File.Exists(output)) File.Delete(output);
            }
        }
        if (!LibreOffice.Ready) throw new UserFacingException("PDF'yi Word'e çevirmek için Microsoft Word veya LibreOffice gerekli.");
        var work = Path.Combine(staging, ".work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var html = await Task.Run(() => EditableHtml(input, Math.Min(dpi, 200), token), token);
            var source = Path.Combine(work, SafePaths.CleanName(stem) + ".html");
            await File.WriteAllTextAsync(source, html, token);
            return await LibreOffice.ConvertAsync(source, "docx", staging, token);
        }
        finally { try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    // Taranmış sayfalar ve PdfPig'in çözemediği resimler PDFium ile çizilip eklenir.
    private static string EditableHtml(string input, int dpi, CancellationToken token)
    {
        try
        {
            var html = LibreOffice.PdfHtml(input, "", null, out var needed);
            if (needed.Length == 0) return html;
            using var pdf = PdfRasterDocument.Open(input);
            var pages = needed.Distinct().ToDictionary(n => n, n => pdf.RenderBitmap(n - 1, dpi, token));
            return LibreOffice.PdfHtml(input, "", (n, area) => pages.TryGetValue(n, out var page) ? Crop(page, area, dpi) : null, out _);
        }
        catch (Exception error) when (error is not (UserFacingException or OperationCanceledException))
        {
            throw new UserFacingException(PdfPages.Friendly(input, error), error);
        }
    }

    private static byte[]? Crop(BitmapSource page, LibreOffice.PdfArea? area, int dpi)
    {
        if (area is not { } a) return Images.Png(page);
        var scale = dpi / 72d;
        var x = Math.Clamp((int)Math.Floor(a.Left * scale), 0, page.PixelWidth); var y = Math.Clamp((int)Math.Floor(a.Top * scale), 0, page.PixelHeight);
        var width = Math.Min(page.PixelWidth - x, (int)Math.Ceiling(a.Width * scale)); var height = Math.Min(page.PixelHeight - y, (int)Math.Ceiling(a.Height * scale));
        return width < 1 || height < 1 ? null : Images.Png(new CroppedBitmap(page, new System.Windows.Int32Rect(x, y, width, height)));
    }

    /// PDF'deki metni çıkarır; metni olmayan (taranmış) sayfalarda Windows'un yazı tanıma özelliğini kullanır.
    public static async Task<string> PdfText(string input, CancellationToken token)
    {
        UglyToad.PdfPig.PdfDocument pdf;
        try { pdf = UglyToad.PdfPig.PdfDocument.Open(input); }
        catch (Exception error) when (error is not OperationCanceledException) { throw new UserFacingException(PdfPages.Friendly(input, error), error); }
        using (pdf)
        {
            PdfRasterDocument? raster = null;
            try
            {
                var text = new List<string>();
                foreach (var page in pdf.GetPages())
                {
                    token.ThrowIfCancellationRequested();
                    var content = UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(page);
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        raster ??= PdfRasterDocument.Open(input);
                        content = await Recognize(raster.RenderBitmap(page.Number - 1, 200, token), token);
                    }
                    text.Add(content);
                }
                var output = string.Join("\r\n\r\n\f\r\n\r\n", text.Select(t => t.Replace("\r\n", "\n").Replace("\n", "\r\n")));
                if (string.IsNullOrWhiteSpace(output)) throw new UserFacingException("Bu PDF'de okunabilir metin bulunamadı.");
                return output;
            }
            finally { raster?.Dispose(); }
        }
    }

    private static OcrEngine? Engine() =>
        OcrEngine.TryCreateFromUserProfileLanguages()
        ?? OcrEngine.TryCreateFromLanguage(new global::Windows.Globalization.Language("tr-TR"))
        ?? OcrEngine.TryCreateFromLanguage(new global::Windows.Globalization.Language("en-US"));

    private static async Task<string> Recognize(BitmapSource image, CancellationToken token)
    {
        var engine = Engine() ?? throw new UserFacingException("Taranmış sayfalardaki yazıyı okumak için Windows Ayarlar > Dil bölümünden bir dil paketi yükleyin.");
        var factor = Math.Min(1, (double)OcrEngine.MaxImageDimension / Math.Max(image.PixelWidth, image.PixelHeight));
        if (factor < 1) image = new TransformedBitmap(image, new ScaleTransform(factor, factor));
        using var memory = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(memory.GetOutputStreamAt(0))) { writer.WriteBytes(Images.Png(image)); await writer.StoreAsync(); }
        memory.Seek(0);
        var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(memory);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);
        token.ThrowIfCancellationRequested();
        return string.Join("\n", result.Lines.Select(l => l.Text));
    }
}
