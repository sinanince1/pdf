using System.Windows;
using System.Windows.Controls;

namespace PanDaF.Views;

public partial class HomeView : UserControl
{
    private readonly MainWindow shell;

    public HomeView(MainWindow shell)
    {
        this.shell = shell;
        InitializeComponent();
        DropHelper.Attach(this, files => shell.OpenFiles(files));
        // Dar pencerede kartlar iki sütuna iner.
        SizeChanged += (_, e) => Tiles.Columns = e.NewSize.Width < 900 ? 2 : 3;
        Loaded += (_, _) => MergeTile.Focus();
    }

    private void Merge_Click(object sender, RoutedEventArgs e) => shell.Navigate(new MergeView(shell), "PDF birleştir");
    private void Split_Click(object sender, RoutedEventArgs e) => shell.Navigate(new SplitView(shell), "PDF böl");
    private void Organize_Click(object sender, RoutedEventArgs e) => shell.Navigate(new OrganizeView(shell), "Sayfaları düzenle");
    private void ToPdf_Click(object sender, RoutedEventArgs e) => shell.Navigate(new ToPdfView(shell), "PDF'ye çevir");
    private void FromPdf_Click(object sender, RoutedEventArgs e) => shell.Navigate(new FromPdfView(shell), "PDF'den çevir");
}
