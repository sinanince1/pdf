using System.IO;
using System.IO.Compression;
using PanDaF.Core;
using PdfSharp.Pdf.IO;
using SkiaSharp;

// PanDaF motor testleri. Windows, macOS ve Linux'ta çalışır: dotnet run --project tests/PanDaF.Tests
// PANDAF_LIBREOFFICE=1 ise LibreOffice gerçekten indirilir (Windows) veya kurulu olan kullanılır ve Office dönüşümleri denenir.
internal static class Program
{
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

        string Sample(string path, int width, int height, SKColor color, ushort orientation = 1)
        {
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            surface.Canvas.Clear(SKColors.White);
            using (var paint = new SKPaint { Color = color }) surface.Canvas.DrawRect(new SKRect(0, 0, width / 2f, height / 3f), paint);
            using var image = surface.Snapshot();
            using var data = image.Encode(path.EndsWith(".jpg") ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, 90);
            var bytes = data.ToArray();
            if (orientation != 1) bytes = TestImages.WithExifOrientation(bytes, orientation);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        var images = Folder("resimler");
        var landscape = Sample(Path.Combine(images, "yatay.jpg"), 1600, 1000, new SKColor(70, 130, 180));
        var portrait = Sample(Path.Combine(images, "dikey.png"), 900, 1400, new SKColor(46, 139, 87));
        var rotated = Sample(Path.Combine(images, "telefon fotoğrafı.jpg"), 1200, 800, new SKColor(178, 34, 34), orientation: 6);
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
            using var image = Images.Load(rotated, out var orientation);
            Check(orientation == 6 && image.Width == 800 && image.Height == 1200, $"döndürülmüş fotoğraf dik açıldı ({image.Width}x{image.Height})");
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
            using var frame = SKBitmap.Decode(first);
            Check(frame.Width == 842, $"72 DPI yatay A4 genişliği ({frame.Width} px)");
            var png = await converter.FromPdfAsync([threePages], Folder("png"), PdfTarget.Png, 50, null, default);
            Check(Directory.GetFiles(png[0], "*.png").Length == 3, "3 PNG");
            using var raster = PdfRasterDocument.Open(threePages);
            using var preview = raster.RenderBitmap(1, 36);
            Check(raster.PageCount == 3 && preview.Height == 421, "önizleme çizimi");
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
            var all = TextRewriter.ReplaceAll(bytes, "satır", "SATIR", caseSensitive: true, null, default);
            using var pdf2 = UglyToad.PdfPig.PdfDocument.Open(all.Document);
            Check(all.ReplacedLines == 1 && pdf2.GetPage(1).GetWords().Any(w => w.Text == "SATIR"), $"bul-değiştir: {all.ReplacedLines} satır");
            return Task.CompletedTask;
        });

        await Test("Paragraf yeniden akıtma", () =>
        {
            var pdfPath = Path.Combine(Folder("paragraf"), "p.pdf");
            using (var doc = new PdfSharp.Pdf.PdfDocument())
            {
                var pg = doc.AddPage();
                using var g = PdfSharp.Drawing.XGraphics.FromPdfPage(pg);
                var font = new PdfSharp.Drawing.XFont("Arial", 12);
                new PdfSharp.Drawing.Layout.XTextFormatter(g).DrawString("Sevgili veliler, bu hafta okulumuzda kitap okuma etkinliği düzenlenecektir. Öğrencilerimizin evden sevdikleri bir kitabı getirmelerini rica ederiz. Etkinlik perşembe günü ikinci derste yapılacaktır.", font, PdfSharp.Drawing.XBrushes.Black, new PdfSharp.Drawing.XRect(60, 100, 400, 80), PdfSharp.Drawing.XStringFormats.TopLeft);
                g.DrawString("Saygılarımızla", font, PdfSharp.Drawing.XBrushes.Black, new PdfSharp.Drawing.XPoint(60, 220));
                doc.Save(pdfPath);
            }
            var bytes = File.ReadAllBytes(pdfPath);
            var paragraphs = TextRewriter.Paragraphs(bytes, 0, 842);
            Console.WriteLine("  BİLGİ paragraflar: " + string.Join(" || ", paragraphs.Select(p => $"{p.Lines.Count} satır: {p.Text[..Math.Min(40, p.Text.Length)]}…")));
            Check(paragraphs.Count >= 1 && paragraphs[0].Lines.Count >= 2, $"çok satırlı paragraf bulundu ({paragraphs.Count})");
            if (paragraphs.Count > 0)
            {
                var result = TextRewriter.ReplaceParagraph(bytes, 0, paragraphs[0], "Sevgili veliler, etkinlik cuma günü yapılacaktır. Kitap getirmeyi unutmayın.");
                using var pdf = UglyToad.PdfPig.PdfDocument.Open(result.Document);
                var text = string.Join(" ", pdf.GetPage(1).GetWords().Select(w => w.Text));
                Check(text.Contains("cuma") && !text.Contains("perşembe") && text.Contains("Saygılarımızla"), "paragraf yeniden akıtıldı: " + text);
            }
            return Task.CompletedTask;
        });

        await Test("Yedek yazı tipi eşleme (aynı aile, bilgisayardaki yazı tiplerinden)", () =>
        {
            var (family, bold, italic) = TextRewriter.Parse("ABCDEF+Calibri-Bold", false);
            Check(family == "Calibri" && bold && !italic, $"Calibri-Bold çözüldü: {family} kalın={bold}");
            var (f2, b2, i2) = TextRewriter.Parse("TimesNewRomanPS-BoldItalicMT", false);
            Check(f2 == "Times New Roman" && b2 && i2, $"Times New Roman BoldItalic çözüldü: {f2}");
            if (OperatingSystem.IsWindows())
            {
                var calibri = TextRewriter.FindFontFile("Calibri", true, false);
                Check(calibri is not null && calibri.EndsWith("calibrib.ttf", StringComparison.OrdinalIgnoreCase), "Calibri Bold dosyası bulundu: " + calibri);
                var arial = TextRewriter.FindFontFile("Arial", false, true);
                Check(arial is not null && arial.EndsWith("ariali.ttf", StringComparison.OrdinalIgnoreCase), "Arial Italic dosyası bulundu: " + arial);
            }
            else Console.WriteLine("  BİLGİ Arial: " + (TextRewriter.FindFontFile("Arial", false, false) ?? "(yok, yedek kullanılacak)"));
            Check(TextRewriter.FindFontFile("BöyleBirYazıTipiYok", false, false) is null, "bilinmeyen aile → Arial'a düşer");
            Check(TextRewriter.SubstituteFor("ABCDEF+Calibri", false) is not null, "yedek yazı tipi yüklendi");
            return Task.CompletedTask;
        });

        await Test("Sayfa içeriği ve satırı taşıma: taban çizgisi, renk, resim", () =>
        {
            var folder = Folder("icerik");
            var source = Path.Combine(folder, "kaynak.pdf");
            using (var doc = new PdfSharp.Pdf.PdfDocument())
            {
                var pg = doc.AddPage(); pg.Width = PdfSharp.Drawing.XUnit.FromPoint(595); pg.Height = PdfSharp.Drawing.XUnit.FromPoint(842);
                using var g = PdfSharp.Drawing.XGraphics.FromPdfPage(pg);
                g.DrawString("Birinci satır yerinde kalır", new PdfSharp.Drawing.XFont("Arial", 14), PdfSharp.Drawing.XBrushes.Black, new PdfSharp.Drawing.XPoint(60, 100));
                g.DrawString("Taşınacak mavi satır", new PdfSharp.Drawing.XFont("Arial", 16, PdfSharp.Drawing.XFontStyleEx.Bold), PdfSharp.Drawing.XBrushes.DarkBlue, new PdfSharp.Drawing.XPoint(60, 140));
                using var picture = PdfSharp.Drawing.XImage.FromFile(landscape);
                g.DrawImage(picture, 300, 200, 160, 100);
                doc.Save(source);
            }
            var bytes = File.ReadAllBytes(source);
            var content = TextRewriter.Page(bytes, 0);
            Check(content.Lines.Count == 2, $"iki satır bulundu ({content.Lines.Count})");
            var line = content.Lines.First(l => l.Text.StartsWith("Taşınacak"));
            Check(Math.Abs(line.Baseline - 140) < .6 && line.Color == "#00008B" && line.Bold && Math.Abs(line.FontSize - 16) < .1, $"taban çizgisi, renk, kalınlık ve boyut okundu ({line.Baseline:0.0}, {line.Color}, {line.Bold}, {line.FontSize})");
            Check(content.Images.Count == 1 && Math.Abs(content.Images[0].X - 300) < 1 && Math.Abs(content.Images[0].Y - 200) < 1 && Math.Abs(content.Images[0].Width - 160) < 1, $"resmin yeri okundu ({content.Images.Count})");
            // Düzenleyicinin yaptığı gibi: satırı belgeden çıkar, aynı yazı tipiyle 150 pt sağa ve 200 pt aşağıya yaz.
            var family = TextRewriter.SystemFamily(line.FontName) ?? "";
            var (ascent, lineHeight) = PdfAnnotator.TextMetrics(family, line.FontSize, line.Bold, line.Italic);
            var mark = new Mark { Kind = MarkKind.Text, Page = 0, X = line.X - 2 + 150, Y = line.Baseline - ascent - 2 + 200, Width = line.Width + 40, Height = lineHeight + 4, Text = line.Text, FontSize = line.FontSize, Bold = line.Bold, Color = line.Color, FontFamily = family };
            var removed = TextRewriter.Replace(bytes, 0, line, " ").Document;
            var work = Path.Combine(folder, "ara.pdf"); File.WriteAllBytes(work, removed);
            var output = Path.Combine(folder, "tasindi.pdf");
            PdfAnnotator.Save(work, output, [mark]);
            var after = TextRewriter.Lines(File.ReadAllBytes(output), 0);
            var moved = after.Where(l => l.Text.StartsWith("Taşınacak")).ToList();
            Check(moved.Count == 1, $"satır eski yerinden çıktı, yenisi tek ({moved.Count})");
            Check(moved.Count == 1 && Math.Abs(moved[0].Baseline - 340) < .8 && Math.Abs(moved[0].X - (line.X + 150)) < 1.5 && moved[0].Color == "#00008B",
                $"taşınan satır aynı taban çizgisinde ve renkte ({(moved.Count == 1 ? $"{moved[0].X:0.0},{moved[0].Baseline:0.0} {moved[0].Color}" : "yok")}; beklenen {line.X + 150:0.0},340.0)");
            Check(after.Any(l => l.Text.StartsWith("Birinci")), "öbür satır duruyor");
            // Yerinde değiştirilen yazı görünür mü denetimi: beyaz yazılan satır yakalanır, düzgün yazılan yakalanmaz.
            var first = content.Lines.First(l => l.Text.StartsWith("Birinci"));
            var white = TextRewriter.Replace(bytes, 0, first, "Beyaza dönen yazı", fillColor: "#FFFFFF").Document;
            var normal = TextRewriter.Replace(bytes, 0, first, "Düzgün değişen ve eskisinden epeyce daha uzun bir yazı").Document;
            Check(TextRewriter.Invisible(bytes, white, 0, first) && !TextRewriter.Invisible(bytes, normal, 0, first), "zeminle aynı renkte yazılan satır fark edildi");
            var red = TextRewriter.Lines(TextRewriter.Replace(bytes, 0, first, "Kırmızı yazı", fillColor: "#C00000").Document, 0).First(l => l.Text.StartsWith("Kırmızı"));
            Check(red.Color == "#C00000", $"satır istenen renkle yazılabiliyor ({red.Color})");
            var visible = TextRewriter.ReplaceVisibly(bytes, 0, first, "Görünür kalan yazı");
            Check(!visible.ColorRestored && TextRewriter.Lines(visible.Document, 0).First(l => l.Text.StartsWith("Görünür")).Color == "#000000", "düzgün değişen satırın rengine dokunulmadı");
            return Task.CompletedTask;
        });

        await Test("Eklenen yazıda yazı tipi ailesi ve koleksiyon (.ttc) dosyaları", () =>
        {
            var folder = Folder("yazi-tipi");
            var family = new[] { "Times New Roman", "Liberation Serif", "DejaVu Serif", "Georgia", "Comic Sans MS" }.FirstOrDefault(f => TextRewriter.FindFontFile(f, false, false) is not null && PdfAnnotator.Usable(f));
            if (family is null) Console.WriteLine("  BİLGİ Arial dışında bilinen bir aile kurulu değil; aile testi atlandı");
            else
            {
                var output = Path.Combine(folder, "aile.pdf");
                PdfAnnotator.Save(threePages, output, [
                    new Mark { Kind = MarkKind.Text, Page = 0, X = 60, Y = 60, Width = 400, Height = 30, Text = "Seçilen aileyle yazı ĞÜŞİÖÇ", FontSize = 16, FontFamily = family },
                    new Mark { Kind = MarkKind.Text, Page = 0, X = 60, Y = 100, Width = 400, Height = 30, Text = "Arial ile yazı", FontSize = 16 }]);
                using var pdf = UglyToad.PdfPig.PdfDocument.Open(output);
                var names = pdf.GetPage(1).Letters.Select(l => (l.FontName ?? "").Replace(" ", "")).Distinct().ToList();
                Check(names.Any(n => n.Contains(family.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)), $"{family} PDF'ye gömüldü: {string.Join(", ", names)}");
                Check(names.Count >= 2, "Arial yazısı ayrı yazı tipiyle duruyor");
                Check(TextRewriter.SystemFamily("ABCDEF+" + family.Replace(" ", "") + "-Bold") is { } system && TextRewriter.FindFontFile(TextRewriter.Parse(system, false).Family, false, false) is not null, "PDF yazı tipi adı kurulu aileye eşlendi");
            }
            Check(TextRewriter.SystemFamily("ABCDEF+BöyleBirYazıTipiYok") is null, "kurulu olmayan aile listelenmez");
            // İki TrueType dosyasından bir koleksiyon kur, ikinci yüzü geri çıkar.
            var first = WindowsFontResolver.Locate("arial.ttf", "Arial.ttf", "LiberationSans-Regular.ttf", "DejaVuSans.ttf");
            var second = WindowsFontResolver.Locate("arialbd.ttf", "Arial Bold.ttf", "LiberationSans-Bold.ttf", "DejaVuSans-Bold.ttf");
            if (first is null || second is null) { Console.WriteLine("  BİLGİ koleksiyon testi için iki yazı tipi dosyası bulunamadı"); return Task.CompletedTask; }
            var faces = new[] { File.ReadAllBytes(first), File.ReadAllBytes(second) };
            var collection = new MemoryStream();
            void Write32(uint value) { Span<byte> b = stackalloc byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b, value); collection.Write(b); }
            collection.Write("ttcf"u8); Write32(0x00010000); Write32(2);
            var position = 12 + 8; var starts = new List<int>();
            foreach (var face in faces) { starts.Add(position); position += face.Length + 3 & ~3; }
            foreach (var start in starts) Write32((uint)start);
            for (var i = 0; i < faces.Length; i++)
            {
                var face = (byte[])faces[i].Clone();
                int tables = face[4] << 8 | face[5];
                for (var t = 0; t < tables; t++)
                {
                    var record = 12 + t * 16 + 8;
                    var offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(face.AsSpan(record, 4)) + (uint)starts[i];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(face.AsSpan(record, 4), offset);
                }
                collection.Write(face);
                while (collection.Length % 4 != 0) collection.WriteByte(0);
            }
            var path = Path.Combine(folder, "deneme.ttc"); File.WriteAllBytes(path, collection.ToArray());
            var extracted = FontFiles.Read(path, 1);
            using var original = SKTypeface.FromFile(second);
            using var data = SKData.CreateCopy(extracted);
            using var typeface = SKTypeface.FromData(data);
            Check(FontFiles.IsCollection(File.ReadAllBytes(path)) && !FontFiles.IsCollection(extracted) && typeface is not null && typeface.FamilyName == original.FamilyName && typeface.IsBold == original.IsBold && typeface.GlyphCount == original.GlyphCount,
                $"koleksiyondan ikinci yüz çıkarıldı ({typeface?.FamilyName}, {typeface?.GlyphCount} glif)");
            Check(KillerPdf.Engine.Fonts.TrueTypeFont.Load(extracted) is not null, "çıkarılan yüz düzenleme motorunda yüklendi");
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
