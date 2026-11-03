using PanDaF.Core;

namespace PanDaF.Views;

public partial class FromPdfView : UserControl
{
    private readonly MainWindow shell;
    private readonly FileList list = new() { Reorderable = false };

    public FromPdfView(MainWindow shell)
    {
        this.shell = shell;
        InitializeComponent();
        var drop = new DropZone("PDF dosyalarını buraya sürükleyin", "veya bilgisayarınızdan seçin. Birden fazla dosya seçebilirsiniz.", Pick.Pdf, multiple: true);
        drop.FilesChosen += Add;
        DropHost.Content = drop;
        ListHost.Content = list;
        list.Changed += Update;
        DropHelper.Attach(this, Add);
        OptionCards.Bind(ToWord, ToWordCard); OptionCards.Bind(ToExcel, ToExcelCard); OptionCards.Bind(ToJpg, ToJpgCard);
        OptionCards.Bind(ToPng, ToPngCard); OptionCards.Bind(ToText, ToTextCard);
    }

    public void Add(IReadOnlyList<string> files)
    {
        var pdfs = files.Where(FileKinds.IsPdf).ToList();
        if (pdfs.Count < files.Count) _ = shell.Info("Yalnızca PDF dosyaları eklenebilir.");
        list.Add(pdfs);
    }

    private PdfTarget Target => ToExcel.IsChecked == true ? PdfTarget.Excel : ToJpg.IsChecked == true ? PdfTarget.Jpg : ToPng.IsChecked == true ? PdfTarget.Png : ToText.IsChecked == true ? PdfTarget.Text : PdfTarget.Word;

    private void Target_Changed(object? sender, RoutedEventArgs e)
    {
        if (QualityPanel is null) return;
        QualityPanel.IsVisible = Target is PdfTarget.Jpg or PdfTarget.Png;
        Update();
    }

    private void Update()
    {
        if (Summary is null) return;
        var any = list.Files.Count > 0;
        DropHost.IsVisible = !any;
        ListPanel.IsVisible = any;
        Go.IsEnabled = any;
        if (!any) { Summary.Text = "Dönüştürmek için PDF ekleyin."; return; }
        Summary.Text = Target switch
        {
            PdfTarget.Word => MicrosoftOffice.HasWord ? "Microsoft Word ile Word belgesine çevrilecek."
                : App.Converter.LibreOffice.Ready ? "LibreOffice ile Word belgesine çevrilecek."
                : OperatingSystem.IsWindows() ? "LibreOffice ile Word belgesine çevrilecek (ilk kullanımda bir kez indirilir)." : "Word belgesine çevrilecek (LibreOffice kuruluysa daha iyi sonuç verir).",
            PdfTarget.Excel => "Her sayfa bir çalışma sayfası olur; ek program gerekmez.",
            PdfTarget.Jpg or PdfTarget.Png => "Her sayfa ayrı bir resim olarak, yeni bir klasöre kaydedilecek.",
            _ => "Metin, aynı adla .txt dosyası olarak kaydedilecek.",
        };
    }

    private async void AddMore_Click(object? sender, RoutedEventArgs e) => Add(await Pick.Files(this, Pick.Pdf, multiple: true));

    private async void Go_Click(object? sender, RoutedEventArgs e)
    {
        var files = list.Files.ToList();
        if (files.Count == 0) return;
        var target = Target;
        var converter = App.Converter;
        if (target == PdfTarget.Word && converter.NeedsLibreOfficeForWord && OperatingSystem.IsWindows())
        {
            if (!await shell.Confirm("Bu bilgisayarda Microsoft Word bulunamadı. PDF'yi Word belgesine çevirmek için ücretsiz LibreOffice bileşeni bir kez indirilecek (yaklaşık 350 MB, birkaç dakika sürebilir).\n\nİndirilsin mi?"))
                return;
            var installed = await shell.RunAsync("LibreOffice hazırlanıyor", async (_, progress, token) => { await converter.LibreOffice.InstallAsync(progress, token); return [converter.LibreOffice.Executable ?? ""]; });
            if (installed is null) return;
        }
        var dpi = Quality.SelectedIndex == 1 ? 300 : 150;
        var destination = OutputFolder.For(files[0]);
        var title = target switch { PdfTarget.Word => "Word belgesine çevriliyor", PdfTarget.Excel => "Excel'e aktarılıyor", PdfTarget.Text => "Metin çıkarılıyor", _ => "Resimlere çevriliyor" };
        var result = await shell.RunAsync(title, (_, progress, token) => converter.FromPdfAsync(files, destination, target, dpi, progress, token));
        if (result is null) return;
        shell.ShowResult(result, target switch
        {
            PdfTarget.Word => result.Count == 1 ? "Word belgesi hazır." : $"{result.Count} Word belgesi hazır.",
            PdfTarget.Excel => result.Count == 1 ? "Excel tablosu hazır." : $"{result.Count} Excel tablosu hazır.",
            PdfTarget.Text => "Metin dosyası hazır.",
            _ => "Resimler hazır.",
        });
    }

    /// Shift panelinden gelen PDF'ler için: hedefi seçer ve hemen dönüştürmeye başlar.
    internal void Start(IReadOnlyList<string> files, PdfTarget target)
    {
        (target switch { PdfTarget.Excel => ToExcel, PdfTarget.Jpg => ToJpg, PdfTarget.Png => ToPng, PdfTarget.Text => ToText, _ => ToWord }).IsChecked = true;
        list.Clear();
        Add(files);
        if (list.Files.Count > 0) Go_Click(this, new RoutedEventArgs());
    }
}
