using System.Windows;
using System.Windows.Controls;
using PanDaF.Core;

namespace PanDaF.Views;

public partial class MergeView : UserControl
{
    private const string Filter = "PDF dosyaları|*.pdf";
    private readonly MainWindow shell;
    private readonly FileList list = new();

    public MergeView(MainWindow shell)
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
        if (pdfs.Count < files.Count) shell.Info("Yalnızca PDF dosyaları eklenebilir. Diğer dosyaları önce “PDF'ye çevir” aracıyla PDF yapabilirsiniz.");
        list.Add(pdfs);
    }

    private void Update()
    {
        var any = list.Files.Count > 0;
        DropHost.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        ListPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        Go.IsEnabled = list.Files.Count >= 2;
        Summary.Text = list.Files.Count switch
        {
            0 => "Birleştirmek için en az iki PDF ekleyin.",
            1 => "Bir PDF daha ekleyin.",
            var n => $"{n} PDF tek dosyada birleştirilecek.",
        };
    }

    private void AddMore_Click(object sender, RoutedEventArgs e) => Add(DropZone.Pick(Filter, multiple: true));

    private async void Go_Click(object sender, RoutedEventArgs e)
    {
        var files = list.Files.ToList();
        var destination = OutputFolder.For(files[0]);
        var result = await shell.RunAsync("PDF'ler birleştiriliyor", (lossy, progress, token) =>
            Task.Run<IReadOnlyList<string>>(() => [PdfPages.Merge(files, destination, lossy, token)], token));
        if (result is not null) shell.ShowResult(result, $"{files.Count} PDF birleştirildi.");
    }
}
