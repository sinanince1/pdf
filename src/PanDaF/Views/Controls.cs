using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PanDaF.Core;

namespace PanDaF.Views;

internal static class Ui
{
    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    public static Style Style(string key) => (Style)Application.Current.Resources[key];
    public static FontFamily IconFont => (FontFamily)Application.Current.Resources["IconFont"];

    public static TextBlock Icon(string glyph, double size, Brush? color = null) => new()
    {
        Text = glyph, FontFamily = IconFont, FontSize = size, Foreground = color ?? Brush("Ink"),
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
    };

    public static Button IconButton(string glyph, string tip, RoutedEventHandler click, bool enabled = true)
    {
        var button = new Button { Content = glyph, Style = Style("IconButton"), ToolTip = tip, IsEnabled = enabled };
        AutomationName(button, tip);
        button.Click += click;
        return button;
    }

    public static void AutomationName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);

    public static string Glyph(string path) => FileKinds.Extension(path) switch
    {
        "pdf" => "",
        var e when FileKinds.Images.Contains(e) => "",
        var e when FileKinds.Slides.Contains(e) => "",
        var e when FileKinds.Sheets.Contains(e) => "",
        _ => "",
    };

    public static string Kind(string path) => FileKinds.Extension(path) switch
    {
        "pdf" => "PDF",
        var e when FileKinds.Images.Contains(e) => "Resim",
        var e when FileKinds.Slides.Contains(e) => "PowerPoint sunusu",
        var e when FileKinds.Sheets.Contains(e) => "Excel tablosu",
        var e when FileKinds.WordDocs.Contains(e) => "Word belgesi",
        _ => "Dosya",
    };
}

/// Pencereye dosya sürükleyip bırakmayı etkinleştirir. Klasör bırakılırsa içindeki dosyalar alınır.
internal static class DropHelper
{
    public static void Attach(UIElement target, Action<string[]> onFiles)
    {
        target.AllowDrop = true;
        target.DragOver += (_, e) =>
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        };
        target.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] items) return;
            e.Handled = true;
            var files = Expand(items);
            if (files.Length > 0) onFiles(files);
        };
    }

    public static string[] Expand(IEnumerable<string> items)
    {
        var result = new List<string>();
        foreach (var item in items)
        {
            if (File.Exists(item)) result.Add(item);
            else if (Directory.Exists(item))
            {
                try { result.AddRange(Directory.GetFiles(item).Order(StringComparer.CurrentCultureIgnoreCase)); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        return result.ToArray();
    }
}

/// Kesikli çizgili "dosyaları buraya bırakın" alanı.
internal sealed class DropZone : UserControl
{
    private readonly System.Windows.Shapes.Rectangle frame;
    private readonly string filter;
    private readonly bool multiple;
    public event Action<string[]>? FilesChosen;

    public DropZone(string title, string subtitle, string filter, bool multiple)
    {
        this.filter = filter; this.multiple = multiple;
        frame = new System.Windows.Shapes.Rectangle
        {
            Stroke = Ui.Brush("Line"), StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 6, 4 }, RadiusX = 18, RadiusY = 18,
            Fill = Ui.Brush("Card"), SnapsToDevicePixels = true,
        };
        var circle = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(32), Background = Ui.Brush("PrimarySoft"), Margin = new Thickness(0, 0, 0, 14), Child = Ui.Icon("", 26, Ui.Brush("Primary")) };
        var button = new Button { Content = multiple ? "Dosyaları seç" : "Dosya seç", Style = Ui.Style("PrimaryButton"), Margin = new Thickness(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        button.Click += (_, _) => Browse();
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 36, 24, 36) };
        stack.Children.Add(circle);
        stack.Children.Add(new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock { Text = subtitle, Foreground = Ui.Brush("Muted"), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        stack.Children.Add(button);
        var grid = new Grid { MinHeight = 260 };
        grid.Children.Add(frame);
        grid.Children.Add(stack);
        Content = grid;
        AllowDrop = true;
        DragEnter += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) Highlight(true); };
        DragLeave += (_, _) => Highlight(false);
        Drop += (_, _) => Highlight(false);
    }

    private void Highlight(bool on)
    {
        frame.Stroke = Ui.Brush(on ? "Primary" : "Line");
        frame.Fill = Ui.Brush(on ? "PrimarySoft" : "Card");
    }

    public void Browse()
    {
        var files = Pick(filter, multiple);
        if (files.Length > 0) FilesChosen?.Invoke(files);
    }

    public static string[] Pick(string filter, bool multiple)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = filter, Multiselect = multiple, Title = "Dosya seçin" };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileNames : [];
    }
}

/// Seçilen dosyaların listesi: sıra numarası, ad, ayrıntı, yukarı/aşağı/kaldır.
internal sealed class FileList : UserControl
{
    private readonly StackPanel rows = new();
    private readonly Dictionary<string, string> details = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Files { get; } = [];
    public bool Reorderable { get; init; } = true;
    public event Action? Changed;

    public FileList() { Content = rows; }

    public void Add(IEnumerable<string> paths)
    {
        foreach (var path in paths.Select(p => Path.GetFullPath(p)))
            if (!Files.Contains(path, StringComparer.OrdinalIgnoreCase)) Files.Add(path);
        Refresh();
    }

    public void Clear() { Files.Clear(); Refresh(); }

    private void Move(int index, int delta)
    {
        var target = index + delta;
        if (target < 0 || target >= Files.Count) return;
        (Files[index], Files[target]) = (Files[target], Files[index]);
        Refresh();
    }

    private void Remove(int index) { Files.RemoveAt(index); Refresh(); }

    private void Refresh()
    {
        rows.Children.Clear();
        for (var i = 0; i < Files.Count; i++)
        {
            var index = i; var path = Files[i];
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var number = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = Ui.Brush("Hover"), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = (i + 1).ToString(), FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            grid.Children.Add(number);
            var icon = Ui.Icon(Ui.Glyph(path), 20, Ui.Brush("Primary")); icon.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetColumn(icon, 1); grid.Children.Add(icon);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = Path.GetFileName(path), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = path });
            var detail = new TextBlock { Text = Ui.Kind(path), FontSize = 13, Foreground = Ui.Brush("Muted") };
            text.Children.Add(detail);
            Grid.SetColumn(text, 2); grid.Children.Add(text);
            Describe(path, detail);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (Reorderable)
            {
                buttons.Children.Add(Ui.IconButton("", "Yukarı taşı", (_, _) => Move(index, -1), index > 0));
                buttons.Children.Add(Ui.IconButton("", "Aşağı taşı", (_, _) => Move(index, 1), index < Files.Count - 1));
            }
            buttons.Children.Add(Ui.IconButton("", "Listeden kaldır", (_, _) => Remove(index)));
            Grid.SetColumn(buttons, 3); grid.Children.Add(buttons);

            rows.Children.Add(new Border
            {
                Background = Ui.Brush("Card"), BorderBrush = Ui.Brush("Line"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10, 8, 10), Margin = new Thickness(0, 0, 0, 8), Child = grid,
            });
        }
        Changed?.Invoke();
    }

    private async void Describe(string path, TextBlock target)
    {
        if (!FileKinds.IsPdf(path)) return;
        if (!details.TryGetValue(path, out var text))
        {
            try { var count = await Task.Run(() => PdfPages.PageCount(path)); text = $"PDF · {count} sayfa"; }
            catch (UserFacingException) { text = "PDF · açılamadı (şifreli veya bozuk olabilir)"; }
            details[path] = text;
        }
        target.Text = text;
    }
}
