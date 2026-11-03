// Nashi'nin "sürüklerken tuşa bas, çark açılsın" yaklaşımından uyarlanmıştır
// (https://github.com/orkunisitmak/nashi-app, Copyright (c) 2026 Orkun Işıtmak and Nashi contributors, MIT).
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using PanDaF.Core;
using PanDaF.Views;

namespace PanDaF;

public sealed record WheelChoice(string Id, string Title, string Icon);

/// İmlecin altında açılan yuvarlak panel. Sürüklenen dosya üzerine gelene kadar görünmezdir;
/// böylece Shift'le yapılan sıradan seçimlerde (metin seçme gibi) ortaya çıkmaz.
public sealed class PandaWheel : Window
{
    public const double Diameter = 380;
    private const double Inner = 108, Outer = 250, Center = 270;
    // Tamamen saydam pencere fareyi algılamaz; çok küçük bir opaklıkla başlar.
    private const double HiddenOpacity = .02;
    private readonly Action<string, string[]> chosen;
    private readonly Canvas canvas = new() { Width = 540, Height = 540, Background = Brushes.Transparent };
    private IReadOnlyList<WheelChoice> choices = [];
    private string[] files = [];
    private string selection = "";
    private TextBlock? centerText;
    private bool logged, virtualOnly;
    public bool Revealed { get; private set; }

