// Adapted from Nashi (https://github.com/orkunisitmak/nashi-app),
// Copyright (c) 2026 Orkun Işıtmak and Nashi contributors, MIT License.
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PanDaF.Core;

// PDFium ile CPU üzerinde sayfa görüntüsü üretir. PDFium iş parçacığı güvenli değildir;
// tüm çağrılar tek bir kilitle sıraya sokulur.
public sealed class PdfRasterDocument : IDisposable
{
    private static readonly object gate = new(); private static bool initialized;
    private nint document; private GCHandle source; private PdfFormPreview? form;
    public int PageCount { get; }

    private PdfRasterDocument(string path, string password)
    {
        byte[] data;
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var length = input.Length;
            if (length < 1 || length > 300_000_000) throw new UserFacingException("Lütfen 300 MB'tan küçük bir PDF seçin.");
            data = new byte[checked((int)length)]; input.ReadExactly(data);
        }
        catch (IOException error) { throw new UserFacingException(PdfPages.Friendly(path, error), error); }
        lock (gate)
        {
            if (!initialized) { Native.FPDF_InitLibrary(); initialized = true; }
            source = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                document = Native.FPDF_LoadMemDocument64(source.AddrOfPinnedObject(), (nuint)data.LongLength, password);
                if (document == 0)
                    throw new UserFacingException(Native.FPDF_GetLastError() == 4
                        ? $"“{Path.GetFileName(path)}” şifreli. Önce şifresini kaldırıp tekrar deneyin."
                        : $"“{Path.GetFileName(path)}” açılamadı. Dosya bozuk olabilir.");
                PageCount = Native.FPDF_GetPageCount(document);
                if (PageCount < 1 || PageCount > 2000) throw new UserFacingException("PDF 1 ile 2000 sayfa arasında olmalı.");
                if (Native.FPDF_GetFormType(document) != 0) form = new PdfFormPreview(document);
            }
            catch
            {
                form?.Dispose(); form = null;
                if (document != 0) { Native.FPDF_CloseDocument(document); document = 0; }
                if (source.IsAllocated) source.Free();
                throw;
            }
        }
    }

    public static PdfRasterDocument Open(string path, string password = "") => new(path, password);

    /// Sayfayı (0'dan başlayan index) verilen DPI'da beyaz zeminle çizer.
    public BitmapSource RenderBitmap(int index, int dpi, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (dpi < 20 || dpi > 600) throw new ArgumentOutOfRangeException(nameof(dpi));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(document == 0, this);
            if (index < 0 || index >= PageCount) throw new ArgumentOutOfRangeException(nameof(index));
            var page = Native.FPDF_LoadPage(document, index);
            if (page == 0) throw new UserFacingException($"PDF'in {index + 1}. sayfası okunamadı.");
            try
            {
                form?.Load(page, index);
                var w = Native.FPDF_GetPageWidth(page) * dpi / 72; var h = Native.FPDF_GetPageHeight(page) * dpi / 72;
                if (!double.IsFinite(w) || !double.IsFinite(h) || w < 1 || h < 1 || w * h > 100_000_000)
                    throw new UserFacingException("Sayfa çok büyük. Daha düşük bir kalite seçin.");
                var width = checked((int)Math.Ceiling(w)); var height = checked((int)Math.Ceiling(h));
                var stride = checked(width * 4); var pixels = new byte[checked(stride * height)];
                var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned); nint bitmap = 0;
                try
                {
                    bitmap = Native.FPDFBitmap_CreateEx(width, height, 4, pinned.AddrOfPinnedObject(), stride);
                    if (bitmap == 0) throw new UserFacingException("Sayfa görüntüsü için bellek ayrılamadı.");
                    if (Native.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xffffffff) == 0) throw new UserFacingException("Sayfa zemini hazırlanamadı.");
                    // 0x01 = FPDF_ANNOT: notlar ve form görünümleri de çizilsin.
                    Native.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, 1);
                    form?.Draw(bitmap, width, height);
                    token.ThrowIfCancellationRequested();
                    var image = BitmapSource.Create(width, height, dpi, dpi, PixelFormats.Bgra32, null, pixels, stride);
                    image.Freeze();
                    return image;
                }
                finally { if (bitmap != 0) Native.FPDFBitmap_Destroy(bitmap); pinned.Free(); }
            }
            finally { form?.ClosePage(); Native.FPDF_ClosePage(page); }
        }
    }

    public byte[] RenderPng(int index, int dpi, CancellationToken token = default) => Images.Png(RenderBitmap(index, dpi, token));

    public void Dispose()
    {
        lock (gate)
        {
            if (document == 0) return;
            form?.Dispose(); form = null;
            Native.FPDF_CloseDocument(document); document = 0;
            if (source.IsAllocated) source.Free();
        }
        GC.SuppressFinalize(this);
    }

    ~PdfRasterDocument() { Dispose(); }

    private static class Native
    {
        [DllImport("pdfium", CallingConvention = CallingConvention.Winapi)] internal static extern void FPDF_InitLibrary();
        [DllImport("pdfium", CallingConvention = CallingConvention.Winapi)] internal static extern nint FPDF_LoadMemDocument64(nint data, nuint size, [MarshalAs(UnmanagedType.LPUTF8Str)] string password);
        [DllImport("pdfium", CallingConvention = CallingConvention.Winapi)] internal static extern uint FPDF_GetLastError();
        [DllImport("pdfium", CallingConvention = CallingConvention.Winapi)] internal static extern int FPDF_GetPageCount(nint document);
        [DllImport("pdfium", CallingConvention = CallingConvention.Winapi)] internal static extern int FPDF_GetFormType(nint document);
        [DllImport("pdfium", CallingConvention = CallingConvention.Winapi)] internal static extern nint FPDF_LoadPage(nint document, int index);
        [DllImport("pdfium", CallingConvention = CallingConvention.Winapi)] internal static extern double FPDF_GetPageWidth(nint page);
        [DllImport("pdfium", CallingConvention = CallingConvention.Winapi)] internal static extern double FPDF_GetPageHeight(nint page);
        [DllImport("pdfium", CallingConvention = CallingConvention.Winapi)] internal static extern nint FPDFBitmap_CreateEx(int width, int height, int format, nint data, int stride);
        [DllImport("pdfium", CallingConvention = CallingConvention.Winapi)] internal static extern int FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);
        [DllImport("pdfium", CallingConvention = CallingConvention.Winapi)] internal static extern void FPDF_RenderPageBitmap(nint bitmap, nint page, int left, int top, int width, int height, int rotate, int flags);
        [DllImport("pdfium", CallingConvention = CallingConvention.Winapi)] internal static extern void FPDFBitmap_Destroy(nint bitmap);
        [DllImport("pdfium", CallingConvention = CallingConvention.Winapi)] internal static extern void FPDF_ClosePage(nint page);
        [DllImport("pdfium", CallingConvention = CallingConvention.Winapi)] internal static extern void FPDF_CloseDocument(nint document);
    }
}
