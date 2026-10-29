using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PanDaF.Core;
using PdfSharp.Pdf.IO;

// PanDaF motor testleri. Windows'ta çalışır: dotnet run --project tests/PanDaF.Tests
// PANDAF_LIBREOFFICE=1 ise LibreOffice gerçekten indirilir, doğrulanır ve Office dönüşümleri denenir.
internal static class Program
{
    // WPF çizim nesneleri (örnek resimler) STA iş parçacığı ister.
    [STAThread]
    private static int Main() => Run().GetAwaiter().GetResult();

    private static async Task<int> Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "PanDaFTests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var failures = 0;
        void Check(bool condition, string name)
        {
            Console.WriteLine((condition ? "  OK    " : "  HATA  ") + name);
            if (!condition) failures++;
        }
        async Task Test(string name, Func<Task> body)
        {
            Console.WriteLine(name);
            try { await body(); }
            catch (Exception error) { failures++; Console.WriteLine("  HATA  " + error); }
        }
        string Folder(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;

        string Sample(string path, int width, int height, Color color, ushort orientation = 1)
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                context.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, width / 2.0, height / 3.0));
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            BitmapEncoder encoder;
            if (path.EndsWith(".jpg"))
            {
                var metadata = new BitmapMetadata("jpg");
                if (orientation != 1) metadata.SetQuery("/app1/ifd/{ushort=274}", orientation);
                encoder = new JpegBitmapEncoder { QualityLevel = 90 };
                encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0), null, metadata, null));
            }
            else
            {
                encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
            }
            using var file = File.Create(path);
            encoder.Save(file);
            return path;
        }

        var images = Folder("resimler");
        var landscape = Sample(Path.Combine(images, "yatay.jpg"), 1600, 1000, Colors.SteelBlue);
        var portrait = Sample(Path.Combine(images, "dikey.png"), 900, 1400, Colors.SeaGreen);
        var rotated = Sample(Path.Combine(images, "telefon fotoğrafı.jpg"), 1200, 800, Colors.Firebrick, orientation: 6);
        string threePages = "";

        await Test("Sayfa aralığı yazımı", () =>
        {
            Check(PdfPages.PageRange("1-3, 5", 10).SequenceEqual([1, 2, 3, 5]), "1-3, 5");
            Check(PdfPages.PageRange("4–2", 10).SequenceEqual([4, 3, 2]), "ters aralık ve uzun tire");
            Check(PdfPages.PageRange("", 3).SequenceEqual([1, 2, 3]), "boş = tümü");
            var threw = false; try { PdfPages.PageRange("1-20", 10); } catch (UserFacingException) { threw = true; }
            Check(threw, "PDF dışındaki aralık Türkçe hata verir");
            return Task.CompletedTask;
        });

        await Test("Telefon fotoğrafı yönü (EXIF)", () =>
        {
            var image = Images.Load(rotated, out var orientation);
            Check(orientation == 6 && image.PixelWidth == 800 && image.PixelHeight == 1200, $"döndürülmüş fotoğraf dik açıldı ({image.PixelWidth}x{image.PixelHeight})");
            return Task.CompletedTask;
        });

        await Test("Resimleri PDF'ye çevir", () =>
        {
            var output = Images.ToPdf([landscape, portrait, rotated], Folder("pdf-a4"), fitA4: true, combine: true, "Resimler");
            Check(output.Count == 1, "tek PDF oluştu");
            using (var pdf = PdfReader.Open(output[0], PdfDocumentOpenMode.Import))
            {
                Check(pdf.PageCount == 3, "3 sayfa");
                Check(Math.Abs(pdf.Pages[0].Width.Point - Images.A4Height) < 1, "yatay resim yatay A4");
                Check(Math.Abs(pdf.Pages[1].Width.Point - Images.A4Width) < 1, "dikey resim dikey A4");
                Check(Math.Abs(pdf.Pages[2].Width.Point - Images.A4Width) < 1, "EXIF ile dönen fotoğraf dikey A4");
            }
            threePages = output[0];
            var separate = Images.ToPdf([landscape, portrait], Folder("pdf-ayri"), fitA4: false, combine: false, "x");
            Check(separate.Count == 2 && separate.All(File.Exists), "her resim ayrı PDF");
            using (var pdf = PdfReader.Open(separate[0], PdfDocumentOpenMode.Import))
                Check(Math.Abs(pdf.Pages[0].Width.Point - 1200) < 1, "özgün boyut korunur (1600 px @96 dpi = 1200 pt)");
            return Task.CompletedTask;
        });

        await Test("Birleştir, böl, düzenle", () =>
        {
            var copy = Path.Combine(root, "kopya.pdf"); File.Copy(threePages, copy);
            var merged = PdfPages.Merge([threePages, copy], Folder("birlesik"));
            Check(PdfPages.PageCount(merged) == 6, "6 sayfalık birleşik PDF");
            Check(Path.GetFileName(merged) == "Birleştirilmiş PDF.pdf", "Türkçe dosya adı");

            var each = PdfPages.Split(merged, Folder("bol-her"), SplitMode.EachPage);
            Check(Directory.GetFiles(each, "*.pdf").Length == 6, "her sayfa ayrı (6 dosya, klasörde)");
            var pairs = PdfPages.Split(merged, Folder("bol-iki"), SplitMode.EveryN, pagesPerFile: 4);
            var pairFiles = Directory.GetFiles(pairs, "*.pdf").Order().ToArray();
            Check(pairFiles.Length == 2 && PdfPages.PageCount(pairFiles[0]) == 4 && PdfPages.PageCount(pairFiles[1]) == 2, "her 4 sayfada bir (4 + 2)");
            var extract = PdfPages.Split(merged, Folder("bol-sec"), SplitMode.Extract, ranges: "6, 1-2");
            Check(PdfPages.PageCount(extract) == 3, "seçilen 3 sayfa");

            var organized = PdfPages.Organize(merged, Folder("duzen"), [6, 1, 3], [90, 0, 270]);
            using var pdf = PdfReader.Open(organized, PdfDocumentOpenMode.Import);
            Check(pdf.PageCount == 3 && pdf.Pages[0].Rotate == 90 && pdf.Pages[2].Rotate == 270, "sil + sırala + döndür");
            Check(File.Exists(merged) && PdfPages.PageCount(merged) == 6, "asıl dosya değişmedi");
            return Task.CompletedTask;
        });

        await Test("PDF'den resme", async () =>
        {
            var converter = new Converter(Folder("araclar-yok"));
            var jpg = await converter.FromPdfAsync([threePages], Folder("jpg"), PdfTarget.Jpg, 72, null, default);
            Check(Directory.Exists(jpg[0]) && Directory.GetFiles(jpg[0], "*.jpg").Length == 3, "3 JPG");
            var first = Directory.GetFiles(jpg[0], "*.jpg").Order().First();
            var frame = BitmapDecoder.Create(new Uri(first), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            Check(frame.PixelWidth == 842, $"72 DPI yatay A4 genişliği ({frame.PixelWidth} px)");
            var png = await converter.FromPdfAsync([threePages], Folder("png"), PdfTarget.Png, 50, null, default);
            Check(Directory.GetFiles(png[0], "*.png").Length == 3, "3 PNG");
            using var raster = PdfRasterDocument.Open(threePages);
            Check(raster.PageCount == 3 && raster.RenderBitmap(1, 36).PixelHeight == 421, "önizleme çizimi");
        });

        await Test("İptal yarım dosya bırakmaz", async () =>
        {
            var folder = Folder("iptal");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            var threw = false;
            try { await new Converter(Folder("araclar-yok2")).FromPdfAsync([threePages], folder, PdfTarget.Png, 72, null, cancel.Token); }
            catch (OperationCanceledException) { threw = true; }
            Check(threw && Directory.GetFileSystemEntries(folder).Length == 0, "klasör boş kaldı");
        });

        if (Environment.GetEnvironmentVariable("PANDAF_LIBREOFFICE") == "1")
        {
            await Test("LibreOffice: indir, doğrula, Word/PowerPoint/Excel → PDF, PDF → Word", async () =>
            {
                var converter = new Converter(Folder("araclar"));
                await converter.LibreOffice.InstallAsync(new Progress<OperationProgress>(p => { }), default);
                Check(converter.LibreOffice.Ready, "LibreOffice hazır");
                var docx = Path.Combine(root, "Öğretmen notu.docx");
                WriteDocx(docx, "Merhaba öğretmenler ĞÜŞİÖÇ çalışma kağıdı");
                var pdfs = await converter.ToPdfAsync([docx], Folder("office-pdf"), new ImageToPdfOptions(), null, default);
                Check(pdfs.Count == 1 && Path.GetFileName(pdfs[0]) == "Öğretmen notu.pdf", "Word → PDF (Türkçe ad)");
                var text = await Converter.PdfText(pdfs[0], default);
                Check(text.Contains("öğretmenler") && text.Contains("ĞÜŞİÖÇ"), "PDF'den Türkçe metin çıkarıldı");
                var word = await converter.FromPdfAsync([pdfs[0]], Folder("office-docx"), PdfTarget.Word, 150, null, default);
                Check(word.Count == 1 && word[0].EndsWith(".docx") && new FileInfo(word[0]).Length > 0, "PDF → Word");
                using var zip = ZipFile.OpenRead(word[0]);
                using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
                Check(reader.ReadToEnd().Contains("öğretmenler"), "Word belgesinde metin var");
            });
        }

        Console.WriteLine(failures == 0 ? "\nTÜM TESTLER GEÇTİ" : $"\n{failures} HATA");
        try { Directory.Delete(root, true); } catch (IOException) { }
        return failures == 0 ? 0 : 1;
    }

    static void WriteDocx(string path, string text)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string content) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(content); }
        Add("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
        Add("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
        Add("word/document.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>" + System.Security.SecurityElement.Escape(text) + "</w:t></w:r></w:p></w:body></w:document>");
    }
}
