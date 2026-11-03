#if WINDOWS
using SkiaSharp;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using PanDaF.Core;

namespace PanDaF;

/// Windows'un yerleşik yazı tanıması (dil paketleri Ayarlar > Dil'den gelir).
internal static class WindowsOcr
{
    private static OcrEngine? Engine() =>
        OcrEngine.TryCreateFromUserProfileLanguages()
        ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("tr-TR"))
        ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));

    public static bool Available
    {
        get { try { return Engine() is not null; } catch (Exception) { return false; } }
    }

    public static async Task<string> Recognize(SKBitmap image, CancellationToken token)
    {
        var engine = Engine() ?? throw new UserFacingException("Taranmış sayfalardaki yazıyı okumak için Windows Ayarlar > Dil bölümünden bir dil paketi yükleyin.");
        var factor = Math.Min(1, (double)OcrEngine.MaxImageDimension / Math.Max(image.Width, image.Height));
        SKBitmap? scaled = null;
        try
        {
            if (factor < 1) scaled = image.Resize(new SKImageInfo((int)(image.Width * factor), (int)(image.Height * factor)), SKFilterQuality.High);
            var png = Images.Png(scaled ?? image);
            using var memory = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(memory.GetOutputStreamAt(0))) { writer.WriteBytes(png); await writer.StoreAsync(); }
            memory.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(memory);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var result = await engine.RecognizeAsync(bitmap);
            token.ThrowIfCancellationRequested();
            return string.Join("\n", result.Lines.Select(l => l.Text));
        }
        finally { scaled?.Dispose(); }
    }
}
#endif
