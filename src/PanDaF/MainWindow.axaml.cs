using Avalonia.Input;
using Avalonia.Threading;
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
    private readonly SemaphoreSlim quickGate = new(1, 1);
    private TaskCompletionSource<bool>? messageAnswer;
    private bool exiting;
    private IDisposable? watcher;

    public MainWindow()
    {
        InitializeComponent();
        GoHome();
#if WINDOWS
        // Shift paneli: PanDaF çalıştığı sürece (pencere kapalı, simgede olsa bile) dosya sürüklemelerini izler.
        Opened += (_, _) => watcher ??= new GlobalDragWatcher(this, () => App.Settings.ShiftPanel && !App.SmokeMode);
#endif
        Closing += (_, e) =>
        {
            // Kapat düğmesi PanDaF'ı kapatmaz, arka plana gönderir; böylece Shift paneli ve hızlı açılış hazır kalır.
            if (exiting) return;
            if (App.SmokeMode || Application.Current is not App { HasTray: true }) { exiting = true; watcher?.Dispose(); App.ExitApp(); return; }
            e.Cancel = true;
            Hide();
            if (!App.Settings.TrayHintShown)
            {
                App.Settings.TrayHintShown = true; App.Settings.Save();
                Toast.Open("PanDaF arka planda çalışıyor").Note(OperatingSystem.IsWindows()
                    ? "Shift paneli hazır. Pencereyi açmak için saatin yanındaki panda simgesine tıklayın; tamamen kapatmak için sağ tıklayıp “PanDaF'tan çık”ı seçin."
                    : "Pencereyi açmak için menü çubuğundaki panda simgesine tıklayın; tamamen kapatmak için oradan “PanDaF'tan çık”ı seçin.");
            }
        };
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (MessageOverlay.IsVisible) { Answer(false); e.Handled = true; }
            else if (ResultOverlay.IsVisible) { CloseResult(); e.Handled = true; }
            else if (!BusyOverlay.IsVisible && Host.Content is not HomeView) { GoHome(); e.Handled = true; }
        };
    }

    public bool IsBusy => BusyOverlay.IsVisible;

    public void GoHome() => Navigate(new HomeView(this), "");

    public void Navigate(Control view, string title)
    {
        Host.Content = view;
        PageTitle.Text = string.IsNullOrEmpty(title) ? "" : "·  " + title;
        BackButton.IsVisible = view is not HomeView;
    }

    public void BringToFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// Dosyaları türüne göre en uygun araca yönlendirir (ana sayfaya veya simgeye bırakılınca).
    public void OpenFiles(IReadOnlyList<string> files)
    {
        var pdfs = files.Where(FileKinds.IsPdf).ToList();
        var convertible = files.Where(FileKinds.CanConvertToPdf).ToList();
        if (pdfs.Count > 0 && convertible.Count == 0)
        {
            if (pdfs.Count == 1) { var view = new EditView(this); Navigate(view, "PDF'yi düzenle"); view.Load(pdfs[0]); }
            else { var view = new MergeView(this); Navigate(view, "PDF birleştir"); view.Add(pdfs); }
        }
        else if (convertible.Count > 0)
        {
            var view = new ToPdfView(this); Navigate(view, "PDF'ye çevir"); view.Add(convertible);
            if (pdfs.Count > 0) _ = Info("PDF dosyaları bu listeye eklenmedi. PDF'leri birleştirmek için “PDF birleştir” aracını kullanın.");
        }
        else _ = Info("Bu dosyalar desteklenmiyor. PDF, Word, PowerPoint, Excel veya resim dosyası seçin.");
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
            try { return await work(allowLossy, progress, cancellation.Token); }
            catch (LossyPdfException error)
            {
                HideBusy();
                if (await Confirm(error.Message + "\n\nYine de devam edilsin mi?")) { allowLossy = true; continue; }
                return null;
            }
            catch (OperationCanceledException) { return null; }
            catch (UserFacingException error) { App.Log(error); HideBusy(); await Error(error.Message); return null; }
            catch (Exception error) { App.Log(error); HideBusy(); await Error("Beklenmeyen bir sorun oluştu:\n" + error.Message); return null; }
            finally { HideBusy(); running = null; }
        }
    }

    private void ShowBusy(string title)
    {
        BusyTitle.Text = title; BusyLabel.Text = "Hazırlanıyor…"; BusyBar.IsIndeterminate = true; CancelButton.IsEnabled = true;
        BusyOverlay.IsVisible = true;
    }

    private void UpdateBusy(OperationProgress progress)
    {
        if (!string.IsNullOrWhiteSpace(progress.Label)) BusyLabel.Text = progress.Label;
        if (progress.Fraction is { } fraction && double.IsFinite(fraction)) { BusyBar.IsIndeterminate = false; BusyBar.Value = Math.Clamp(fraction, 0, 1); }
    }

    private void HideBusy() => BusyOverlay.IsVisible = false;

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false; BusyLabel.Text = "İptal ediliyor…"; running?.Cancel();
    }

    public void ShowResult(IReadOnlyList<string> outputs, string message)
    {
        if (outputs.Count == 0) return;
        lastOutputs = outputs;
        ResultMessage.Text = message;
        var folder = Path.GetDirectoryName(outputs[0]) ?? "";
        ResultPath.Text = outputs.Count == 1 ? "Kaydedilen yer: " + outputs[0] : "Kaydedilen klasör: " + folder;
        var single = outputs.Count == 1;
        ResultOpen.IsVisible = single;
        ResultOpen.Content = single && Directory.Exists(outputs[0]) ? "Klasörü aç" : "Aç";
        ResultOverlay.IsVisible = true;
        ResultOk.Focus();
    }

    private void CloseResult() => ResultOverlay.IsVisible = false;
    private void ResultOk_Click(object? sender, RoutedEventArgs e) => CloseResult();
    private void ResultOpen_Click(object? sender, RoutedEventArgs e) { if (lastOutputs.Count > 0) Platform.Open(lastOutputs[0]); CloseResult(); }
    private void ResultOpenFolder_Click(object? sender, RoutedEventArgs e) { if (lastOutputs.Count > 0) Platform.Reveal(lastOutputs[0]); CloseResult(); }

    // ----- Mesajlar (pencere içi) -----

    private Task<bool> Show(string title, string message, bool question)
    {
        if (App.SmokeMode) { if (title == "Sorun") SmokeErrors.Add(message); return Task.FromResult(true); }
        messageAnswer?.TrySetResult(false);
        messageAnswer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        MessageTitle.Text = title; MessageText.Text = message;
        MessageNo.IsVisible = question; MessageYes.Content = question ? "Evet" : "Tamam";
        MessageOverlay.IsVisible = true;
        MessageYes.Focus();
        return messageAnswer.Task;
    }

    private void Answer(bool value) { MessageOverlay.IsVisible = false; messageAnswer?.TrySetResult(value); messageAnswer = null; }
    private void MessageYes_Click(object? sender, RoutedEventArgs e) => Answer(true);
    private void MessageNo_Click(object? sender, RoutedEventArgs e) => Answer(false);

    public Task Error(string message) => Show("Sorun", message, false);
    public Task Info(string message) => Show("Bilgi", message, false);
    public Task<bool> Confirm(string message) => Show("Soru", message, true);

    private void Back_Click(object? sender, RoutedEventArgs e) => GoHome();

    public void ExitApp() { exiting = true; Close(); }

    private void Settings_Click(object? sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var shift = new MenuItem { Header = "Shift paneli (dosya sürüklerken Shift'e basınca)", IsEnabled = Platform.SupportsShiftPanel, Icon = new PathIcon { Data = App.Settings.ShiftPanel ? (Avalonia.Media.Geometry)this.FindResource("IconCheck")! : null } };
        shift.Click += (_, _) => { App.Settings.ShiftPanel = !App.Settings.ShiftPanel; App.Settings.Save(); };
        var startup = new MenuItem { Header = "Bilgisayar açılınca başlat", Icon = new PathIcon { Data = App.Settings.StartWithWindows ? (Avalonia.Media.Geometry)this.FindResource("IconCheck")! : null } };
        startup.Click += (_, _) => { App.Settings.StartWithWindows = !App.Settings.StartWithWindows; App.Settings.Save(); AutoStart.Apply(App.Settings.StartWithWindows); };
        var signature = new MenuItem { Header = "İmza ekle…" };
        signature.Click += async (_, _) => { if ((await EditView.ChooseSignatures(this)).Count > 0) await Info("İmza kaydedildi ve bu bilgisayarda saklanıyor. Düzenleyicide “İmza” düğmesiyle eklenir; orada birden fazla imza tutabilir, istemediğinizi silebilirsiniz."); };
        var about = new MenuItem { Header = "Hakkında" };
        about.Click += async (_, _) => await About();
        var exit = new MenuItem { Header = "PanDaF'tan çık" };
        exit.Click += (_, _) => ExitApp();
        menu.Items.Add(shift); menu.Items.Add(startup); menu.Items.Add(signature); menu.Items.Add(new Separator()); menu.Items.Add(about); menu.Items.Add(exit);
        menu.Open(SettingsButton);
    }

    private Task About()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "2.0.0";
        return Info($"PanDaF {version}\n\n" +
            "Öğretmenler için basit PDF araçları: birleştirme, bölme, sayfa düzenleme, yerinde yazı değiştirme, PDF'ye ve PDF'den dönüştürme.\n\n" +
            "Dosyalarınız internete yüklenmez; tüm işlemler bu bilgisayarda yapılır. Asıl dosyalarınız hiçbir zaman değiştirilmez.\n\n" +
            "Sinan İnce 2026 · GPL-3.0. PDF motoru Nashi'den (Orkun Işıtmak, MIT) uyarlandı; yerinde yazı değiştirme KillerPdf.Engine (GPL-3.0) ile.");
    }

    // ----- Shift paneli / dosya bırakma ile hızlı işlemler -----

    public void QuickAction(string id, IReadOnlyList<string> files)
    {
        if (files.Count == 0) return;
        if (id is "pdf" or "topdf" or "pdf-combine" or "pdf-separate" or "word" or "excel" or "jpg") { _ = QuickConvertAsync(id, files); return; }
        BringToFront();
        if (IsBusy) { _ = Info("Önceki işlem bitmeden yeni işlem başlatılamaz. Lütfen bekleyin."); return; }
        CloseResult();
        switch (id)
        {
            case "merge": { var view = new MergeView(this); Navigate(view, "PDF birleştir"); view.Add(files); break; }
            case "split": { var view = new SplitPdfView(this); Navigate(view, "PDF böl"); view.Load(files[0]); break; }
            case "organize": { var view = new OrganizeView(this); Navigate(view, "Sayfaları düzenle"); view.Load(files[0]); break; }
            case "edit": { var view = new EditView(this); Navigate(view, "PDF'yi düzenle"); view.Load(files[0]); break; }
            default: OpenFiles(files); break;
        }
    }

    /// Pencere açılmadan, soru sormadan dönüştürür; sonucu asıl dosyanın yanına kaydeder ve köşede bildirir.
    public async Task<IReadOnlyList<string>> QuickConvertAsync(string id, IReadOnlyList<string> files)
    {
        var converter = App.Converter;
        var toPdf = id is "pdf" or "topdf" or "pdf-combine" or "pdf-separate";
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
        catch (OperationCanceledException) { toast.Close(); return []; }
        catch (UserFacingException error) { App.Log(error); toast.Fail(error.Message); if (App.SmokeMode) SmokeErrors.Add(error.Message); return []; }
        catch (Exception error) { App.Log(error); toast.Fail("Beklenmeyen bir sorun oluştu: " + error.Message); if (App.SmokeMode) SmokeErrors.Add(error.Message); return []; }
        finally { quickGate.Release(); }
    }
}
