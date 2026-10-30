using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PanDaF.Core;
using PanDaF.Views;

namespace PanDaF;

public delegate Task<IReadOnlyList<string>> Work(bool allowLossy, IProgress<OperationProgress> progress, CancellationToken token);

public partial class MainWindow : Window
{
    private CancellationTokenSource? running;
    private IReadOnlyList<string> lastOutputs = [];
    public List<string> SmokeErrors { get; } = [];
    public IReadOnlyList<string> LastOutputs => lastOutputs;

    private readonly Settings settings = Settings.Load();
    private readonly GlobalDragWatcher watcher;
    private readonly SemaphoreSlim quickGate = new(1, 1);
    private Tray? tray;
    private bool exiting;
    public Settings Settings => settings;

    public MainWindow()
    {
        InitializeComponent();
        GoHome();
        // Shift paneli: PanDaF çalıştığı sürece (pencere kapalı, simgede olsa bile) dosya sürüklemelerini izler.
        watcher = new GlobalDragWatcher(this, () => settings.ShiftPanel && !App.SmokeMode);
        Closing += (_, e) =>
        {
            // Kapat düğmesi PanDaF'ı kapatmaz, sistem tepsisine gönderir; böylece Shift paneli çalışmaya devam eder.
            if (exiting || App.SmokeMode || tray is null) return;
            e.Cancel = true;
            Hide();
            if (!settings.TrayHintShown)
            {
                settings.TrayHintShown = true; settings.Save();
                tray.Hint("PanDaF arka planda çalışıyor", "Shift paneli hazır. Pencereyi açmak için saatin yanındaki panda simgesine tıklayın; tamamen kapatmak için sağ tıklayıp \"PanDaF'tan çık\"ı seçin.");
            }
        };
        Closed += (_, _) => { watcher.Dispose(); tray?.Dispose(); if (!App.SmokeMode) Application.Current?.Shutdown(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (ResultOverlay.Visibility == Visibility.Visible) { CloseResult(); e.Handled = true; }
            else if (BusyOverlay.Visibility != Visibility.Visible && Host.Content is not HomeView) { GoHome(); e.Handled = true; }
        };
    }

    public bool IsBusy => BusyOverlay.Visibility == Visibility.Visible;

    public void GoHome() => Navigate(new HomeView(this), "");

    public void Navigate(UserControl view, string title)
    {
        Host.Content = view;
        PageTitle.Text = string.IsNullOrEmpty(title) ? "" : "·  " + title;
        BackButton.Visibility = view is HomeView ? Visibility.Collapsed : Visibility.Visible;
    }

    /// Dosyaları türüne göre en uygun araca yönlendirir (ana sayfaya veya simgeye bırakılınca).
    public void OpenFiles(IReadOnlyList<string> files)
    {
        var pdfs = files.Where(FileKinds.IsPdf).ToList();
        var convertible = files.Where(FileKinds.CanConvertToPdf).ToList();
        if (pdfs.Count > 0 && convertible.Count == 0)
        {
            if (pdfs.Count == 1) { var view = new OrganizeView(this); Navigate(view, "Sayfaları düzenle"); view.Load(pdfs[0]); }
            else { var view = new MergeView(this); Navigate(view, "PDF birleştir"); view.Add(pdfs); }
        }
        else if (convertible.Count > 0)
        {
            var view = new ToPdfView(this); Navigate(view, "PDF'ye çevir"); view.Add(convertible);
            if (pdfs.Count > 0) Info("PDF dosyaları bu listeye eklenmedi. PDF'leri birleştirmek için “PDF birleştir” aracını kullanın.");
        }
        else Info("Bu dosyalar desteklenmiyor. PDF, Word, PowerPoint, Excel veya resim dosyası seçin.");
    }

    /// İşi arka planda çalıştırır; ilerlemeyi gösterir, hataları Türkçe açıklar.
    public async Task<IReadOnlyList<string>?> RunAsync(string title, Work work)
    {
        var allowLossy = false;
        while (true)
        {
            using var cancellation = new CancellationTokenSource();
            running = cancellation;
            ShowBusy(title);
            var progress = new Progress<OperationProgress>(UpdateBusy);
            try
            {
                var result = await work(allowLossy, progress, cancellation.Token);
                return result;
            }
            catch (LossyPdfException error)
            {
                HideBusy();
                if (Confirm(error.Message + "\n\nYine de devam edilsin mi?")) { allowLossy = true; continue; }
                return null;
            }
            catch (OperationCanceledException) { return null; }
            catch (UserFacingException error) { App.Log(error); HideBusy(); Error(error.Message); return null; }
            catch (Exception error) { App.Log(error); HideBusy(); Error("Beklenmeyen bir sorun oluştu:\n" + error.Message); return null; }
            finally { HideBusy(); running = null; }
        }
    }

    private void ShowBusy(string title)
    {
        BusyTitle.Text = title;
        BusyLabel.Text = "Hazırlanıyor…";
        BusyBar.IsIndeterminate = true;
        CancelButton.IsEnabled = true;
        BusyOverlay.Visibility = Visibility.Visible;
    }

    private void UpdateBusy(OperationProgress progress)
    {
        if (!string.IsNullOrWhiteSpace(progress.Label)) BusyLabel.Text = progress.Label;
        if (progress.Fraction is { } fraction && double.IsFinite(fraction))
        {
            BusyBar.IsIndeterminate = false;
            BusyBar.Value = Math.Clamp(fraction, 0, 1);
        }
    }

    private void HideBusy() => BusyOverlay.Visibility = Visibility.Collapsed;

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        BusyLabel.Text = "İptal ediliyor…";
        running?.Cancel();
    }