    public PandaWheel(Action<string, string[]> chosen)
    {
        this.chosen = chosen;
        Title = "PanDaF paneli";
        Width = Height = Diameter;
        SystemDecorations = SystemDecorations.None; TransparencyLevelHint = [WindowTransparencyLevel.Transparent]; Background = Brushes.Transparent;
        CanResize = false; ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        Content = new Viewbox { Child = canvas };
        Build();
        Opacity = HiddenOpacity;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, (_, e) => { Track(e); if (choices.Count > 0) Reveal(); });
        AddHandler(DragDrop.DragOverEvent, (_, e) => { Track(e); if (choices.Count > 0) Reveal(); });
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            Track(e);
            var id = selection; var dropped = files;
            e.Handled = true;
            Close();
            // Dosyayı bırakan program (Gezgin/Finder) beklemesin: iş, bırakma bittikten sonra başlar.
            PanelLog.Write(id.Length > 0 ? $"Bırakıldı: {id}" : "Boş yere bırakıldı (işlem yok).");
            if (id.Length > 0 && dropped.Length > 0)
                Dispatcher.UIThread.Post(() => chosen(id, dropped), DispatcherPriority.Background);
        });
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    /// Sürüklenen dosyalara göre paneldeki seçenekler.
    /// pdf / pdf-combine / pdf-separate / word / excel / jpg: pencere açılmadan, dosyanın yanına kaydeder.
    /// edit / merge / split / organize / open: ilgili aracı dosyalar yüklenmiş olarak açar.
    public static IReadOnlyList<WheelChoice> For(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return [];
        var pdfs = files.Count(FileKinds.IsPdf);
        var images = files.Count(FileKinds.IsImage);
        var convertible = files.Count(FileKinds.CanConvertToPdf);
        if (pdfs == 1 && files.Count == 1)
            return [new("edit", "İçeriği düzenle", "IconEdit"), new("organize", "Sayfaları düzenle", "IconOrganize"), new("split", "Böl", "IconSplit"), new("word", "Word'e çevir", "IconWord"), new("excel", "Excel'e çevir", "IconTable"), new("jpg", "Resme çevir", "IconImage")];
        if (pdfs == files.Count)
            return [new("merge", "Birleştir", "IconMerge"), new("word", "Word'e çevir", "IconWord"), new("excel", "Excel'e çevir", "IconTable"), new("jpg", "Resme çevir", "IconImage")];
        if (images == files.Count && images > 1)
            return [new("pdf-combine", "Tek PDF yap", "IconPdf"), new("pdf-separate", "Ayrı PDF'ler", "IconMerge"), new("open", "PanDaF'ta aç", "IconOpen")];
        if (convertible == files.Count) return [new("pdf", "PDF yap", "IconPdf"), new("open", "PanDaF'ta aç", "IconOpen")];
        if (pdfs + convertible > 0) return [new("open", "PanDaF'ta aç", "IconOpen")];
        return [];
    }

    private void Reveal()
    {
        if (Revealed) return;
        Revealed = true;
        Opacity = 1;
    }

    private void Track(DragEventArgs e)
    {
        string[] dropped;
        try { dropped = DropHelper.Paths(e.Data); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or OutOfMemoryException) { dropped = []; }
        if (!logged)
        {
            logged = true;
            string formats;
            try { formats = string.Join(", ", e.Data.GetDataFormats()); } catch (Exception error) when (error is InvalidOperationException) { formats = "?"; }
            PanelLog.Write($"Dosya panele geldi. Dosya sayısı: {dropped.Length}. Biçimler: {formats}. Dosyalar: {string.Join(" | ", dropped.Take(5))}");
        }
        if (!dropped.SequenceEqual(files))
        {
            files = dropped;
            var updated = For(files);
            if (!updated.SequenceEqual(choices)) { choices = updated; Build(); }
        }
        // Tarayıcı indirmeleri, e-posta ekleri, ZIP içi dosyalar ve yalnızca çevrim içi bulut dosyaları
        // gerçek bir dosya yolu taşımaz. Panel bu durumda görünmez kalmasın, ne yapılacağını söylesin.
        if (files.Length == 0 && !virtualOnly && (e.Data.Contains("FileGroupDescriptorW") || e.Data.Contains("FileGroupDescriptor") || e.Data.Contains("Shell IDList Array")))
        {
            virtualOnly = true;
            Revealed = false; Reveal();
            if (centerText is not null) centerText.Text = "Bu dosya bir klasörde değil. Önce masaüstüne kaydedip oradan sürükleyin.";
        }
        var p = e.GetPosition(canvas);
        var distance = Math.Sqrt((p.X - Center) * (p.X - Center) + (p.Y - Center) * (p.Y - Center));
        if (distance < Inner || distance > Outer + 20 || choices.Count == 0) { selection = ""; Highlight(-1); }
        else
        {
            var slice = 360d / choices.Count;
            var angle = (Math.Atan2(p.Y - Center, p.X - Center) * 180 / Math.PI - StartAngle(choices.Count) + 360 + slice / 2) % 360;
            var index = Math.Clamp((int)(angle / slice), 0, choices.Count - 1);
            selection = choices[index].Id;
            Highlight(index);
        }
        e.DragEffects = selection.Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static double StartAngle(int count) => count == 2 ? -180 : -90;

    private void Highlight(int index)
    {
        var slices = canvas.Children.OfType<Avalonia.Controls.Shapes.Path>().ToArray();
        for (var n = 0; n < slices.Length; n++)
        {
            slices[n].Fill = Ui.Brush(n == index ? "PrimarySoft" : "Card");
            slices[n].Stroke = Ui.Brush(n == index ? "Primary" : "Line");
            slices[n].StrokeThickness = n == index ? 4 : 2;
        }
        if (centerText is not null && !virtualOnly)
            centerText.Text = index >= 0 ? choices[index].Title : choices.Count == 0 ? "PanDaF" : "Bir dilime bırakın";
    }

    private void Build()
    {
        canvas.Children.Clear();
        canvas.Children.Add(new Avalonia.Controls.Shapes.Ellipse { Width = 540, Height = 540, Fill = Ui.Brush("Bg"), Stroke = Ui.Brush("Line"), StrokeThickness = 2 });
        var count = choices.Count;
        for (var i = 0; i < count; i++)
        {
            var angle = (StartAngle(count) + 360d * i / count) * Math.PI / 180;
            var half = (180d / count - 1) * Math.PI / 180;
            Point P(double r, double a) => new(Center + r * Math.Cos(a), Center + r * Math.Sin(a));
            var a = angle - half; var b = angle + half; var large = b - a > Math.PI;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(P(Inner, a), true);
                g.LineTo(P(Outer, a));
                g.ArcTo(P(Outer, b), new Size(Outer, Outer), 0, large, SweepDirection.Clockwise);
                g.LineTo(P(Inner, b));
                g.ArcTo(P(Inner, a), new Size(Inner, Inner), 0, large, SweepDirection.CounterClockwise);
                g.EndFigure(true);
            }
            canvas.Children.Add(new Avalonia.Controls.Shapes.Path { Data = geometry, Fill = Ui.Brush("Card"), Stroke = Ui.Brush("Line"), StrokeThickness = 2 });

            var label = new StackPanel { Width = 116, IsHitTestVisible = false };
            label.Children.Add(Ui.IconControl(choices[i].Icon, 30, Ui.Brush("Primary")));
            label.Children.Add(new TextBlock { Text = choices[i].Title, FontSize = 19, FontWeight = FontWeight.SemiBold, Foreground = Ui.Brush("Ink"), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
            label.Measure(new Size(116, double.PositiveInfinity));
            var radius = count == 2 ? 172 : (Inner + Outer) / 2;
            var position = count == 1 ? new Point(Center, Center - (Inner + Outer) / 2) : P(radius, angle);
            Canvas.SetLeft(label, position.X - 58); Canvas.SetTop(label, position.Y - label.DesiredSize.Height / 2);
            canvas.Children.Add(label);
        }
        var middle = new StackPanel { Width = 190, IsHitTestVisible = false };
        middle.Children.Add(new Image { Source = Logo.Value, Width = 92, Height = 92, Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Center });
        centerText = new TextBlock { Text = count == 0 ? "PanDaF" : "Bir dilime bırakın", FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = Ui.Brush("Ink"), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        middle.Children.Add(centerText);
        middle.Measure(new Size(190, double.PositiveInfinity));
        Canvas.SetLeft(middle, Center - 95); Canvas.SetTop(middle, Center - middle.DesiredSize.Height / 2);
        canvas.Children.Add(middle);
    }

    private static readonly Lazy<Bitmap> Logo = new(() => new Bitmap(AssetLoader.Open(new Uri("avares://PanDaF/Assets/PanDaF.png"))));

    /// Paneli imlecin bulunduğu ekrana, ekran dışına taşmadan yerleştirir (x, y: fiziksel piksel).
    public void PlaceAt(int x, int y)
    {
        var screen = Screens.ScreenFromPoint(new PixelPoint(x, y)) ?? Screens.Primary;
        if (screen is null) { Position = new PixelPoint(x - (int)(Diameter / 2), y - (int)(Diameter / 2)); return; }
        var work = screen.WorkingArea;
        var size = (int)Math.Round(Diameter * screen.Scaling);
        size = Math.Min(size, Math.Min(work.Width, work.Height));
        var left = Math.Clamp(x - size / 2, work.X, work.Right - size);
        var top = Math.Clamp(y - size / 2, work.Y, work.Bottom - size);
        Width = Height = size / screen.Scaling;
        Position = new PixelPoint(left, top);
    }

    // Duman testi: paneli dosyalarla doldurup görünür hâle getirir (gerçek sürükleme olmadan).
    internal void Preview(IReadOnlyList<string> sample, int highlighted)
    {
        files = sample.ToArray();
        choices = For(files);
        Build();
        Highlight(highlighted);
        Reveal();
    }
}

/// Panelin ne yaptığını PanDaF veri klasöründeki panel-gunlugu.txt dosyasına yazar (sorun bildirmek için).
internal static class PanelLog
{
    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(App.DataFolder);
            var file = Path.Combine(App.DataFolder, "panel-gunlugu.txt");
            if (File.Exists(file) && new FileInfo(file).Length > 300_000) File.Delete(file);
            File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\r\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
