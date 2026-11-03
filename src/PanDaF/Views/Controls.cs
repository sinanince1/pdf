using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using SkiaSharp;
using PanDaF.Core;

namespace PanDaF.Views;

internal static class Ui
{
    public static IBrush Brush(string key) => (IBrush)Application.Current!.FindResource(key)!;
    public static Geometry Icon(string key) => (Geometry)Application.Current!.FindResource(key)!;

    public static PathIcon IconControl(string key, double size = 16, IBrush? color = null)
    {
        var icon = new PathIcon { Data = Icon(key), Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        if (color is not null) icon.Foreground = color;
        return icon;
    }

    public static Button IconButton(string iconKey, string tip, EventHandler<RoutedEventArgs> click, bool enabled = true)
    {
        var button = new Button { Content = IconControl(iconKey, 15), IsEnabled = enabled };
        button.Classes.Add("icon");
        ToolTip.SetTip(button, tip);
        button.Click += click;
        return button;
    }

    public static StackPanel IconText(string iconKey, string text, double size = 15)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(IconControl(iconKey, size));
        panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    public static string IconFor(string path) => FileKinds.Extension(path) switch
    {
        "pdf" => "IconPdf",
        var e when FileKinds.Images.Contains(e) => "IconImage",
        var e when FileKinds.Sheets.Contains(e) => "IconTable",
        _ => "IconWord",
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

    public static IBrush BrushOf(string hex) => new SolidColorBrush(Color.Parse(hex));
}

/// SkiaSharp bitmap'lerini Avalonia'nın çizebileceği bitmap'e çevirir.
internal static class Pictures
{
    public static Bitmap ToBitmap(SKBitmap source)
    {
        using var converted = source.ColorType == SKColorType.Bgra8888 && source.AlphaType == SKAlphaType.Premul ? null : source.Copy(SKColorType.Bgra8888);
        var bitmap = converted ?? source;
        var result = new WriteableBitmap(new PixelSize(bitmap.Width, bitmap.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var frame = result.Lock())
        {
            var rowBytes = bitmap.Width * 4;
            for (var y = 0; y < bitmap.Height; y++)
                unsafe { Buffer.MemoryCopy((byte*)bitmap.GetPixels() + y * bitmap.RowBytes, (byte*)frame.Address + y * frame.RowBytes, rowBytes, rowBytes); }
        }
        return result;
    }

    public static Bitmap FromPng(byte[] png) { using var stream = new MemoryStream(png); return new Bitmap(stream); }

    public static byte[] Asset(string name)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://PanDaF/Assets/{name}"));
        using var memory = new MemoryStream(); stream.CopyTo(memory); return memory.ToArray();
    }
}

/// Dosya seçme pencereleri.
internal static class Pick
{
    public static readonly FilePickerFileType Pdf = new("PDF dosyaları") { Patterns = ["*.pdf"] };
    public static readonly FilePickerFileType Images = new("Resimler") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.jfif", "*.bmp", "*.gif", "*.tif", "*.tiff", "*.webp", "*.heic"] };
    public static readonly FilePickerFileType Convertible = new("Belgeler ve resimler") { Patterns = ["*.doc", "*.docx", "*.odt", "*.rtf", "*.ppt", "*.pptx", "*.pps", "*.ppsx", "*.odp", "*.xls", "*.xlsx", "*.ods", "*.png", "*.jpg", "*.jpeg", "*.jfif", "*.bmp", "*.gif", "*.tif", "*.tiff", "*.webp", "*.heic"] };

    public static async Task<string[]> Files(Visual owner, FilePickerFileType filter, bool multiple)
    {
        var top = TopLevel.GetTopLevel(owner);
        if (top is null) return [];
        var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = multiple, Title = "Dosya seçin", FileTypeFilter = [filter] });
        return picked.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Cast<string>().ToArray();
    }
}

