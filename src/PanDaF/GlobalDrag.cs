// Nashi'nin "sürüklerken tuşa bas, çark açılsın" yaklaşımından uyarlanmıştır
// (https://github.com/orkunisitmak/nashi-app, Copyright (c) 2026 Orkun Işıtmak and Nashi contributors, MIT).
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using ShapePath = System.Windows.Shapes.Path;
using System.Windows.Threading;
using PanDaF.Core;

namespace PanDaF;

public sealed record WheelChoice(string Id, string Title, string Glyph);

/// Masaüstünde veya bir klasörde dosya sürüklenirken Shift'e basılırsa imlecin altında PanDaF
/// panelini açar. Yalnızca fare sol tuşunun ve Shift/Ctrl/Alt'ın basılı olup olmadığına bakar;
/// klavyede yazılanları okumaz, panoya dokunmaz. Dosyalar ancak panele bırakılınca alınır.
public sealed class GlobalDragWatcher : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] internal struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo { public int Size, Flags; public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret; public NativeRect CaretRect; }
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int metric);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    private readonly MainWindow owner;
    private readonly Func<bool> enabled;
    private readonly DispatcherTimer timer;
    private NativePoint start;
    private bool down, opened;
    private PandaWheel? wheel;

    public GlobalDragWatcher(MainWindow owner, Func<bool> enabled)
    {
        this.owner = owner; this.enabled = enabled;
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) => Poll();
        timer.Start();
    }

    private static bool Held(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    // Sol/sağ el fare ayarına göre "birincil" tuş.
    private static bool PrimaryPressed => Held(GetSystemMetrics(23) != 0 ? 0x02 : 0x01);
    private static bool OnlyShift => Held(0x10) && !Held(0x11) && !Held(0x12);
    // Pencere başlığından sürükleme (taşıma/boyutlandırma) dosya taşımaz.
    private static bool InMoveSize() { var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() }; return GetGUIThreadInfo(0, ref info) && (info.Flags & 0x2) != 0; }

    private void Poll()
    {
        if (!enabled() || !owner.IsEnabled) { Reset(); return; }
        var held = PrimaryPressed;
        GetCursorPos(out var point);
        if (!held)
        {
            if (down)
            {
                down = false; opened = false;
                if (wheel is { IsVisible: true } old)
                {
                    // Bırakma (drop) olayı işlenebilsin diye kısa bir süre bekle.
                    var dismiss = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                    dismiss.Tick += (_, _) => { dismiss.Stop(); if (old.IsVisible) old.Close(); };
                    dismiss.Start();
                }
                wheel = null;
            }
            return;
        }
        if (!down) { down = true; start = point; opened = false; return; }
        if (opened || Math.Abs(point.X - start.X) + Math.Abs(point.Y - start.Y) < 10) return;
        if (!OnlyShift) return;
        opened = true;
        if (InMoveSize()) { PanelLog.Write("Pencere taşınıyor; panel açılmadı."); return; }
        // PanDaF penceresinin içindeki sürüklemeler (ör. sayfa sıralama) paneli açmaz.
        if (GetForegroundWindow() == new WindowInteropHelper(owner).Handle && owner.IsVisible) { PanelLog.Write("Sürükleme PanDaF penceresinin içinde; panel açılmadı."); return; }
        PanelLog.Write($"Shift ile sürükleme algılandı, panel açılıyor ({point.X},{point.Y}).");
        wheel = new PandaWheel((id, files) => owner.QuickAction(id, files));
        wheel.Show();
        PlaceAt(wheel, point.X, point.Y, PandaWheel.Diameter);
    }

    private void Reset()
    {
        wheel?.Close(); wheel = null; down = false; opened = false;
    }

    public void Dispose() { timer.Stop(); wheel?.Close(); }

    // ----- Paneli imlecin bulunduğu ekrana, ekran dışına taşmadan yerleştir -----
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);

    internal static void PlaceAt(Window window, int x, int y, double logicalSize)
    {
        var monitor = MonitorFromPoint(new NativePoint { X = x, Y = y }, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        var dpi = 96u;
        try { if (GetDpiForMonitor(monitor, 0, out var actual, out _) == 0) dpi = actual; } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
        var size = (int)Math.Round(logicalSize * dpi / 96d);
        size = Math.Min(size, Math.Min(info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top));
        var left = Math.Clamp(x - size / 2, info.Work.Left, info.Work.Right - size);
        var top = Math.Clamp(y - size / 2, info.Work.Top, info.Work.Bottom - size);
        // 0x14 = SWP_NOZORDER | SWP_NOACTIVATE
        SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero, left, top, size, size, 0x14);
    }
}

