using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PanDaF.Core;

namespace PanDaF.Views;

/// PDF üzerine yazı, kapatma, vurgu, çizim, resim ve logo ekleme. Asıl içerik değişmez;
/// eklenenler kaydederken sayfanın üstüne işlenir.
public partial class EditView : UserControl
{
    private const string Filter = "PDF dosyaları|*.pdf";
    private const int RenderDpi = 144;
    private readonly MainWindow shell;
    private readonly List<Mark> marks = [];
    private readonly Stack<List<Mark>> history = new();
    private readonly Dictionary<int, BitmapSource> pages = [];
    private readonly Dictionary<int, Size> sizes = [];
    private string? file;
    private int pageCount, page;
    private double zoom = 1;
    private Mark? selected, dragging, drawing;
    private Point dragStart; private Mark? dragOriginal; private bool resizing;
    private TextBox? editor;
    private string color = "#000000";
    private readonly Dictionary<string, Border> swatches = [];

    private enum Tool { Select, Text, Whiteout, Highlight, Pen }
    private Tool Current => TextTool.IsChecked == true ? Tool.Text : WhiteoutTool.IsChecked == true ? Tool.Whiteout : HighlightTool.IsChecked == true ? Tool.Highlight : PenTool.IsChecked == true ? Tool.Pen : Tool.Select;

    public EditView(MainWindow shell)
    {
        this.shell = shell;
        InitializeComponent();
        var drop = new DropZone("Düzenlemek istediğiniz PDF'yi buraya sürükleyin", "Üzerine yazı, kapatma, vurgu, çizim, resim ve okul logoları ekleyebilirsiniz.", Filter, multiple: false);
        drop.FilesChosen += files => Load(files[0]);
        DropHost.Content = drop;
        DropHelper.Attach(this, files => { if (file is null) Load(files[0]); });
        foreach (var hex in new[] { "#000000", "#D9480F", "#1F5FBF", "#16805C", "#F2C811" }) AddSwatch(hex);
        foreach (var tool in new[] { SelectTool, TextTool, WhiteoutTool, HighlightTool, PenTool })
            tool.Checked += (_, _) => ToolChanged();
    }

    public int MarkCount => marks.Count;

