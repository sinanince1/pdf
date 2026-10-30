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
        WindowsFontResolver.Install();
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

        await Test("PDF → Excel: tablo çıkarma", async () =>
        {
            var pdfPath = Path.Combine(root, "Not listesi.pdf");
            // PDFsharp Core'da yazı tipi çözümleyici yok; metinli PDF'yi LibreOffice ile HTML'den üret.
            // LibreOffice indirilmediyse önceden yazılmış küçük bir metin PDF'i kullanılır.
            var tableConverter = new Converter(Folder("araclar"));
            string[][] table = [["Ad Soyad", "Türkçe", "Matematik", "Ortalama"], ["Ayşe Yılmaz", "85", "90", "87,5"], ["Mehmet Kaya", "70", "65", "67,5"], ["Zeynep Demir", "100", "95", "97,5"]];
            if (Environment.GetEnvironmentVariable("PANDAF_LIBREOFFICE") == "1")
            {
                await tableConverter.LibreOffice.InstallAsync(null, default);
                var html = "<html><head><meta charset=\"utf-8\"/></head><body><table style=\"border-collapse:collapse;font-family:Arial;font-size:11pt\">" +
                    string.Join("", table.Select(r => "<tr>" + string.Join("", r.Select(c => "<td style=\"padding:4pt 18pt 4pt 4pt\">" + c + "</td>")) + "</tr>")) + "</table></body></html>";
                var htmlPath = Path.Combine(root, "Not listesi.html");
                File.WriteAllText(htmlPath, html);
                pdfPath = await tableConverter.LibreOffice.ConvertAsync(htmlPath, "pdf", root, default);
            }
            else
            {
                Console.WriteLine("  ATLA  LibreOffice yok; tablo PDF'i üretilemedi");
                return;
            }
            Check(!Converter.IsScanned(pdfPath), "metinli PDF taranmış sayılmaz");
            Check(Converter.IsScanned(threePages), "resimlerden yapılan PDF taranmış sayılır");
            var xlsx = await tableConverter.FromPdfAsync([pdfPath], Folder("excel"), PdfTarget.Excel, 150, null, default);
            Check(xlsx.Count == 1 && xlsx[0].EndsWith("Not listesi.xlsx"), "xlsx oluştu");
            using var zip = ZipFile.OpenRead(xlsx[0]);
            using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            var xml = reader.ReadToEnd();
            Console.WriteLine("  BİLGİ sözcükler: " + string.Join(" | ", UglyToad.PdfPig.PdfDocument.Open(pdfPath).GetPage(1).GetWords().Select(w => $"{w.Text}@{w.BoundingBox.Left:0},{w.BoundingBox.Bottom:0}")));
            Console.WriteLine("  BİLGİ hücreler: " + string.Join(" ; ", PdfTables.ExtractRows(UglyToad.PdfPig.PdfDocument.Open(pdfPath).GetPage(1)).Select(r => string.Join(" | ", r))));
            Check(xml.Contains("<t xml:space=\"preserve\">Ayşe Yılmaz</t>"), "ad soyad tek hücrede");
            Check(xml.Contains("<c r=\"B2\"><v>85</v></c>") && xml.Contains("<c r=\"D2\"><v>87.5</v></c>"), "sayılar doğru sütunda ve sayı olarak (85, 87,5)");
            Check(xml.Contains("<c r=\"D1\"") && xml.Contains(">Ortalama</t>"), "4 sütun başlığı");
        });

        await Test("PDF üzerine ekleme (yazı, kapatma, vurgu, çizim, resim)", () =>
        {
            var output = Path.Combine(Folder("ekleme"), "ek.pdf");
            var png = File.ReadAllBytes(portrait);
            PdfAnnotator.Save(threePages, output,
            [
                new Mark { Kind = MarkKind.Whiteout, Page = 0, X = 50, Y = 50, Width = 200, Height = 40 },
                new Mark { Kind = MarkKind.Text, Page = 0, X = 54, Y = 54, Width = 200, Height = 30, Text = "Merhaba ĞÜŞİÖÇ", FontSize = 14, Bold = true, Color = "#D9480F" },
                new Mark { Kind = MarkKind.Highlight, Page = 1, X = 50, Y = 100, Width = 200, Height = 20, Color = "#F2C811" },
                new Mark { Kind = MarkKind.Ink, Page = 2, X = 100, Y = 100, Width = 100, Height = 50, StrokeWidth = 3, Points = [(0, 0), (50, 50), (100, 0)] },
                new Mark { Kind = MarkKind.Image, Page = 2, X = 300, Y = 300, Width = 90, Height = 140, ImagePng = png },
            ]);
            Check(PdfPages.PageCount(output) == 3, "sayfa sayısı korundu");
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(output);
            Console.WriteLine("  BİLGİ sayfa1 metin: [" + pdf.GetPage(1).Text + "] sözcükler: " + string.Join(" | ", pdf.GetPage(1).GetWords().Select(w => w.Text)));
            Check(pdf.GetPage(1).Text.Replace(" ", "").Contains("MerhabaĞÜŞİÖÇ"), "Türkçe yazı gömüldü");
            Check(pdf.GetPage(3).GetImages().Count() >= 2, "resim eklendi (asıl + yeni)");
            return Task.CompletedTask;
        });

        await Test("Yerinde yazı değiştirme (KillerPdf.Engine)", () =>
        {
            var pdfPath = Path.Combine(Folder("yerinde"), "metin.pdf");
            using (var doc = new PdfSharp.Pdf.PdfDocument())
            {
                var pg = doc.AddPage();
                using var g = PdfSharp.Drawing.XGraphics.FromPdfPage(pg);
                g.DrawString("Merhaba ESKİ dünya", new PdfSharp.Drawing.XFont("Arial", 14), PdfSharp.Drawing.XBrushes.Black, new PdfSharp.Drawing.XPoint(72, 100));
                g.DrawString("İkinci satır sabit", new PdfSharp.Drawing.XFont("Arial", 14), PdfSharp.Drawing.XBrushes.Black, new PdfSharp.Drawing.XPoint(72, 130));
                doc.Save(pdfPath);
            }
            var bytes = File.ReadAllBytes(pdfPath);
            var lines = TextRewriter.Lines(bytes, 0);
            Check(lines.Count == 2 && lines[0].Text == "Merhaba ESKİ dünya", $"satırlar çıkarıldı: {string.Join(" / ", lines.Select(l => l.Text))}");
            Check(TextRewriter.Blocker(bytes, 0, lines[0]) is null, "satır değiştirilebilir");
            var result = TextRewriter.Replace(bytes, 0, lines[0], "Merhaba YENİ dünya ğüşiöç");
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(result.Document);
            var words = pdf.GetPage(1).GetWords().Select(w => w.Text).ToList();
            Console.WriteLine("  BİLGİ yeni sözcükler: " + string.Join(" | ", words) + (result.UsedSubstituteFont ? " (yedek yazı tipi)" : " (asıl yazı tipi)"));
            Check(words.Contains("YENİ") && !words.Contains("ESKİ"), "yazı yerinde değişti");
            Check(words.Contains("İkinci") && words.Contains("sabit"), "diğer satır yerinde kaldı");
            var after = pdf.GetPage(1).GetWords().First(w => w.Text == "Merhaba").BoundingBox.Left;
            Check(Math.Abs(after - 72) < 1, $"satır başlangıcı aynı yerde ({after:0.0})");
            return Task.CompletedTask;
        });

        await Test("Yedek yazı tipi eşleme (aynı aile, Windows'tan)", () =>
        {
            var (family, bold, italic) = TextRewriter.Parse("ABCDEF+Calibri-Bold", false);
            Check(family == "Calibri" && bold && !italic, $"Calibri-Bold çözüldü: {family} kalın={bold}");
            var (f2, b2, i2) = TextRewriter.Parse("TimesNewRomanPS-BoldItalicMT", false);
            Check(f2 == "Times New Roman" && b2 && i2, $"Times New Roman BoldItalic çözüldü: {f2}");
            var calibri = TextRewriter.FindFontFile("Calibri", true, false);
            Check(calibri is not null && calibri.EndsWith("calibrib.ttf", StringComparison.OrdinalIgnoreCase), "Calibri Bold dosyası bulundu: " + calibri);
            var arial = TextRewriter.FindFontFile("Arial", false, true);
            Check(arial is not null && arial.EndsWith("ariali.ttf", StringComparison.OrdinalIgnoreCase), "Arial Italic dosyası bulundu: " + arial);
            Check(TextRewriter.FindFontFile("BöyleBirYazıTipiYok", false, false) is null, "bilinmeyen aile → Arial'a düşer");
            Check(TextRewriter.SubstituteFor("ABCDEF+Calibri", false) is not null, "yedek yazı tipi yüklendi");
            return Task.CompletedTask;
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
