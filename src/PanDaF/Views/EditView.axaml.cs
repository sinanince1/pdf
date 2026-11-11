using Avalonia.Collections;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using PanDaF.Core;
using SkiaSharp;

namespace PanDaF.Views;

/// PDF üzerine yazı, kapatma, vurgu, çizim, resim ve logo ekleme; var olan yazıyı yerinde değiştirme;
/// sayfanın kendi yazılarını ve resimlerini seçip taşıma. Asıl dosya değişmez; sonuç yeni bir PDF olarak kaydedilir.
public partial class EditView : UserControl
{
    private const int RenderDpi = 144;
    private static readonly FontFamily TextFont = new("Arial, Liberation Sans, Helvetica, sans-serif");
    private readonly MainWindow shell;
    private readonly List<Mark> marks = [];
    private readonly Stack<(List<Mark> Marks, byte[] Document)> history = new();
    private readonly Stack<(List<Mark> Marks, byte[] Document)> redo = new();
    private TextRewriter.DocumentSurvey? survey;      // belgedeki yazı tipleri ve yazı renkleri
    private bool picking;                             // damlalık: sıradaki tıklama rengi sayfadan alır
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
    private Point dragStart; private bool resizing, moved, pressed, banding;
    // Çoklu seçim: 'selected' dışındaki seçili eklemeler. Birlikte taşınır ve silinir.
    private readonly List<Mark> others = [];
    private readonly Dictionary<Mark, Mark> dragOriginals = [];
    // Sayfanın kendi öğeleri (satırlar ve resimler). Seçilenler taşınmaya başlayınca eklemeye dönüşür.
    private sealed record PageObject(Rect Bounds, TextLine? Line, bool Area = false);
    private sealed class LiftJob(int page) { public int Page => page; public List<(PageObject Object, Mark Cover, Mark? Mark)> Items { get; } = []; }
    private List<PageObject> objects = [];
    private readonly List<PageObject> picked = [];
    private PageObject? hoverObject;
    private Rect? band;
    private LiftJob? pendingLift;
    private bool busy, syncing;
    private List<string> pageFonts = [];
    private TextBox? editor;
    private string color = "#000000";
    private readonly Dictionary<string, Border> swatches = [];
    private double overlayWidth;

    private enum Tool { Select, Rewrite, ParagraphEdit, Text, Whiteout, Highlight, Pen }
    // Seçili araç, işaretlenen düğmenin kendisinden okunur. Düğmeler tek tek sorulursa, biri işaretlendiği anda
    // eskisi henüz işaretli göründüğü için yanlış araç bulunur (yeni yazı kutusu açılır açılmaz kapanıyordu).
    private Tool Current { get; set; } = Tool.Select;
    private Tool ToolOf(RadioButton? button) => button == RewriteTool ? Tool.Rewrite : button == ParagraphTool ? Tool.ParagraphEdit : button == TextTool ? Tool.Text : button == WhiteoutTool ? Tool.Whiteout : button == HighlightTool ? Tool.Highlight : button == PenTool ? Tool.Pen : Tool.Select;