/// İmlecin altında açılan yuvarlak panel. Sürüklenen dosya üzerine gelene kadar görünmezdir;
/// böylece Shift'le yapılan sıradan seçimlerde (metin seçme gibi) ortaya çıkmaz.
public sealed class PandaWheel : Window
{
    public const double Diameter = 380;
    private const double Inner = 108, Outer = 250, Center = 270;
    // Tamamen saydam pencere fareyi algılamaz; çok küçük bir opaklıkla başlar.
    private const double HiddenOpacity = .02;
    private readonly Action<string, string[]> chosen;
    private readonly Canvas canvas = new() { Width = 540, Height = 540, Background = Brushes.Transparent, AllowDrop = true };
    private IReadOnlyList<WheelChoice> choices = [];
    private string[] files = [];
    private string selection = "";
    private TextBlock? centerText;
    public bool Revealed { get; private set; }

    public PandaWheel(Action<string, string[]> chosen)
    {
        this.chosen = chosen;
        Title = "PanDaF paneli";
        Width = Height = Diameter;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; ShowActivated = false; Topmost = true; AllowDrop = true;
        Content = new Viewbox { Child = canvas };
        Build();
        Opacity = HiddenOpacity;
        DragEnter += (_, e) => { Track(e); if (choices.Count > 0) Reveal(); };
        DragOver += (_, e) => { Track(e); if (choices.Count > 0) Reveal(); };
        Drop += (_, e) =>
        {
            Track(e);
            var id = selection; var dropped = files;
            e.Handled = true;
            Close();
            // Dosyayı bırakan program (Gezgin) beklemesin: iş, bırakma bittikten sonra başlar.
            PanelLog.Write(id.Length > 0 ? $"Bırakıldı: {id}" : "Boş yere bırakıldı (işlem yok).");
            if (id.Length > 0 && dropped.Length > 0)
                Application.Current.Dispatcher.InvokeAsync(() => chosen(id, dropped), DispatcherPriority.Background);
        };
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }

    /// Sürüklenen dosyalara göre paneldeki seçenekler.
    /// pdf / pdf-combine / pdf-separate / word / jpg: pencere açılmadan, dosyanın yanına kaydeder.
    /// merge / split / organize / open: ilgili aracı dosyalar yüklenmiş olarak açar.
    public static IReadOnlyList<WheelChoice> For(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return [];
        var pdfs = files.Count(FileKinds.IsPdf);
        var images = files.Count(FileKinds.IsImage);
        var convertible = files.Count(FileKinds.CanConvertToPdf);
        if (pdfs == 1 && files.Count == 1)
            return [new("organize", "Sayfaları düzenle", "\uE70F"), new("split", "Böl", "\uE8C6"), new("word", "Word'e çevir", "\uE8A5"), new("excel", "Excel'e çevir", "\uE80A"), new("jpg", "Resme çevir", "\uE91B")];
        if (pdfs == files.Count)
            return [new("merge", "Birleştir", "\uE8C8"), new("word", "Word'e çevir", "\uE8A5"), new("excel", "Excel'e çevir", "\uE80A"), new("jpg", "Resme çevir", "\uE91B")];
        if (images == files.Count && images > 1)
            return [new("pdf-combine", "Tek PDF yap", "\uE8A5"), new("pdf-separate", "Ayrı PDF'ler", "\uE8C8"), new("open", "PanDaF'ta aç", "\uE8E5")];
        if (convertible == files.Count) return [new("pdf", "PDF yap", "\uE8A5"), new("open", "PanDaF'ta aç", "\uE8E5")];
        if (pdfs + convertible > 0) return [new("open", "PanDaF'ta aç", "\uE8E5")];
        return [];
    }

    private void Reveal()
    {
        if (Revealed) return;
        Revealed = true;
        Opacity = 1;
    }

    private bool logged;
    private bool virtualOnly;

