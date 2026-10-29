using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PanDaF.Core;

namespace PanDaF;

/// Ekranın sağ altında çıkan küçük bildirim. Shift panelinden başlatılan işlemler ana pencereyi
/// açmadan bununla gösterilir: önce ilerleme, sonra "Hazır" (Aç / Klasörü göster) veya hata.
public sealed class Toast : Window
{
    private static readonly List<Toast> open = [];
    private readonly TextBlock title = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock message = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
    private readonly ProgressBar bar = new() { Minimum = 0, Maximum = 1, IsIndeterminate = true, Height = 6, Margin = new Thickness(0, 10, 0, 0) };
    private readonly WrapPanel buttons = new() { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
    private readonly DispatcherTimer closeTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    public CancellationTokenSource Cancellation { get; } = new();
    public IReadOnlyList<string> Outputs { get; private set; } = [];

    private Toast(string heading)
    {
        Title = "PanDaF";
        Width = 380; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        FontFamily = new FontFamily("Segoe UI");
        title.Text = heading;
        title.Foreground = message.Foreground = Res("Ink");
        message.Foreground = Res("Muted");
        bar.Foreground = Res("Primary"); bar.Background = Res("Hover"); bar.BorderThickness = new Thickness(0);

        var logo = new Image { Width = 40, Height = 40, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top, Source = Logo };
        var text = new StackPanel();
        text.Children.Add(title); text.Children.Add(message); text.Children.Add(bar); text.Children.Add(buttons);
        var row = new DockPanel();
        DockPanel.SetDock(logo, Dock.Left);
        row.Children.Add(logo); row.Children.Add(text);
        Content = new Border
        {
            Margin = new Thickness(12), Padding = new Thickness(16, 14, 16, 14), CornerRadius = new CornerRadius(14),
            Background = Res("Card"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), Child = row,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = .25 },
        };
        AddButton("İptal", () => { Cancellation.Cancel(); message.Text = "İptal ediliyor…"; });
        closeTimer.Tick += (_, _) => { if (!IsMouseOver) Close(); };
        Closed += (_, _) => { closeTimer.Stop(); open.Remove(this); Restack(); };
        Loaded += (_, _) => Restack();
    }

    public static Toast Open(string heading)
    {
        var toast = new Toast(heading);
        open.Add(toast);
        toast.Show();
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
        bar.Visibility = Visibility.Collapsed;
        buttons.Children.Clear();
        if (outputs.Count == 1) AddButton(Directory.Exists(outputs[0]) ? "Klasörü aç" : "Aç", () => { Shell(outputs[0]); Close(); });
        AddButton("Klasörde göster", () => { Reveal(outputs[0]); Close(); });
        AddButton("Kapat", Close);
        closeTimer.Start();
    }

    public void Fail(string error)
    {
        title.Text = "İşlem yapılamadı";
        message.Text = error;
        message.Foreground = Res("Ink");
        bar.Visibility = Visibility.Collapsed;
        buttons.Children.Clear();
        AddButton("Kapat", Close);
        closeTimer.Interval = TimeSpan.FromSeconds(20);
        closeTimer.Start();
    }

    public void Cancelled() { Close(); }

    private void AddButton(string text, Action click)
    {
        var button = new Button { Content = text, Style = (Style)Application.Current.Resources["GhostButton"], FontSize = 13, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(4, 0, 0, 0) };
        button.Click += (_, _) => click();
        buttons.Children.Add(button);
    }

    // Bildirimleri sağ altta, görev çubuğunun üstünde alt alta diz.
    private static void Restack()
    {
        var area = SystemParameters.WorkArea;
        var bottom = area.Bottom;
        foreach (var toast in open.AsEnumerable().Reverse())
        {
            if (!toast.IsLoaded) continue;
            toast.Left = area.Right - toast.ActualWidth - 4;
            toast.Top = bottom - toast.ActualHeight - 4;
            bottom = toast.Top;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info) { base.OnRenderSizeChanged(info); Restack(); }

    private static void Shell(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private static void Reveal(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private static readonly BitmapImage Logo = LoadLogo();
    private static BitmapImage LoadLogo()
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri("pack://application:,,,/Assets/PanDaF.png");
        image.DecodePixelWidth = 80;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