    public EditView(MainWindow shell)
    {
        this.shell = shell;
        InitializeComponent();
        var drop = new DropZone("Düzenlemek istediğiniz PDF'yi buraya sürükleyin", "Var olan yazıyı değiştirebilir; üzerine yazı, kapatma, vurgu, çizim, resim ve okul logoları ekleyebilirsiniz.", Pick.Pdf, multiple: false);
        drop.FilesChosen += files => Load(files[0]);
        DropHost.Content = drop;
        DropHelper.Attach(this, files => { if (file is null) Load(files[0]); else _ = AddImageFiles(files.Where(FileKinds.IsImage).ToArray()); });
        BuildSwatches(); FillFonts();
        _ = FontCatalog.Preload().ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(FillFonts), TaskScheduler.Default);
        foreach (var tool in new[] { SelectTool, RewriteTool, ParagraphTool, TextTool, WhiteoutTool, HighlightTool, PenTool })
            tool.IsCheckedChanged += (s, _) => { if (s is RadioButton { IsChecked: true } button) { Current = ToolOf(button); ToolChanged(); } };
        // Eklenen yazılar sayfanın kendi yazısı gibi gri tonlu yumuşatmayla çizilir (renkli kenar olmadan).
        RenderOptions.SetTextRenderingMode(Overlay, TextRenderingMode.Antialias);
        RenderOptions.SetTextRenderingMode(EditorLayer, TextRenderingMode.Antialias);
        Overlay.PointerPressed += Overlay_Down;
        Overlay.PointerMoved += Overlay_Move;
        Overlay.PointerReleased += Overlay_Up;
        Overlay.PointerExited += (_, _) => { if (hoverObject is not null && !pressed) { hoverObject = null; Redraw(); } };
        KeyDown += View_KeyDown;
        DetachedFromVisualTree += (_, _) => { try { if (File.Exists(workPath)) File.Delete(workPath); } catch (IOException) { } catch (UnauthorizedAccessException) { } };
    }

    public int MarkCount => marks.Count;

    private static readonly string[] BaseColors = ["#000000", "#D9480F", "#1F5FBF", "#16805C", "#F2C811"];

    /// Renk kutuları: hazır renkler, belgedeki yazı renkleri (çerçevesi kesikli) ve gerekirse seçilen özel renk.
    private void BuildSwatches()
    {
        Swatches.Children.Clear(); swatches.Clear();
        foreach (var hex in BaseColors) AddSwatch(hex, "Renk");
        var fromDocument = (survey?.Colors ?? []).Where(c => !BaseColors.Contains(c, StringComparer.OrdinalIgnoreCase) && !IsNearWhite(c)).Take(6).ToList();
        if (fromDocument.Count > 0) Swatches.Children.Add(new Rectangle { Width = 1, Height = 18, Fill = Ui.Brush("Line"), Margin = new Thickness(5, 0, 5, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        foreach (var hex in fromDocument) AddSwatch(hex, "Bu belgedeki yazı rengi");
        if (!swatches.ContainsKey(color)) AddSwatch(color, "Seçtiğiniz renk");
        MarkSwatch();
    }

    private static bool IsNearWhite(string hex) { var c = PdfAnnotator.ParseColor(hex); return c.R > 240 && c.G > 240 && c.B > 240; }

    private void AddSwatch(string hex, string tip)
    {
        hex = hex.ToUpperInvariant();
        var border = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(3, 0, 3, 0), Cursor = new Cursor(StandardCursorType.Hand),
            Background = Ui.BrushOf(hex), BorderBrush = Ui.Brush("Line"), BorderThickness = new Thickness(1),
        };
        ToolTip.SetTip(border, tip);
        border.PointerPressed += (_, e) => { e.Handled = true; SetColor(hex); };
        swatches[hex] = border;
        Swatches.Children.Add(border);
    }

    private void MarkSwatch()
    {
        foreach (var (key, border) in swatches) { border.BorderThickness = new Thickness(key == color ? 3 : 1); border.BorderBrush = key == color ? Ui.Brush("Ink") : Ui.Brush("Line"); }
    }

    private void SetColor(string hex)
    {
        color = hex.ToUpperInvariant();
        if (swatches.ContainsKey(color)) MarkSwatch(); else BuildSwatches();
        // Seçili sayfa yazıları: belgede yerinde yeniden renklendirilir.
        if (picked.Any(o => o.Line is not null) && !busy) { _ = RecolorPicked(color); return; }
        var targets = Chosen.Where(m => m.Kind is MarkKind.Text or MarkKind.Ink or MarkKind.Highlight).ToList();
        if (targets.Count == 0) return;
        Remember();
        foreach (var mark in targets) mark.Color = color;
        if (editor?.Tag is Mark edited && targets.Contains(edited)) Dress(editor, Ui.BrushOf(color), Ui.Brush("Primary"), Argb(200, 255, 255, 255));
        Redraw();
    }

    /// Seç aracıyla seçilmiş sayfa satırlarını aynı yazıyla, yeni renkte yeniden yazar.
    private async Task RecolorPicked(string hex)
    {
        var targets = picked.Where(o => o.Line is not null).ToList();
        var n = page; var before = document;
        Remember();
        busy = true;
        try
        {
            var (current, done) = await Task.Run(() =>
            {
                var bytes = before; var count = 0;
                foreach (var item in targets)
                {
                    try { if (TextRewriter.Blocker(bytes, n, item.Line!) is not null) continue; bytes = TextRewriter.Replace(bytes, n, item.Line!, item.Line!.Text, fillColor: hex).Document; count++; }
                    catch (UserFacingException) { }
                }
                return (bytes, count);
            });
            if (done == 0) { if (history.Count > 0) history.Pop(); await shell.Info("Bu yazının rengi yerinde değiştirilemiyor. Yazıyı sürükleyip taşıdıktan sonra rengini değiştirebilirsiniz."); return; }
            document = current;
            await ReloadPages(keepSelection: true);
            // Aynı satırlar seçili kalsın: başka bir renk de denenebilsin.
            foreach (var item in objects.Where(o => o.Line is not null && targets.Any(t => t.Bounds.Contains(o.Bounds.Center)))) picked.Add(item);
        }
        finally { busy = false; Redraw(); }
    }

    // Özel renk: renk seçici açılır pencerede.
    private void CustomColor_Click(object? sender, RoutedEventArgs e)
    {
        var start = Color.TryParse(color, out var parsed) ? parsed : Colors.Black;
        var view = new ColorView { Color = start, IsAlphaEnabled = false, IsAlphaVisible = false, IsColorModelVisible = false, IsAccentColorsVisible = false, Width = 320 };
        var apply = new Button { Content = "Bu rengi kullan", Classes = { "primary" }, FontSize = 14, Padding = new Thickness(16, 7), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var flyout = new Flyout { Content = new StackPanel { Children = { view, apply } }, Placement = PlacementMode.BottomEdgeAlignedRight };
        apply.Click += (_, _) => { var c = view.Color; flyout.Hide(); SetColor($"#{c.R:X2}{c.G:X2}{c.B:X2}"); };
        flyout.ShowAt(ColorButton);
    }

    // Damlalık: sıradaki tıklama, tıklanan yazının (yoksa noktanın) rengini alır.
    private void PickColor_Changed(object? sender, RoutedEventArgs e)
    {
        picking = PickColorButton.IsChecked == true;
        if (Overlay is null) return;
        Overlay.Cursor = new Cursor(picking ? StandardCursorType.Cross : Current == Tool.Select ? StandardCursorType.Arrow : Current is Tool.Text or Tool.Rewrite or Tool.ParagraphEdit ? StandardCursorType.Ibeam : StandardCursorType.Cross);
        if (picking) Summary.Text = "Rengini almak istediğiniz yazıya veya yere tıklayın.";
        else Redraw();
    }

    private string ColorAt(Point p)
    {
        // Yazının üstüne tıklandıysa yazının kendi rengi (kenar yumuşatmasından etkilenmez).
        var line = lines.FirstOrDefault(l => p.X >= l.X - 1 && p.X <= l.X + l.Width + 1 && p.Y >= l.Y - 1 && p.Y <= l.Y + l.Height + 1);
        if (line is not null) return line.Color;
        if (!pages.TryGetValue(page, out var entry)) return color;
        var bitmap = entry.Skia; var scale = RenderDpi / 72.0;
        var cx = Math.Clamp((int)(p.X * scale), 0, bitmap.Width - 1); var cy = Math.Clamp((int)(p.Y * scale), 0, bitmap.Height - 1);
        static int Distance(SKColor a, SKColor b) => Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue);
        // Çevredeki en yaygın renk zemindir; tıklanan noktanın yakınında zeminden en farklı piksel (ince çizgi, harf) seçilir.
        var counts = new Dictionary<SKColor, int>();
        for (var y = Math.Max(0, cy - 12); y <= Math.Min(bitmap.Height - 1, cy + 12); y += 2)
            for (var x = Math.Max(0, cx - 12); x <= Math.Min(bitmap.Width - 1, cx + 12); x += 2) { var c = bitmap.GetPixel(x, y); counts[c] = counts.GetValueOrDefault(c) + 1; }
        var background = counts.MaxBy(c => c.Value).Key;
        var best = bitmap.GetPixel(cx, cy); var far = Distance(best, background);
        for (var y = Math.Max(0, cy - 3); y <= Math.Min(bitmap.Height - 1, cy + 3); y++)
            for (var x = Math.Max(0, cx - 3); x <= Math.Min(bitmap.Width - 1, cx + 3); x++) { var c = bitmap.GetPixel(x, y); var d = Distance(c, background); if (d > far) { far = d; best = c; } }
        return $"#{best.Red:X2}{best.Green:X2}{best.Blue:X2}";
    }

    private void ToolChanged()
    {
        if (PenOptions is null) return;
        PenOptions.IsVisible = Current == Tool.Pen;
        TextOptions.IsVisible = Current is Tool.Text or Tool.Select;
        if (Current != Tool.Select) { EndEdit(); Select(null); }
        hoverObject = null;
        if (Current == Tool.Highlight && color == "#000000") SetColor("#F2C811");
        if (Current != Tool.Rewrite) EndRewrite(commit: false);
        if (Current != Tool.ParagraphEdit) EndParagraph(commit: false);
        if (PickColorButton.IsChecked == true) PickColorButton.IsChecked = false;
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
        file = path; page = 0; marks.Clear(); history.Clear(); redo.Clear(); Select(null); lines = []; objects = []; pageFonts = []; firstFonts = true; survey = null;
        BuildSwatches();
        _ = LoadSurvey(document);
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

    private async Task ShowPage(int index, bool keepSelection = false)
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
        EndEdit(); EndRewrite(commit: false); EndParagraph(commit: false);
        if (keepSelection) { picked.Clear(); hoverObject = null; if (selected is not null && !marks.Contains(selected)) Select(null); others.RemoveAll(m => !marks.Contains(m)); }
        else Select(null);
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
    private async void PrevPage_Click(object? sender, RoutedEventArgs e) { if (!busy) await ShowPage(page - 1); }
    private async void NextPage_Click(object? sender, RoutedEventArgs e) { if (!busy) await ShowPage(page + 1); }

    // ----- Çizim -----

    private IEnumerable<Mark> OnPage => marks.Where(m => m.Page == page);
    private static AvaloniaList<double> Dashes(double a, double b) => [a, b];
    private static IBrush Argb(byte a, byte r, byte g, byte b) => new SolidColorBrush(Color.FromArgb(a, r, g, b));

    private void Redraw()
    {
        Overlay.Children.Clear();
        foreach (var mark in OnPage) Overlay.Children.Add(Element(mark));
        foreach (var mark in Chosen.Where(m => m.Page == page && m != drawing))
        {
            var outline = new Rectangle { Width = Math.Max(1, mark.Width), Height = Math.Max(1, mark.Height), Stroke = Ui.Brush("Primary"), StrokeThickness = 1.5 / zoom, StrokeDashArray = Dashes(4, 3), IsHitTestVisible = false };
            Canvas.SetLeft(outline, mark.X); Canvas.SetTop(outline, mark.Y); Overlay.Children.Add(outline);
        }
        if (selected is not null && selected.Page == page && selected != drawing && Single)
        {
            var grip = 10 / zoom;
            var handle = new Rectangle { Width = grip, Height = grip, Fill = Ui.Brush("Primary"), Stroke = Brushes.White, StrokeThickness = 1 / zoom, IsHitTestVisible = false };
            Canvas.SetLeft(handle, selected.X + selected.Width - grip / 2); Canvas.SetTop(handle, selected.Y + selected.Height - grip / 2); Overlay.Children.Add(handle);
        }
        if (Current == Tool.Select)
        {
            // Sayfanın kendi öğeleri: üzerine gelinen soluk, seçilen belirgin çerçeveyle gösterilir.
            if (hoverObject is not null && !picked.Contains(hoverObject)) Overlay.Children.Add(Frame(hoverObject.Bounds, Argb(150, 31, 95, 191), Argb(18, 31, 95, 191), 1));
            foreach (var item in picked) Overlay.Children.Add(Frame(item.Bounds, Ui.Brush("Primary"), Argb(28, 217, 72, 15), 1.5));
            if (band is { } area) Overlay.Children.Add(Frame(area, Ui.Brush("Primary"), Argb(22, 217, 72, 15), 1));
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
        DeleteButton.IsEnabled = selected is not null || picked.Count > 0;
        UndoButton.IsEnabled = history.Count > 0;
        RedoButton.IsEnabled = redo.Count > 0;
        var changedText = !ReferenceEquals(document, original);
        Summary.Text = Current == Tool.ParagraphEdit ? (paragraphs.Count > 0 ? "Değiştirmek istediğiniz paragrafa tıklayın. Ctrl+Enter uygular." : "Bu sayfada paragraf bulunamadı.")
            : Current == Tool.Rewrite ? (lines.Count > 0 ? "Değiştirmek istediğiniz yazıya tıklayın." : "Bu sayfada değiştirilebilir yazı bulunamadı (taranmış veya döndürülmüş olabilir).")
            : picked.Count > 0 ? (picked.Count == 1 ? "Seçildi: sürükleyerek taşıyın, Delete ile silin" + (picked[0].Line is not null ? ", çift tıklayarak düzenleyin." : ".") : $"{picked.Count} öğe seçildi: sürükleyerek birlikte taşıyın, Delete ile silin.")
            : Added == 0 && !changedText ? (Current == Tool.Select ? "Sayfadaki bir yazıyı veya resmi sürükleyerek taşıyın; birkaçını seçmek için boş yerden sürükleyin." : "Araç seçip sayfaya tıklayın veya sürükleyin.")
            : string.Join(" · ", new[] { Added > 0 ? $"{Added} ekleme" : "", changedText ? "yazı değiştirildi" : "" }.Where(t => t.Length > 0));
    }

    private int Added => marks.Count(m => !m.Cover);
    private bool Single => others.Count == 0 && picked.Count == 0;
    private IEnumerable<Mark> Chosen => selected is null ? others : others.Prepend(selected);

    private void Select(Mark? mark)
    {
        selected = mark; others.Clear(); picked.Clear();
        if (mark is { Kind: MarkKind.Text }) SyncTextOptions(mark);
    }

    private Rectangle Frame(Rect rect, IBrush stroke, IBrush fill, double thickness)
    {
        var frame = new Rectangle { Width = Math.Max(1, rect.Width), Height = Math.Max(1, rect.Height), Stroke = stroke, Fill = fill, StrokeThickness = thickness / zoom, StrokeDashArray = Dashes(4, 3), IsHitTestVisible = false };
        Canvas.SetLeft(frame, rect.X); Canvas.SetTop(frame, rect.Y);
        return frame;
    }

    private Control Element(Mark mark)
    {
        Control element;
        switch (mark.Kind)
        {
            case MarkKind.Whiteout:
                element = new Rectangle { Width = mark.Width, Height = mark.Height, Fill = string.IsNullOrEmpty(mark.Color) || mark.Color == "#000000" ? Brushes.White : Ui.BrushOf(mark.Color), Stroke = mark.Cover ? null : Argb(120, 150, 150, 150), StrokeThickness = .7 / zoom, StrokeDashArray = Dashes(3, 3) };
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
                var text = new TextBlock { Text = mark.Text, FontFamily = FontOf(mark.FontFamily), FontSize = mark.FontSize, FontWeight = mark.Bold ? FontWeight.Bold : FontWeight.Normal, FontStyle = mark.Italic ? FontStyle.Italic : FontStyle.Normal, Foreground = Ui.BrushOf(mark.Color), TextWrapping = TextWrapping.Wrap, Width = Math.Max(10, mark.Width - 4), Margin = new Thickness(2, 2 + BaselineShift(mark), 2, 2) };
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

    private Mark? Hit(Point p) => OnPage.Reverse().FirstOrDefault(m => !m.Cover && p.X >= m.X - 3 && p.X <= m.X + m.Width + 3 && p.Y >= m.Y - 3 && p.Y <= m.Y + m.Height + 3);

    /// İşaretçinin altındaki sayfa öğesi: önce satır, yoksa en küçük resim. Daha önce taşınmış (örtülmüş) öğeler sayılmaz.
    private PageObject? ObjectAt(Point p) =>
        objects.Where(o => o.Bounds.Contains(p) && !Covered(o)).OrderBy(o => o.Line is null ? 1 : 0).ThenBy(o => o.Bounds.Width * o.Bounds.Height).FirstOrDefault();
    private bool Covered(PageObject item) => OnPage.Any(m => m.Cover && item.Bounds.Center.X >= m.X && item.Bounds.Center.X <= m.X + m.Width && item.Bounds.Center.Y >= m.Y && item.Bounds.Center.Y <= m.Y + m.Height);
    private bool OnGrip(Mark m, Point p) => Math.Abs(p.X - (m.X + m.Width)) <= 8 / zoom && Math.Abs(p.Y - (m.Y + m.Height)) <= 8 / zoom;
    private static bool InTextBox(object? source) => source is Visual v && v.FindAncestorOfType<TextBox>(true) is not null;
    private static bool Contains(Mark m, Point p) => p.X >= m.X && p.X <= m.X + m.Width && p.Y >= m.Y && p.Y <= m.Y + m.Height;

    private void Overlay_Down(object? sender, PointerPressedEventArgs e)
    {
        if (file is null || busy || !e.GetCurrentPoint(Overlay).Properties.IsLeftButtonPressed) return;
        if (InTextBox(e.Source)) return;
        Focus();
        var p = e.GetPosition(Overlay);
        if (picking) { var taken = ColorAt(p); PickColorButton.IsChecked = false; SetColor(taken); return; }
        if (editor is not null) { if (selected is not null && Contains(selected, p)) return; EndEdit(); }
        switch (Current)
        {
            case Tool.Rewrite:
                var line = lines.FirstOrDefault(l => p.X >= l.X - 2 && p.X <= l.X + l.Width + 2 && p.Y >= l.Y - 2 && p.Y <= l.Y + l.Height + 2);
                if (rewriteBox is not null) { EndRewrite(commit: true); if (line is null) return; }
                if (line is null) return;
                _ = BeginRewrite(line, p.X);
                break;
            case Tool.ParagraphEdit:
                var para = paragraphs.FirstOrDefault(q => p.X >= q.X - 3 && p.X <= q.X + q.Width + 3 && p.Y >= q.Y - 3 && p.Y <= q.Y + q.Height + 3);
                if (paragraphBox is not null) { EndParagraph(commit: true); if (para is null) return; }
                if (para is null) return;
                BeginParagraph(para);
                break;
            case Tool.Select:
                var hit = Hit(p);
                var additive = e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
                dragStart = p; moved = false; resizing = false; pressed = false; banding = false; band = null; dragging = null; hoverObject = null;
                if (hit is not null)
                {
                    if (e.ClickCount == 2 && hit.Kind == MarkKind.Text) { Select(hit); Redraw(); BeginEdit(hit); return; }
                    if (additive) Toggle(hit); else if (!Chosen.Contains(hit)) Select(hit); else if (hit.Kind == MarkKind.Text && Single) SyncTextOptions(hit);
                    if (Chosen.Contains(hit)) { dragging = hit; resizing = Single && OnGrip(hit, p); pressed = true; }
                }
                else if (ObjectAt(p) is { } item)
                {
                    // Sayfadaki bir satıra çift tıklamak onu yerinde düzenlemeye açar.
                    if (e.ClickCount == 2 && item.Line is not null) { Select(null); Redraw(); _ = BeginRewrite(item.Line, p.X); return; }
                    if (additive) { if (!picked.Remove(item)) picked.Add(item); }
                    else if (!picked.Contains(item)) { Select(null); picked.Add(item); }
                    pressed = picked.Contains(item);
                }
                else { if (!additive) Select(null); banding = true; }
                e.Pointer.Capture(Overlay); Redraw();
                break;
            case Tool.Text:
                var size = ChosenFontSize();
                var text = new Mark { Kind = MarkKind.Text, Page = page, X = p.X, Y = p.Y - size * .8, Width = Math.Min(260, overlayWidth - p.X), Height = size * 1.6 + 4, FontSize = size, Bold = BoldBox.IsChecked == true, Color = color == "#F2C811" ? "#000000" : color, FontFamily = ChosenFont() };
                Remember(); marks.Add(text); selected = text; others.Clear(); picked.Clear(); Redraw(); BeginEdit(text);
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
        if (picking) return;
        if (Current == Tool.Select && !pressed && !banding && !busy)
        {
            var over = Hit(p);
            var item = over is null ? ObjectAt(p) : null;
            if (item != hoverObject) { hoverObject = item; Redraw(); }
            Overlay.Cursor = new Cursor(selected is not null && Single && OnGrip(selected, p) ? StandardCursorType.BottomRightCorner : over is not null || item is not null ? StandardCursorType.SizeAll : StandardCursorType.Arrow);
        }
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
        if (banding)
        {
            band = new Rect(Math.Min(dragStart.X, p.X), Math.Min(dragStart.Y, p.Y), Math.Abs(p.X - dragStart.X), Math.Abs(p.Y - dragStart.Y));
            Redraw();
            return;
        }
        if (!pressed) return;
        var dx = p.X - dragStart.X; var dy = p.Y - dragStart.Y;
        if (!moved)
        {
            // Sayfanın kendi öğesi yanlışlıkla kıpırdamasın diye taşıma biraz daha geç başlar.
            if (Math.Abs(dx) + Math.Abs(dy) <= (picked.Count > 0 ? 4 : 1) / zoom) return;
            moved = true;
            Remember();
            if (picked.Count > 0) LiftPicked(createMarks: true);
            dragOriginals.Clear();
            foreach (var mark in Chosen) dragOriginals[mark] = mark.Clone();
        }
        if (resizing && dragging is not null && dragOriginals.TryGetValue(dragging, out var dragOriginal))
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
        else foreach (var (mark, start) in dragOriginals) { mark.X = start.X + dx; mark.Y = start.Y + dy; }
        Redraw();
    }

    private void Toggle(Mark mark)
    {
        if (selected == mark) { selected = others.FirstOrDefault(); if (selected is not null) others.Remove(selected); }
        else if (!others.Remove(mark)) { if (selected is null) selected = mark; else others.Add(mark); }
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
                Remember(); marks.Add(drawing); selected = drawing; others.Clear(); picked.Clear(); if (drawing.Kind == MarkKind.Whiteout) SelectTool.IsChecked = true;
            }
            drawing = null; Redraw();
        }
        if (banding && band is { } area && area.Width >= 4 && area.Height >= 4) SelectIn(area);
        var hadPress = pressed || banding;
        dragging = null; dragOriginals.Clear(); moved = false; resizing = false; pressed = false; banding = false; band = null;
        if (pendingLift is { } job) { pendingLift = null; _ = FinishLift(job); }
        else if (hadPress) Redraw();
    }

    /// Sürüklenen alanın içindeki eklemeleri ve sayfa öğelerini seçer. İçinde öğe yoksa ama alan boş da değilse
    /// (ör. taranmış sayfa) alanın kendisi tek parça olarak seçilir.
    private void SelectIn(Rect area)
    {
        foreach (var mark in OnPage.Where(m => !m.Cover && area.Contains(new Point(m.X + m.Width / 2, m.Y + m.Height / 2)) && !Chosen.Contains(m)).ToList())
            if (selected is null) selected = mark; else others.Add(mark);
        foreach (var item in objects.Where(o => area.Contains(o.Bounds.Center) && !Covered(o) && !picked.Contains(o))) picked.Add(item);
        if (selected is not null || picked.Count > 0 || area.Width < 12 || area.Height < 12 || !sizes.TryGetValue(page, out var size)) return;
        var clipped = area.Intersect(new Rect(0, 0, size.Width, size.Height));
        if (clipped.Width >= 12 && clipped.Height >= 12 && !IsBlank(clipped)) picked.Add(new PageObject(clipped, null, Area: true));
    }

    // ----- Sayfanın kendi öğelerini taşıma -----

    /// Seçili sayfa öğelerini eklemeye çevirir: satır aynı yazı tipi, boyut ve renkle yazı kutusu; resim ve alan
    /// görüntü olur. Asıl yerleri örtülür; satırlar işlem bitince belgeden gerçekten çıkarılır (FinishLift).
    private void LiftPicked(bool createMarks)
    {
        var job = new LiftJob(page);
        var missingFont = false;
        foreach (var item in picked)
        {
            // Resmin kenar yumuşatması sınırın biraz dışına taşar; örtü o payı da kapatır.
            var bounds = item.Bounds.Inflate(item.Line is null ? 2.5 : 0);
            var cover = new Mark { Kind = MarkKind.Whiteout, Cover = true, Page = page, X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height };
            cover.Color = SampleBackground(cover, outside: true);
            // Örtüler en altta durur: yalnızca sayfanın kendi içeriğini kapatır, eklenenlerin üstüne çıkmaz.
            marks.Insert(marks.FindLastIndex(m => m.Cover) + 1, cover);
            Mark? mark = null;
            if (createMarks)
            {
                if (item.Line is { } line) { mark = TextMarkOf(line); missingFont |= mark.FontFamily.Length == 0 && !IsArial(line.FontName); }
                else mark = new Mark { Kind = MarkKind.Image, Page = page, X = item.Bounds.X, Y = item.Bounds.Y, Width = item.Bounds.Width, Height = item.Bounds.Height, ImagePng = pages.TryGetValue(page, out var entry) ? Crop(entry.Skia, item.Bounds, RenderDpi) : null };
                marks.Add(mark);
                if (selected is null) selected = mark; else others.Add(mark);
            }
            job.Items.Add((item, cover, mark));
        }
        // Öğeler listede kalır; örtülü olduklarından yeniden seçilemezler, geri alınınca yeniden seçilebilirler.
        picked.Clear(); hoverObject = null;
        pendingLift = job;
        if (missingFont) notice = "Bu yazının yazı tipi bilgisayarda kurulu değil; taşınan yazı Arial ile yazıldı.";
    }

    private string? notice;
    private static bool IsArial(string pdfFontName) => TextRewriter.Parse(pdfFontName, false).Family.Equals("Arial", StringComparison.OrdinalIgnoreCase);

    private Mark TextMarkOf(TextLine line)
    {
        var family = TextRewriter.SystemFamily(line.FontName) ?? "";
        if (family.Equals("Arial", StringComparison.OrdinalIgnoreCase)) family = "";
        var (ascent, lineHeight) = PdfAnnotator.TextMetrics(family, line.FontSize, line.Bold, line.Italic);
        var width = Math.Max(line.Width, TextRewriter.MeasureWidth(line.Text, family, line.FontSize, line.Bold, line.Italic)) + 12;
        return new Mark
        {
            Kind = MarkKind.Text, Page = page, X = line.X - 2, Y = line.Baseline - ascent - 2, Width = width, Height = lineHeight + 4,
            Text = line.Text, FontSize = line.FontSize, Bold = line.Bold, Italic = line.Italic, Color = line.Color, FontFamily = family,
        };
    }

    /// Taşıma bittiğinde: resimleri baskı kalitesinde yeniden keser, satırları belgeden çıkarır ve sayfayı yeniden çizer.
    private async Task FinishLift(LiftJob job)
    {
        busy = true;
        try
        {
            var n = job.Page; var before = document; var path = workPath;
            var rasters = job.Items.Where(i => i.Mark is { Kind: MarkKind.Image }).ToList();
            if (rasters.Count > 0)
            {
                var crops = await Task.Run(() =>
                {
                    using var raster = PdfRasterDocument.Open(path);
                    using var bitmap = raster.RenderBitmap(n, 300);
                    return rasters.Select(r => Crop(bitmap, r.Object.Bounds, 300)).ToList();
                });
                for (var i = 0; i < rasters.Count; i++) if (crops[i] is not null) rasters[i].Mark!.ImagePng = crops[i];
            }
            var texts = job.Items.Where(i => i.Object.Line is not null).ToList();
            if (texts.Count > 0)
            {
                var (current, done) = await Task.Run(() =>
                {
                    var bytes = before; var ok = new List<int>();
                    for (var i = 0; i < texts.Count; i++)
                    {
                        var line = texts[i].Object.Line!;
                        try { if (TextRewriter.Blocker(bytes, n, line) is not null) continue; bytes = TextRewriter.Replace(bytes, n, line, " ").Document; ok.Add(i); }
                        catch (UserFacingException) { }
                    }
                    return (bytes, ok);
                });
                // Belgeden çıkarılan satırın örtüsüne gerek kalmaz; çıkarılamayanın örtüsü kalır.
                foreach (var i in done) marks.Remove(texts[i].Cover);
                if (!ReferenceEquals(current, before)) { document = current; await ReloadPages(keepSelection: true); }
            }
        }
        catch (Exception error) when (error is UserFacingException or IOException or UnauthorizedAccessException) { await shell.Error(error is UserFacingException ? error.Message : "Sayfa okunamadı: " + error.Message); }
        finally
        {
            busy = false; Redraw();
            if (notice is not null) { Summary.Text = notice; notice = null; }
        }
    }

    /// Sayfa görüntüsünden bir dikdörtgeni keser (nokta cinsinden). Küçük parçalar PNG, büyükler JPEG olur.
    private static byte[]? Crop(SKBitmap bitmap, Rect rect, double dpi)
    {
        var scale = dpi / 72.0;
        var left = Math.Clamp((int)Math.Floor(rect.X * scale), 0, bitmap.Width - 1); var top = Math.Clamp((int)Math.Floor(rect.Y * scale), 0, bitmap.Height - 1);
        var right = Math.Clamp((int)Math.Ceiling(rect.Right * scale), left + 1, bitmap.Width); var bottom = Math.Clamp((int)Math.Ceiling(rect.Bottom * scale), top + 1, bitmap.Height);
        using var part = new SKBitmap(new SKImageInfo(right - left, bottom - top, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (!bitmap.ExtractSubset(part, new SKRectI(left, top, right, bottom))) return null;
        using var copy = part.Copy();
        return copy.Width * copy.Height > 500_000 ? Images.Jpeg(copy, 92) : Images.Png(copy);
    }

    /// Alan, sayfa zemininden başka bir şey içermiyor mu? (Boş yere çizilen seçim alanı bir şey seçmesin.)
    private bool IsBlank(Rect rect)
    {
        if (!pages.TryGetValue(page, out var entry)) return true;
        var bitmap = entry.Skia; var scale = RenderDpi / 72.0;
        var left = Math.Max(0, (int)(rect.X * scale)); var top = Math.Max(0, (int)(rect.Y * scale));
        var right = Math.Min(bitmap.Width, (int)(rect.Right * scale)); var bottom = Math.Min(bitmap.Height, (int)(rect.Bottom * scale));
        if (right - left < 2 || bottom - top < 2) return true;
        var first = bitmap.GetPixel(left, top); var different = 0; var total = 0;
        for (var y = top; y < bottom; y += 2)
            for (var x = left; x < right; x += 2)
            {
                var c = bitmap.GetPixel(x, y); total++;
                if (Math.Abs(c.Red - first.Red) + Math.Abs(c.Green - first.Green) + Math.Abs(c.Blue - first.Blue) > 40) different++;
            }
        return different < Math.Max(3, total / 500);
    }

    // ----- Yazı düzenleme -----

    private double ChosenFontSize() => double.TryParse((FontSizeBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out var v) ? v : 14;
    private string ChosenFont() => (FontBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private static readonly Dictionary<string, FontFamily> fontFamilies = new(StringComparer.OrdinalIgnoreCase);
    /// Ekrandaki yazı tipi: ad boşsa Arial; ad bilgisayarda yoksa sıradaki yedeklere düşer.
    private static FontFamily FontOf(string family) =>
        string.IsNullOrWhiteSpace(family) ? TextFont : fontFamilies.TryGetValue(family, out var found) ? found : fontFamilies[family] = new FontFamily(family + ", Arial, Liberation Sans, Helvetica, sans-serif");

    private static readonly Dictionary<(string, double, bool, bool), double> shifts = [];
    /// Ekrandaki yazının taban çizgisini kaydedilen PDF'dekiyle aynı yere getiren dikey kaydırma (nokta).
    private static double BaselineShift(Mark mark)
    {
        var key = (mark.FontFamily, mark.FontSize, mark.Bold, mark.Italic);
        if (shifts.TryGetValue(key, out var shift)) return shift;
        try
        {
            var typeface = new Typeface(FontOf(mark.FontFamily), mark.Italic ? FontStyle.Italic : FontStyle.Normal, mark.Bold ? FontWeight.Bold : FontWeight.Normal);
            var metrics = typeface.GlyphTypeface.Metrics;
            var screen = -metrics.Ascent * mark.FontSize / Math.Max(1, (double)metrics.DesignEmHeight);
            shift = Math.Clamp(PdfAnnotator.TextMetrics(mark.FontFamily, mark.FontSize, mark.Bold, mark.Italic).Ascent - screen, -mark.FontSize, mark.FontSize);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or NotSupportedException) { shift = 0; }
        return shifts[key] = shift;
    }

    /// Düzenleme kutusunun renklerini sabitler: üzerine gelince, odaklanınca veya metin seçilince tema renkleri
    /// (beyaz seçili yazı, mavi kalın çerçeve) devreye girmez; yazı her durumda kendi renginde kalır.
    private void Dress(TextBox box, IBrush foreground, IBrush border, IBrush background)
    {
        box.Foreground = foreground; box.CaretBrush = foreground; box.SelectionForegroundBrush = foreground; box.SelectionBrush = Argb(70, 31, 95, 191);
        box.Background = background; box.BorderBrush = border;
        foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" }) box.Resources[key] = foreground;
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused" }) box.Resources[key] = background;
        foreach (var key in new[] { "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" }) box.Resources[key] = border;
        box.Resources["TextControlBorderThemeThicknessFocused"] = box.BorderThickness;
    }

    /// Düzenleme kutuları ayrı bir katmanda durur: sayfa yeniden çizilirken yerinden çıkarılmaz, odağını ve seçimini
    /// kaybetmez. Kutu, yeri belli olduktan sonra odaklanır; böylece görünüm sayfanın başına sıçramaz.
    private void ShowEditor(TextBox box)
    {
        EditorLayer.Children.Add(box);
        EditorLayer.UpdateLayout();
        var offset = Scroller.Offset;
        box.Focus();
        Scroller.Offset = offset;
    }

    private void BeginEdit(Mark mark)
    {
        EndEdit();
        var shift = BaselineShift(mark); var line = 1 / zoom;
        editor = new TextBox
        {
            Tag = mark, Text = mark.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = FontOf(mark.FontFamily), FontSize = mark.FontSize,
            FontWeight = mark.Bold ? FontWeight.Bold : FontWeight.Normal, FontStyle = mark.Italic ? FontStyle.Italic : FontStyle.Normal, Width = mark.Width, MinHeight = mark.Height,
            Padding = new Thickness(Math.Max(0, 2 - line), Math.Max(0, 2 + shift - line), Math.Max(0, 2 - line), 0), Margin = new Thickness(0), BorderThickness = new Thickness(line), CornerRadius = new CornerRadius(0),
        };
        var box = editor;
        Dress(box, Ui.BrushOf(mark.Color), Ui.Brush("Primary"), Argb(200, 255, 255, 255));
        box.TextChanged += (_, _) => { mark.Text = box.Text ?? ""; box.UpdateLayout(); mark.Height = Math.Max(mark.FontSize * 1.6 + 4, box.Bounds.Height); if (editor == box) Redraw(); };
        box.LostFocus += (_, _) => { if (editor == box) EndEdit(); };
        box.AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Escape) { EndEdit(); e.Handled = true; } }, RoutingStrategies.Tunnel);
        Canvas.SetLeft(box, mark.X); Canvas.SetTop(box, mark.Y);
        Redraw();
        ShowEditor(box);
        box.CaretIndex = box.Text?.Length ?? 0;
    }

    private void EndEdit()
    {
        if (editor is null) return;
        var box = editor; editor = null;
        EditorLayer.Children.Remove(box);
        if (box.Tag is Mark mark)
        {
            mark.Text = box.Text ?? "";
            if (string.IsNullOrWhiteSpace(mark.Text)) { marks.Remove(mark); if (selected == mark) selected = null; others.Remove(mark); }
        }
        Redraw();
        Focus();
    }

    // Seçenek kutuları yalnızca değişen özelliği uygular; seçili yazıların öbür özelliklerine dokunmaz.
    private void FontSize_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (FontSizeBox?.SelectedItem is null) return;
        var size = ChosenFontSize();
        ApplyToTexts(mark => mark.FontSize = size, box => box.FontSize = size);
    }

    private void TextOption_Changed(object? sender, RoutedEventArgs e)
    {
        var bold = BoldBox?.IsChecked == true;
        ApplyToTexts(mark => mark.Bold = bold, box => box.FontWeight = bold ? FontWeight.Bold : FontWeight.Normal);
    }

    private void Font_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (FontBox?.SelectedItem is null) return;
        var family = ChosenFont();
        ApplyToTexts(mark => mark.FontFamily = family, box => box.FontFamily = FontOf(family));
    }

    private void ApplyToTexts(Action<Mark> change, Action<TextBox> changeEditor)
    {
        if (syncing) return;
        var targets = Chosen.Where(m => m.Kind == MarkKind.Text).ToList();
        if (targets.Count == 0) return;
        Remember();
        foreach (var mark in targets) change(mark);
        if (editor?.Tag is Mark edited && targets.Contains(edited)) changeEditor(editor);
        Redraw();
    }

    /// Seçilen yazının boyutunu, kalınlığını ve yazı tipini seçenek kutularına yansıtır.
    private void SyncTextOptions(Mark mark)
    {
        if (FontSizeBox is null || FontBox is null) return;
        syncing = true;
        try
        {
            FontSizeBox.SelectedItem = FontSizeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => double.TryParse(i.Content?.ToString(), out var v) && Math.Abs(v - mark.FontSize) < .01);
            BoldBox.IsChecked = mark.Bold;
            var item = FontBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is string tag && string.Equals(tag, mark.FontFamily, StringComparison.OrdinalIgnoreCase));
            if (item is null) FontBox.Items.Add(item = FontItem(mark.FontFamily));
            FontBox.SelectedItem = item;
        }
        finally { syncing = false; }
    }

    private static ComboBoxItem FontItem(string family, string? note = null) => new() { Content = (family.Length == 0 ? "Arial" : family) + (note is null ? "" : "  (" + note + ")"), Tag = family, FontFamily = FontOf(family), FontSize = 14 };

    private async Task LoadSurvey(byte[] bytes)
    {
        TextRewriter.DocumentSurvey result;
        try { result = await Task.Run(() => { _ = FontCatalog.Families; return TextRewriter.Survey(bytes); }); }
        catch (Exception error) when (error is not OperationCanceledException) { App.Log(error); return; }
        if (!ReferenceEquals(bytes, original)) return; // bu arada başka belge açıldı
        survey = result;
        BuildSwatches(); FillFonts();
    }

    private bool firstFonts = true;
    /// Yazı tipi kutusu: en üstte bu belgede kullanılanlar ("bu belgede var"), altında bilgisayardaki tüm yazı tipleri.
    /// Belge açılınca belgenin asıl yazı tipi seçili gelir; kullanıcı başka birini seçtiyse seçimi korunur.
    private void FillFonts()
    {
        if (FontBox is null) return;
        syncing = true;
        try
        {
            static string Key(string family) => family.Equals("Arial", StringComparison.OrdinalIgnoreCase) ? "" : family;
            var inDocument = survey?.Fonts ?? [];
            var installed = inDocument.Where(f => f.Installed).Select(f => Key(f.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var wanted = firstFonts ? installed.FirstOrDefault() ?? "" : ChosenFont();
            if (survey is not null) firstFonts = false;
            FontBox.Items.Clear();
            foreach (var family in installed) FontBox.Items.Add(FontItem(family, "bu belgede var"));
            foreach (var font in inDocument.Where(f => !f.Installed))
                FontBox.Items.Add(new ComboBoxItem { Content = font.Name + "  (bu belgede var; bilgisayarda kurulu değil)", IsEnabled = false, FontSize = 13 });
            var rest = (FontCatalog.Ready ? FontCatalog.Families : []).Select(Key).Append("").Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(f => !installed.Contains(f, StringComparer.OrdinalIgnoreCase)).OrderBy(f => f.Length == 0 ? "Arial" : f, StringComparer.CurrentCultureIgnoreCase);
            foreach (var family in rest) FontBox.Items.Add(FontItem(family));
            var item = FontBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is string tag && string.Equals(tag, wanted, StringComparison.OrdinalIgnoreCase));
            if (item is null) FontBox.Items.Add(item = FontItem(wanted));
            FontBox.SelectedItem = item;
        }
        finally { syncing = false; }
    }

    // ----- Resim ve logolar -----

    private async void AddImage_Click(object? sender, RoutedEventArgs e)
    {
        if (file is null) return;
        await AddImageFiles(await Pick.Files(this, Pick.Images, multiple: true));
    }

    /// Bilgisayardan seçilen (veya sayfaya sürüklenip bırakılan) resimleri sayfanın ortasına ekler.
    private async Task AddImageFiles(string[] paths)
    {
        if (file is null || busy || paths.Length == 0) return;
        EndEdit();
        var remembered = false; var index = 0;
        foreach (var path in paths)
        {
            try
            {
                byte[] png; int pixelWidth, pixelHeight;
                (png, pixelWidth, pixelHeight) = await Task.Run(() => { using var bitmap = Images.Load(path); return (Images.Png(bitmap), bitmap.Width, bitmap.Height); });
                var size = sizes[page];
                var width = Math.Min(size.Width * .5, 220.0); var height = width * pixelHeight / Math.Max(1, pixelWidth);
                if (height > size.Height * .6) { height = size.Height * .6; width = height * pixelWidth / Math.Max(1, pixelHeight); }
                if (!remembered) { Remember(); remembered = true; Select(null); }
                var shift = 14.0 * index++;
                var mark = new Mark { Kind = MarkKind.Image, Page = page, X = (size.Width - width) / 2 + shift, Y = (size.Height - height) / 2 + shift, Width = width, Height = height, ImagePng = png };
                marks.Add(mark);
                if (selected is null) selected = mark; else others.Add(mark);
            }
            catch (Exception error) when (error is UserFacingException or IOException or UnauthorizedAccessException) { await shell.Error(error is UserFacingException ? error.Message : "Resim okunamadı: " + error.Message); }
        }
        if (remembered) { SelectTool.IsChecked = true; Redraw(); Summary.Text = "Resim eklendi: sürükleyerek taşıyın, köşesinden boyutlandırın."; }
    }

    private static readonly Lazy<byte[]> IstekLogo = new(() => Pictures.Asset("LogoIstek.png"));
    private static readonly Lazy<byte[]> IbLogo = new(() => Pictures.Asset("LogoIB.png"));
    private const double LogoHeight = 54, LogoMargin = 26;

    private void AddImage(byte[] png, double x, double y, double width, double height, int target)
    {
        var mark = new Mark { Kind = MarkKind.Image, Page = target, X = x, Y = y, Width = width, Height = height, ImagePng = png };
        marks.Add(mark); selected = mark; others.Clear(); picked.Clear();
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
        Select(null); SelectTool.IsChecked = true; Redraw();
    }

    internal void PlaceLogos(int target, bool istek, bool ib)
    {
        var size = sizes[target];
        if (istek) { var (w, h) = LogoSize(IstekLogo.Value); AddImage(IstekLogo.Value, LogoMargin, LogoMargin * .8, w, h, target); }
        if (ib) { var (w, h) = LogoSize(IbLogo.Value); AddImage(IbLogo.Value, size.Width - LogoMargin - w, LogoMargin * .8, w, h, target); }
    }

    // ----- Geri al, sil, klavye -----

    /// Geçmişe şu anki durumu yazar (eklemeler ve belge).
    private void Remember()
    {
        history.Push((marks.Select(m => m.Clone()).ToList(), document));
        redo.Clear(); // yeni bir işlem yapıldı: ileri alınacak bir şey kalmaz
        while (history.Count > 60) { var keep = history.Take(60).Reverse().ToList(); history.Clear(); foreach (var s in keep) history.Push(s); }
    }

    private void Undo_Click(object? sender, RoutedEventArgs e) => Undo();
    private void Undo()
    {
        if (busy) return;
        EndEdit(); EndRewrite(commit: false); EndParagraph(commit: false);
        if (history.Count == 0) return;
        redo.Push((marks.Select(m => m.Clone()).ToList(), document));
        Restore(history.Pop());
    }

    private void Redo_Click(object? sender, RoutedEventArgs e) => Redo();
    /// Geri alınan son işlemi yeniden uygular.
    private void Redo()
    {
        if (busy) return;
        EndEdit(); EndRewrite(commit: false); EndParagraph(commit: false);
        if (redo.Count == 0) return;
        history.Push((marks.Select(m => m.Clone()).ToList(), document));
        Restore(redo.Pop());
    }

    private void Restore((List<Mark> Marks, byte[] Document) state)
    {
        marks.Clear(); marks.AddRange(state.Marks); Select(null);
        if (!ReferenceEquals(state.Document, document)) { document = state.Document; _ = ReloadPages(); return; }
        Redraw();
    }

    private void Delete_Click(object? sender, RoutedEventArgs e) => DeleteSelected();
    private void DeleteSelected()
    {
        if (busy || (selected is null && picked.Count == 0)) return;
        EndEdit();
        Remember();
        foreach (var mark in Chosen.ToList()) marks.Remove(mark);
        selected = null; others.Clear();
        // Sayfanın kendi öğeleri: yeri örtülür, satırlar belgeden çıkarılır.
        if (picked.Count > 0) { LiftPicked(createMarks: false); var job = pendingLift!; pendingLift = null; _ = FinishLift(job); return; }
        Redraw();
    }

    private void Nudge(double dx, double dy)
    {
        var targets = Chosen.ToList();
        if (targets.Count == 0) return;
        Remember();
        foreach (var mark in targets) { mark.X += dx; mark.Y += dy; }
        Redraw();
    }

    private async void View_KeyDown(object? sender, KeyEventArgs e)
    {
        if (file is null || InTextBox(e.Source)) return;
        // Açılır kutu veya kaydırıcı klavyeyle kullanılırken tuşlar seçili öğeye gitmesin.
        if (e.Source is Visual origin && (origin.FindAncestorOfType<ComboBox>(true) is not null || origin.FindAncestorOfType<Slider>(true) is not null)) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (e.Key == Key.Delete || e.Key == Key.Back) { DeleteSelected(); e.Handled = true; }
        else if (ctrl && (e.Key == Key.Y || (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Shift)))) { Redo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Z) { Undo(); e.Handled = true; }
        else if (e.Key == Key.Escape && (selected is not null || picked.Count > 0)) { Select(null); Redraw(); e.Handled = true; }
        else if (e.Key == Key.PageDown) { if (!busy) await ShowPage(page + 1); e.Handled = true; }
        else if (e.Key == Key.PageUp) { if (!busy) await ShowPage(page - 1); e.Handled = true; }
        else if (e.Key == Key.F2 && selected is { Kind: MarkKind.Text } && Single) { BeginEdit(selected); e.Handled = true; }
        else if (selected is not null && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
            Nudge(e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0, e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0);
            e.Handled = true;
        }
    }

    // ----- Kaydet -----

    private async void Save_Click(object? sender, RoutedEventArgs e) => await SaveAsync();

    internal async Task<IReadOnlyList<string>?> SaveAsync()
    {
        if (file is null || busy) return null;
        EndEdit(); EndRewrite(commit: true); EndParagraph(commit: true);
        if (marks.Count == 0 && ReferenceEquals(document, original)) { await shell.Info("Henüz bir değişiklik yapmadınız."); return null; }
        var input = file; var list = marks.Select(m => m.Clone()).ToList(); var source = workPath;
        var destination = OutputFolder.For(input);
        var result = await shell.RunAsync("Düzenlenen PDF kaydediliyor", (_, progress, token) =>
            Task.Run<IReadOnlyList<string>>(() => [PdfAnnotator.SaveNextTo(source, destination, list, token, originalName: input)], token));
        if (result is not null) { history.Clear(); redo.Clear(); Redraw(); shell.ShowResult(result, "Düzenlediğiniz PDF yeni bir dosya olarak kaydedildi. Asıl PDF olduğu gibi duruyor."); }
        return result;
    }

    // ----- Yerinde yazı değiştirme (KillerPdf.Engine) -----

    private async Task LoadLines()
    {
        var bytes = document; var n = page;
        PageContent content;
        try { content = await Task.Run(() => TextRewriter.Page(bytes, n)); }
        catch (Exception error) when (error is not OperationCanceledException) { App.Log(error); content = new PageContent([], [], []); }
        lines = content.Lines; pageFonts = content.Fonts;
        hoveredLine = null; hoverObject = null;
        // Seçilip taşınabilen öğeler: her satır ve sayfanın büyük bölümünü kaplamayan resimler (taranmış sayfanın kendisi sayılmaz).
        var area = sizes.TryGetValue(n, out var size) ? size.Width * size.Height : double.MaxValue;
        objects = [.. lines.Select(l => new PageObject(new Rect(l.X - 1.5, l.Y - 1.5, l.Width + 3, l.Height + 3), l)),
                   .. content.Images.Where(i => i.Width * i.Height < area * .6).Select(i => new PageObject(new Rect(i.X, i.Y, i.Width, i.Height), null))];
    }

    private async Task BeginRewrite(TextLine line, double? clickX = null)
    {
        var blocker = TextRewriter.Blocker(document, page, line);
        if (blocker is not null) { await shell.Info(blocker); return; }
        var box = rewriteBox = new TextBox
        {
            Tag = line, Text = line.Text, AcceptsReturn = false, FontFamily = FontOf(TextRewriter.SystemFamily(line.FontName) ?? ""), FontSize = line.FontSize, FontWeight = line.Bold ? FontWeight.Bold : FontWeight.Normal,
            FontStyle = line.Italic ? FontStyle.Italic : FontStyle.Normal, MinHeight = 0,
            MinWidth = Math.Max(line.Width + 20, 60), MaxWidth = Math.Max(overlayWidth - line.X, 60), Padding = new Thickness(1, 0, 1, 0), Margin = new Thickness(0),
            BorderThickness = new Thickness(1.5 / zoom), CornerRadius = new CornerRadius(0),
        };
        Dress(box, Brushes.Black, Ui.Brush("Primary"), Brushes.White);
        ToolTip.SetTip(box, "Enter: uygula · Esc: vazgeç");
        Canvas.SetLeft(box, line.X - 1); Canvas.SetTop(box, line.Y - 1);
        box.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter) { EndRewrite(commit: true); e.Handled = true; }
            else if (e.Key == Key.Escape) { EndRewrite(commit: false); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        box.LostFocus += (_, _) => { if (rewriteBox == box) EndRewrite(commit: true); };
        Redraw();
        ShowEditor(box);
        // İmleç tıklanan harfin yanına konur; yazının tümü seçilmez (yanlışlıkla silinmesin).
        box.CaretIndex = CaretAt(box, line, clickX);
    }

    private static int CaretAt(TextBox box, TextLine line, double? clickX)
    {
        var text = box.Text ?? "";
        if (clickX is null || text.Length == 0 || line.Width <= 0) return text.Length;
        var fraction = Math.Clamp((clickX.Value - line.X) / line.Width, 0, 1);
        try
        {
            var layout = new Avalonia.Media.TextFormatting.TextLayout(text, new Typeface(box.FontFamily, box.FontStyle, box.FontWeight), box.FontSize, Brushes.Black);
            var hit = layout.HitTestPoint(new Point(fraction * layout.WidthIncludingTrailingWhitespace, layout.Height / 2));
            return Math.Clamp(hit.TextPosition + (hit.IsTrailing ? 1 : 0), 0, text.Length);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or NotSupportedException) { return (int)Math.Round(fraction * text.Length); }
    }

    private void EndRewrite(bool commit)
    {
        if (rewriteBox is null) return;
        var box = rewriteBox; rewriteBox = null;
        EditorLayer.Children.Remove(box);
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
        try { result = await Task.Run(() => TextRewriter.ReplaceVisibly(before, n, line, text)); }
        catch (UserFacingException error) { await shell.Error(error.Message); Redraw(); return; }
        Remember();
        document = result.Document;
        await ReloadPages();
        if (result.UsedSubstituteFont) Summary.Text = "Değiştirildi. Gömülü yazı tipinde bu harfler yoktu; satır bilgisayardaki aynı yazı tipiyle (yoksa Arial) yazıldı.";
        else if (result.ColorRestored) Summary.Text = "Değiştirildi. Yazı görünmeyen bir renkle yazılmıştı; eski rengi geri verildi.";
    }

    private async Task ReloadPages(bool keepSelection = false)
    {
        try { await File.WriteAllBytesAsync(workPath, document); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { await shell.Error("Geçici dosya yazılamadı: " + error.Message); return; }
        foreach (var entry in pages.Values) entry.Skia.Dispose();
        pages.Clear();
        await ShowPage(page, keepSelection);
    }

    // ----- Kapatma rengi: kutunun kenarındaki sayfa renginden -----

    /// outside: örnekler kutunun 2 pt dışından alınır (kutunun içi yazı veya resimle doluysa).
    private string SampleBackground(Mark box, bool outside = false)
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
            var inset = outside ? -2.0 : 2.0;
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
            BorderThickness = new Thickness(1.5 / zoom), CornerRadius = new CornerRadius(0),
        };
        Dress(box, Brushes.Black, Ui.Brush("Success"), Brushes.White);
        ToolTip.SetTip(box, "Ctrl+Enter: uygula · Esc: vazgeç");
        Canvas.SetLeft(box, para.X - 3); Canvas.SetTop(box, para.Y - 3);
        box.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))) { EndParagraph(commit: true); e.Handled = true; }
            else if (e.Key == Key.Escape) { EndParagraph(commit: false); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        Redraw();
        ShowEditor(box);
        box.CaretIndex = box.Text?.Length ?? 0;
    }

    private void EndParagraph(bool commit)
    {
        if (paragraphBox is null) return;
        var box = paragraphBox; paragraphBox = null;
        EditorLayer.Children.Remove(box);
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

    // İmzalar bu bilgisayarda, uygulamanın veri klasöründe saklanır; uygulama kapatılıp açılınca yerinde durur.
    private static string SignatureFolder => System.IO.Path.Combine(App.DataFolder, "imzalar");

    /// Kayıtlı imzalar (eklenme sırasıyla). Eski tek imza dosyası varsa klasöre taşınır.
    public static List<string> Signatures()
    {
        try
        {
            Directory.CreateDirectory(SignatureFolder);
            var old = System.IO.Path.Combine(App.DataFolder, "imza.png");
            if (File.Exists(old)) File.Move(old, System.IO.Path.Combine(SignatureFolder, "İmza.png"), overwrite: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        try { return [.. Directory.EnumerateFiles(SignatureFolder, "*.png").OrderBy(File.GetCreationTimeUtc).ThenBy(p => p, StringComparer.OrdinalIgnoreCase)]; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    /// Bir veya birkaç imza resmi seçtirir ve kalıcı olarak kaydeder. Ayarlar menüsünden de çağrılır.
    public static async Task<List<string>> ChooseSignatures(MainWindow owner)
    {
        var saved = new List<string>();
        foreach (var source in await Pick.Files(owner, Pick.Images, multiple: true))
        {
            try
            {
                using var bitmap = Images.Load(source);
                Directory.CreateDirectory(SignatureFolder);
                var stem = string.Concat(System.IO.Path.GetFileNameWithoutExtension(source).Where(c => !System.IO.Path.GetInvalidFileNameChars().Contains(c))).Trim();
                if (stem.Length == 0) stem = "İmza";
                var target = System.IO.Path.Combine(SignatureFolder, stem + ".png");
                for (var i = 2; File.Exists(target); i++) target = System.IO.Path.Combine(SignatureFolder, $"{stem} ({i}).png");
                File.WriteAllBytes(target, Images.Png(bitmap));
                saved.Add(target);
            }
            catch (Exception error) when (error is UserFacingException or IOException or UnauthorizedAccessException)
            { await owner.Error("İmza kaydedilemedi: " + error.Message); }
        }
        return saved;
    }

    private async void AddSignature_Click(object? sender, RoutedEventArgs e)
    {
        if (file is null) return;
        if (Signatures().Count == 0)
        {
            var added = await ChooseSignatures(shell);
            if (added.Count > 0) await PlaceSignature(added[0]);
            return;
        }
        ShowSignatures();
    }

    /// İmza listesi: imzaya tıklayınca sayfaya eklenir; çarpı imzayı kayıtlardan siler; alttan yeni imza yüklenir.
    private void ShowSignatures()
    {
        var panel = new StackPanel { Spacing = 4, MinWidth = 260 };
        var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
        void Fill()
        {
            panel.Children.Clear();
            panel.Children.Add(new TextBlock { Text = "Kayıtlı imzalar", FontWeight = FontWeight.SemiBold, Margin = new Thickness(4, 0, 0, 4) });
            foreach (var path in Signatures())
            {
                Control preview;
                try { preview = new Image { Source = Decode(File.ReadAllBytes(path)), Height = 36, MaxWidth = 130, Stretch = Stretch.Uniform }; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { continue; }
                var add = new Button { Classes = { "ghost" }, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                    Content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, Children = { new Border { Background = Brushes.White, Padding = new Thickness(4), CornerRadius = new CornerRadius(4), Child = preview }, new TextBlock { Text = System.IO.Path.GetFileNameWithoutExtension(path), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, MaxWidth = 160, TextTrimming = TextTrimming.CharacterEllipsis } } } };
                ToolTip.SetTip(add, "Sayfaya ekle");
                add.Click += async (_, _) => { flyout.Hide(); await PlaceSignature(path); };
                var remove = new Button { Classes = { "ghost" }, Padding = new Thickness(8, 6), Content = new PathIcon { Data = (Geometry)this.FindResource("IconClose")!, Width = 11, Height = 11 } };
                ToolTip.SetTip(remove, "Bu imzayı kayıtlardan sil");
                remove.Click += (_, _) =>
                {
                    try { File.Delete(path); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                    if (Signatures().Count == 0) flyout.Hide(); else Fill();
                };
                var row = new DockPanel(); DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove); row.Children.Add(add);
                panel.Children.Add(row);
            }
            var more = new Button { Classes = { "ghost" }, Content = "＋ Yeni imza yükle…", Margin = new Thickness(0, 4, 0, 0) };
            more.Click += async (_, _) => { flyout.Hide(); if ((await ChooseSignatures(shell)).Count > 0) ShowSignatures(); };
            panel.Children.Add(more);
        }
        Fill();
        flyout.ShowAt(SignatureButton);
    }

    private async Task PlaceSignature(string path)
    {
        try
        {
            var png = File.ReadAllBytes(path);
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

    internal Vector ScrollOffset { get => Scroller.Offset; set => Scroller.Offset = value; }
    internal Size ScrollExtent => Scroller.Extent;
    internal void SmokeZoom(double value) => SetZoom(value);
    internal void SmokeRedraw() => Redraw();
    internal bool RewriteBoxOpen => rewriteBox is not null && EditorLayer.Children.Contains(rewriteBox);
    internal bool RewriteBoxFocused => rewriteBox?.IsFocused == true;
    internal int RewriteCaret => rewriteBox?.CaretIndex ?? -1;
    internal IReadOnlyList<string> PageFonts => pageFonts;
    internal int ObjectCount => objects.Count;
    internal bool Busy => busy;
    internal async Task SmokeRewriteBox(int index, double fraction)
    {
        RewriteTool.IsChecked = true;
        var line = lines.OrderBy(l => l.Y).ElementAt(index);
        await BeginRewrite(line, line.X + line.Width * fraction);
    }
    internal void SmokeCancelRewrite() => EndRewrite(commit: false);
    /// Sayfanın kendi öğelerini (yazısı verilen satırlar ve istenirse ilk resim) seçip taşır; kullanıcının sürüklemesiyle aynı yol.
    internal async Task SmokeMove(string[] startsWith, bool image, double dx, double dy)
    {
        SelectTool.IsChecked = true; Select(null);
        foreach (var start in startsWith) picked.Add(objects.First(o => o.Line is not null && o.Line.Text.StartsWith(start, StringComparison.Ordinal)));
        if (image) picked.Add(objects.First(o => o.Line is null));
        Remember();
        LiftPicked(createMarks: true);
        foreach (var mark in Chosen) { mark.X += dx; mark.Y += dy; }
        var job = pendingLift!; pendingLift = null;
        await FinishLift(job);
    }
    internal void SmokeText(string text, string family, double x, double y)
    {
        var mark = new Mark { Kind = MarkKind.Text, Page = page, X = x, Y = y, Width = 360, Height = 26, Text = text, FontSize = 14, FontFamily = family };
        marks.Add(mark); Select(mark); Redraw();
    }
    internal string CurrentColor => color;
    internal IReadOnlyList<string> SwatchColors => [.. swatches.Keys];
    internal IReadOnlyList<string> FontChoices => [.. FontBox.Items.OfType<ComboBoxItem>().Select(i => (i.Content?.ToString() ?? "") + (i.IsEnabled ? "" : " [kapalı]"))];
    internal string SelectedFontLabel => (FontBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
    internal bool SurveyReady => survey is not null && FontCatalog.Ready;
    internal bool CanUndo => history.Count > 0;
    internal bool CanRedo => redo.Count > 0;
    internal void SmokeRedo() => Redo();
    internal void SmokeColor(string hex) => SetColor(hex);
    internal void SmokeDropper(bool on) => PickColorButton.IsChecked = on;
    internal void SmokePick(string startsWith) { SelectTool.IsChecked = true; Select(null); picked.Add(objects.First(o => o.Line is not null && o.Line.Text.StartsWith(startsWith, StringComparison.Ordinal))); Redraw(); }
    internal string LineColor(string startsWith) => lines.First(x => x.Text.StartsWith(startsWith, StringComparison.Ordinal)).Color;
    internal void SmokeFont(string label) { FontBox.SelectedItem = FontBox.Items.OfType<ComboBoxItem>().First(i => i.IsEnabled && (i.Content?.ToString() ?? "").StartsWith(label, StringComparison.OrdinalIgnoreCase)); }
    internal void SmokeOpen(string what)
    {
        if (what == "fonts") FontBox.IsDropDownOpen = true;
        else if (what == "color") CustomColor_Click(null, new RoutedEventArgs());
        else if (what == "signatures") ShowSignatures();
    }
    internal static string SmokeSignatureFolder => SignatureFolder;
    internal int PickedCount => picked.Count;
    internal int ChosenCount => Chosen.Count();
    internal bool EditorOpen => editor is not null && EditorLayer.Children.Contains(editor);
    internal void SmokeType(string text) { if (editor is not null) editor.Text = text; }
    internal void SmokeDelete() => DeleteSelected();
    internal void SmokeUndo() => Undo();
    internal void SmokeTool(string name) => (name switch { "text" => TextTool, "rewrite" => RewriteTool, _ => SelectTool }).IsChecked = true;
    internal IReadOnlyList<(MarkKind Kind, double X, double Y, string Text, string Font, bool Cover)> SmokeMarks => marks.Select(m => (m.Kind, m.X, m.Y, m.Text, m.FontFamily, m.Cover)).ToList();
    internal (double X, double Y, double Width, double Height) SmokeLine(string startsWith) { var l = lines.First(x => x.Text.StartsWith(startsWith, StringComparison.Ordinal)); return (l.X, l.Y, l.Width, l.Height); }
    internal bool HasLine(string startsWith) => lines.Any(x => x.Text.StartsWith(startsWith, StringComparison.Ordinal));

    private readonly Pointer smokePointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, true);
    /// Gerçek işaretçi olaylarını üretir (0 bas, 1 sürükle, 2 bırak); konum sayfa noktası cinsindendir.
    internal void SmokePointer(int phase, double x, double y, KeyModifiers modifiers = KeyModifiers.None, int clicks = 1)
    {
        var root = (Visual)TopLevel.GetTopLevel(this)!;
        var position = Overlay.TranslatePoint(new Point(x, y), root) ?? default;
        var down = new PointerPointProperties(RawInputModifiers.LeftMouseButton, phase == 0 ? PointerUpdateKind.LeftButtonPressed : PointerUpdateKind.Other);
        var up = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased);
        if (phase == 0) Overlay_Down(Overlay, new PointerPressedEventArgs(Overlay, smokePointer, root, position, 0, down, modifiers, clicks));
        else if (phase == 1) Overlay_Move(Overlay, new PointerEventArgs(PointerMovedEvent, Overlay, smokePointer, root, position, 0, down, modifiers));
        else Overlay_Up(Overlay, new PointerReleasedEventArgs(Overlay, smokePointer, root, position, 0, up, modifiers, MouseButton.Left));
    }

    internal void SmokeEditSelected() { if (selected is { Kind: MarkKind.Text }) BeginEdit(selected); }
    internal void SmokeEndEdit() => EndEdit();

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