    private void Track(DragEventArgs e)
    {
        string[] dropped;
        try { dropped = e.Data.GetData(DataFormats.FileDrop) as string[] ?? []; }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or OutOfMemoryException or ArgumentException) { dropped = []; }
        if (!logged)
        {
            logged = true;
            string formats;
            try { formats = string.Join(", ", e.Data.GetFormats(false)); } catch (Exception error) when (error is System.Runtime.InteropServices.COMException) { formats = "?"; }
            PanelLog.Write($"Dosya panele geldi. Dosya sayısı: {dropped.Length}. Biçimler: {formats}. Dosyalar: {string.Join(" | ", dropped.Take(5))}");
        }
        if (!dropped.SequenceEqual(files))
        {
            files = Views.DropHelper.Expand(dropped);
            var updated = For(files);
            if (!updated.SequenceEqual(choices)) { choices = updated; Build(); }
        }
        // Tarayıcı indirmeleri, e-posta ekleri, ZIP içi dosyalar ve yalnızca çevrim içi bulut dosyaları
        // gerçek bir dosya yolu taşımaz. Panel bu durumda görünmez kalmasın, ne yapılacağını söylesin.
        if (files.Length == 0 && !virtualOnly && (e.Data.GetDataPresent("FileGroupDescriptorW") || e.Data.GetDataPresent("FileGroupDescriptor") || e.Data.GetDataPresent("Shell IDList Array")))
        {
            virtualOnly = true;
            Revealed = false; Reveal();
            if (centerText is not null) centerText.Text = "Bu dosya bir klasörde değil. Önce masaüstüne kaydedip oradan sürükleyin.";
        }
        var p = e.GetPosition(canvas);
        var distance = (p - new Point(Center, Center)).Length;
        if (distance < Inner || distance > Outer + 20 || choices.Count == 0) { selection = ""; Highlight(-1); }
        else
        {
            var slice = 360d / choices.Count;
            var angle = (Math.Atan2(p.Y - Center, p.X - Center) * 180 / Math.PI - StartAngle(choices.Count) + 360 + slice / 2) % 360;
            var index = Math.Clamp((int)(angle / slice), 0, choices.Count - 1);
            selection = choices[index].Id;
            Highlight(index);
        }
        e.Effects = selection.Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static double StartAngle(int count) => count == 2 ? -180 : -90;

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private void Highlight(int index)
    {
        var slices = canvas.Children.OfType<ShapePath>().ToArray();
        for (var n = 0; n < slices.Length; n++)
        {
            slices[n].Fill = Res(n == index ? "PrimarySoft" : "Card");
            slices[n].Stroke = Res(n == index ? "Primary" : "Line");
            slices[n].StrokeThickness = n == index ? 4 : 2;
        }
        if (centerText is not null && !virtualOnly)
            centerText.Text = index >= 0 ? choices[index].Title : choices.Count == 0 ? "PanDaF" : "Bir dilime bırakın";
    }

    private void Build()
    {
        canvas.Children.Clear();
        canvas.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 540, Height = 540, Fill = Res("Bg"), Stroke = Res("Line"), StrokeThickness = 2,
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 4, Opacity = .28 },
        });
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
                g.BeginFigure(P(Inner, a), true, true);
                g.LineTo(P(Outer, a), true, false);
                g.ArcTo(P(Outer, b), new Size(Outer, Outer), 0, large, SweepDirection.Clockwise, true, false);
                g.LineTo(P(Inner, b), true, false);
                g.ArcTo(P(Inner, a), new Size(Inner, Inner), 0, large, SweepDirection.Counterclockwise, true, false);
            }
            geometry.Freeze();
            canvas.Children.Add(new ShapePath { Data = geometry, Fill = Res("Card"), Stroke = Res("Line"), StrokeThickness = 2 });

            var label = new StackPanel { Width = 116, IsHitTestVisible = false };
            label.Children.Add(new TextBlock { Text = choices[i].Glyph, FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = 30, Foreground = Res("Primary"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) });
            label.Children.Add(new TextBlock { Text = choices[i].Title, FontFamily = new FontFamily("Segoe UI"), FontSize = 19, FontWeight = FontWeights.SemiBold, Foreground = Res("Ink"), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
            label.Measure(new Size(116, double.PositiveInfinity));
            var radius = count == 2 ? 172 : (Inner + Outer) / 2;
            var position = count == 1 ? new Point(Center, Center - (Inner + Outer) / 2) : P(radius, angle);
            Canvas.SetLeft(label, position.X - 58); Canvas.SetTop(label, position.Y - label.DesiredSize.Height / 2);
            canvas.Children.Add(label);
        }
        var middle = new StackPanel { Width = 190, IsHitTestVisible = false };
        middle.Children.Add(new Image { Source = Logo, Width = 92, Height = 92, Margin = new Thickness(0, 0, 0, 6) });
        centerText = new TextBlock { Text = count == 0 ? "PanDaF" : "Bir dilime bırakın", FontFamily = new FontFamily("Segoe UI"), FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Res("Ink"), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        middle.Children.Add(centerText);
        middle.Measure(new Size(190, double.PositiveInfinity));
        Canvas.SetLeft(middle, Center - 95); Canvas.SetTop(middle, Center - middle.DesiredSize.Height / 2);
        canvas.Children.Add(middle);
    }

    private static readonly BitmapImage Logo = LoadLogo();
    private static BitmapImage LoadLogo()
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri("pack://application:,,,/Assets/PanDaF.png");
        image.DecodePixelWidth = 192;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
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

/// Panelin ne yaptığını %LOCALAPPDATA%\PanDaF\panel-gunlugu.txt dosyasına yazar (sorun bildirmek için).
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
