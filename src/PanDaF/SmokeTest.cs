using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PanDaF.Core;
using PanDaF.Views;

namespace PanDaF;

/// "PanDaF.exe --smoke klasör" ile çalışır: gerçek pencerede her aracı açar, gerçek dosyalarla
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
            await Settle();
            Capture(window, Path.Combine(shots, "01-ana-sayfa.png"));

            // Örnek resimler: biri yatay, biri dikey, biri saydam PNG.
            var images = new[]
            {
                Sample(Path.Combine(input, "Yatay resim.jpg"), 1600, 1000, Colors.SteelBlue, "1"),
                Sample(Path.Combine(input, "Dikey resim.png"), 900, 1400, Colors.SeaGreen, "2"),
                Sample(Path.Combine(input, "Çalışma kağıdı.png"), 1240, 1754, Colors.DarkOrange, "3"),
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
            var split = new SplitView(window); window.Navigate(split, "PDF böl"); await Settle();
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
            await WaitUntil(() => edit.IsLoaded && edit.MarkCount == 0 && ((FrameworkElement)edit.FindName("Toolbar")).Visibility == Visibility.Visible, 30000);
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
            using (var raster = PdfRasterDocument.Open(edited[0])) Capture(raster.RenderBitmap(0, 72), Path.Combine(shots, "18-duzenlenmis-sayfa.png"));

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
            await WaitUntil(() => ((FrameworkElement)rewrite.FindName("Toolbar")).Visibility == Visibility.Visible, 30000);
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
            await WaitUntil(() => ((FrameworkElement)paraView.FindName("Toolbar")).Visibility == Visibility.Visible, 30000);
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
                await Settle();
                Capture(wheel, Path.Combine(shots, name));
                wheel.Close();
            }

            // Panelden sessiz işlem: pencere açılmadan, dosyanın yanına kaydeder
            var overlay = (Grid)window.FindName("ResultOverlay");
            var quickTask = window.QuickConvertAsync("jpg", [threePages]);
            await Settle(150);
            CaptureToast(Path.Combine(shots, "14-bildirim-calisiyor.png"));
            var quick = await quickTask;
            Check(quick.Count == 1 && Directory.Exists(quick[0]) && Directory.GetFiles(quick[0], "*.jpg").Length == 3, "Panelden sessiz işlem: PDF → resim");
            await Settle();
            CaptureToast(Path.Combine(shots, "15-bildirim-hazir.png"));
            var single = await window.QuickConvertAsync("pdf", [images[0]]);
            Check(single.Count == 1 && Path.GetFileName(single[0]) == "Yatay resim.pdf" && Path.GetDirectoryName(single[0]) == Path.GetDirectoryName(images[0]), "Panelden sessiz işlem: resim → aynı klasöre aynı adla PDF");
            var combined = await window.QuickConvertAsync("pdf-combine", images);
            Check(combined.Count == 1 && PdfPages.PageCount(combined[0]) == 3, "Panelden sessiz işlem: 3 resim → tek PDF");
            Check(overlay.Visibility != Visibility.Visible, "Sessiz işlemde uygulama penceresi sonuç göstermedi");
            foreach (var toast in Application.Current.Windows.OfType<Toast>().ToList()) toast.Close();

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

    private static void Click(Button button)
    {
        if (!button.IsEnabled) throw new InvalidOperationException($"“{button.Content}” butonu etkin değil.");
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    private static async Task<IReadOnlyList<string>> Result(MainWindow window)
    {
        var overlay = (Grid)window.FindName("ResultOverlay");
        await WaitUntil(() => overlay.Visibility == Visibility.Visible || window.SmokeErrors.Count > 0, 120000);
        if (window.SmokeErrors.Count > 0) throw new InvalidOperationException("Arayüz hatası: " + string.Join(" | ", window.SmokeErrors));
        var outputs = window.LastOutputs;
        overlay.Visibility = Visibility.Collapsed;
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
        Application.Current.MainWindow?.UpdateLayout();
    }

    private static void Capture(BitmapSource bitmap, string path) => File.WriteAllBytes(path, Images.Png(bitmap));

    private static void CaptureToast(string path)
    {
        if (Application.Current.Windows.OfType<Toast>().LastOrDefault() is { } toast) Capture(toast, path);
    }

    private static void Capture(Window window, string path)
    {
        if (window.Content is not FrameworkElement root || root.ActualWidth < 1) return;
        var dpi = VisualTreeHelper.GetDpi(root);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle((Brush)Application.Current.Resources["Bg"], null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            context.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        bitmap.Render(visual);
        File.WriteAllBytes(path, Images.Png(bitmap));
    }

    private static string Sample(string path, int width, int height, Color color, string label)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var transparent = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && label == "2";
            if (!transparent) context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            context.DrawRoundedRectangle(new SolidColorBrush(color), null, new Rect(width * .1, height * .1, width * .8, height * .8), 60, 60);
            var text = new FormattedText(label, System.Globalization.CultureInfo.GetCultureInfo("tr-TR"), FlowDirection.LeftToRight, new Typeface("Segoe UI"), height * .4, Brushes.White, 1);
            context.DrawText(text, new Point((width - text.Width) / 2, (height - text.Height) / 2));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        File.WriteAllBytes(path, path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? Images.Jpeg(bitmap, 88) : Images.Png(bitmap));
        return path;
    }
}
