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

            // PDF'den resme
            var fromPdf = new FromPdfView(window); window.Navigate(fromPdf, "PDF'den çevir"); await Settle();
            fromPdf.Add([threePages]); fromPdf.ToJpg.IsChecked = true; await Settle(600);
            Capture(window, Path.Combine(shots, "10-pdfden-cevir.png"));
            Click(fromPdf.Go);
            var pictures = await Result(window);
            Check(Directory.Exists(pictures[0]) && Directory.GetFiles(pictures[0], "*.jpg").Length == 3, "PDF sayfaları JPG oldu (3 resim)");

            // Shift paneli: seçenekler ve görünüm
            Check(PandaWheel.For([threePages]).Select(c => c.Id).SequenceEqual(["organize", "split", "word", "jpg"]), "Tek PDF için panel seçenekleri");
            Check(PandaWheel.For([threePages, copy]).Select(c => c.Id).SequenceEqual(["merge", "word", "jpg"]), "Birden fazla PDF için panel seçenekleri");
            Check(PandaWheel.For(images).First().Id == "topdf", "Resimler için panel: PDF yap");
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

            // Panelden hızlı işlem: PDF'yi doğrudan resme çevir
            window.QuickAction("jpg", [threePages]);
            var quick = await Result(window);
            Check(Directory.Exists(quick[0]) && Directory.GetFiles(quick[0], "*.jpg").Length == 3, "Panelden hızlı işlem (resme çevir)");

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
