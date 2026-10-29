using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PanDaF.Core;

namespace PanDaF.Views;

public sealed class PageItem(int sourcePage) : INotifyPropertyChanged
{
    private int rotation, position;
    private bool selected, dropTarget;
    private ImageSource? thumb;

    public int SourcePage { get; } = sourcePage;
    public int Rotation { get => rotation; set => Set(ref rotation, value); }
    public bool Selected { get => selected; set => Set(ref selected, value); }
    public bool DropTarget { get => dropTarget; set => Set(ref dropTarget, value); }
    public ImageSource? Thumb { get => thumb; set { if (Set(ref thumb, value)) Changed(nameof(Loading)); } }
    public bool Loading => thumb is null;
    public int Position { get => position; set { if (Set(ref position, value)) { Changed(nameof(Label)); Changed(nameof(Tip)); } } }
    public string Label => $"{Position}. sayfa";
    public string Tip => Position == SourcePage ? $"Asıl PDF'de de {SourcePage}. sayfa" : $"Asıl PDF'de {SourcePage}. sayfaydı";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Changed(name); return true;
    }
}

public partial class OrganizeView : UserControl
{
    private const string Filter = "PDF dosyaları|*.pdf";
    private readonly MainWindow shell;
    private readonly ObservableCollection<PageItem> pages = [];
    private readonly Stack<List<(PageItem Item, int Rotation)>> history = new();
    private List<PageItem> original = [];
    private CancellationTokenSource? thumbnails;
    private string? file;
    private Point? dragStart;
    private PageItem? pressed;

    public OrganizeView(MainWindow shell)
    {
        this.shell = shell;
        InitializeComponent();
        PagesView.ItemsSource = pages;
        var drop = new DropZone("Düzenlemek istediğiniz PDF'yi buraya sürükleyin", "veya bilgisayarınızdan seçin.", Filter, multiple: false);
        drop.FilesChosen += files => Load(files[0]);
        DropHost.Content = drop;
        DropHelper.Attach(this, files => Load(files[0]));
        Unloaded += (_, _) => thumbnails?.Cancel();
    }

    public int PageCount => pages.Count;
    public int LoadedThumbnails => pages.Count(p => p.Thumb is not null);

    public async void Load(string path)
    {
        if (!FileKinds.IsPdf(path)) { shell.Info("Lütfen bir PDF dosyası seçin."); return; }
        if (file is not null && history.Count > 0 && !shell.Confirm("Yaptığınız değişiklikler kaydedilmedi. Yine de başka bir PDF açılsın mı?")) return;
        int count;
        try { count = await Task.Run(() => PdfPages.PageCount(path)); }
        catch (UserFacingException error) { shell.Error(error.Message); return; }
        thumbnails?.Cancel();
        file = path;
        history.Clear();
        original = Enumerable.Range(1, count).Select(n => new PageItem(n)).ToList();
        pages.Clear();
        foreach (var item in original) pages.Add(item);
        DropHost.Visibility = Visibility.Collapsed;
        Editor.Visibility = Visibility.Visible;
        Toolbar.Visibility = Visibility.Visible;
        Update();
        Focus();
        await RenderThumbnails(path, original);
    }

    private async Task RenderThumbnails(string path, List<PageItem> items)
    {
        var cancellation = thumbnails = new CancellationTokenSource();
        var token = cancellation.Token;
        // Çok sayfalı PDF'lerde bellek kullanımı düşük kalsın.
        var dpi = items.Count > 300 ? 22 : 32;
        try
        {
            await Task.Run(() =>
            {
                using var pdf = PdfRasterDocument.Open(path);
                for (var i = 0; i < Math.Min(pdf.PageCount, items.Count); i++)
                {
                    token.ThrowIfCancellationRequested();
                    var bitmap = pdf.RenderBitmap(i, dpi, token);
                    var item = items[i];
                    _ = Dispatcher.InvokeAsync(() => { if (!token.IsCancellationRequested) item.Thumb = bitmap; });
                }
            }, token);
        }
        catch (OperationCanceledException) { }
        catch (UserFacingException error) { if (!token.IsCancellationRequested) shell.Error("Sayfa önizlemeleri hazırlanamadı: " + error.Message); }
    }

    private void Update()
    {
        for (var i = 0; i < pages.Count; i++) pages[i].Position = i + 1;
        var selected = pages.Count(p => p.Selected);
        RotateSelected.IsEnabled = DeleteSelected.IsEnabled = selected > 0;
        UndoButton.IsEnabled = ResetButton.IsEnabled = history.Count > 0;
        SelectAllText.Text = selected == pages.Count && pages.Count > 0 ? "Seçimi kaldır" : "Tümünü seç";
        Go.IsEnabled = file is not null && pages.Count > 0;
        if (file is null) return;
        var removed = original.Count - pages.Count;
        var rotated = pages.Count(p => p.Rotation != 0);
        var moved = pages.Where((p, i) => p.SourcePage != i + 1).Any();
        var parts = new List<string> { $"{pages.Count} sayfa" };
        if (selected > 0) parts.Add($"{selected} seçili");
        if (removed > 0) parts.Add($"{removed} sayfa silinecek");
        if (rotated > 0) parts.Add($"{rotated} sayfa döndürüldü");
        if (moved && removed == 0) parts.Add("sıralama değişti");
        Summary.Text = pages.Count == 0 ? "Tüm sayfaları sildiniz. “Geri al” ile geri getirebilirsiniz." : string.Join(" · ", parts);
        if (pages.Count == 0) Go.IsEnabled = false;
    }

