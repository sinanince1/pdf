using Avalonia.Collections;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using PanDaF.Core;
using SkiaSharp;

namespace PanDaF.Views;

/// PDF üzerine yazı, kapatma, vurgu, çizim, resim ve logo ekleme; var olan yazıyı yerinde değiştirme.
/// Asıl dosya değişmez; sonuç yeni bir PDF olarak kaydedilir.
public partial class EditView : UserControl
{
    private const int RenderDpi = 144;
    private static readonly FontFamily TextFont = new("Arial, Liberation Sans, Helvetica, sans-serif");
    private readonly MainWindow shell;
    private readonly List<Mark> marks = [];
    private readonly Stack<(List<Mark> Marks, byte[] Document)> history = new();
    // Çalışılan belge: yerinde yazı değişiklikleri bu baytlara işlenir; sayfalar buradan çizilir.
    private byte[] document = [];
    private byte[] original = [];
    private string workPath = "";
    private List<TextLine> lines = [];
    private TextLine? hoveredLine;
    private TextBox? rewriteBox;
    private List<Paragraph> paragraphs = [];
    private Paragraph? hoveredParagraph;
    private TextBox? paragraphBox;
    private readonly Dictionary<int, (SKBitmap Skia, Bitmap Image)> pages = [];
    private readonly Dictionary<int, Size> sizes = [];
    private string? file;
    private int pageCount, page;
    private double zoom = 1;
    private Mark? selected, dragging, drawing;
    private Point dragStart; private Mark? dragOriginal; private bool resizing, moved;
    private TextBox? editor;
    private string color = "#000000";
    private readonly Dictionary<string, Border> swatches = [];
    private double overlayWidth;

    private enum Tool { Select, Rewrite, ParagraphEdit, Text, Whiteout, Highlight, Pen }
    private Tool Current => RewriteTool.IsChecked == true ? Tool.Rewrite : ParagraphTool.IsChecked == true ? Tool.ParagraphEdit : TextTool.IsChecked == true ? Tool.Text : WhiteoutTool.IsChecked == true ? Tool.Whiteout : HighlightTool.IsChecked == true ? Tool.Highlight : PenTool.IsChecked == true ? Tool.Pen : Tool.Select;

    public EditView(MainWindow shell)
    {
        this.shell = shell;
        InitializeComponent();
        var drop = new DropZone("Düzenlemek istediğiniz PDF'yi buraya sürükleyin", "Var olan yazıyı değiştirebilir; üzerine yazı, kapatma, vurgu, çizim, resim ve okul logoları ekleyebilirsiniz.", Pick.Pdf, multiple: false);
        drop.FilesChosen += files => Load(files[0]);
        DropHost.Content = drop;
        DropHelper.Attach(this, files => { if (file is null) Load(files[0]); });
        foreach (var hex in new[] { "#000000", "#D9480F", "#1F5FBF", "#16805C", "#F2C811" }) AddSwatch(hex);
        foreach (var tool in new[] { SelectTool, RewriteTool, ParagraphTool, TextTool, WhiteoutTool, HighlightTool, PenTool })
            tool.IsCheckedChanged += (s, _) => { if ((s as RadioButton)?.IsChecked == true) ToolChanged(); };
        Overlay.PointerPressed += Overlay_Down;
        Overlay.PointerMoved += Overlay_Move;
        Overlay.PointerReleased += Overlay_Up;
        Overlay.PointerExited += (_, _) => { if (dragging is not null && drawing is null && !moved) dragging = null; };
        KeyDown += View_KeyDown;
        DetachedFromVisualTree += (_, _) => { try { if (File.Exists(workPath)) File.Delete(workPath); } catch (IOException) { } catch (UnauthorizedAccessException) { } };
    }

    public int MarkCount => marks.Count;

