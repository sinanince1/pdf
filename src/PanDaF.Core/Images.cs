using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SkiaSharp;

namespace PanDaF.Core;

/// Resim işleri (SkiaSharp): açma (EXIF yönü ile), PNG/JPEG kodlama, resimden PDF.
public static class Images
{
    // A4, nokta (1/72 inç) cinsinden.
    public const double A4Width = 595.276, A4Height = 841.89;
    private const double A4Margin = 24;

    public static byte[] Png(SKBitmap image)
    {
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static byte[] Jpeg(SKBitmap image, int quality = 90)
    {
        // JPEG saydamlık taşımaz; önce beyaz zemine yerleştir.
        using var opaque = HasAlpha(image) ? Flatten(image) : null;
        using var data = (opaque ?? image).Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100));
        return data.ToArray();
    }

    public static bool HasAlpha(SKBitmap image) => image.AlphaType != SKAlphaType.Opaque && !image.Info.IsOpaque;

    public static SKBitmap Flatten(SKBitmap image)
    {
        var result = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(image, 0, 0);
        return result;
    }

    /// Resmi açar ve telefon fotoğraflarındaki EXIF yönünü uygular.
    public static SKBitmap Load(string path) => Load(path, out _);

    public static SKBitmap Load(string path, out int orientation)
    {
        orientation = 1;
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new UserFacingException($"“{Path.GetFileName(path)}” okunamadı. Başka bir programda açık olabilir.", error); }
        using var codec = SKCodec.Create(new MemoryStream(bytes));
        if (codec is null) throw new UserFacingException($"“{Path.GetFileName(path)}” açılamadı. Bu resim biçimi desteklenmiyor olabilir; resmi JPG veya PNG olarak kaydedip tekrar deneyin.");
        var info = codec.Info;
        if ((long)info.Width * info.Height > 150_000_000) throw new UserFacingException($"“{Path.GetFileName(path)}” çok büyük (150 megapikselden fazla).");
        var bitmap = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var result = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            throw new UserFacingException($"“{Path.GetFileName(path)}” çözülemedi ({result}).");
        }
        orientation = codec.EncodedOrigin switch
        {
            SKEncodedOrigin.TopRight => 2, SKEncodedOrigin.BottomRight => 3, SKEncodedOrigin.BottomLeft => 4,
            SKEncodedOrigin.LeftTop => 5, SKEncodedOrigin.RightTop => 6, SKEncodedOrigin.RightBottom => 7, SKEncodedOrigin.LeftBottom => 8, _ => 1,
        };
        if (orientation == 1) return bitmap;
        using (bitmap) return Orient(bitmap, orientation);
    }

    private static SKBitmap Orient(SKBitmap source, int orientation)
    {
        var swap = orientation is >= 5 and <= 8;
        var result = new SKBitmap(new SKImageInfo(swap ? source.Height : source.Width, swap ? source.Width : source.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(result);
        canvas.Translate(result.Width / 2f, result.Height / 2f);
        switch (orientation)
        {
            case 2: canvas.Scale(-1, 1); break;
            case 3: canvas.RotateDegrees(180); break;
            case 4: canvas.Scale(1, -1); break;
            case 5: canvas.RotateDegrees(90); canvas.Scale(-1, 1); break;
            case 6: canvas.RotateDegrees(90); break;
            case 7: canvas.RotateDegrees(270); canvas.Scale(-1, 1); break;
            case 8: canvas.RotateDegrees(270); break;
        }
        canvas.Translate(-source.Width / 2f, -source.Height / 2f);
        canvas.DrawBitmap(source, 0, 0);
        return result;
    }

    /// Resimleri PDF'ye çevirir. combine=true ise hepsi tek PDF'de birleşir (adı combinedName),
    /// değilse her resim kendi adıyla ayrı bir PDF olur.
    public static IReadOnlyList<string> ToPdf(IReadOnlyList<string> images, string destination, bool fitA4, bool combine, string combinedName,
        IProgress<OperationProgress>? progress = null, CancellationToken token = default)
    {
        if (images.Count == 0) throw new UserFacingException("Resim seçilmedi.");
        using var tx = new ExportTransaction(destination);
        var outputs = new List<string>();
        List<string[]> groups = combine ? [images.ToArray()] : images.Select(i => new[] { i }).ToList();
        var done = 0;
        foreach (var group in groups)
        {
            var keep = new List<IDisposable>();
            try
            {
                using var pdf = new PdfDocument();
                foreach (var file in group)
                {
                    token.ThrowIfCancellationRequested();
                    progress?.Report(new((double)done / images.Count * .95, $"Resim ekleniyor: {Path.GetFileName(file)}"));
                    AddImagePage(pdf, file, fitA4, keep);
                    done++;
                }
                var name = combine ? SafePaths.CleanName(combinedName) : SafePaths.Stem(group[0]);
                var output = SafePaths.Unique(tx.StagingDirectory, name + ".pdf");
                pdf.Save(output);
                outputs.Add(output);
            }
            finally { foreach (var item in keep) item.Dispose(); }
        }
        progress?.Report(new(.98, "Kaydediliyor…"));
        return tx.Publish(outputs, token);
    }

    private static void AddImagePage(PdfDocument pdf, string file, bool fitA4, List<IDisposable> keep)
    {
        using var bitmap = Load(file, out var orientation);
        var ext = FileKinds.Extension(file);
        byte[] bytes;
        // Yönü düzgün JPEG dosyaları olduğu gibi gömülür: kalite kaybı yok, dosya küçük kalır.
        if (ext is "jpg" or "jpeg" or "jfif" && orientation == 1) bytes = File.ReadAllBytes(file);
        else if (HasAlpha(bitmap) || ext is "png" or "gif" or "bmp") bytes = Png(bitmap);
        else bytes = Jpeg(bitmap, 92);
        var stream = new MemoryStream(bytes); keep.Add(stream);
        XImage image;
        try { image = XImage.FromStream(stream); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Bazı JPEG türlerini (ör. CMYK) PDFsharp doğrudan gömemez; yeniden kodlanır.
            var fallback = new MemoryStream(Png(bitmap)); keep.Add(fallback);
            try { image = XImage.FromStream(fallback); }
            catch (Exception inner) when (inner is not OperationCanceledException) { throw new UserFacingException($"“{Path.GetFileName(file)}” PDF'ye eklenemedi.", error); }
        }
        keep.Add(image);

        double pixelWidth = bitmap.Width, pixelHeight = bitmap.Height;
        var page = pdf.AddPage();
        double pageWidth, pageHeight, x, y, width, height;
        if (fitA4)
        {
            var landscape = pixelWidth > pixelHeight;
            pageWidth = landscape ? A4Height : A4Width; pageHeight = landscape ? A4Width : A4Height;
            var scale = Math.Min((pageWidth - 2 * A4Margin) / pixelWidth, (pageHeight - 2 * A4Margin) / pixelHeight);
            width = pixelWidth * scale; height = pixelHeight * scale;
            x = (pageWidth - width) / 2; y = (pageHeight - height) / 2;
        }
        else
        {
            // Çözünürlük bilgisi güvenilir olmadığından 96 dpi varsayılır; en büyük sayfa kenarı 200 inç.
            var factor = Math.Min(72 / 96.0, 14400 / Math.Max(pixelWidth, pixelHeight));
            pageWidth = width = pixelWidth * factor; pageHeight = height = pixelHeight * factor; x = y = 0;
        }
        page.Width = XUnit.FromPoint(pageWidth); page.Height = XUnit.FromPoint(pageHeight);
        using var graphics = XGraphics.FromPdfPage(page);
        graphics.DrawImage(image, x, y, width, height);
    }
}