    private void AddSwatch(string hex)
    {
        var border = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(3, 0, 3, 0), Cursor = Cursors.Hand,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)), BorderBrush = Ui.Brush("Line"), BorderThickness = new Thickness(hex == color ? 3 : 1),
            ToolTip = "Renk",
        };
        border.MouseLeftButtonDown += (_, e) => { e.Handled = true; SetColor(hex); };
        swatches[hex] = border;
        Swatches.Children.Add(border);
    }

    private void SetColor(string hex)
    {
        color = hex;
        foreach (var (key, border) in swatches) { border.BorderThickness = new Thickness(key == hex ? 3 : 1); border.BorderBrush = key == hex ? Ui.Brush("Ink") : Ui.Brush("Line"); }
        if (selected is { Kind: MarkKind.Text or MarkKind.Ink or MarkKind.Highlight }) { Remember(); selected.Color = hex; Redraw(); }
    }

    private void ToolChanged()
    {
        if (PenOptions is null) return;
        PenOptions.Visibility = Current == Tool.Pen ? Visibility.Visible : Visibility.Collapsed;
        TextOptions.Visibility = Current is Tool.Text or Tool.Select ? Visibility.Visible : Visibility.Collapsed;
        if (Current == Tool.Highlight && color == "#000000") SetColor("#F2C811");
        if (Current != Tool.Select) { EndEdit(); selected = null; Redraw(); }
        Overlay.Cursor = Current == Tool.Select ? Cursors.Arrow : Current == Tool.Text ? Cursors.IBeam : Cursors.Cross;
    }

    // ----- Yükleme ve görüntüleme -----

    public async void Load(string path)
    {
        if (!FileKinds.IsPdf(path)) { shell.Info("Lütfen bir PDF dosyası seçin."); return; }
        try
        {
            pageCount = await Task.Run(() => PdfPages.PageCount(path));
            using var raster = PdfRasterDocument.Open(path);
            var first = raster.RenderBitmap(0, RenderDpi);
            pages[0] = first; sizes[0] = new Size(first.PixelWidth * 72.0 / RenderDpi, first.PixelHeight * 72.0 / RenderDpi);
        }
        catch (UserFacingException error) { shell.Error(error.Message); return; }
        file = path; page = 0; marks.Clear(); history.Clear(); selected = null;
        DropHost.Visibility = Visibility.Collapsed;
        Toolbar.Visibility = PageArea.Visibility = Footer.Visibility = Visibility.Visible;
        await ShowPage(0);
        ZoomFit_Click(this, new RoutedEventArgs());
        Focus();
    }

    private async Task ShowPage(int index)
    {
        if (file is null) return;
        page = Math.Clamp(index, 0, pageCount - 1);
        if (!pages.TryGetValue(page, out var bitmap))
        {
            var path = file; var n = page;
            try
            {
                bitmap = await Task.Run(() => { using var raster = PdfRasterDocument.Open(path); return raster.RenderBitmap(n, RenderDpi); });
            }
            catch (UserFacingException error) { shell.Error(error.Message); return; }
            pages[page] = bitmap; sizes[page] = new Size(bitmap.PixelWidth * 72.0 / RenderDpi, bitmap.PixelHeight * 72.0 / RenderDpi);
            if (pages.Count > 12) foreach (var old in pages.Keys.Where(k => Math.Abs(k - page) > 5).ToList()) pages.Remove(old);
        }
        PageImage.Source = bitmap;
        var size = sizes[page];
        PageImage.Width = Overlay.Width = size.Width; PageImage.Height = Overlay.Height = size.Height;
        PageLabel.Text = $"Sayfa {page + 1} / {pageCount}";
        EndEdit(); selected = null;
        Redraw();
    }

    private void SetZoom(double value)
    {
        zoom = Math.Clamp(value, .25, 4);
        Zoom.ScaleX = Zoom.ScaleY = zoom;
        ZoomLabel.Text = $"{Math.Round(zoom * 100)}%";
        Redraw();
    }

    private void ZoomFit_Click(object sender, RoutedEventArgs e)
    {
        if (!sizes.TryGetValue(page, out var size) || Scroller.ViewportWidth < 50) return;
        SetZoom((Scroller.ViewportWidth - 60) / size.Width);
    }
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(zoom * 1.2);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(zoom / 1.2);
    private bool fitted;
    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e) { if (!fitted && file is not null) { fitted = true; ZoomFit_Click(this, new RoutedEventArgs()); } }
    private async void PrevPage_Click(object sender, RoutedEventArgs e) => await ShowPage(page - 1);
    private async void NextPage_Click(object sender, RoutedEventArgs e) => await ShowPage(page + 1);

    // ----- Çizim -----

    private Point Pt(MouseEventArgs e) => e.GetPosition(Overlay);
    private IEnumerable<Mark> OnPage => marks.Where(m => m.Page == page);

    private void Redraw()
    {
        Overlay.Children.Clear();
        foreach (var mark in OnPage) Overlay.Children.Add(Element(mark));
        if (selected is not null && selected.Page == page && selected != drawing)
        {
            var outline = new Rectangle { Width = Math.Max(1, selected.Width), Height = Math.Max(1, selected.Height), Stroke = Ui.Brush("Primary"), StrokeThickness = 1.5 / zoom, StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false };
            Canvas.SetLeft(outline, selected.X); Canvas.SetTop(outline, selected.Y); Overlay.Children.Add(outline);
            var grip = 10 / zoom;
            var handle = new Rectangle { Width = grip, Height = grip, Fill = Ui.Brush("Primary"), Stroke = Brushes.White, StrokeThickness = 1 / zoom, IsHitTestVisible = false };
            Canvas.SetLeft(handle, selected.X + selected.Width - grip / 2); Canvas.SetTop(handle, selected.Y + selected.Height - grip / 2); Overlay.Children.Add(handle);
        }
        if (editor is not null) Overlay.Children.Add(editor);
        DeleteButton.IsEnabled = selected is not null;
        UndoButton.IsEnabled = history.Count > 0;
        Summary.Text = marks.Count == 0 ? "Araç seçip sayfaya tıklayın veya sürükleyin." : $"{marks.Count} ekleme" + (marks.Any(m => m.Page != page) ? " (tüm sayfalar)" : "");
    }

    private static Brush BrushOf(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

    private FrameworkElement Element(Mark mark)
    {
        FrameworkElement element;
        switch (mark.Kind)
        {
            case MarkKind.Whiteout:
                element = new Rectangle { Width = mark.Width, Height = mark.Height, Fill = Brushes.White, Stroke = new SolidColorBrush(Color.FromArgb(120, 150, 150, 150)), StrokeThickness = .7 / zoom, StrokeDashArray = new DoubleCollection { 3, 3 } };
                break;
            case MarkKind.Highlight:
                element = new Rectangle { Width = mark.Width, Height = mark.Height, Fill = BrushOf(mark.Color), Opacity = .35 };
                break;
            case MarkKind.Ink:
                var line = new Polyline { Stroke = BrushOf(mark.Color), StrokeThickness = mark.StrokeWidth, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                foreach (var (x, y) in mark.Points) line.Points.Add(new Point(x, y));
                element = line;
                break;
            case MarkKind.Image:
                var image = new Image { Width = mark.Width, Height = mark.Height, Stretch = Stretch.Fill };
                if (mark.ImagePng is not null) image.Source = Decode(mark.ImagePng);
                element = image;
                break;
            default:
                var text = new TextBlock { Text = mark.Text, FontFamily = new FontFamily("Arial"), FontSize = mark.FontSize, FontWeight = mark.Bold ? FontWeights.Bold : FontWeights.Normal, Foreground = BrushOf(mark.Color), TextWrapping = TextWrapping.Wrap, Width = Math.Max(10, mark.Width - 4), Margin = new Thickness(2) };
                element = new Border { Width = mark.Width, MinHeight = mark.Height, Child = text, Background = Brushes.Transparent, Visibility = editor is not null && editor.Tag == mark ? Visibility.Hidden : Visibility.Visible };
                break;
        }
        element.IsHitTestVisible = false;
        Canvas.SetLeft(element, mark.X); Canvas.SetTop(element, mark.Y);
        return element;
    }

    private static readonly Dictionary<byte[], BitmapSource> decoded = new(ReferenceEqualityComparer.Instance);
    private static BitmapSource Decode(byte[] png)
    {
        if (decoded.TryGetValue(png, out var cached)) return cached;
        var image = new BitmapImage();
        using (var stream = new MemoryStream(png)) { image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); }
        image.Freeze();
        return decoded[png] = image;
    }

    // ----- Fare -----

    private Mark? Hit(Point p) => OnPage.Reverse().FirstOrDefault(m => p.X >= m.X - 3 && p.X <= m.X + m.Width + 3 && p.Y >= m.Y - 3 && p.Y <= m.Y + m.Height + 3);
    private bool OnGrip(Mark m, Point p) => Math.Abs(p.X - (m.X + m.Width)) <= 8 / zoom && Math.Abs(p.Y - (m.Y + m.Height)) <= 8 / zoom;

    private void Overlay_Down(object sender, MouseButtonEventArgs e)
    {
        if (file is null) return;
        Focus();
        var p = Pt(e);
        if (editor is not null) { if (selected is not null && Contains(selected, p)) return; EndEdit(); }
        switch (Current)
        {
            case Tool.Select:
                var hit = Hit(p);
                if (hit is null) { selected = null; Redraw(); return; }
                selected = hit;
                if (e.ClickCount == 2 && hit.Kind == MarkKind.Text) { Redraw(); BeginEdit(hit); return; }
                dragging = hit; dragOriginal = hit.Clone(); dragStart = p; resizing = OnGrip(hit, p);
                Overlay.CaptureMouse(); Redraw();
                break;
            case Tool.Text:
                var size = ChosenFontSize();
                var text = new Mark { Kind = MarkKind.Text, Page = page, X = p.X, Y = p.Y - size * .8, Width = Math.Min(260, Overlay.Width - p.X), Height = size * 1.6 + 4, FontSize = size, Bold = BoldBox.IsChecked == true, Color = color == "#F2C811" ? "#000000" : color };
                Remember(); marks.Add(text); selected = text; Redraw(); BeginEdit(text);
                SelectTool.IsChecked = true;
                break;
            case Tool.Whiteout or Tool.Highlight:
                drawing = new Mark { Kind = Current == Tool.Whiteout ? MarkKind.Whiteout : MarkKind.Highlight, Page = page, X = p.X, Y = p.Y, Color = color };
                dragStart = p; Overlay.CaptureMouse();
                break;
            case Tool.Pen:
                drawing = new Mark { Kind = MarkKind.Ink, Page = page, X = 0, Y = 0, Color = color, StrokeWidth = PenWidth.Value };
                drawing.Points.Add((p.X, p.Y));
                Overlay.CaptureMouse();
                Remember(); marks.Add(drawing); Redraw();
                break;
        }
    }

    private static bool Contains(Mark m, Point p) => p.X >= m.X && p.X <= m.X + m.Width && p.Y >= m.Y && p.Y <= m.Y + m.Height;

    private void Overlay_Move(object sender, MouseEventArgs e)
    {
        var p = Pt(e);
        if (Current == Tool.Select && dragging is null) Overlay.Cursor = selected is not null && OnGrip(selected, p) ? Cursors.SizeNWSE : Hit(p) is not null ? Cursors.SizeAll : Cursors.Arrow;
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (drawing is { Kind: MarkKind.Ink })
        {
            drawing.Points.Add((p.X, p.Y));
            if (Overlay.Children.Count > 0 && Overlay.Children[^1] is Polyline line) line.Points.Add(p); else Redraw();
            return;
        }
        if (drawing is not null)
        {
            drawing.X = Math.Min(dragStart.X, p.X); drawing.Y = Math.Min(dragStart.Y, p.Y);
            drawing.Width = Math.Abs(p.X - dragStart.X); drawing.Height = Math.Abs(p.Y - dragStart.Y);
            Redraw();
            if (drawing.Width > 0 && drawing.Height > 0) Overlay.Children.Add(Element(drawing));
            return;
        }
        if (dragging is null || dragOriginal is null) return;
        var dx = p.X - dragStart.X; var dy = p.Y - dragStart.Y;
        if (!moved && Math.Abs(dx) + Math.Abs(dy) > 1 / zoom) { moved = true; Remember(dragOriginal); }
        if (!moved) return;
        if (resizing)
        {
            var w = Math.Max(8, dragOriginal.Width + dx); var h = Math.Max(8, dragOriginal.Height + dy);
            if (dragging.Kind == MarkKind.Image) h = w * dragOriginal.Height / Math.Max(1, dragOriginal.Width);
            if (dragging.Kind == MarkKind.Ink)
            {
                var sx = w / Math.Max(1, dragOriginal.Width); var sy = h / Math.Max(1, dragOriginal.Height);
                dragging.Points = dragOriginal.Points.Select(q => (q.X * sx, q.Y * sy)).ToList();
            }
            dragging.Width = w; dragging.Height = h;
        }
        else { dragging.X = dragOriginal.X + dx; dragging.Y = dragOriginal.Y + dy; }
        Redraw();
    }

    private bool moved;

    private void Overlay_Up(object sender, MouseButtonEventArgs e)
    {
        Overlay.ReleaseMouseCapture();
        if (drawing is not null)
        {
            if (drawing.Kind == MarkKind.Ink)
            {
                var xs = drawing.Points.Select(q => q.X).ToList(); var ys = drawing.Points.Select(q => q.Y).ToList();
                var minX = xs.Min() - drawing.StrokeWidth; var minY = ys.Min() - drawing.StrokeWidth;
                drawing.Points = drawing.Points.Select(q => (q.X - minX, q.Y - minY)).ToList();
                drawing.X = minX; drawing.Y = minY; drawing.Width = xs.Max() - minX + drawing.StrokeWidth; drawing.Height = ys.Max() - minY + drawing.StrokeWidth;
            }
            else if (drawing.Width >= 3 && drawing.Height >= 3) { Remember(); marks.Add(drawing); selected = drawing; if (drawing.Kind == MarkKind.Whiteout) SelectTool.IsChecked = true; }
            drawing = null; Redraw();
        }
        dragging = null; dragOriginal = null; moved = false; resizing = false;
    }

    private void Overlay_Leave(object sender, MouseEventArgs e) { if (!Overlay.IsMouseCaptured) { dragging = null; } }

    // ----- Yazı düzenleme -----

    private double ChosenFontSize() => double.TryParse((FontSizeBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out var v) ? v : 14;

    private void BeginEdit(Mark mark)
    {
        EndEdit();
        editor = new TextBox
        {
            Tag = mark, Text = mark.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Arial"), FontSize = mark.FontSize,
            FontWeight = mark.Bold ? FontWeights.Bold : FontWeights.Normal, Foreground = BrushOf(mark.Color), Width = mark.Width, MinHeight = mark.Height,
            Padding = new Thickness(0), Margin = new Thickness(0), BorderThickness = new Thickness(1 / zoom), BorderBrush = Ui.Brush("Primary"), Background = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
        };
        editor.TextChanged += (_, _) => { mark.Text = editor.Text; editor.UpdateLayout(); mark.Height = Math.Max(mark.FontSize * 1.6 + 4, editor.ActualHeight); };
        editor.LostKeyboardFocus += (_, _) => EndEdit();
        editor.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { EndEdit(); e.Handled = true; } };
        Canvas.SetLeft(editor, mark.X); Canvas.SetTop(editor, mark.Y);
        Redraw();
        editor.Focus(); editor.CaretIndex = editor.Text.Length;
    }

    private void EndEdit()
    {
        if (editor is null) return;
        var box = editor; editor = null;
        if (box.Tag is Mark mark)
        {
            mark.Text = box.Text;
            if (string.IsNullOrWhiteSpace(mark.Text)) { marks.Remove(mark); if (selected == mark) selected = null; }
        }
        Redraw();
        Focus();
    }

    private void TextOption_Changed(object sender, RoutedEventArgs e)
    {
        if (selected is { Kind: MarkKind.Text })
        {
            Remember(); selected.FontSize = ChosenFontSize(); selected.Bold = BoldBox.IsChecked == true;
            if (editor?.Tag == selected) { editor.FontSize = selected.FontSize; editor.FontWeight = selected.Bold ? FontWeights.Bold : FontWeights.Normal; }
            Redraw();
        }
    }

    // ----- Resim ve logolar -----

    private void AddImage_Click(object sender, RoutedEventArgs e)
    {
        if (file is null) return;
        var picked = DropZone.Pick("Resimler|*.png;*.jpg;*.jpeg;*.bmp;*.gif", multiple: false);
        if (picked.Length == 0) return;
        try
        {
            var bitmap = Images.Load(picked[0]);
            var png = Images.Png(bitmap);
            var size = sizes[page];
            var width = Math.Min(size.Width * .5, 200.0); var height = width * bitmap.PixelHeight / Math.Max(1, bitmap.PixelWidth);
            AddImage(png, (size.Width - width) / 2, (size.Height - height) / 2, width, height, page);
        }
        catch (UserFacingException error) { shell.Error(error.Message); }
    }

    private static byte[] Resource(string name)
    {
        using var stream = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/{name}"))!.Stream;
        using var memory = new MemoryStream(); stream.CopyTo(memory); return memory.ToArray();
    }
    private static readonly Lazy<byte[]> IstekLogo = new(() => Resource("LogoIstek.png"));
    private static readonly Lazy<byte[]> IbLogo = new(() => Resource("LogoIB.png"));
    private const double LogoHeight = 54, LogoMargin = 26;

    private void AddImage(byte[] png, double x, double y, double width, double height, int target)
    {
        var mark = new Mark { Kind = MarkKind.Image, Page = target, X = x, Y = y, Width = width, Height = height, ImagePng = png };
        marks.Add(mark); selected = mark;
    }

    private (double Width, double Height) LogoSize(byte[] png) { var image = Decode(png); var h = LogoHeight; return (h * image.PixelWidth / image.PixelHeight, h); }

    private void AddIstek_Click(object sender, RoutedEventArgs e) { if (file is null) return; Remember(); PlaceLogos(page, istek: true, ib: false); SelectTool.IsChecked = true; Redraw(); }
    private void AddIB_Click(object sender, RoutedEventArgs e) { if (file is null) return; Remember(); PlaceLogos(page, istek: false, ib: true); SelectTool.IsChecked = true; Redraw(); }

    private async void AddBoth_Click(object sender, RoutedEventArgs e)
    {
        if (file is null) return;
        Remember();
        for (var n = 0; n < pageCount; n++)
        {
            if (!sizes.ContainsKey(n))
            {
                var path = file;
                var size = await Task.Run(() => { using var raster = PdfRasterDocument.Open(path); var b = raster.RenderBitmap(n, 36); return new Size(b.PixelWidth * 2, b.PixelHeight * 2); });
                sizes[n] = size;
            }
            PlaceLogos(n, istek: true, ib: true);
        }
        selected = null; SelectTool.IsChecked = true; Redraw();
    }

    internal void PlaceLogos(int target, bool istek, bool ib)
    {
        var size = sizes[target];
        if (istek) { var (w, h) = LogoSize(IstekLogo.Value); AddImage(IstekLogo.Value, LogoMargin, LogoMargin * .8, w, h, target); }
        if (ib) { var (w, h) = LogoSize(IbLogo.Value); AddImage(IbLogo.Value, size.Width - LogoMargin - w, LogoMargin * .8, w, h, target); }
    }

    // ----- Geri al, sil, klavye -----

    /// Geçmişe şu anki durumu yazar. Sürüklemede, taşınan öğenin hareket öncesi hâli (original) yazılır.
    private void Remember(Mark? original = null)
    {
        history.Push(marks.Select(m => original is not null && m == dragging ? original.Clone() : m.Clone()).ToList());
        while (history.Count > 60) { var keep = history.Take(60).Reverse().ToList(); history.Clear(); foreach (var s in keep) history.Push(s); }
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Undo()
    {
        EndEdit();
        if (history.Count == 0) return;
        marks.Clear(); marks.AddRange(history.Pop()); selected = null; Redraw();
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();
    private void DeleteSelected()
    {
        if (selected is null) return;
        EndEdit();
        Remember(); marks.Remove(selected); selected = null; Redraw();
    }

    private async void View_KeyDown(object sender, KeyEventArgs e)
    {
        if (file is null || e.OriginalSource is TextBox) return;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Z) { Undo(); e.Handled = true; }
        else if (e.Key == Key.Escape && selected is not null) { selected = null; Redraw(); e.Handled = true; }
        else if (e.Key == Key.PageDown) { await ShowPage(page + 1); e.Handled = true; }
        else if (e.Key == Key.PageUp) { await ShowPage(page - 1); e.Handled = true; }
        else if (e.Key == Key.F2 && selected is { Kind: MarkKind.Text }) { BeginEdit(selected); e.Handled = true; }
    }

    // ----- Kaydet -----

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();

    internal async Task<IReadOnlyList<string>?> SaveAsync()
    {
        if (file is null) return null;
        EndEdit();
        if (marks.Count == 0) { shell.Info("Henüz bir şey eklemediniz."); return null; }
        var input = file; var list = marks.Select(m => m.Clone()).ToList();
        var destination = OutputFolder.For(input);
        var result = await shell.RunAsync("Düzenlenen PDF kaydediliyor", (_, progress, token) => Task.Run<IReadOnlyList<string>>(() =>
        {
            var output = SafePaths.Unique(destination, SafePaths.Stem(input) + " (düzenlendi).pdf");
            PdfAnnotator.Save(input, output, list, token);
            return [output];
        }, token));
        if (result is not null) { history.Clear(); Redraw(); shell.ShowResult(result, "Düzenlediğiniz PDF yeni bir dosya olarak kaydedildi. Asıl PDF olduğu gibi duruyor."); }
        return result;
    }

    // Duman testi
    internal void SmokeAdd()
    {
        var size = sizes[page];
        marks.Add(new Mark { Kind = MarkKind.Whiteout, Page = page, X = 60, Y = 120, Width = 200, Height = 40 });
        marks.Add(new Mark { Kind = MarkKind.Text, Page = page, X = 64, Y = 124, Width = 220, Height = 30, Text = "Düzeltilmiş başlık ĞÜŞİÖÇ", FontSize = 16, Bold = true, Color = "#1F5FBF" });
        marks.Add(new Mark { Kind = MarkKind.Highlight, Page = page, X = 60, Y = 200, Width = 240, Height = 22, Color = "#F2C811" });
        marks.Add(new Mark { Kind = MarkKind.Ink, Page = page, X = 80, Y = 260, Width = 160, Height = 60, Color = "#D9480F", StrokeWidth = 3, Points = [(0, 50), (40, 0), (80, 50), (120, 0), (160, 50)] });
        PlaceLogos(page, istek: true, ib: true);
        selected = marks[1];
        Redraw();
    }
}