    private void Remember()
    {
        history.Push(pages.Select(p => (p, p.Rotation)).ToList());
        if (history.Count > 100)
        {
            var keep = history.Take(100).Reverse().ToList();
            history.Clear();
            foreach (var state in keep) history.Push(state);
        }
    }

    private void Restore(List<(PageItem Item, int Rotation)> state)
    {
        pages.Clear();
        foreach (var (item, rotation) in state) { item.Rotation = rotation; item.Selected = false; pages.Add(item); }
        Update();
    }

    private static PageItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as PageItem;

    private static bool InsideButton(object source)
    {
        for (var node = source as DependencyObject; node is not null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is ButtonBase) return true;
        return false;
    }

    // ----- Seçme ve sürükleyerek sıralama -----

    private void Card_Down(object sender, MouseButtonEventArgs e)
    {
        if (InsideButton(e.OriginalSource)) { pressed = null; return; }
        pressed = ItemOf(sender);
        dragStart = e.GetPosition(this);
        Focus();
    }

    private void Card_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || pressed is null || dragStart is not { } start) return;
        var now = e.GetPosition(this);
        if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance * 2 && Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance * 2) return;
        var item = pressed;
        pressed = null; dragStart = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(PageItem), item), DragDropEffects.Move);
        foreach (var page in pages) page.DropTarget = false;
    }

    private void Card_Up(object sender, MouseButtonEventArgs e)
    {
        if (pressed is { } item && ItemOf(sender) == item)
        {
            item.Selected = !item.Selected;
            Update();
        }
        pressed = null; dragStart = null;
    }

    private void Card_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(PageItem))) return;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        if (ItemOf(sender) is { } target)
            foreach (var page in pages) page.DropTarget = page == target && page != e.Data.GetData(typeof(PageItem));
    }

    private void Card_DragLeave(object sender, DragEventArgs e)
    {
        if (ItemOf(sender) is { } target) target.DropTarget = false;
    }

    private void Card_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(PageItem)) is not PageItem source || ItemOf(sender) is not { } target) return;
        e.Handled = true;
        target.DropTarget = false;
        var from = pages.IndexOf(source); var to = pages.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return;
        Remember();
        pages.Move(from, to);
        Update();
    }

    // ----- Komutlar -----

    private void RotateOne_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        Remember();
        item.Rotation = (item.Rotation + 90) % 360;
        Update();
    }

    private void DeleteOne_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        Remember();
        pages.Remove(item);
        Update();
    }

    private void RotateSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = pages.Where(p => p.Selected).ToList();
        if (selected.Count == 0) return;
        Remember();
        foreach (var item in selected) item.Rotation = (item.Rotation + 90) % 360;
        Update();
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e) => DeleteSelection();

    private void DeleteSelection()
    {
        var selected = pages.Where(p => p.Selected).ToList();
        if (selected.Count == 0) return;
        Remember();
        foreach (var item in selected) { item.Selected = false; pages.Remove(item); }
        Update();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => ToggleAll();

    private void ToggleAll()
    {
        var all = pages.Count > 0 && pages.All(p => p.Selected);
        foreach (var page in pages) page.Selected = !all;
        Update();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    private void Undo()
    {
        if (history.Count == 0) return;
        Restore(history.Pop());
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (history.Count == 0) return;
        Remember();
        Restore(original.Select(p => (p, 0)).ToList());
    }

    private void Change_Click(object sender, RoutedEventArgs e)
    {
        var picked = DropZone.Pick(Filter, multiple: false);
        if (picked.Length > 0) Load(picked[0]);
    }

    private void View_KeyDown(object sender, KeyEventArgs e)
    {
        if (file is null || shell.IsBusy || e.OriginalSource is TextBox) return;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.Key == Key.Delete) { DeleteSelection(); e.Handled = true; }
        else if (ctrl && e.Key == Key.A) { ToggleAll(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Z) { Undo(); e.Handled = true; }
    }

    private async void Go_Click(object sender, RoutedEventArgs e)
    {
        if (file is null || pages.Count == 0) return;
        var input = file;
        var order = pages.Select(p => p.SourcePage).ToArray();
        var rotations = pages.Select(p => p.Rotation).ToArray();
        var destination = OutputFolder.For(input);
        var result = await shell.RunAsync("Yeni PDF kaydediliyor", (lossy, progress, token) =>
            Task.Run<IReadOnlyList<string>>(() => [PdfPages.Organize(input, destination, order, rotations, lossy, token)], token));
        if (result is null) return;
        history.Clear();
        Update();
        shell.ShowResult(result, "Düzenlediğiniz PDF yeni bir dosya olarak kaydedildi. Asıl PDF olduğu gibi duruyor.");
    }

    // Duman testi için: belirli bir değişikliği uygular.
    internal void SmokeEdit()
    {
        if (pages.Count < 2) return;
        Remember();
        pages.Move(pages.Count - 1, 0);
        pages[0].Rotation = 90;
        pages.RemoveAt(pages.Count - 1);
        Update();
    }

    internal void SmokeSave() => Go_Click(this, new RoutedEventArgs());
}
