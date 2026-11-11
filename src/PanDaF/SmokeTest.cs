using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using PanDaF.Core;
using PanDaF.Views;
using SkiaSharp;

namespace PanDaF;

/// "PanDaF --smoke klasör" ile çalışır: gerçek pencerede her aracı açar, gerçek dosyalarla
/// işlem yaptırır ve ekran görüntülerini kaydeder. Otomatik derlemede (GitHub Actions) kullanılır.
internal static class SmokeTest
{
    public static async Task<int> RunAsync(MainWindow window, string folder)
    {
        var log = new List<string>();
        void Check(bool condition, string message) { log.Add((condition ? "OK   " : "FAIL ") + message); if (!condition) throw new InvalidOperationException(message); }
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            var input = Directory.CreateDirectory(Path.Combine(folder, "girdi")).FullName;
            var shots = Directory.CreateDirectory(Path.Combine(folder, "ekran")).FullName;
            await Settle(800);
            Capture(window, Path.Combine(shots, "01-ana-sayfa.png"));

            // Örnek resimler: biri yatay, biri dikey, biri saydam PNG.
            var images = new[]
            {
                Sample(Path.Combine(input, "Yatay resim.jpg"), 1600, 1000, new SKColor(70, 130, 180), "1"),
                Sample(Path.Combine(input, "Dikey resim.png"), 900, 1400, new SKColor(46, 139, 87), "2"),
                Sample(Path.Combine(input, "Çalışma kağıdı.png"), 1240, 1754, new SKColor(255, 140, 0), "3"),
            };

            // PDF'ye çevir (arayüz üzerinden)
            var toPdf = new ToPdfView(window); window.Navigate(toPdf, "PDF'ye çevir"); await Settle();
            Capture(window, Path.Combine(shots, "02-pdfye-cevir-bos.png"));
            toPdf.Add(images); await Settle();
            Capture(window, Path.Combine(shots, "03-pdfye-cevir-liste.png"));
            Click(toPdf.Go);
            var created = await Result(window);
            Check(created.Count == 1 && PdfPages.PageCount(created[0]) == 3, "Resimler tek PDF'de birleşti (3 sayfa)");
            var threePages = created[0];

            // Birleştir
            var merge = new MergeView(window); window.Navigate(merge, "PDF birleştir"); await Settle();
            var copy = Path.Combine(input, "Kopya.pdf"); File.Copy(threePages, copy);
            merge.Add([threePages, copy]); await Settle(600);
            Capture(window, Path.Combine(shots, "04-birlestir.png"));
            Click(merge.Go);
            var merged = await Result(window);
            Check(PdfPages.PageCount(merged[0]) == 6, "İki PDF birleşti (6 sayfa)");
            Capture(window, Path.Combine(shots, "05-sonuc.png"));

            // Böl
            var split = new SplitPdfView(window); window.Navigate(split, "PDF böl"); await Settle();
            split.Load(merged[0]); await WaitUntil(() => split.Go.IsEnabled);
            Capture(window, Path.Combine(shots, "06-bol.png"));
            Click(split.Go);
            var parts = await Result(window);
            Check(Directory.Exists(parts[0]) && Directory.GetFiles(parts[0], "*.pdf").Length == 6, "Her sayfa ayrı PDF oldu (6 dosya)");
            split.Extract.IsChecked = true; split.Ranges.Text = "2-3, 5"; await Settle();
            Click(split.Go);
            var extracted = await Result(window);
            Check(PdfPages.PageCount(extracted[0]) == 3, "Seçilen sayfalar alındı (3 sayfa)");

            // Sayfaları düzenle
            var organize = new OrganizeView(window); window.Navigate(organize, "Sayfaları düzenle"); await Settle();
            Capture(window, Path.Combine(shots, "07-duzenle-bos.png"));
            organize.Load(merged[0]);
            await WaitUntil(() => organize.PageCount == 6 && organize.LoadedThumbnails == 6, 30000);
            await Settle();
            Capture(window, Path.Combine(shots, "08-duzenle-sayfalar.png"));
            organize.SmokeEdit(); await Settle();
            Capture(window, Path.Combine(shots, "09-duzenle-degisti.png"));
            organize.SmokeSave();
            var organized = await Result(window);
            Check(PdfPages.PageCount(organized[0]) == 5, "Sayfa silindi ve sıralandı (5 sayfa)");

            // PDF'yi düzenle
            var edit = new EditView(window); window.Navigate(edit, "PDF'yi düzenle"); await Settle();
            Capture(window, Path.Combine(shots, "16-duzenle-bos.png"));
            edit.Load(merged[0]);
            await WaitUntil(() => edit.MarkCount == 0 && edit.Toolbar.IsVisible, 30000);
            await Settle(600);
            edit.SmokeAdd(); await Settle();
            Capture(window, Path.Combine(shots, "17-duzenle-eklendi.png"));
            var editTask = edit.SaveAsync();
            var edited = await Result(window);
            await editTask;
            Check(edited.Count == 1 && edited[0].EndsWith(" (düzenlendi).pdf") && PdfPages.PageCount(edited[0]) == 6, $"Düzenlenen PDF kaydedildi (6 sayfa): {string.Join(", ", edited.Select(Path.GetFileName))}");
            using (var check = UglyToad.PdfPig.PdfDocument.Open(edited[0]))
            {
                var words = check.GetPage(1).GetWords().Select(w => w.Text).ToList();
                Check(words.Contains("Düzeltilmiş") && words.Contains("ĞÜŞİÖÇ"), "Eklenen Türkçe yazı PDF'de okunuyor: " + string.Join(" ", words));
                Check(check.GetPage(1).GetImages().Count() >= 3, "Logolar sayfaya gömüldü");
            }
            using (var raster = PdfRasterDocument.Open(edited[0])) { using var page = raster.RenderBitmap(0, 72); File.WriteAllBytes(Path.Combine(shots, "18-duzenlenmis-sayfa.png"), Images.Png(page)); }

            // Yerinde yazı değiştirme (KillerPdf.Engine)
            var textPdf = Path.Combine(input, "Duyuru.pdf");
            using (var doc = new PdfSharp.Pdf.PdfDocument())
            {
                var pg = doc.AddPage(); pg.Width = PdfSharp.Drawing.XUnit.FromPoint(595); pg.Height = PdfSharp.Drawing.XUnit.FromPoint(842);
                using var g = PdfSharp.Drawing.XGraphics.FromPdfPage(pg);
                g.DrawString("Veli Toplantısı 12 Kasım Çarşamba", new PdfSharp.Drawing.XFont("Arial", 18, PdfSharp.Drawing.XFontStyleEx.Bold), PdfSharp.Drawing.XBrushes.DarkBlue, new PdfSharp.Drawing.XPoint(60, 100));
                g.DrawString("Saat 15.30, 2A sınıfı", new PdfSharp.Drawing.XFont("Arial", 13), PdfSharp.Drawing.XBrushes.Black, new PdfSharp.Drawing.XPoint(60, 130));
                doc.Save(textPdf);
            }
            var rewrite = new EditView(window); window.Navigate(rewrite, "PDF'yi düzenle"); await Settle();
            rewrite.Load(textPdf);
            await WaitUntil(() => rewrite.Toolbar.IsVisible, 30000);
            await Settle(500);
            await rewrite.SmokeRewrite(1, "Saat 16.00, 2A sınıfı");
            await Settle(500);
            Check(rewrite.DocumentChanged && rewrite.LineCount == 2, "Satır yerinde değiştirildi, sayfa yeniden okundu");
            Check(PandaWheel.For([textPdf]).Count == 6, "panel seçenekleri");
            Capture(window, Path.Combine(shots, "19-yazi-degistir.png"));
            // Bul ve değiştir (tüm belge)
            var all = await rewrite.ReplaceAllAsync("2A", "2B", caseSensitive: true);
            Check(all is { ReplacedLines: 1, FailedLines: 0 }, $"Bul ve değiştir: {all?.ReplacedLines} satır değişti, {all?.FailedLines} başarısız");
            var rewriteTask = rewrite.SaveAsync();
            var rewritten = await Result(window);
            await rewriteTask;
            using (var check = UglyToad.PdfPig.PdfDocument.Open(rewritten[0]))
            {
                var text = string.Join(" ", check.GetPage(1).GetWords().Select(w => w.Text));
                Check(text.Contains("16.00") && !text.Contains("15.30") && text.Contains("Veli Toplantısı 12 Kasım Çarşamba") && text.Contains("2B") && !text.Contains("2A"), "Yeni yazı var, eskisi yok, diğer satır duruyor, 2A→2B: " + text);
            }

            // Paragraf düzenleme
            var paraPdf = Path.Combine(input, "Paragraf.pdf");
            using (var doc = new PdfSharp.Pdf.PdfDocument())
            {
                var pg = doc.AddPage(); pg.Width = PdfSharp.Drawing.XUnit.FromPoint(595); pg.Height = PdfSharp.Drawing.XUnit.FromPoint(842);
                using var g = PdfSharp.Drawing.XGraphics.FromPdfPage(pg);
                var font = new PdfSharp.Drawing.XFont("Arial", 12);
                var tf = new PdfSharp.Drawing.Layout.XTextFormatter(g);
                tf.DrawString("Sevgili veliler, bu hafta okulumuzda kitap okuma etkinliği düzenlenecektir. Öğrencilerimizin evden sevdikleri bir kitabı getirmelerini rica ederiz. Etkinlik perşembe günü ikinci derste yapılacaktır.", font, PdfSharp.Drawing.XBrushes.Black, new PdfSharp.Drawing.XRect(60, 100, 400, 80), PdfSharp.Drawing.XStringFormats.TopLeft);
                g.DrawString("Saygılarımızla, 2A öğretmeni", font, PdfSharp.Drawing.XBrushes.Black, new PdfSharp.Drawing.XPoint(60, 220));
                doc.Save(paraPdf);
            }
            var paraView = new EditView(window); window.Navigate(paraView, "PDF'yi düzenle"); await Settle();
            paraView.Load(paraPdf);
            await WaitUntil(() => paraView.Toolbar.IsVisible, 30000);
            await Settle(500);
            await paraView.SmokeParagraph(0, "Sevgili veliler, bu hafta okulumuzda kitap okuma etkinliği düzenlenecektir. Öğrenciler sevdikleri bir kitabı getirsin. Etkinlik cuma günü üçüncü derste yapılacaktır.");
            await Settle(500);
            Check(paraView.DocumentChanged, "Paragraf yeniden akıtıldı");
            Check(paraView.ParagraphCount >= 1, $"Değişen paragraf hâlâ paragraf olarak tanınıyor ({paraView.ParagraphCount})");
            Capture(window, Path.Combine(shots, "20-paragraf.png"));
            var paraTask = paraView.SaveAsync();
            var paraOut = await Result(window);
            await paraTask;
            using (var check = UglyToad.PdfPig.PdfDocument.Open(paraOut[0]))
            {
                var text = string.Join(" ", check.GetPage(1).GetWords().Select(w => w.Text));
                Check(text.Contains("cuma") && !text.Contains("perşembe") && text.Contains("Saygılarımızla"), "Paragraf metni değişti, altındaki satır duruyor: " + text);
            }

            // Seç ve taşı, yazı ekle, düzenleme kutusu (gerçek işaretçi olaylarıyla)
            var movePdf = Path.Combine(input, "Çalışma kâğıdı.pdf");
            using (var doc = new PdfSharp.Pdf.PdfDocument())
            {
                var pg = doc.AddPage(); pg.Width = PdfSharp.Drawing.XUnit.FromPoint(595); pg.Height = PdfSharp.Drawing.XUnit.FromPoint(842);
                using var g = PdfSharp.Drawing.XGraphics.FromPdfPage(pg);
                for (var i = 0; i < 22; i++) g.DrawString($"Soru {i + 1}: örnek yazı burada duruyor", new PdfSharp.Drawing.XFont("Arial", 13), PdfSharp.Drawing.XBrushes.Black, new PdfSharp.Drawing.XPoint(60, 90 + i * 32));
                using var picture = PdfSharp.Drawing.XImage.FromFile(images[0]);
                g.DrawImage(picture, 360, 100, 160, 100);
                doc.Save(movePdf);
            }
            var move = new EditView(window); window.Navigate(move, "PDF'yi düzenle"); await Settle();
            move.Load(movePdf);
            await WaitUntil(() => move.Toolbar.IsVisible && move.ObjectCount > 0, 30000);
            await Settle(500);
            Check(move.ObjectCount == 23, $"Sayfadaki seçilebilir öğeler bulundu: 22 satır ve 1 resim ({move.ObjectCount})");
            async Task Idle() { await Settle(200); await WaitUntil(() => !move.Busy, 60000); await Settle(300); }
            async Task Drag(double x, double y, double dx, double dy)
            {
                move.SmokePointer(0, x, y);
                for (var i = 1; i <= 5; i++) move.SmokePointer(1, x + dx * i / 5, y + dy * i / 5);
                move.SmokePointer(2, x + dx, y + dy);
                await Idle();
            }
            // Sayfanın altındaki satır düzenlemeye açılınca görünüm yerinde kalır; kutu yeniden çizimde kapanmaz.
            move.SmokeZoom(2); await Settle(400);
            move.ScrollOffset = new Vector(0, move.ScrollExtent.Height); await Settle(400);
            var scrolled = move.ScrollOffset;
            await move.SmokeRewriteBox(20, .5); await Settle(500);
            Check(move.RewriteBoxOpen && Math.Abs(move.ScrollOffset.Y - scrolled.Y) < 2 && Math.Abs(move.ScrollOffset.X - scrolled.X) < 2, $"Alttaki satır düzenlenirken sayfa yerinde kaldı (kaydırma {scrolled.Y:0} → {move.ScrollOffset.Y:0})");
            move.SmokeRedraw(); await Settle();
            Check(move.RewriteBoxOpen && move.RewriteCaret is > 5 and < 30, $"Düzenleme kutusu yeniden çizimde açık kaldı, imleç tıklanan yerde ({move.RewriteCaret}; odak {move.RewriteBoxFocused})");
            Capture(window, Path.Combine(shots, "21-alttaki-satir.png"));
            move.SmokeCancelRewrite(); move.SmokeZoom(1); move.ScrollOffset = default; await Settle(400);
            // Tek satırı sürükle
            move.SmokeTool("select");
            var q3 = move.SmokeLine("Soru 3:");
            await Drag(q3.X + 20, q3.Y + 4, 200, 6);
            Check(!move.HasLine("Soru 3:") && move.SmokeMarks.Any(m => m.Kind == MarkKind.Text && m.Text.StartsWith("Soru 3:") && Math.Abs(m.X - (q3.X - 2 + 200)) < 1), "Sayfadaki satır sürüklenerek taşındı; eski yerinden çıkarıldı");
            // Alan seçip birlikte sürükle
            var q5 = move.SmokeLine("Soru 5:"); var q7 = move.SmokeLine("Soru 7:");
            await Drag(q5.X - 20, q5.Y - 6, q5.Width + 60, q7.Y + q7.Height - q5.Y + 12);
            Check(move.PickedCount == 3, $"Alan seçimi üç satırı seçti ({move.PickedCount})");
            await Drag(q5.X + 10, q5.Y + 4, 220, 0);
            Check(!move.HasLine("Soru 5:") && !move.HasLine("Soru 6:") && !move.HasLine("Soru 7:") && move.SmokeMarks.Count(m => m.Kind == MarkKind.Text && Math.Abs(m.X - (q5.X - 2 + 220)) < 1) == 3 && move.ChosenCount == 3, "Seçilen üç satır birlikte taşındı");
            // Boş yere çizilen alan bir şey seçmez
            await Drag(430, 600, 90, 70);
            Check(move.PickedCount == 0 && move.ChosenCount == 0, "Boş alanda seçim oluşmadı");
            // Sayfadaki satırı sil
            var q9 = move.SmokeLine("Soru 9:");
            move.SmokePointer(0, q9.X + 10, q9.Y + 4); move.SmokePointer(2, q9.X + 10, q9.Y + 4); await Idle();
            Check(move.PickedCount == 1, "Sayfadaki satır tıklanınca seçildi");
            move.SmokeDelete(); await Idle();
            Check(!move.HasLine("Soru 9:") && !move.SmokeMarks.Any(m => m.Text.StartsWith("Soru 9:")), "Seçilen satır sayfadan silindi");
            // Resmi taşı, geri al, yeniden taşı
            await Drag(505, 186, -40, 420);
            Check(move.SmokeMarks.Any(m => m.Kind == MarkKind.Image) && move.SmokeMarks.Any(m => m.Cover), "Sayfadaki resim taşındı, eski yeri örtüldü");
            move.SmokeUndo(); await Idle();
            Check(!move.SmokeMarks.Any(m => m.Kind == MarkKind.Image) && !move.SmokeMarks.Any(m => m.Cover), "Geri al resmi eski yerine döndürdü");
            await Drag(505, 186, -40, 420);
            Check(move.SmokeMarks.Count(m => m.Kind == MarkKind.Image) == 1 && move.SmokeMarks.Count(m => m.Cover) == 1, "Geri alınan resim yeniden taşınabildi");
            // Yazı ekle: kutu açılır, yazılır, yeniden çizimde kapanmaz
            move.SmokeTool("text");
            move.SmokePointer(0, 360, 330); move.SmokePointer(2, 360, 330); await Settle(400);
            Check(move.EditorOpen, "Yazı ekle: tıklanan yerde yazı kutusu açıldı");
            move.SmokeType("Elle eklenen yazı ĞÜŞİÖÇ"); await Settle(); move.SmokeRedraw(); await Settle();
            Check(move.EditorOpen, "Yazı kutusu yazarken açık kaldı");
            move.SmokeEndEdit(); await Settle();
            Capture(window, Path.Combine(shots, "22-sec-ve-tasi.png"));
            var moveTask = move.SaveAsync();
            var movedOut = await Result(window);
            await moveTask;
            var movedLines = TextRewriter.Lines(File.ReadAllBytes(movedOut[0]), 0);
            var moved3 = movedLines.FirstOrDefault(l => l.Text.StartsWith("Soru 3:"));
            Check(moved3 is not null && Math.Abs(moved3.X - (q3.X + 200)) < 2.5 && Math.Abs(moved3.Y - (q3.Y + 6)) < 2.5, $"Kaydedilen PDF'de taşınan satır yeni yerinde ({moved3?.X:0.0},{moved3?.Y:0.0}; beklenen {q3.X + 200:0.0},{q3.Y + 6:0.0})");
            Check(movedLines.Count(l => l.Text.StartsWith("Soru 3:")) == 1 && !movedLines.Any(l => l.Text.StartsWith("Soru 9:")) && movedLines.Any(l => l.Text.StartsWith("Elle eklenen yazı ĞÜŞİÖÇ")), "Kaydedilen PDF: taşınan satır tek, silinen yok, eklenen yazı var");
            using (var raster = PdfRasterDocument.Open(movedOut[0])) { using var bitmap = raster.RenderBitmap(0, 72); File.WriteAllBytes(Path.Combine(shots, "23-tasinmis-sayfa.png"), Images.Png(bitmap)); }

            // PDF'den resme
            var fromPdf = new FromPdfView(window); window.Navigate(fromPdf, "PDF'den çevir"); await Settle();
            fromPdf.Add([threePages]); fromPdf.ToJpg.IsChecked = true; await Settle(600);
            Capture(window, Path.Combine(shots, "10-pdfden-cevir.png"));
            Click(fromPdf.Go);
            var pictures = await Result(window);
            Check(Directory.Exists(pictures[0]) && Directory.GetFiles(pictures[0], "*.jpg").Length == 3, "PDF sayfaları JPG oldu (3 resim)");

            // Shift paneli: seçenekler ve görünüm
            Check(PandaWheel.For([threePages]).Select(c => c.Id).SequenceEqual(["edit", "organize", "split", "word", "excel", "jpg"]), "Tek PDF için panel seçenekleri");
            Check(PandaWheel.For([threePages, copy]).Select(c => c.Id).SequenceEqual(["merge", "word", "excel", "jpg"]), "Birden fazla PDF için panel seçenekleri");
            Check(PandaWheel.For(images).Select(c => c.Id).SequenceEqual(["pdf-combine", "pdf-separate", "open"]), "Birden fazla resim için panel seçenekleri");
            Check(PandaWheel.For([images[0]]).First().Id == "pdf", "Tek resim için panel: PDF yap");
            var panels = new List<(IReadOnlyList<string> Sample, string Name, int Index)>
            {
                (new[] { threePages }, "11-panel-tek-pdf.png", 1),
                (new[] { threePages, copy }, "12-panel-cok-pdf.png", 0),
                (images, "13-panel-resimler.png", 0),
            };
            foreach (var (sample, name, index) in panels)
            {
                var wheel = new PandaWheel((_, _) => { });
                wheel.Preview(sample, index);
                wheel.Show();
                await Settle(500);
                Capture(wheel, Path.Combine(shots, name));
                wheel.Close();
            }

            // Panelden sessiz işlem: pencere açılmadan, dosyanın yanına kaydeder
            var quickTask = window.QuickConvertAsync("jpg", [threePages]);
            await Settle(150);
            var quick = await quickTask;
            Check(quick.Count == 1 && Directory.Exists(quick[0]) && Directory.GetFiles(quick[0], "*.jpg").Length == 3, "Panelden sessiz işlem: PDF → resim");
            var single = await window.QuickConvertAsync("pdf", [images[0]]);
            Check(single.Count == 1 && Path.GetFileName(single[0]) == "Yatay resim.pdf" && Path.GetDirectoryName(single[0]) == Path.GetDirectoryName(images[0]), "Panelden sessiz işlem: resim → aynı klasöre aynı adla PDF");
            var combined = await window.QuickConvertAsync("pdf-combine", images);
            Check(combined.Count == 1 && PdfPages.PageCount(combined[0]) == 3, "Panelden sessiz işlem: 3 resim → tek PDF");
            Check(!window.ResultOverlay.IsVisible, "Sessiz işlemde uygulama penceresi sonuç göstermedi");
            foreach (var toast in Windows.OfType<Toast>().ToList()) toast.Close();

            window.GoHome(); await Settle();
            Check(window.SmokeErrors.Count == 0, "Arayüz hata göstermedi" + (window.SmokeErrors.Count > 0 ? ": " + string.Join(" | ", window.SmokeErrors) : ""));
            log.Add("SONUÇ: BAŞARILI");
            return 0;
        }
        catch (Exception error)
        {
            log.Add("HATA: " + error);
            if (window.SmokeErrors.Count > 0) log.Add("Arayüz hataları: " + string.Join(" | ", window.SmokeErrors));
            return 1;
        }
        finally
        {
            Directory.CreateDirectory(folder);
            File.WriteAllLines(Path.Combine(folder, "rapor.txt"), log);
            foreach (var line in log) Console.WriteLine(line);
        }
    }

    private static IEnumerable<Window> Windows => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows ?? [];

    private static void Click(Button button)
    {
        if (!button.IsEnabled) throw new InvalidOperationException($"“{button.Content}” butonu etkin değil.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static async Task<IReadOnlyList<string>> Result(MainWindow window)
    {
        await WaitUntil(() => window.ResultOverlay.IsVisible || window.SmokeErrors.Count > 0, 120000);
        if (window.SmokeErrors.Count > 0) throw new InvalidOperationException("Arayüz hatası: " + string.Join(" | ", window.SmokeErrors));
        var outputs = window.LastOutputs;
        window.ResultOverlay.IsVisible = false;
        await Settle();
        return outputs;
    }

    private static async Task WaitUntil(Func<bool> condition, int timeout = 20000)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeout) throw new TimeoutException("Beklenen durum oluşmadı.");
            await Task.Delay(100);
        }
    }

    private static async Task Settle(int milliseconds = 300)
    {
        await Task.Delay(milliseconds);
        App.Main?.UpdateLayout();
    }

    private static void Capture(Window window, string path)
    {
        if (window.Content is not Control root || root.Bounds.Width < 1) return;
        var scale = window.RenderScaling;
        var size = new PixelSize((int)Math.Ceiling(root.Bounds.Width * scale), (int)Math.Ceiling(root.Bounds.Height * scale));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(root);
        bitmap.Save(path);
    }

    private static string Sample(string path, int width, int height, SKColor color, string label)
    {
        var transparent = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && label == "2";
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(transparent ? SKColors.Transparent : SKColors.White);
        using (var fill = new SKPaint { Color = color, IsAntialias = true })
            canvas.DrawRoundRect(new SKRect(width * .1f, height * .1f, width * .9f, height * .9f), 60, 60, fill);
        using (var font = new SKFont(SKTypeface.Default, height * .4f))
        using (var paint = new SKPaint { Color = SKColors.White, IsAntialias = true })
        {
            var textWidth = font.MeasureText(label);
            canvas.DrawText(label, (width - textWidth) / 2, height / 2f + font.Size * .35f, font, paint);
        }
        using var image = surface.Snapshot();
        using var data = image.Encode(path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, 88);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }
}