    public void ShowResult(IReadOnlyList<string> outputs, string message)
    {
        if (outputs.Count == 0) return;
        lastOutputs = outputs;
        ResultMessage.Text = message;
        var folder = Path.GetDirectoryName(outputs[0]) ?? "";
        ResultPath.Text = outputs.Count == 1 ? "Kaydedilen yer: " + outputs[0] : "Kaydedilen klasör: " + folder;
        var single = outputs.Count == 1;
        ResultOpen.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
        ResultOpen.Content = single && Directory.Exists(outputs[0]) ? "Klasörü aç" : "Aç";
        ResultOverlay.Visibility = Visibility.Visible;
        ResultOk.Focus();
    }

    private void CloseResult() => ResultOverlay.Visibility = Visibility.Collapsed;
    private void ResultOk_Click(object sender, RoutedEventArgs e) => CloseResult();

    private void ResultOpen_Click(object sender, RoutedEventArgs e)
    {
        if (lastOutputs.Count > 0) Shell(lastOutputs[0]);
        CloseResult();
    }

    private void ResultOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (lastOutputs.Count == 0) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{lastOutputs[0]}\"") { UseShellExecute = false }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { Error("Klasör açılamadı: " + error.Message); }
        CloseResult();
    }

    public void Shell(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        { Error("Dosya açılamadı. Bu dosya türünü açacak bir program kurulu olmayabilir.\n\n" + error.Message); }
    }

    public void Error(string message)
    {
        if (App.SmokeMode) { SmokeErrors.Add(message); return; }
        MessageBox.Show(this, message, "PanDaF", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public void Info(string message)
    {
        if (App.SmokeMode) return;
        MessageBox.Show(this, message, "PanDaF", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public bool Confirm(string message)
    {
        if (App.SmokeMode) return true;
        return MessageBox.Show(this, message, "PanDaF", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => GoHome();

    /// Sistem tepsisi simgesini kurar (App başlangıcında çağrılır).
    public void InstallTray()
    {
        tray = new Tray(BringToFront, () => settings.ShiftPanel, SetShiftPanel, () => settings.StartWithWindows, SetStartup, ExitApp);
    }

    public void SetShiftPanel(bool on) { settings.ShiftPanel = on; settings.Save(); }

    public void SetStartup(bool on) { settings.StartWithWindows = on; settings.Save(); AutoStart.Apply(on); }

    public void ExitApp()
    {
        exiting = true;
        Close();
        Application.Current.Shutdown();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = SettingsButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, FontSize = 15 };
        var shift = new MenuItem { Header = "Shift paneli (dosya sürüklerken Shift'e basınca)", IsCheckable = true, IsChecked = settings.ShiftPanel };
        shift.Click += (_, _) => SetShiftPanel(shift.IsChecked);
        var startup = new MenuItem { Header = "Bilgisayar açılınca başlat", IsCheckable = true, IsChecked = settings.StartWithWindows };
        startup.Click += (_, _) => SetStartup(startup.IsChecked);
        var signature = new MenuItem { Header = "İmza resmini seç…" };
        signature.Click += (_, _) => { if (PanDaF.Views.EditView.ChooseSignature()) Info("İmza kaydedildi. Düzenleyicide “İmza” düğmesiyle eklenir."); };
        var about = new MenuItem { Header = "Hakkında" };
        about.Click += About_Click;
        var exit = new MenuItem { Header = "PanDaF'tan çık" };
        exit.Click += (_, _) => ExitApp();
        menu.Items.Add(shift); menu.Items.Add(startup); menu.Items.Add(signature); menu.Items.Add(new Separator()); menu.Items.Add(about); menu.Items.Add(exit);
        menu.IsOpen = true;
    }

    /// Shift panelinde bir dilime bırakılan dosyalar.
    public void QuickAction(string id, IReadOnlyList<string> files)
    {
        if (files.Count == 0) return;
        if (id is "pdf" or "topdf" or "pdf-combine" or "pdf-separate" or "word" or "excel" or "jpg") { _ = QuickConvertAsync(id, files); return; }
        BringToFront();
        if (IsBusy) { Info("Önceki işlem bitmeden yeni işlem başlatılamaz. Lütfen bekleyin."); return; }
        CloseResult();
        switch (id)
        {
            case "merge": { var view = new MergeView(this); Navigate(view, "PDF birleştir"); view.Add(files); break; }
            case "split": { var view = new SplitView(this); Navigate(view, "PDF böl"); view.Load(files[0]); break; }
            case "organize": { var view = new OrganizeView(this); Navigate(view, "Sayfaları düzenle"); view.Load(files[0]); break; }
            case "edit": { var view = new EditView(this); Navigate(view, "PDF'yi düzenle"); view.Load(files[0]); break; }
            default: OpenFiles(files); break;
        }
    }

    /// Pencere açılmadan, soru sormadan dönüştürür; sonucu asıl dosyanın yanına kaydeder ve sağ altta bildirir.
    public async Task<IReadOnlyList<string>> QuickConvertAsync(string id, IReadOnlyList<string> files)
    {
        var converter = App.Converter;
        var toPdf = id is "pdf" or "topdf" or "pdf-combine" or "pdf-separate";
        // LibreOffice bir kez indirilecekse bunun için izin istemek gerekir: araç penceresi açılır.
        if (toPdf ? converter.NeedsLibreOfficeToPdf(files) : id == "word" && converter.NeedsLibreOfficeForWord)
        {
            BringToFront();
            if (toPdf) { var view = new ToPdfView(this); Navigate(view, "PDF'ye çevir"); view.Start(files); }
            else { var view = new FromPdfView(this); Navigate(view, "PDF'den çevir"); view.Start(files, PdfTarget.Word); }
            return [];
        }
        var heading = toPdf ? "PDF'ye çevriliyor" : id == "word" ? "Word belgesine çevriliyor" : id == "excel" ? "Excel'e aktarılıyor" : "Resimlere çevriliyor";
        var toast = Toast.Open(heading);
        await quickGate.WaitAsync();
        try
        {
            var token = toast.Cancellation.Token;
            var progress = new Progress<OperationProgress>(toast.Progress);
            var destination = OutputFolder.For(files[0]);
            IReadOnlyList<string> result;
            if (toPdf)
            {
                var images = files.Count(FileKinds.IsImage);
                var combine = id == "pdf-combine" || (id != "pdf-separate" && images == 1 && files.Count == 1);
                var name = images == 1 || !combine ? SafePaths.Stem(files[0]) : $"{SafePaths.Stem(files.First(FileKinds.IsImage))} ({images} resim)";
                result = await converter.ToPdfAsync(files, destination, new ImageToPdfOptions(FitA4: true, Combine: combine, CombinedName: name), progress, token);
            }
            else result = await converter.FromPdfAsync(files, destination, id == "word" ? PdfTarget.Word : id == "excel" ? PdfTarget.Excel : PdfTarget.Jpg, 150, progress, token);
            toast.Done(result, toPdf ? (result.Count == 1 ? "PDF hazır" : $"{result.Count} PDF hazır") : id == "word" ? "Word belgesi hazır" : id == "excel" ? "Excel tablosu hazır" : "Resimler hazır");
            return result;
        }
        catch (OperationCanceledException) { toast.Cancelled(); return []; }
        catch (UserFacingException error) { App.Log(error); toast.Fail(error.Message); if (App.SmokeMode) SmokeErrors.Add(error.Message); return []; }
        catch (Exception error) { App.Log(error); toast.Fail("Beklenmeyen bir sorun oluştu: " + error.Message); if (App.SmokeMode) SmokeErrors.Add(error.Message); return []; }
        finally { quickGate.Release(); }
    }

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        // Windows başka programın önüne geçmeyi kısıtlar; kısa süre "en üstte" yapmak işe yarar.
        Topmost = true; Activate(); Topmost = false; Focus();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        MessageBox.Show(this,
            $"PanDaF {version}\n\n" +
            "Öğretmenler için basit PDF araçları: birleştirme, bölme, sayfa silme ve sıralama, PDF'ye ve PDF'den dönüştürme.\n\n" +
            "Dosyalarınız internete yüklenmez; tüm işlemler bu bilgisayarda yapılır. Asıl dosyalarınız hiçbir zaman değiştirilmez.\n\n" +
            "Sinan İnce 2026 tarafından hazırlandı.\n" +
            "PDF motoru, Orkun Işıtmak'ın açık kaynaklı Nashi uygulamasından (github.com/orkunisitmak/nashi-app) uyarlanmıştır.\n\n" +
            "MIT lisansı. Kullanılan kütüphaneler: PDFsharp, PdfPig, PDFium; Office dönüşümleri için Microsoft Office veya LibreOffice.",
            "Hakkında", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
