using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using PanDaF.Core;
using PanDaF.Views;

namespace PanDaF;

/// Ekranın köşesinde çıkan küçük bildirim. Shift panelinden başlatılan işlemler ana pencereyi
/// açmadan bununla gösterilir: önce ilerleme, sonra "Hazır" (Aç / Klasörde göster) veya hata.
public sealed class Toast : Window
{
    private static readonly List<Toast> open = [];
    private readonly TextBlock title = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock message = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
    private readonly ProgressBar bar = new() { Minimum = 0, Maximum = 1, IsIndeterminate = true, Height = 6, Margin = new Thickness(0, 10, 0, 0) };
    private readonly WrapPanel buttons = new() { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
    private readonly DispatcherTimer closeTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private bool hovering;
    public CancellationTokenSource Cancellation { get; } = new();
    public IReadOnlyList<string> Outputs { get; private set; } = [];

    private Toast(string heading)
    {
        Title = "PanDaF";
        Width = 380; SizeToContent = SizeToContent.Height;
        SystemDecorations = SystemDecorations.None; TransparencyLevelHint = [WindowTransparencyLevel.Transparent]; Background = Brushes.Transparent;
        CanResize = false; ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        title.Text = heading;
        title.Foreground = Ui.Brush("Ink");
        message.Foreground = Ui.Brush("Muted");

        var logo = new Image { Width = 40, Height = 40, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top, Source = Logo.Value };
        var text = new StackPanel();
        text.Children.Add(title); text.Children.Add(message); text.Children.Add(bar); text.Children.Add(buttons);
        var row = new DockPanel();
        DockPanel.SetDock(logo, Dock.Left);
        row.Children.Add(logo); row.Children.Add(text);
        Content = new Border
        {
            Margin = new Thickness(12), Padding = new Thickness(16, 14, 16, 14), CornerRadius = new CornerRadius(14),
            Background = Ui.Brush("Card"), BorderBrush = Ui.Brush("Line"), BorderThickness = new Thickness(1), Child = row,
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 18, OffsetY = 2, Color = Color.FromArgb(64, 0, 0, 0) }),
        };
        AddButton("İptal", () => { Cancellation.Cancel(); message.Text = "İptal ediliyor…"; });
        PointerEntered += (_, _) => hovering = true;
        PointerExited += (_, _) => hovering = false;
        closeTimer.Tick += (_, _) => { if (!hovering) Close(); };
        Closed += (_, _) => { closeTimer.Stop(); open.Remove(this); Restack(); };
        Opened += (_, _) => Restack();
        SizeChanged += (_, _) => Restack();
    }

    public static Toast Open(string heading)
    {
        var toast = new Toast(heading);
        open.Add(toast);
        if (!App.SmokeMode) toast.Show();
        return toast;
    }

    public void Progress(OperationProgress progress)
    {
        if (!string.IsNullOrWhiteSpace(progress.Label)) message.Text = progress.Label;
        if (progress.Fraction is { } f && double.IsFinite(f)) { bar.IsIndeterminate = false; bar.Value = Math.Clamp(f, 0, 1); }
    }

    public void Done(IReadOnlyList<string> outputs, string heading)
    {
        Outputs = outputs;
        title.Text = heading;
        message.Text = outputs.Count == 1 ? Path.GetFileName(outputs[0]) : $"{outputs.Count} dosya · {Path.GetDirectoryName(outputs[0])}";
        bar.IsVisible = false;
        buttons.Children.Clear();
        if (outputs.Count == 1) AddButton(Directory.Exists(outputs[0]) ? "Klasörü aç" : "Aç", () => { Platform.Open(outputs[0]); Close(); });
        AddButton("Klasörde göster", () => { Platform.Reveal(outputs[0]); Close(); });
        AddButton("Kapat", Close);
        closeTimer.Start();
    }

    public void Fail(string error)
    {
        title.Text = "İşlem yapılamadı";
        message.Text = error;
        message.Foreground = Ui.Brush("Ink");
        bar.IsVisible = false;
        buttons.Children.Clear();
        AddButton("Kapat", Close);
        closeTimer.Interval = TimeSpan.FromSeconds(20);
        closeTimer.Start();
    }

    /// Yalnızca bilgi: ilerleme çubuğu yok, bir süre sonra kendiliğinden kapanır.
    public void Note(string text)
    {
        message.Text = text;
        bar.IsVisible = false;
        buttons.Children.Clear();
        AddButton("Kapat", Close);
        closeTimer.Interval = TimeSpan.FromSeconds(12);
        closeTimer.Start();
    }

    private void AddButton(string text, Action click)
    {
        var button = new Button { Content = text, FontSize = 13, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(4, 0, 0, 0) };
        button.Classes.Add("ghost");
        button.Click += (_, _) => click();
        buttons.Children.Add(button);
    }

    // Bildirimleri sağ altta (macOS'ta sağ üstte, bildirim merkezine yakın) alt alta diz.
    private static void Restack()
    {
        var first = open.FirstOrDefault(t => t.IsVisible && t.Screens.ScreenFromWindow(t) is not null);
        if (first is null) return;
        var screen = first.Screens.ScreenFromWindow(first)!;
        var area = screen.WorkingArea;
        var scale = first.DesktopScaling;
        var fromTop = OperatingSystem.IsMacOS();
        var edge = fromTop ? area.Y : area.Bottom;
        foreach (var toast in open.AsEnumerable().Reverse())
        {
            if (!toast.IsVisible) continue;
            var width = (int)(toast.Bounds.Width * scale); var height = (int)(toast.Bounds.Height * scale);
            if (width == 0 || height == 0) continue;
            var x = area.Right - width - 4;
            var y = fromTop ? edge + 4 : edge - height - 4;
            toast.Position = new PixelPoint(x, y);
            edge = fromTop ? y + height : y;
        }
    }

    private static readonly Lazy<Bitmap> Logo = new(() => new Bitmap(AssetLoader.Open(new Uri("avares://PanDaF/Assets/PanDaF.png"))));
}