    private void AddSwatch(string hex)
    {
        var border = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(3, 0, 3, 0), Cursor = new Cursor(StandardCursorType.Hand),
            Background = Ui.BrushOf(hex), BorderBrush = Ui.Brush("Line"), BorderThickness = new Thickness(hex == color ? 3 : 1),
        };
        ToolTip.SetTip(border, "Renk");
        border.PointerPressed += (_, e) => { e.Handled = true; SetColor(hex); };
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
        PenOptions.IsVisible = Current == Tool.Pen;
        TextOptions.IsVisible = Current is Tool.Text or Tool.Select;
        if (Current == Tool.Highlight && color == "#000000") SetColor("#F2C811");
        if (Current != Tool.Select) { EndEdit(); selected = null; }
        if (Current != Tool.Rewrite) EndRewrite(commit: false);
        if (Current != Tool.ParagraphEdit) EndParagraph(commit: false);
        Overlay.Cursor = new Cursor(Current == Tool.Select ? StandardCursorType.Arrow : Current is Tool.Text or Tool.Rewrite or Tool.ParagraphEdit ? StandardCursorType.Ibeam : StandardCursorType.Cross);
        if (Current == Tool.ParagraphEdit && file is not null) _ = LoadParagraphs(); else Redraw();
    }

    // ----- Yükleme ve görüntüleme -----

    public async void Load(string path)
    {
        if (!FileKinds.IsPdf(path)) { await shell.Info("Lütfen bir PDF dosyası seçin."); return; }
        try
        {
            pageCount = await Task.Run(() => PdfPages.PageCount(path));
            original = document = await File.ReadAllBytesAsync(path);
            workPath = System.IO.Path.Combine(Directory.CreateDirectory(System.IO.Path.Combine(App.DataFolder, "gecici")).FullName, Guid.NewGuid().ToString("N") + ".pdf");
            await File.WriteAllBytesAsync(workPath, document);
            var work = workPath;
            var first = await Task.Run(() => { using var raster = PdfRasterDocument.Open(work); return raster.RenderBitmap(0, RenderDpi); });
            Cache(0, first);
        }
        catch (Exception error) when (error is UserFacingException or IOException or UnauthorizedAccessException) { await shell.Error(error is UserFacingException ? error.Message : "Dosya okunamadı: " + error.Message); return; }
        file = path; page = 0; marks.Clear(); history.Clear(); selected = null; lines = [];
        DropHost.IsVisible = false;
        Toolbar.IsVisible = PageArea.IsVisible = Footer.IsVisible = true;
        await ShowPage(0);
        ZoomFit_Click(this, new RoutedEventArgs());
        Focus();
    }

    private void Cache(int index, SKBitmap skia)
    {
        if (pages.TryGetValue(index, out var old)) { old.Skia.Dispose(); }
        pages[index] = (skia, Pictures.ToBitmap(skia));
        sizes[index] = new Size(skia.Width * 72.0 / RenderDpi, skia.Height * 72.0 / RenderDpi);
        if (pages.Count > 12)
            foreach (var key in pages.Keys.Where(k => Math.Abs(k - index) > 5).ToList()) { pages[key].Skia.Dispose(); pages.Remove(key); }
    }

    private async Task ShowPage(int index)
    {
        if (file is null) return;
        page = Math.Clamp(index, 0, pageCount - 1);
        if (!pages.ContainsKey(page))
        {
            var path = workPath; var n = page;
            try
            {
                var skia = await Task.Run(() => { using var raster = PdfRasterDocument.Open(path); return raster.RenderBitmap(n, RenderDpi); });
                Cache(page, skia);
            }
            catch (UserFacingException error) { await shell.Error(error.Message); return; }
        }
        PageImage.Source = pages[page].Image;
        var size = sizes[page];
        PageImage.Width = Overlay.Width = size.Width; PageImage.Height = Overlay.Height = size.Height;
        overlayWidth = size.Width;
        PageLabel.Text = $"Sayfa {page + 1} / {pageCount}";
        EndEdit(); EndRewrite(commit: false); EndParagraph(commit: false); selected = null;
        await LoadLines();
        if (Current == Tool.ParagraphEdit) await LoadParagraphs(redraw: false);
        Redraw();
    }

    private void SetZoom(double value)
    {
        zoom = Math.Clamp(value, .25, 4);
        ZoomHost.LayoutTransform = new ScaleTransform(zoom, zoom);
        ZoomLabel.Text = $"{Math.Round(zoom * 100)}%";
        Redraw();
    }

    private void ZoomFit_Click(object? sender, RoutedEventArgs e)
    {
        if (!sizes.TryGetValue(page, out var size) || Scroller.Viewport.Width < 50) return;
        SetZoom((Scroller.Viewport.Width - 60) / size.Width);
    }
    private void ZoomIn_Click(object? sender, RoutedEventArgs e) => SetZoom(zoom * 1.2);
    private void ZoomOut_Click(object? sender, RoutedEventArgs e) => SetZoom(zoom / 1.2);
    private bool fitted;
    private void Scroller_SizeChanged(object? sender, SizeChangedEventArgs e) { if (!fitted && file is not null) { fitted = true; ZoomFit_Click(this, new RoutedEventArgs()); } }
    private async void PrevPage_Click(object? sender, RoutedEventArgs e) => await ShowPage(page - 1);
    private async void NextPage_Click(object? sender, RoutedEventArgs e) => await ShowPage(page + 1);

    // ----- Çizim -----

    private IEnumerable<Mark> OnPage => marks.Where(m => m.Page == page);
    private static AvaloniaList<double> Dashes(double a, double b) => [a, b];
    private static IBrush Argb(byte a, byte r, byte g, byte b) => new SolidColorBrush(Color.FromArgb(a, r, g, b));

    private void Redraw()
    {
        Overlay.Children.Clear();
        foreach (var mark in OnPage) Overlay.Children.Add(Element(mark));
        if (selected is not null && selected.Page == page && selected != drawing)
        {
            var outline = new Rectangle { Width = Math.Max(1, selected.Width), Height = Math.Max(1, selected.Height), Stroke = Ui.Brush("Primary"), StrokeThickness = 1.5 / zoom, StrokeDashArray = Dashes(4, 3), IsHitTestVisible = false };
            Canvas.SetLeft(outline, selected.X); Canvas.SetTop(outline, selected.Y); Overlay.Children.Add(outline);
            var grip = 10 / zoom;
            var handle = new Rectangle { Width = grip, Height = grip, Fill = Ui.Brush("Primary"), Stroke = Brushes.White, StrokeThickness = 1 / zoom, IsHitTestVisible = false };
            Canvas.SetLeft(handle, selected.X + selected.Width - grip / 2); Canvas.SetTop(handle, selected.Y + selected.Height - grip / 2); Overlay.Children.Add(handle);
        }
        if (Current == Tool.Rewrite)
            foreach (var line in lines)
            {
                var box = new Rectangle { Width = Math.Max(1, line.Width + 4), Height = Math.Max(1, line.Height + 4), Fill = line == hoveredLine ? Argb(50, 217, 72, 15) : Brushes.Transparent, Stroke = line == hoveredLine ? Ui.Brush("Primary") : Argb(90, 31, 95, 191), StrokeThickness = 1 / zoom, StrokeDashArray = Dashes(3, 2), IsHitTestVisible = false };
                Canvas.SetLeft(box, line.X - 2); Canvas.SetTop(box, line.Y - 2); Overlay.Children.Add(box);
            }
        if (Current == Tool.ParagraphEdit)
            foreach (var para in paragraphs)
            {
                var box = new Rectangle { Width = Math.Max(1, para.Width + 6), Height = Math.Max(1, para.Height + 6), Fill = para == hoveredParagraph ? Argb(40, 217, 72, 15) : Brushes.Transparent, Stroke = para == hoveredParagraph ? Ui.Brush("Primary") : Argb(110, 22, 128, 92), StrokeThickness = 1.2 / zoom, StrokeDashArray = Dashes(4, 2), IsHitTestVisible = false };
                Canvas.SetLeft(box, para.X - 3); Canvas.SetTop(box, para.Y - 3); Overlay.Children.Add(box);
            }
        if (paragraphBox is not null) Overlay.Children.Add(paragraphBox);
        if (rewriteBox is not null) Overlay.Children.Add(rewriteBox);
        if (editor is not null) Overlay.Children.Add(editor);
        DeleteButton.IsEnabled = selected is not null;
        UndoButton.IsEnabled = history.Count > 0;
        var changedText = !ReferenceEquals(document, original);
        Summary.Text = Current == Tool.ParagraphEdit ? (paragraphs.Count > 0 ? "Değiştirmek istediğiniz paragrafa tıklayın. Ctrl+Enter uygular." : "Bu sayfada paragraf bulunamadı.")
            : Current == Tool.Rewrite ? (lines.Count > 0 ? "Değiştirmek istediğiniz yazıya tıklayın." : "Bu sayfada değiştirilebilir yazı bulunamadı (taranmış veya döndürülmüş olabilir).")
            : marks.Count == 0 && !changedText ? "Araç seçip sayfaya tıklayın veya sürükleyin."
            : string.Join(" · ", new[] { marks.Count > 0 ? $"{marks.Count} ekleme" : "", changedText ? "yazı değiştirildi" : "" }.Where(t => t.Length > 0));
    }

    private Control Element(Mark mark)
    {
        Control element;
        switch (mark.Kind)
        {
            case MarkKind.Whiteout:
                element = new Rectangle { Width = mark.Width, Height = mark.Height, Fill = string.IsNullOrEmpty(mark.Color) || mark.Color == "#000000" ? Brushes.White : Ui.BrushOf(mark.Color), Stroke = Argb(120, 150, 150, 150), StrokeThickness = .7 / zoom, StrokeDashArray = Dashes(3, 3) };
                break;
            case MarkKind.Highlight:
                element = new Rectangle { Width = mark.Width, Height = mark.Height, Fill = Ui.BrushOf(mark.Color), Opacity = .35 };
                break;
            case MarkKind.Ink:
                element = new Polyline { Stroke = Ui.BrushOf(mark.Color), StrokeThickness = mark.StrokeWidth, StrokeJoin = PenLineJoin.Round, StrokeLineCap = PenLineCap.Round, Points = mark.Points.Select(q => new Point(q.X, q.Y)).ToList() };
                break;
            case MarkKind.Image:
                var image = new Image { Width = mark.Width, Height = mark.Height, Stretch = Stretch.Fill };
                if (mark.ImagePng is not null) image.Source = Decode(mark.ImagePng);
                element = image;
                break;
            default:
                var text = new TextBlock { Text = mark.Text, FontFamily = TextFont, FontSize = mark.FontSize, FontWeight = mark.Bold ? FontWeight.Bold : FontWeight.Normal, Foreground = Ui.BrushOf(mark.Color), TextWrapping = TextWrapping.Wrap, Width = Math.Max(10, mark.Width - 4), Margin = new Thickness(2) };
                element = new Border { Width = mark.Width, MinHeight = mark.Height, Child = text, Background = Brushes.Transparent, Opacity = editor is not null && editor.Tag == mark ? 0 : 1 };
                break;
        }
        element.IsHitTestVisible = false;
        Canvas.SetLeft(element, mark.X); Canvas.SetTop(element, mark.Y);
        return element;
    }

    private static readonly Dictionary<byte[], Bitmap> decoded = new(ReferenceEqualityComparer.Instance);
    private static Bitmap Decode(byte[] png) => decoded.TryGetValue(png, out var cached) ? cached : decoded[png] = Pictures.FromPng(png);

    // ----- İşaretçi -----

    private Mark? Hit(Point p) => OnPage.Reverse().FirstOrDefault(m => p.X >= m.X - 3 && p.X <= m.X + m.Width + 3 && p.Y >= m.Y - 3 && p.Y <= m.Y + m.Height + 3);
    private bool OnGrip(Mark m, Point p) => Math.Abs(p.X - (m.X + m.Width)) <= 8 / zoom && Math.Abs(p.Y - (m.Y + m.Height)) <= 8 / zoom;
    private static bool InTextBox(object? source) => source is Visual v && v.FindAncestorOfType<TextBox>(true) is not null;
    private static bool Contains(Mark m, Point p) => p.X >= m.X && p.X <= m.X + m.Width && p.Y >= m.Y && p.Y <= m.Y + m.Height;

    private void Overlay_Down(object? sender, PointerPressedEventArgs e)
    {
        if (file is null || !e.GetCurrentPoint(Overlay).Properties.IsLeftButtonPressed) return;
        if (InTextBox(e.Source)) return;
        Focus();
        var p = e.GetPosition(Overlay);
        if (editor is not null) { if (selected is not null && Contains(selected, p)) return; EndEdit(); }
        switch (Current)
        {
            case Tool.Rewrite:
                var line = lines.FirstOrDefault(l => p.X >= l.X - 2 && p.X <= l.X + l.Width + 2 && p.Y >= l.Y - 2 && p.Y <= l.Y + l.Height + 2);
                if (rewriteBox is not null) { EndRewrite(commit: true); if (line is null) return; }
                if (line is null) return;
                _ = BeginRewrite(line);
                break;
            case Tool.ParagraphEdit:
                var para = paragraphs.FirstOrDefault(q => p.X >= q.X - 3 && p.X <= q.X + q.Width + 3 && p.Y >= q.Y - 3 && p.Y <= q.Y + q.Height + 3);
                if (paragraphBox is not null) { EndParagraph(commit: true); if (para is null) return; }
                if (para is null) return;
                BeginParagraph(para);
                break;
            case Tool.Select:
                var hit = Hit(p);
                if (hit is null) { selected = null; Redraw(); return; }
                selected = hit;
                if (e.ClickCount == 2 && hit.Kind == MarkKind.Text) { Redraw(); BeginEdit(hit); return; }
                dragging = hit; dragOriginal = hit.Clone(); dragStart = p; resizing = OnGrip(hit, p); moved = false;
                e.Pointer.Capture(Overlay); Redraw();
                break;
            case Tool.Text:
                var size = ChosenFontSize();
                var text = new Mark { Kind = MarkKind.Text, Page = page, X = p.X, Y = p.Y - size * .8, Width = Math.Min(260, overlayWidth - p.X), Height = size * 1.6 + 4, FontSize = size, Bold = BoldBox.IsChecked == true, Color = color == "#F2C811" ? "#000000" : color };
                Remember(); marks.Add(text); selected = text; Redraw(); BeginEdit(text);
                SelectTool.IsChecked = true;
                break;
            case Tool.Whiteout or Tool.Highlight:
                drawing = new Mark { Kind = Current == Tool.Whiteout ? MarkKind.Whiteout : MarkKind.Highlight, Page = page, X = p.X, Y = p.Y, Color = Current == Tool.Whiteout ? "#FFFFFF" : color };
                dragStart = p; e.Pointer.Capture(Overlay);
                break;
            case Tool.Pen:
                drawing = new Mark { Kind = MarkKind.Ink, Page = page, X = 0, Y = 0, Color = color, StrokeWidth = PenWidth.Value };
                drawing.Points.Add((p.X, p.Y));
                e.Pointer.Capture(Overlay);
                Remember(); marks.Add(drawing); Redraw();
                break;
        }
    }

    private void Overlay_Move(object? sender, PointerEventArgs e)
    {
        var p = e.GetPosition(Overlay);
        if (Current == Tool.Rewrite)
        {
            var over = lines.FirstOrDefault(l => p.X >= l.X - 2 && p.X <= l.X + l.Width + 2 && p.Y >= l.Y - 2 && p.Y <= l.Y + l.Height + 2);
            if (over != hoveredLine) { hoveredLine = over; Redraw(); }
            return;
        }
        if (Current == Tool.ParagraphEdit)
        {
            var over = paragraphs.FirstOrDefault(q => p.X >= q.X - 3 && p.X <= q.X + q.Width + 3 && p.Y >= q.Y - 3 && p.Y <= q.Y + q.Height + 3);
            if (over != hoveredParagraph) { hoveredParagraph = over; Redraw(); }
            return;
        }
        if (Current == Tool.Select && dragging is null)
            Overlay.Cursor = new Cursor(selected is not null && OnGrip(selected, p) ? StandardCursorType.BottomRightCorner : Hit(p) is not null ? StandardCursorType.SizeAll : StandardCursorType.Arrow);
        if (!e.GetCurrentPoint(Overlay).Properties.IsLeftButtonPressed) return;
        if (drawing is { Kind: MarkKind.Ink })
        {
            drawing.Points.Add((p.X, p.Y));
            if (Overlay.Children.Count > 0 && Overlay.Children[^1] is Polyline line) line.Points = drawing.Points.Select(q => new Point(q.X, q.Y)).ToList(); else Redraw();
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

    private void Overlay_Up(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        if (drawing is not null)
        {
            if (drawing.Kind == MarkKind.Ink)
            {
                var xs = drawing.Points.Select(q => q.X).ToList(); var ys = drawing.Points.Select(q => q.Y).ToList();
                var minX = xs.Min() - drawing.StrokeWidth; var minY = ys.Min() - drawing.StrokeWidth;
                drawing.Points = drawing.Points.Select(q => (q.X - minX, q.Y - minY)).ToList();
                drawing.X = minX; drawing.Y = minY; drawing.Width = xs.Max() - minX + drawing.StrokeWidth; drawing.Height = ys.Max() - minY + drawing.StrokeWidth;
            }
            else if (drawing.Width >= 3 && drawing.Height >= 3)
            {
                if (drawing.Kind == MarkKind.Whiteout) drawing.Color = SampleBackground(drawing);
                Remember(); marks.Add(drawing); selected = drawing; if (drawing.Kind == MarkKind.Whiteout) SelectTool.IsChecked = true;
            }
            drawing = null; Redraw();
        }
        dragging = null; dragOriginal = null; moved = false; resizing = false;
    }

    // ----- Yazı düzenleme -----

    private double ChosenFontSize() => double.TryParse((FontSizeBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out var v) ? v : 14;

    private void BeginEdit(Mark mark)
    {
        EndEdit();
        editor = new TextBox
        {
            Tag = mark, Text = mark.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = TextFont, FontSize = mark.FontSize,
            FontWeight = mark.Bold ? FontWeight.Bold : FontWeight.Normal, Foreground = Ui.BrushOf(mark.Color), Width = mark.Width, MinHeight = mark.Height,
            Padding = new Thickness(0), Margin = new Thickness(0), BorderThickness = new Thickness(1 / zoom), BorderBrush = Ui.Brush("Primary"), Background = Argb(200, 255, 255, 255), CornerRadius = new CornerRadius(0),
        };
        var box = editor;
        box.TextChanged += (_, _) => { mark.Text = box.Text ?? ""; box.UpdateLayout(); mark.Height = Math.Max(mark.FontSize * 1.6 + 4, box.Bounds.Height); };
        box.LostFocus += (_, _) => { if (editor == box) EndEdit(); };
        box.AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Escape) { EndEdit(); e.Handled = true; } }, RoutingStrategies.Tunnel);
        Canvas.SetLeft(box, mark.X); Canvas.SetTop(box, mark.Y);
        Redraw();
        box.Focus(); box.CaretIndex = box.Text?.Length ?? 0;
    }

    private void EndEdit()
    {
        if (editor is null) return;
        var box = editor; editor = null;
        if (box.Tag is Mark mark)
        {
            mark.Text = box.Text ?? "";
            if (string.IsNullOrWhiteSpace(mark.Text)) { marks.Remove(mark); if (selected == mark) selected = null; }
        }
        Redraw();
        Focus();
    }

    private void FontSize_Changed(object? sender, SelectionChangedEventArgs e) => TextOption_Changed(sender, e);

    private void TextOption_Changed(object? sender, RoutedEventArgs e)
    {
        if (selected is { Kind: MarkKind.Text })
        {
            Remember(); selected.FontSize = ChosenFontSize(); selected.Bold = BoldBox.IsChecked == true;
            if (editor?.Tag == selected) { editor.FontSize = selected.FontSize; editor.FontWeight = selected.Bold ? FontWeight.Bold : FontWeight.Normal; }
            Redraw();
        }
    }

    // ----- Resim ve logolar -----

    private async void AddImage_Click(object? sender, RoutedEventArgs e)
    {
        if (file is null) return;
        var picked = await Pick.Files(this, Pick.Images, multiple: false);
        if (picked.Length == 0) return;
        try
        {
            using var bitmap = Images.Load(picked[0]);
            var png = Images.Png(bitmap);
            var size = sizes[page];
            var width = Math.Min(size.Width * .5, 200.0); var height = width * bitmap.Height / Math.Max(1, bitmap.Width);
            Remember();
            AddImage(png, (size.Width - width) / 2, (size.Height - height) / 2, width, height, page);
            SelectTool.IsChecked = true; Redraw();
        }
        catch (UserFacingException error) { await shell.Error(error.Message); }
    }

    private static readonly Lazy<byte[]> IstekLogo = new(() => Pictures.Asset("LogoIstek.png"));
    private static readonly Lazy<byte[]> IbLogo = new(() => Pictures.Asset("LogoIB.png"));
    private const double LogoHeight = 54, LogoMargin = 26;

    private void AddImage(byte[] png, double x, double y, double width, double height, int target)
    {
        var mark = new Mark { Kind = MarkKind.Image, Page = target, X = x, Y = y, Width = width, Height = height, ImagePng = png };
        marks.Add(mark); selected = mark;
    }

    private static (double Width, double Height) LogoSize(byte[] png) { var image = Decode(png); var h = LogoHeight; return (h * image.PixelSize.Width / Math.Max(1, image.PixelSize.Height), h); }

    private void AddIstek_Click(object? sender, RoutedEventArgs e) { if (file is null) return; Remember(); PlaceLogos(page, istek: true, ib: false); SelectTool.IsChecked = true; Redraw(); }
    private void AddIB_Click(object? sender, RoutedEventArgs e) { if (file is null) return; Remember(); PlaceLogos(page, istek: false, ib: true); SelectTool.IsChecked = true; Redraw(); }

    private async void AddBoth_Click(object? sender, RoutedEventArgs e)
    {
        if (file is null) return;
        Remember();
        for (var n = 0; n < pageCount; n++)
        {
            if (!sizes.ContainsKey(n))
            {
                var path = workPath; var index = n;
                sizes[n] = await Task.Run(() => { using var raster = PdfRasterDocument.Open(path); using var b = raster.RenderBitmap(index, 36); return new Size(b.Width * 2, b.Height * 2); });
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
        history.Push((marks.Select(m => original is not null && m == dragging ? original.Clone() : m.Clone()).ToList(), document));
        while (history.Count > 60) { var keep = history.Take(60).Reverse().ToList(); history.Clear(); foreach (var s in keep) history.Push(s); }
    }

    private void Undo_Click(object? sender, RoutedEventArgs e) => Undo();
    private void Undo()
    {
        EndEdit(); EndRewrite(commit: false); EndParagraph(commit: false);
        if (history.Count == 0) return;
        var (previousMarks, previousDocument) = history.Pop();
        marks.Clear(); marks.AddRange(previousMarks); selected = null;
        if (!ReferenceEquals(previousDocument, document)) { document = previousDocument; _ = ReloadPages(); return; }
        Redraw();
    }

    private void Delete_Click(object? sender, RoutedEventArgs e) => DeleteSelected();
    private void DeleteSelected()
    {
        if (selected is null) return;
        EndEdit();
        Remember(); marks.Remove(selected); selected = null; Redraw();
    }

    private async void View_KeyDown(object? sender, KeyEventArgs e)
    {
        if (file is null || InTextBox(e.Source)) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (e.Key == Key.Delete || e.Key == Key.Back) { DeleteSelected(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Z) { Undo(); e.Handled = true; }
        else if (e.Key == Key.Escape && selected is not null) { selected = null; Redraw(); e.Handled = true; }
        else if (e.Key == Key.PageDown) { await ShowPage(page + 1); e.Handled = true; }
        else if (e.Key == Key.PageUp) { await ShowPage(page - 1); e.Handled = true; }
        else if (e.Key == Key.F2 && selected is { Kind: MarkKind.Text }) { BeginEdit(selected); e.Handled = true; }
    }

    // ----- Kaydet -----

    private async void Save_Click(object? sender, RoutedEventArgs e) => await SaveAsync();

    internal async Task<IReadOnlyList<string>?> SaveAsync()
    {
        if (file is null) return null;
        EndEdit(); EndRewrite(commit: true); EndParagraph(commit: true);
        if (marks.Count == 0 && ReferenceEquals(document, original)) { await shell.Info("Henüz bir değişiklik yapmadınız."); return null; }
        var input = file; var list = marks.Select(m => m.Clone()).ToList(); var source = workPath;
        var destination = OutputFolder.For(input);
        var result = await shell.RunAsync("Düzenlenen PDF kaydediliyor", (_, progress, token) =>
            Task.Run<IReadOnlyList<string>>(() => [PdfAnnotator.SaveNextTo(source, destination, list, token, originalName: input)], token));
        if (result is not null) { history.Clear(); Redraw(); shell.ShowResult(result, "Düzenlediğiniz PDF yeni bir dosya olarak kaydedildi. Asıl PDF olduğu gibi duruyor."); }
        return result;
    }

    // ----- Yerinde yazı değiştirme (KillerPdf.Engine) -----

    private async Task LoadLines()
    {
        var bytes = document; var n = page;
        try { lines = await Task.Run(() => TextRewriter.Lines(bytes, n)); }
        catch (Exception error) when (error is not OperationCanceledException) { App.Log(error); lines = []; }
        hoveredLine = null;
    }

    private async Task BeginRewrite(TextLine line)
    {
        var blocker = TextRewriter.Blocker(document, page, line);
        if (blocker is not null) { await shell.Info(blocker); return; }
        var box = rewriteBox = new TextBox
        {
            Tag = line, Text = line.Text, AcceptsReturn = false, FontFamily = TextFont, FontSize = line.FontSize, FontWeight = line.Bold ? FontWeight.Bold : FontWeight.Normal,
            MinWidth = Math.Max(line.Width + 20, 60), MaxWidth = Math.Max(overlayWidth - line.X, 60), Padding = new Thickness(1, 0, 1, 0), Margin = new Thickness(0),
            BorderThickness = new Thickness(1.5 / zoom), BorderBrush = Ui.Brush("Primary"), Background = Brushes.White, CornerRadius = new CornerRadius(0),
        };
        ToolTip.SetTip(box, "Enter: uygula · Esc: vazgeç");
        Canvas.SetLeft(box, line.X - 1); Canvas.SetTop(box, line.Y - 1);
        box.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter) { EndRewrite(commit: true); e.Handled = true; }
            else if (e.Key == Key.Escape) { EndRewrite(commit: false); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        box.LostFocus += (_, _) => { if (rewriteBox == box) EndRewrite(commit: true); };
        Redraw();
        box.Focus(); box.SelectAll();
    }

    private void EndRewrite(bool commit)
    {
        if (rewriteBox is null) return;
        var box = rewriteBox; rewriteBox = null;
        var line = (TextLine)box.Tag!;
        var text = box.Text ?? "";
        if (commit && text != line.Text) _ = ApplyRewrite(line, text);
        else Redraw();
        Focus();
    }

    private async Task ApplyRewrite(TextLine line, string text)
    {
        if (string.IsNullOrEmpty(text)) text = " ";
        var before = document; var n = page;
        RewriteResult result;
        try { result = await Task.Run(() => TextRewriter.Replace(before, n, line, text)); }
        catch (UserFacingException error) { await shell.Error(error.Message); Redraw(); return; }
        Remember();
        document = result.Document;
        await ReloadPages();
        if (result.UsedSubstituteFont) Summary.Text = "Değiştirildi. Gömülü yazı tipinde bu harfler yoktu; satır bilgisayardaki aynı yazı tipiyle (yoksa Arial) yazıldı.";
    }

    private async Task ReloadPages()
    {
        try { await File.WriteAllBytesAsync(workPath, document); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { await shell.Error("Geçici dosya yazılamadı: " + error.Message); return; }
        foreach (var entry in pages.Values) entry.Skia.Dispose();
        pages.Clear();
        await ShowPage(page);
    }

    // ----- Kapatma rengi: kutunun kenarındaki sayfa renginden -----

    private string SampleBackground(Mark box)
    {
        if (!pages.TryGetValue(page, out var entry)) return "#FFFFFF";
        try
        {
            var bitmap = entry.Skia;
            var scale = RenderDpi / 72.0;
            var samples = new List<(byte R, byte G, byte B)>();
            void Take(double x, double y)
            {
                var px = (int)Math.Round(x * scale); var py = (int)Math.Round(y * scale);
                if (px < 0 || py < 0 || px >= bitmap.Width || py >= bitmap.Height) return;
                var c = bitmap.GetPixel(px, py);
                samples.Add((c.Red, c.Green, c.Blue));
            }
            // Kutunun hemen içinde, kenar boyunca 2 pt içeriden örnekler.
            const double inset = 2;
            for (var i = 0; i <= 10; i++)
            {
                var t = i / 10.0;
                Take(box.X + inset + t * (box.Width - 2 * inset), box.Y + inset);
                Take(box.X + inset + t * (box.Width - 2 * inset), box.Y + box.Height - inset);
                Take(box.X + inset, box.Y + inset + t * (box.Height - 2 * inset));
                Take(box.X + box.Width - inset, box.Y + inset + t * (box.Height - 2 * inset));
            }
            if (samples.Count == 0) return "#FFFFFF";
            byte Median(Func<(byte R, byte G, byte B), byte> pick) { var v = samples.Select(pick).OrderBy(x => x).ToList(); return v[v.Count / 2]; }
            var r = Median(s => s.R); var g = Median(s => s.G); var b = Median(s => s.B);
            if (r > 245 && g > 245 && b > 245) return "#FFFFFF";
            return $"#{r:X2}{g:X2}{b:X2}";
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or ObjectDisposedException) { return "#FFFFFF"; }
    }

    // ----- Paragraf düzenleme -----

    private async Task LoadParagraphs(bool redraw = true)
    {
        var bytes = document; var n = page; var height = sizes.TryGetValue(page, out var size) ? size.Height : 842;
        try { paragraphs = await Task.Run(() => TextRewriter.Paragraphs(bytes, n, height)); }
        catch (Exception error) when (error is not OperationCanceledException) { App.Log(error); paragraphs = []; }
        hoveredParagraph = null;
        if (redraw) Redraw();
    }

    private void BeginParagraph(Paragraph para)
    {
        var box = paragraphBox = new TextBox
        {
            Tag = para, Text = para.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = TextFont, FontSize = para.FontSize,
            Width = Math.Max(para.Width + 12, 80), MinHeight = para.Height + 8, Padding = new Thickness(2), Margin = new Thickness(0),
            BorderThickness = new Thickness(1.5 / zoom), BorderBrush = Ui.Brush("Success"), Background = Brushes.White, CornerRadius = new CornerRadius(0),
        };
        ToolTip.SetTip(box, "Ctrl+Enter: uygula · Esc: vazgeç");
        Canvas.SetLeft(box, para.X - 3); Canvas.SetTop(box, para.Y - 3);
        box.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))) { EndParagraph(commit: true); e.Handled = true; }
            else if (e.Key == Key.Escape) { EndParagraph(commit: false); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        Redraw();
        box.Focus(); box.CaretIndex = box.Text?.Length ?? 0;
    }

    private void EndParagraph(bool commit)
    {
        if (paragraphBox is null) return;
        var box = paragraphBox; paragraphBox = null;
        var para = (Paragraph)box.Tag!;
        var text = (box.Text ?? "").Replace("\r\n", "\n").Trim();
        if (commit && text != para.Text && text.Length > 0) _ = ApplyParagraph(para, text);
        else Redraw();
        Focus();
    }

    private async Task ApplyParagraph(Paragraph para, string text)
    {
        var before = document; var n = page;
        RewriteResult result;
        try { result = await Task.Run(() => TextRewriter.ReplaceParagraph(before, n, para, text)); }
        catch (UserFacingException error) { await shell.Error(error.Message); Redraw(); return; }
        Remember();
        document = result.Document;
        await ReloadPages();
        await LoadParagraphs();
        if (result.ShrunkToFontSize is { } shrunk) Summary.Text = $"Paragraf değiştirildi; sığması için yazı {shrunk:0.#} pt yapıldı.";
    }

    // ----- Bul ve değiştir -----

    private void FindReplace_Click(object? sender, RoutedEventArgs e)
    {
        if (file is null) return;
        FindPanel.IsVisible = !FindPanel.IsVisible;
        if (FindPanel.IsVisible) FindBox.Focus();
    }

    private async void ReplaceAll_Click(object? sender, RoutedEventArgs e) => await ReplaceAllAsync(FindBox.Text ?? "", ReplaceBox.Text ?? "", CaseBox.IsChecked == true);

    internal async Task<ReplaceAllResult?> ReplaceAllAsync(string find, string replace, bool caseSensitive)
    {
        if (file is null) return null;
        if (string.IsNullOrEmpty(find)) { await shell.Info("Aranacak yazıyı girin."); return null; }
        EndRewrite(commit: false); EndParagraph(commit: false);
        var before = document;
        ReplaceAllResult? result = null;
        var outcome = await shell.RunAsync("Belgede değiştiriliyor", (_, progress, token) => Task.Run<IReadOnlyList<string>>(() =>
        {
            result = TextRewriter.ReplaceAll(before, find, replace, caseSensitive, progress, token);
            return [];
        }, token));
        if (outcome is null || result is null) return null;
        if (result.ReplacedLines == 0) { await shell.Info(result.FailedLines > 0 ? $"“{find}” bulundu ama {result.FailedLines} satır yerinde değiştirilemedi (taranmış veya çözülemeyen yazı tipi)." : $"“{find}” belgede bulunamadı."); return result; }
        Remember();
        document = result.Document;
        await ReloadPages();
        Summary.Text = $"{result.ReplacedLines} satır, {result.PagesWithMatches} sayfada değiştirildi." + (result.FailedLines > 0 ? $" {result.FailedLines} satır değiştirilemedi." : "");
        return result;
    }

    // ----- İmza -----

    private static string SignaturePath => System.IO.Path.Combine(App.DataFolder, "imza.png");

    private async void AddSignature_Click(object? sender, RoutedEventArgs e)
    {
        if (file is null) return;
        if (!File.Exists(SignaturePath) && !await ChooseSignature(shell)) return;
        try
        {
            var png = File.ReadAllBytes(SignaturePath);
            var image = Decode(png);
            var size = sizes[page];
            var height = 50.0; var width = height * image.PixelSize.Width / Math.Max(1, image.PixelSize.Height);
            Remember();
            AddImage(png, size.Width - LogoMargin - width, size.Height - LogoMargin - height, width, height, page);
            SelectTool.IsChecked = true; Redraw();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        { await shell.Error("İmza resmi okunamadı: " + error.Message); }
    }

    /// İmza resmini seçtirir ve PNG olarak kaydeder. Ayarlar menüsünden de çağrılır.
    public static async Task<bool> ChooseSignature(MainWindow owner)
    {
        var picked = await Pick.Files(owner, Pick.Images, multiple: false);
        if (picked.Length == 0) return false;
        try
        {
            using var bitmap = Images.Load(picked[0]);
            Directory.CreateDirectory(App.DataFolder);
            File.WriteAllBytes(SignaturePath, Images.Png(bitmap));
            return true;
        }
        catch (Exception error) when (error is UserFacingException or IOException or UnauthorizedAccessException)
        { await owner.Error("İmza kaydedilemedi: " + error.Message); return false; }
    }

    // ----- Duman testi -----

    internal int ParagraphCount => paragraphs.Count;
    internal async Task SmokeParagraph(int index, string text)
    {
        ParagraphTool.IsChecked = true; await LoadParagraphs();
        if (paragraphs.Count <= index) throw new InvalidOperationException($"Paragraf bulunamadı (aday sayısı {paragraphs.Count}).");
        await ApplyParagraph(paragraphs[index], text);
    }
    internal int LineCount => lines.Count;
    internal bool DocumentChanged => !ReferenceEquals(document, original);
    internal async Task SmokeRewrite(int index, string text) { RewriteTool.IsChecked = true; await LoadLines(); await ApplyRewrite(lines[index], text); }

    internal void SmokeAdd()
    {
        marks.Add(new Mark { Kind = MarkKind.Whiteout, Page = page, X = 60, Y = 120, Width = 200, Height = 40 });
        marks.Add(new Mark { Kind = MarkKind.Text, Page = page, X = 64, Y = 124, Width = 220, Height = 30, Text = "Düzeltilmiş başlık ĞÜŞİÖÇ", FontSize = 16, Bold = true, Color = "#1F5FBF" });
        marks.Add(new Mark { Kind = MarkKind.Highlight, Page = page, X = 60, Y = 200, Width = 240, Height = 22, Color = "#F2C811" });
        marks.Add(new Mark { Kind = MarkKind.Ink, Page = page, X = 80, Y = 260, Width = 160, Height = 60, Color = "#D9480F", StrokeWidth = 3, Points = [(0, 50), (40, 0), (80, 50), (120, 0), (160, 50)] });
        PlaceLogos(page, istek: true, ib: true);
        selected = marks[1];
        Redraw();
    }
}