/// Pencereye dosya sürükleyip bırakmayı etkinleştirir. Klasör bırakılırsa içindeki dosyalar alınır.
internal static class DropHelper
{
    public static void Attach(Control target, Action<string[]> onFiles)
    {
        DragDrop.SetAllowDrop(target, true);
        target.AddHandler(DragDrop.DragOverEvent, (_, e) => { e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; });
        target.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var files = Paths(e.Data);
            if (files.Length == 0) return;
            e.Handled = true;
            onFiles(files);
        });
    }

    public static string[] Paths(IDataObject data)
    {
        var items = data.GetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Cast<string>() ?? [];
        return Expand(items);
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
    private readonly Border frame;
    private readonly FilePickerFileType filter;
    private readonly bool multiple;
    public event Action<string[]>? FilesChosen;

    public DropZone(string title, string subtitle, FilePickerFileType filter, bool multiple)
    {
        this.filter = filter; this.multiple = multiple;
        var circle = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(32), Background = Ui.Brush("PrimarySoft"), Margin = new Thickness(0, 0, 0, 14), HorizontalAlignment = HorizontalAlignment.Center, Child = Ui.IconControl("IconUpload", 26, Ui.Brush("Primary")) };
        var button = new Button { Content = multiple ? "Dosyaları seç" : "Dosya seç", Margin = new Thickness(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        button.Classes.Add("primary");
        button.Click += async (_, _) => await Browse();
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 36, 24, 36) };
        stack.Children.Add(circle);
        stack.Children.Add(new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock { Text = subtitle, Foreground = Ui.Brush("Muted"), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        stack.Children.Add(button);
        frame = new Border { MinHeight = 260, BorderBrush = Ui.Brush("Line"), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(18), Background = Ui.Brush("Card"), Child = stack };
        Content = frame;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, (_, e) => { if (e.Data.Contains(DataFormats.Files)) Highlight(true); });
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => Highlight(false));
        AddHandler(DragDrop.DropEvent, (_, _) => Highlight(false));
    }

    private void Highlight(bool on)
    {
        frame.BorderBrush = Ui.Brush(on ? "Primary" : "Line");
        frame.Background = Ui.Brush(on ? "PrimarySoft" : "Card");
    }

    public async Task Browse()
    {
        var files = await Pick.Files(this, filter, multiple);
        if (files.Length > 0) FilesChosen?.Invoke(files);
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
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
            var number = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = Ui.Brush("Hover"), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = (i + 1).ToString(), FontSize = 13, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            grid.Children.Add(number);
            var icon = Ui.IconControl(Ui.IconFor(path), 20, Ui.Brush("Primary")); icon.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetColumn(icon, 1); grid.Children.Add(icon);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { Text = Path.GetFileName(path), FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(name, path);
            text.Children.Add(name);
            var detail = new TextBlock { Text = Ui.Kind(path), FontSize = 13, Foreground = Ui.Brush("Muted") };
            text.Children.Add(detail);
            Grid.SetColumn(text, 2); grid.Children.Add(text);
            _ = Describe(path, detail);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (Reorderable)
            {
                buttons.Children.Add(Ui.IconButton("IconUp", "Yukarı taşı", (_, _) => Move(index, -1), index > 0));
                buttons.Children.Add(Ui.IconButton("IconDown", "Aşağı taşı", (_, _) => Move(index, 1), index < Files.Count - 1));
            }
            buttons.Children.Add(Ui.IconButton("IconClose", "Listeden kaldır", (_, _) => Remove(index)));
            Grid.SetColumn(buttons, 3); grid.Children.Add(buttons);

            rows.Children.Add(new Border
            {
                Background = Ui.Brush("Card"), BorderBrush = Ui.Brush("Line"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10, 8, 10), Margin = new Thickness(0, 0, 0, 8), Child = grid,
            });
        }
        Changed?.Invoke();
    }

    private async Task Describe(string path, TextBlock target)
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

/// Seçenek kartı: RadioButton + çerçeve; seçiliyken çerçeve vurgulanır.
internal static class OptionCards
{
    public static void Bind(RadioButton radio, Border border)
    {
        void Update() => border.Classes.Set("selected", radio.IsChecked == true);
        radio.IsCheckedChanged += (_, _) => Update();
        border.PointerPressed += (_, _) => radio.IsChecked = true;
        Update();
    }
}
