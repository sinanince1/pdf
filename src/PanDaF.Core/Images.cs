using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PanDaF.Core;

public static class Images
{
    // A4, nokta (1/72 inç) cinsinden.
    public const double A4Width = 595.276, A4Height = 841.89;
    private const double A4Margin = 24;

    public static byte[] Png(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var memory = new MemoryStream();
        encoder.Save(memory);
        return memory.ToArray();
    }

    public static byte[] Jpeg(BitmapSource image, int quality = 90)
    {
        // JPEG saydamlık taşımaz; önce beyaz zemine yerleştir.
        var opaque = HasAlpha(image) ? Flatten(image) : image;
        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
        encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(opaque, PixelFormats.Bgr24, null, 0)));
        using var memory = new MemoryStream();
        encoder.Save(memory);
        return memory.ToArray();
    }

    public static bool HasAlpha(BitmapSource image) =>
        image.Format == PixelFormats.Bgra32 || image.Format == PixelFormats.Pbgra32 || image.Format == PixelFormats.Prgba64 ||
        image.Format == PixelFormats.Rgba64 || image.Format == PixelFormats.Rgba128Float || image.Format == PixelFormats.Prgba128Float;

    // Saydam pikselleri beyaz zeminle birleştirir. Çizim nesnesi kullanmaz; her iş parçacığında çalışır.
    private static BitmapSource Flatten(BitmapSource image)
    {
        var source = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int width = source.PixelWidth, height = source.PixelHeight, stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        source.CopyPixels(pixels, stride, 0);
        var output = new byte[checked(width * 3 * height)];
        for (int i = 0, o = 0; i < pixels.Length; i += 4, o += 3)
        {
            var alpha = pixels[i + 3];
            for (var c = 0; c < 3; c++) output[o + c] = (byte)((pixels[i + c] * alpha + 255 * (255 - alpha) + 127) / 255);
        }
        var result = BitmapSource.Create(width, height, image.DpiX > 0 ? image.DpiX : 96, image.DpiY > 0 ? image.DpiY : 96, PixelFormats.Bgr24, null, output, width * 3);
        result.Freeze();
        return result;
    }

    /// Resmi açar ve telefon fotoğraflarındaki EXIF yönünü uygular.
    public static BitmapSource Load(string path) => Load(path, out _);

    public static BitmapSource Load(string path, out int orientation)
    {
        orientation = 1;
        BitmapFrame frame;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            frame = decoder.Frames[0];
        }
        catch (Exception error) when (error is NotSupportedException or FileFormatException or ArgumentException or System.Runtime.InteropServices.COMException or OverflowException)
        {
            throw new UserFacingException($"“{Path.GetFileName(path)}” açılamadı. Bu resim biçimi bu bilgisayarda desteklenmiyor olabilir; resmi JPG veya PNG olarak kaydedip tekrar deneyin.", error);
        }
        catch (IOException error)
        {
            throw new UserFacingException($"“{Path.GetFileName(path)}” okunamadı. Başka bir programda açık olabilir.", error);
        }
        if ((long)frame.PixelWidth * frame.PixelHeight > 150_000_000)
            throw new UserFacingException($"“{Path.GetFileName(path)}” çok büyük (150 megapikselden fazla).");
        orientation = Orientation(frame);
        BitmapSource image = frame;
        if (orientation != 1)
        {
            var transform = new TransformGroup();
            switch (orientation)
            {
                case 2: transform.Children.Add(new ScaleTransform(-1, 1)); break;
                case 3: transform.Children.Add(new RotateTransform(180)); break;
                case 4: transform.Children.Add(new ScaleTransform(1, -1)); break;
                case 5: transform.Children.Add(new RotateTransform(90)); transform.Children.Add(new ScaleTransform(-1, 1)); break;
                case 6: transform.Children.Add(new RotateTransform(90)); break;
                case 7: transform.Children.Add(new RotateTransform(270)); transform.Children.Add(new ScaleTransform(-1, 1)); break;
                case 8: transform.Children.Add(new RotateTransform(270)); break;
            }
            image = new TransformedBitmap(frame, transform);
        }
        image.Freeze();
        return image;
    }

    private static int Orientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is not BitmapMetadata metadata) return 1;
            foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
            {
                if (metadata.ContainsQuery(query) && metadata.GetQuery(query) is { } value)
                {
                    var number = Convert.ToInt32(value);
                    return number is >= 1 and <= 8 ? number : 1;
                }
            }
        }
        catch (Exception error) when (error is NotSupportedException or InvalidOperationException or ArgumentException or InvalidCastException or FormatException or OverflowException or System.Runtime.InteropServices.COMException) { }
        return 1;
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
        var bitmap = Load(file, out var orientation);
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

        double pixelWidth = bitmap.PixelWidth, pixelHeight = bitmap.PixelHeight;
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
            var dpi = bitmap.DpiX is >= 30 and <= 1200 ? bitmap.DpiX : 96;
            var factor = Math.Min(72 / dpi, 14400 / Math.Max(pixelWidth, pixelHeight));
            pageWidth = width = pixelWidth * factor; pageHeight = height = pixelHeight * factor; x = y = 0;
        }
        page.Width = XUnit.FromPoint(pageWidth); page.Height = XUnit.FromPoint(pageHeight);
        using var graphics = XGraphics.FromPdfPage(page);
        graphics.DrawImage(image, x, y, width, height);
    }
}
