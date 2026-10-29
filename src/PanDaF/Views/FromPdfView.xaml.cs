using System.Windows;
using System.Windows.Controls;
using PanDaF.Core;

namespace PanDaF.Views;

public partial class FromPdfView : UserControl
{
    private const string Filter = "PDF dosyaları|*.pdf";
    private readonly MainWindow shell;
    private readonly FileList list = new() { Reorderable = false };

    public FromPdfView(MainWindow shell)
    {
        this.shell = shell;
        InitializeComponent();
        var drop = new DropZone("PDF dosyalarını buraya sürükleyin", "veya bilgisayarınızdan seçin. Birden fazla dosya seçebilirsiniz.", Filter, multiple: true);
        drop.FilesChosen += Add;
        DropHost.Content = drop;
        ListHost.Content = list;
        list.Changed += Update;
        DropHelper.Attach(this, Add);
    }

    public void Add(IReadOnlyList<string> files)
    {
        var pdfs = files.Where(FileKinds.IsPdf).ToList();
        if (pdfs.Count < files.Count) shell.Info("Yalnızca PDF dosyaları eklenebilir.");
        list.Add(pdfs);
    }

    private PdfTarget Target => ToJpg.IsChecked == true ? PdfTarget.Jpg : ToPng.IsChecked == true ? PdfTarget.Png : ToText.IsChecked == true ? PdfTarget.Text : PdfTarget.Word;

    private void Target_Changed(object sender, RoutedEventArgs e)
    {
        if (QualityPanel is null) return;
        QualityPanel.Visibility = Target is PdfTarget.Jpg or PdfTarget.Png ? Visibility.Visible : Visibility.Collapsed;
        Update();
    }

    private void Update()
    {
        if (Summary is null) return;
        var any = list.Files.Count > 0;
        DropHost.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        ListPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        Go.IsEnabled = any;
        if (!any) { Summary.Text = "Dönüştürmek için PDF ekleyin."; return; }
        Summary.Text = Target switch
        {
            PdfTarget.Word => MicrosoftOffice.HasWord ? "Microsoft Word ile Word belgesine çevrilecek."
                : App.Converter.LibreOffice.Ready ? "LibreOffice ile Word belgesine çevrilecek."
                : "LibreOffice ile Word belgesine çevrilecek (ilk kullanımda bir kez indirilir).",
            PdfTarget.Jpg or PdfTarget.Png => "Her sayfa ayrı bir resim olarak, yeni bir klasöre kaydedilecek.",
            _ => "Metin, aynı adla .txt dosyası olarak kaydedilecek.",
        };
    }

    private void AddMore_Click(object sender, RoutedEventArgs e) => Add(DropZone.Pick(Filter, multiple: true));

    private async void Go_Click(object sender, RoutedEventArgs e)
    {
        var files = list.Files.ToList();
        if (files.Count == 0) return;
        var target = Target;
        var converter = App.Converter;
        if (target == PdfTarget.Word && converter.NeedsLibreOfficeForWord)
        {
            if (!shell.Confirm("Bu bilgisayarda Microsoft Word bulunamadı. PDF'yi Word belgesine çevirmek için ücretsiz LibreOffice bileşeni bir kez indirilecek (yaklaşık 350 MB, birkaç dakika sürebilir).\n\nİndirilsin mi?"))
                return;
            var installed = await shell.RunAsync("LibreOffice hazırlanıyor", async (_, progress, token) => { await converter.LibreOffice.InstallAsync(progress, token); return [converter.LibreOffice.Executable ?? ""]; });
            if (installed is null) return;
        }
        var dpi = Quality.SelectedIndex == 1 ? 300 : 150;
        var destination = OutputFolder.For(files[0]);
        var title = target switch { PdfTarget.Word => "Word belgesine çevriliyor", PdfTarget.Text => "Metin çıkarılıyor", _ => "Resimlere çevriliyor" };
        var result = await shell.RunAsync(title, (_, progress, token) => converter.FromPdfAsync(files, destination, target, dpi, progress, token));
        if (result is null) return;
        shell.ShowResult(result, target switch
        {
            PdfTarget.Word => result.Count == 1 ? "Word belgesi hazır." : $"{result.Count} Word belgesi hazır.",
            PdfTarget.Text => "Metin dosyası hazır.",
            _ => "Resimler hazır.",
        });
    }

    /// Shift panelinden gelen PDF'ler için: hedefi seçer ve hemen dönüştürmeye başlar.
    internal void Start(IReadOnlyList<string> files, PdfTarget target)
    {
        (target switch { PdfTarget.Jpg => ToJpg, PdfTarget.Png => ToPng, PdfTarget.Text => ToText, _ => ToWord }).IsChecked = true;
        list.Clear();
        Add(files);
        if (list.Files.Count > 0) Go_Click(this, new RoutedEventArgs());
    }
}
