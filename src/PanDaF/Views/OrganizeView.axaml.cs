using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PanDaF.Core;

namespace PanDaF.Views;

/// Düzenleyicideki bir sayfa: küçük resmi, dönüşü ve seçimiyle birlikte kendi kartını çizer.
public sealed class PageItem
{
    public int SourcePage { get; }
    public int Rotation { get => rotation; set { rotation = value; Refresh(); } }
    public bool Selected { get => selected; set { selected = value; Refresh(); } }
    public bool DropTarget { get => dropTarget; set { dropTarget = value; Refresh(); } }
    public Bitmap? Thumb { get => thumb; set { thumb = value; image.Source = value; loading.IsVisible = value is null; } }
    public int Position { get => position; set { position = value; label.Text = $"{value}. sayfa"; ToolTip.SetTip(Card, value == SourcePage ? $"Asıl PDF'de de {SourcePage}. sayfa" : $"Asıl PDF'de {SourcePage}. sayfaydı"); } }
    public Border Card { get; }

    private int rotation, position;
    private bool selected, dropTarget;
    private Bitmap? thumb;
    private readonly Image image;
    private readonly LayoutTransformControl rotator;
    private readonly TextBlock loading, label;
    private readonly Border check;

    public PageItem(int sourcePage, Action<PageItem> rotate, Action<PageItem> delete)
    {
        SourcePage = sourcePage;
        image = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(4), MaxHeight = 198, MaxWidth = 150 };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
        loading = new TextBlock { Text = "Yükleniyor…", Foreground = Ui.Brush("Muted"), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        check = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = Ui.Brush("Primary"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 6, 0), IsVisible = false,
            Child = Ui.IconControl("IconCheck", 13, Brushes.White) };
        var preview = new Grid { Height = 206 };
        preview.Children.Add(new Border { Background = Ui.Brush("Hover"), CornerRadius = new CornerRadius(6) });
        preview.Children.Add(loading);
        rotator = new LayoutTransformControl { Child = image, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        preview.Children.Add(rotator);
        preview.Children.Add(check);
        label = new TextBlock { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var deleteButton = Ui.IconButton("IconDelete", "Bu sayfayı sil", (_, _) => delete(this));
        var rotateButton = Ui.IconButton("IconRotate", "Bu sayfayı sağa döndür", (_, _) => rotate(this));
        DockPanel.SetDock(deleteButton, Dock.Right); DockPanel.SetDock(rotateButton, Dock.Right);
        footer.Children.Add(deleteButton); footer.Children.Add(rotateButton); footer.Children.Add(label);
        var stack = new StackPanel(); stack.Children.Add(preview); stack.Children.Add(footer);
        Card = new Border { Width = 178, Margin = new Thickness(8), Padding = new Thickness(10), CornerRadius = new CornerRadius(12), Background = Ui.Brush("Card"), BorderBrush = Ui.Brush("Line"), BorderThickness = new Thickness(2), Cursor = new Cursor(StandardCursorType.Hand), Child = stack, Tag = this };
        Position = sourcePage;
    }

    private void Refresh()
    {
        check.IsVisible = selected;
        Card.BorderBrush = Ui.Brush(dropTarget ? "Success" : selected ? "Primary" : "Line");
        Card.Background = Ui.Brush(selected ? "PrimarySoft" : "Card");
        rotator.LayoutTransform = rotation == 0 ? null : new RotateTransform(rotation);
    }
}

public partial class OrganizeView : UserControl
{
    private readonly MainWindow shell;
    private readonly List<PageItem> pages = [];
    private readonly Stack<List<(PageItem Item, int Rotation)>> history = new();
    private List<PageItem> original = [];
    private CancellationTokenSource? thumbnails;
    private string? file;
    private Point? dragStart;
    private PageItem? pressed, dragging;

    public OrganizeView(MainWindow shell)
    {
        this.shell = shell;
        InitializeComponent();
        var drop = new DropZone("Düzenlemek istediğiniz PDF'yi buraya sürükleyin", "veya bilgisayarınızdan seçin.", Pick.Pdf, multiple: false);
        drop.FilesChosen += files => Load(files[0]);
        DropHost.Content = drop;
        DropHelper.Attach(this, files => Load(files[0]));
        DetachedFromVisualTree += (_, _) => thumbnails?.Cancel();
        KeyDown += View_KeyDown;
    }

    public int PageCount => pages.Count;
    public int LoadedThumbnails => pages.Count(p => p.Thumb is not null);

