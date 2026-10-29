using System.Windows;
using System.Windows.Controls;
using PanDaF.Core;

namespace PanDaF.Views;

public partial class ToPdfView : UserControl
{
    private const string Filter =
        "Desteklenen dosyalar|*.doc;*.docx;*.odt;*.rtf;*.ppt;*.pptx;*.pps;*.ppsx;*.odp;*.xls;*.xlsx;*.ods;*.jpg;*.jpeg;*.jfif;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.heic" +
        "|Word belgeleri|*.doc;*.docx;*.odt;*.rtf|PowerPoint sunuları|*.ppt;*.pptx;*.pps;*.ppsx;*.odp|Excel tabloları|*.xls;*.xlsx;*.ods" +
        "|Resimler|*.jpg;*.jpeg;*.jfif;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.heic";
    private readonly MainWindow shell;
    private readonly FileList list = new();

    public ToPdfView(MainWindow shell)
    {
        this.shell = shell;
        InitializeComponent();
        var drop = new DropZone("Dosyaları buraya sürükleyin", "Word, PowerPoint, Excel veya resim (JPG, PNG…) dosyaları. Birden fazla seçebilirsiniz.", Filter, multiple: true);
        drop.FilesChosen += Add;
        DropHost.Content = drop;
        ListHost.Content = list;
        list.Changed += Update;
        DropHelper.Attach(this, Add);
    }

    public void Add(IReadOnlyList<string> files)
    {
        var usable = files.Where(FileKinds.CanConvertToPdf).ToList();
        if (usable.Count < files.Count)
        {
            var skipped = files.Where(f => !FileKinds.CanConvertToPdf(f)).Select(Path.GetFileName);
            shell.Info("Şu dosyalar PDF'ye çevrilemediği için eklenmedi:\n\n" + string.Join("\n", skipped));
        }
        list.Add(usable);
    }

    private void Update()
    {
        var files = list.Files;
        var any = files.Count > 0;
        DropHost.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        ListPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        var images = files.Count(FileKinds.IsImage);
        var documents = files.Count - images;
        ImageOptions.Visibility = images > 0 ? Visibility.Visible : Visibility.Collapsed;
        Go.IsEnabled = any;
        if (!any) { Summary.Text = "Çevirmek için dosya ekleyin."; return; }
        var parts = new List<string>();
        if (documents > 0)
        {
            var engine = files.Where(FileKinds.IsOfficeDocument).All(MicrosoftOffice.CanConvert) ? "Microsoft Office ile"
                : App.Converter.LibreOffice.Ready ? "LibreOffice ile" : "LibreOffice ile (ilk kullanımda bir kez indirilir)";
            parts.Add($"{documents} belge {engine} PDF'ye çevrilecek");
        }
        if (images > 0) parts.Add(images == 1 ? "1 resim PDF yapılacak" : Combine.IsChecked == true ? $"{images} resim tek PDF'de birleşecek" : $"{images} resim ayrı ayrı PDF yapılacak");
        Summary.Text = string.Join(" · ", parts) + ".";
    }

    private void Options_Changed(object sender, RoutedEventArgs e)
    {
        if (NamePanel is null) return;
        NamePanel.Visibility = Combine.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Update();
    }

    private void AddMore_Click(object sender, RoutedEventArgs e) => Add(DropZone.Pick(Filter, multiple: true));

    private async void Go_Click(object sender, RoutedEventArgs e)
    {
        var files = list.Files.ToList();
        if (files.Count == 0) return;
        var converter = App.Converter;
        if (converter.NeedsLibreOfficeToPdf(files))
        {
            if (!shell.Confirm("Bu bilgisayarda Microsoft Office bulunamadı. Word, PowerPoint ve Excel dosyalarını çevirmek için ücretsiz LibreOffice bileşeni bir kez indirilecek (yaklaşık 350 MB, birkaç dakika sürebilir). Sonraki kullanımlarda tekrar indirilmez.\n\nİndirilsin mi?"))
                return;
            var installed = await shell.RunAsync("LibreOffice hazırlanıyor", async (_, progress, token) => { await converter.LibreOffice.InstallAsync(progress, token); return [converter.LibreOffice.Executable ?? ""]; });
            if (installed is null) return;
        }
        var options = new ImageToPdfOptions(FitA4.IsChecked == true, Combine.IsChecked == true, string.IsNullOrWhiteSpace(CombinedName.Text) ? "Resimler" : CombinedName.Text.Trim());
        var destination = OutputFolder.For(files[0]);
        var result = await shell.RunAsync("PDF'ye çevriliyor", (_, progress, token) => converter.ToPdfAsync(files, destination, options, progress, token));
        if (result is null) return;
        Update();
        shell.ShowResult(result, result.Count == 1 ? "PDF hazır." : $"{result.Count} PDF hazır.");
    }

    /// Shift panelinden gelen dosyalar için: listeyi doldurur ve hemen çevirmeye başlar.
    internal void Start(IReadOnlyList<string> files)
    {
        list.Clear();
        Add(files);
        if (list.Files.Count > 0) Go_Click(this, new RoutedEventArgs());
    }
}
