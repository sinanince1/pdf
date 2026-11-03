using Avalonia.Input;
using PanDaF.Core;

namespace PanDaF.Views;

public partial class SplitPdfView : UserControl
{
    private readonly MainWindow shell;
    private string? file;
    private int pages;

    public SplitPdfView(MainWindow shell)
    {
        this.shell = shell;
        InitializeComponent();
        var drop = new DropZone("Bölmek istediğiniz PDF'yi buraya sürükleyin", "veya bilgisayarınızdan seçin.", Pick.Pdf, multiple: false);
        drop.FilesChosen += files => Load(files[0]);
        DropHost.Content = drop;
        DropHelper.Attach(this, files => Load(files[0]));
        OptionCards.Bind(EachPage, EachPageCard);
        OptionCards.Bind(EveryN, EveryNCard);
        OptionCards.Bind(Extract, ExtractCard);
    }

    public async void Load(string path)
    {
        if (!FileKinds.IsPdf(path)) { await shell.Info("Lütfen bir PDF dosyası seçin."); return; }
        try { pages = await Task.Run(() => PdfPages.PageCount(path)); }
        catch (UserFacingException error) { await shell.Error(error.Message); return; }
        file = path;
        FileName.Text = Path.GetFileName(path);
        FileDetails.Text = $"{pages} sayfa";
        Ranges.Text = pages >= 2 ? $"1-{Math.Min(2, pages)}" : "1";
        DropHost.IsVisible = false;
        OptionsPanel.IsVisible = true;
        Go.IsEnabled = true;
        Mode_Changed(this, new RoutedEventArgs());
    }

    private async void Change_Click(object? sender, RoutedEventArgs e)
    {
        var picked = await Pick.Files(this, Pick.Pdf, multiple: false);
        if (picked.Length > 0) Load(picked[0]);
    }

    private void PagesPerFile_Focus(object? sender, GotFocusEventArgs e) => EveryN.IsChecked = true;
    private void Ranges_Focus(object? sender, GotFocusEventArgs e) => Extract.IsChecked = true;

    private void Mode_Changed(object? sender, RoutedEventArgs e)
    {
        if (Summary is null || file is null) return;
        Summary.Text = EachPage.IsChecked == true ? $"{pages} ayrı PDF oluşacak; hepsi yeni bir klasöre kaydedilecek."
            : EveryN.IsChecked == true ? "PDF parçaları yeni bir klasöre kaydedilecek."
            : "Seçtiğiniz sayfalar tek bir PDF olarak kaydedilecek.";
    }

    private async void Go_Click(object? sender, RoutedEventArgs e)
    {
        if (file is null) return;
        var input = file;
        var mode = EachPage.IsChecked == true ? SplitMode.EachPage : EveryN.IsChecked == true ? SplitMode.EveryN : SplitMode.Extract;
        var size = 1;
        if (mode == SplitMode.EveryN && (!int.TryParse((PagesPerFile.Text ?? "").Trim(), out size) || size < 1))
        { await shell.Error("“Her … sayfada bir böl” kutusuna 1 veya daha büyük bir sayı yazın."); PagesPerFile.Focus(); return; }
        var ranges = Ranges.Text ?? "";
        var destination = OutputFolder.For(input);
        var result = await shell.RunAsync("PDF bölünüyor", (lossy, progress, token) =>
            Task.Run<IReadOnlyList<string>>(() => [PdfPages.Split(input, destination, mode, size, ranges, lossy, token)], token));
        if (result is null) return;
        shell.ShowResult(result, mode == SplitMode.Extract ? "Seçtiğiniz sayfalar yeni bir PDF olarak kaydedildi." : "PDF bölündü. Parçalar yeni bir klasörde.");
    }
}