    public async void Load(string path)
    {
        if (!FileKinds.IsPdf(path)) { await shell.Info("Lütfen bir PDF dosyası seçin."); return; }
        if (file is not null && history.Count > 0 && !await shell.Confirm("Yaptığınız değişiklikler kaydedilmedi. Yine de başka bir PDF açılsın mı?")) return;
        int count;
        try { count = await Task.Run(() => PdfPages.PageCount(path)); }
        catch (UserFacingException error) { await shell.Error(error.Message); return; }
        thumbnails?.Cancel();
        file = path;
        history.Clear();
        original = Enumerable.Range(1, count).Select(n => new PageItem(n, RotateOne, DeleteOne)).ToList();
        foreach (var item in original) AttachCard(item);
        pages.Clear();
        pages.AddRange(original);
        DropHost.IsVisible = false;
        Editor.IsVisible = true;
        Toolbar.IsVisible = true;
        Rebuild();
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
                    using var skia = pdf.RenderBitmap(i, dpi, token);
                    var bitmap = Pictures.ToBitmap(skia);
                    var item = items[i];
                    Dispatcher.UIThread.Post(() => { if (!token.IsCancellationRequested) item.Thumb = bitmap; });
                }
            }, token);
        }
        catch (OperationCanceledException) { }
        catch (UserFacingException error) { if (!token.IsCancellationRequested) await shell.Error("Sayfa önizlemeleri hazırlanamadı: " + error.Message); }
    }

    private void Rebuild()
    {
        PagesPanel.Children.Clear();
        foreach (var page in pages) PagesPanel.Children.Add(page.Card);
        Update();
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
        Rebuild();
    }

    // ----- Seçme ve sürükleyerek sıralama (işaretçi ile, işletim sisteminden bağımsız) -----

    private void AttachCard(PageItem item)
    {
        var card = item.Card;
        card.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(card).Properties.IsLeftButtonPressed || e.Source is Visual v && v.FindAncestorOfType<Button>(true) is not null) { pressed = null; return; }
            pressed = item; dragStart = e.GetPosition(this); dragging = null;
            Focus();
            e.Pointer.Capture(card);
        };
        card.PointerMoved += (_, e) =>
        {
            if (pressed is null || dragStart is not { } start) return;
            var now = e.GetPosition(this);
            if (dragging is null && Math.Abs(now.X - start.X) < 8 && Math.Abs(now.Y - start.Y) < 8) return;
            dragging = pressed;
            var target = CardAt(now);
            foreach (var page in pages) page.DropTarget = page == target && page != dragging;
        };
        card.PointerReleased += (_, e) =>
        {
            e.Pointer.Capture(null);
            if (pressed is null) return;
            if (dragging is { } source)
            {
                var target = CardAt(e.GetPosition(this));
                foreach (var page in pages) page.DropTarget = false;
                if (target is not null && target != source)
                {
                    var from = pages.IndexOf(source); var to = pages.IndexOf(target);
                    if (from >= 0 && to >= 0) { Remember(); pages.RemoveAt(from); pages.Insert(to, source); Rebuild(); }
                }
            }
            else { item.Selected = !item.Selected; Update(); }
            pressed = null; dragStart = null; dragging = null;
        };
    }

    private PageItem? CardAt(Point point)
    {
        foreach (var page in pages)
        {
            var card = page.Card;
            if (card.TranslatePoint(new Point(0, 0), this) is not { } origin) continue;
            if (new Rect(origin, card.Bounds.Size).Contains(point)) return page;
        }
        return null;
    }

    // ----- Komutlar -----

    private void RotateOne(PageItem item) { Remember(); item.Rotation = (item.Rotation + 90) % 360; Update(); }
    private void DeleteOne(PageItem item) { Remember(); pages.Remove(item); Rebuild(); }

    private void RotateSelected_Click(object? sender, RoutedEventArgs e)
    {
        var selected = pages.Where(p => p.Selected).ToList();
        if (selected.Count == 0) return;
        Remember();
        foreach (var item in selected) item.Rotation = (item.Rotation + 90) % 360;
        Update();
    }

    private void DeleteSelected_Click(object? sender, RoutedEventArgs e) => DeleteSelection();

    private void DeleteSelection()
    {
        var selected = pages.Where(p => p.Selected).ToList();
        if (selected.Count == 0) return;
        Remember();
        foreach (var item in selected) { item.Selected = false; pages.Remove(item); }
        Rebuild();
    }

    private void SelectAll_Click(object? sender, RoutedEventArgs e) => ToggleAll();

    private void ToggleAll()
    {
        var all = pages.Count > 0 && pages.All(p => p.Selected);
        foreach (var page in pages) page.Selected = !all;
        Update();
    }

    private void Undo_Click(object? sender, RoutedEventArgs e) => Undo();
    private void Undo() { if (history.Count > 0) Restore(history.Pop()); }

    private void Reset_Click(object? sender, RoutedEventArgs e)
    {
        if (history.Count == 0) return;
        Remember();
        Restore(original.Select(p => (p, 0)).ToList());
    }

    private async void Change_Click(object? sender, RoutedEventArgs e)
    {
        var picked = await Pick.Files(this, Pick.Pdf, multiple: false);
        if (picked.Length > 0) Load(picked[0]);
    }

    private void View_KeyDown(object? sender, KeyEventArgs e)
    {
        if (file is null || shell.IsBusy || e.Source is TextBox) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (e.Key == Key.Delete || e.Key == Key.Back) { DeleteSelection(); e.Handled = true; }
        else if (ctrl && e.Key == Key.A) { ToggleAll(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Z) { Undo(); e.Handled = true; }
    }

    private async void Go_Click(object? sender, RoutedEventArgs e)
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
        var last = pages[^1]; pages.RemoveAt(pages.Count - 1); pages.Insert(0, last);
        pages[0].Rotation = 90;
        pages.RemoveAt(pages.Count - 1);
        Rebuild();
    }

    internal void SmokeSave() => Go_Click(this, new RoutedEventArgs());
}
