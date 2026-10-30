// Adapted from Nashi's OfficeRuntime and OfficeDocuments (https://github.com/orkunisitmak/nashi-app),
// Copyright (c) 2026 Orkun Işıtmak and Nashi contributors, MIT License.
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PanDaF.Core;

/// Microsoft Office bulunmayan bilgisayarlarda Word/PowerPoint/Excel dönüşümleri için
/// LibreOffice kullanılır. Bilgisayarda kurulu LibreOffice varsa o kullanılır; yoksa resmi MSI
/// paketi bir kez indirilir, SHA-256 ile doğrulanır ve yalnızca veri olarak açılır
/// (kurulum yapılmaz, sistem ayarı değiştirilmez).
public sealed class LibreOffice(string root)
{
    private const string Version = "26.8.1";
    private const string Sha256 = "c6298b10cfa1748bcbedad94c62ce771e240e5021bfd6d7a967cd571b04b5312";
    private static readonly Uri[] Sources =
    [
        new($"https://download.documentfoundation.org/libreoffice/stable/{Version}/win/x86_64/LibreOffice_{Version}_Win_x86-64.msi"),
        new($"https://downloadarchive.documentfoundation.org/libreoffice/old/{Version}/win/x86_64/LibreOffice_{Version}_Win_x86-64.msi"),
    ];
    private static readonly TimeSpan ConversionTimeout = TimeSpan.FromMinutes(5);
    internal const string DisabledPythonLoader = "pythonloaderlo.dll.disabled-by-nashi";

    private string Managed => Path.Combine(root, "office-" + Version);

    public bool Ready => Executable is not null;

    // Writer'ın ilk açılışında Python bileşenleri kaydedilirken bazı sistemlerde (Windows 11, tr-TR)
    // süreç hiç bitmiyor. Yalnızca yerel --convert-to filtreleri kullanıldığı için özel kopyada Python
    // yükleyicisi kapatılır. Program Files altındaki LibreOffice'e hiç dokunulmaz.
    internal static bool DisablePythonLoader(string application)
    {
        var loader = Path.Combine(application, "program", "pythonloaderlo.dll");
        try { if (File.Exists(loader)) File.Move(loader, Path.Combine(application, "program", DisabledPythonLoader), true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return !File.Exists(loader);
    }

    public string? Executable
    {
        get
        {
            var safe = !Directory.Exists(Managed) || DisablePythonLoader(Managed);
            string[] managed = [Path.Combine(Managed, "program", "soffice.com"), Path.Combine(Managed, "program", "soffice.exe")];
            var installed = new List<string>();
            foreach (var programFiles in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                if (string.IsNullOrEmpty(programFiles)) continue;
                installed.Add(Path.Combine(programFiles, "LibreOffice", "program", "soffice.com"));
                installed.Add(Path.Combine(programFiles, "LibreOffice", "program", "soffice.exe"));
            }
            string[] candidates = safe ? [.. managed, .. installed] : [.. installed, .. managed];
            return candidates.FirstOrDefault(ToolInstallation.UsableFile);
        }
    }

    public async Task InstallAsync(IProgress<OperationProgress>? progress, CancellationToken token)
    {
        if (Executable is not null) return;
        Directory.CreateDirectory(root);
        var stage = Path.Combine(root, ".office-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var msi = Path.Combine(stage, "office.msi");
            Exception? last = null;
            foreach (var source in Sources)
            {
                try
                {
                    await VerifiedDownload.FetchAsync(source, msi, 700L * 1024 * 1024, p => progress?.Report(new(p * .75, $"LibreOffice indiriliyor… %{Math.Round(p * 100)}")), token);
                    last = null; break;
                }
                catch (Exception error) when (!token.IsCancellationRequested && error is HttpRequestException or TimeoutException or EndOfStreamException) { last = error; }
            }
            if (last is not null) throw new UserFacingException("LibreOffice indirilemedi. İnternet bağlantınızı kontrol edip tekrar deneyin.", last);
            progress?.Report(new(.76, "İndirilen dosya doğrulanıyor…"));
            await using (var file = File.OpenRead(msi))
                if (Convert.ToHexString(await SHA256.HashDataAsync(file, token)).ToLowerInvariant() != Sha256)
                    throw new UserFacingException("İndirilen LibreOffice dosyası doğrulanamadı. Hiçbir şey kurulmadı.");
            var content = Path.Combine(stage, "content"); Directory.CreateDirectory(content);
            await Task.Run(() => MsiImage.Extract(msi, content, progress, token), token);
            var executable = Directory.GetFiles(content, "soffice.exe", SearchOption.AllDirectories).Single(p => Path.GetFileName(Path.GetDirectoryName(p)) == "program");
            var application = Directory.GetParent(Path.GetDirectoryName(executable)!)!.FullName;
            var runtimes = Path.Combine(content, ".native-runtime");
            if (Directory.Exists(runtimes)) foreach (var dll in Directory.EnumerateFiles(runtimes, "*.dll")) File.Copy(dll, Path.Combine(application, "program", Path.GetFileName(dll)), true);
            DisablePythonLoader(application);
            token.ThrowIfCancellationRequested();
            ToolInstallation.Publish(application, Managed, p => ToolInstallation.UsableFile(Path.Combine(p, "program", "soffice.exe")));
            progress?.Report(new(1, "LibreOffice hazır"));
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    /// input dosyasını format'a ("pdf" veya "docx") çevirir ve outputDirectory içine yazar.
    public async Task<string> ConvertAsync(string input, string format, string outputDirectory, CancellationToken token)
    {
        var office = Executable ?? throw new UserFacingException("Bu dönüşüm için LibreOffice gerekli.");
        Directory.CreateDirectory(outputDirectory);
        var job = Path.Combine(Path.GetTempPath(), "PanDaFOffice-" + Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(job, "profile"); var rendered = Path.Combine(job, "out");
        Directory.CreateDirectory(Path.Combine(profile, "user")); Directory.CreateDirectory(rendered);
        try
        {
            // Makroları ve dış bağlantı güncellemelerini kapalı tutan geçici profil.
            await File.WriteAllTextAsync(Path.Combine(profile, "user", "registrymodifications.xcu"), "<?xml version=\"1.0\"?><oor:items xmlns:oor=\"http://openoffice.org/2001/registry\"><item oor:path=\"/org.openoffice.Office.Common/Security/Scripting\"><prop oor:name=\"MacroSecurityLevel\" oor:op=\"fuse\"><value>3</value></prop></item><item oor:path=\"/org.openoffice.Office.Common/Load\"><prop oor:name=\"UpdateLinkMode\" oor:op=\"fuse\"><value>0</value></prop></item></oor:items>", token);
            var original = Path.GetFullPath(input); var ext = FileKinds.Extension(input);
            // Türkçe karakterli veya uzun yollar soffice'i şaşırtmasın: kısa bir kopya üzerinden çalış.
            var copy = Path.Combine(job, "Kaynak." + ext); File.Copy(original, copy); input = copy;
            var spreadsheet = FileKinds.Sheets.Contains(ext); var slides = FileKinds.Slides.Contains(ext);
            var filter = format switch
            {
                "docx" => "docx:Office Open XML Text",
                "xlsx" => "xlsx:Calc MS Excel 2007 XML",
                "pdf" => "pdf:" + (spreadsheet ? "calc_pdf_Export" : slides ? "impress_pdf_Export" : "writer_pdf_Export"),
                _ => throw new ArgumentException("Desteklenmeyen biçim."),
            };
            var args = new List<string> { "-env:UserInstallation=" + new Uri(profile + Path.DirectorySeparatorChar).AbsoluteUri, "--headless", "--nologo", "--nodefault", "--nolockcheck", "--nofirststartwizard", "--norestore" };
            // PDF doğrudan Writer'a açılır (Stirling-PDF'in de kullandığı yol): sayfa düzeni korunur.
            if (ext == "pdf") args.Add("--infilter=writer_pdf_import");
            args.AddRange(["--convert-to", filter, "--outdir", rendered, input]);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(ConversionTimeout);
            ProcessResult result;
            try { result = await ProcessRunner.RunAsync(office, args, limit.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new UserFacingException("Dönüştürme 5 dakikada bitmedi. Tekrar deneyin veya dosyayı küçültün."); }
            var output = Path.Combine(rendered, Path.GetFileNameWithoutExtension(input) + "." + format);
            if (!File.Exists(output) || new FileInfo(output).Length == 0)
                throw new UserFacingException($"“{Path.GetFileName(original)}” dönüştürülemedi. {(result.Error + " " + result.Output).Trim()}".Trim());
            var destination = SafePaths.Unique(outputDirectory, Path.GetFileNameWithoutExtension(original) + "." + format);
            File.Move(output, destination);
            return destination;
        }
        finally { try { if (Directory.Exists(job)) Directory.Delete(job, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    // ----- PDF → düzenlenebilir HTML (LibreOffice bunu Word belgesine çevirir) -----

    public readonly record struct PdfArea(double Left, double Top, double Width, double Height);

    // Metni veya resmi okunamayan sayfalar (taranmış sayfalar gibi) boş çıkmasın diye bildirilir;
    // pageImage bunların görüntüsünü (area null ise tüm sayfa) sağlar.
    public static string PdfHtml(string input, string password, Func<int, PdfArea?, byte[]?>? pageImage, out int[] pagesToRender)
    {
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(input, new() { Password = password });
        var html = new StringBuilder("<!DOCTYPE html><html><head><meta charset=\"utf-8\"/></head><body>"); var render = new List<int>();
        foreach (var page in pdf.GetPages())
        {
            var blocks = new List<(double Top, string Html)>(); var needed = false; var media = page.MediaBox.Bounds; var crop = page.CropBox.Bounds;
            var mappable = page.Rotation.Value == 0 && media.Left == 0 && media.Bottom == 0 && crop.Left == media.Left && crop.Bottom == media.Bottom && crop.Width == media.Width && crop.Height == media.Height;
            foreach (var line in page.GetWords().GroupBy(w => Math.Round(w.BoundingBox.Bottom / 4)).OrderByDescending(g => g.Key))
            {
                var text = new StringBuilder("<p style=\"margin:0 0 5pt\">"); var first = true;
                foreach (var word in line.OrderBy(w => w.BoundingBox.Left))
                {
                    var letter = word.Letters.FirstOrDefault(); var font = letter?.FontName ?? "Arial"; var size = Math.Clamp(letter?.FontSize ?? 12, 1, 300);
                    var style = "font-size:" + size.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "pt;" +
                        (font.Contains("Bold", StringComparison.OrdinalIgnoreCase) ? "font-weight:bold;" : "") +
                        (font.Contains("Italic", StringComparison.OrdinalIgnoreCase) || font.Contains("Oblique", StringComparison.OrdinalIgnoreCase) ? "font-style:italic;" : "");
                    if (!first) text.Append(' '); first = false;
                    text.Append("<span style=\"").Append(style).Append("\">").Append(WebUtility.HtmlEncode(word.Text)).Append("</span>");
                }
                blocks.Add((page.Height - line.Max(w => w.BoundingBox.Top), text.Append("</p>").ToString()));
            }
            foreach (var image in page.GetImages())
            {
                byte[]? data = null; var mime = "image/png";
                if (image.TryGetPng(out var png)) data = png;
                else if (image.RawBytes.Length >= 2 && image.RawBytes[0] == 0xff && image.RawBytes[1] == 0xd8) { data = image.RawBytes.ToArray(); mime = "image/jpeg"; }
                if (data?.Length > 20_000_000) continue;
                if (data is null && mappable) { needed = true; data = pageImage?.Invoke(page.Number, new(image.BoundingBox.Left, page.Height - image.BoundingBox.Top, image.BoundingBox.Width, image.BoundingBox.Height)); }
                if (data is null) continue;
                blocks.Add((page.Height - image.BoundingBox.Top, "<p><img style=\"max-width:100%;width:" + image.BoundingBox.Width.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "pt;height:auto\" src=\"data:" + mime + ";base64," + Convert.ToBase64String(data) + "\"/></p>"));
            }
            if (blocks.Count == 0)
            {
                needed = true;
                if (pageImage?.Invoke(page.Number, null) is { } whole)
                    blocks.Add((0, "<p><img style=\"max-width:100%;width:" + page.Width.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "pt;height:auto\" src=\"data:image/png;base64," + Convert.ToBase64String(whole) + "\"/></p>"));
            }
            if (needed) render.Add(page.Number);
            html.Append("<section style=\"page-break-after:always\">");
            foreach (var block in blocks.OrderBy(b => b.Top)) html.Append(block.Html);
            html.Append("</section>");
        }
        pagesToRender = render.ToArray();
        return SanitizedHtml(html.Append("</body></html>").ToString());
    }

    public static string SanitizedHtml(string html)
    {
        html = Regex.Replace(html, @"<(script|iframe|object|embed)\b[^>]*>[\s\S]*?</\1\s*>", "", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        html = Regex.Replace(html, @"<link\b[^>]*>|<base\b[^>]*>", "", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        html = Regex.Replace(html, "\\s(?:src|background)\\s*=\\s*([\"'])(?!data:)[^\"']*\\1", "", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        return Regex.Replace(html, @"url\s*\([^)]*\)", "none", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
    }

    // ----- MSI paketini kurmadan, yalnızca veri olarak açma (Nashi'den aynen) -----
    private static class MsiImage
    {
        [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiOpenDatabase(string path, IntPtr persistence, out uint database);
        [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiDatabaseOpenView(uint database, string query, out uint view);
        [DllImport("msi.dll")] private static extern uint MsiViewExecute(uint view, uint record);
        [DllImport("msi.dll")] private static extern uint MsiViewFetch(uint view, out uint record);
        [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiRecordGetString(uint record, uint field, StringBuilder buffer, ref uint length);
        [DllImport("msi.dll")] private static extern uint MsiRecordReadStream(uint record, uint field, byte[] buffer, ref uint size);
        [DllImport("msi.dll")] private static extern uint MsiCloseHandle(uint handle);
        private static void Check(uint result) { if (result != 0) throw new IOException("Office paketi açılamadı (Windows Installer kodu " + result + ")."); }
        private static List<string[]> Rows(uint database, string sql, int fields)
        {
            Check(MsiDatabaseOpenView(database, sql, out var view));
            try
            {
                Check(MsiViewExecute(view, 0)); var result = new List<string[]>(); uint code;
                while ((code = MsiViewFetch(view, out var record)) == 0)
                {
                    try { var row = new string[fields]; for (uint n = 1; n <= fields; n++) { var text = new StringBuilder(32768); uint length = 32768; Check(MsiRecordGetString(record, n, text, ref length)); row[n - 1] = text.ToString(); } result.Add(row); }
                    finally { MsiCloseHandle(record); }
                }
                if (code != 259) Check(code);
                return result;
            }
            finally { MsiCloseHandle(view); }
        }
        private static string LongName(string name) => name.Split(':')[0].Split('|')[^1];
        private static int CompareVersion(string first, string second)
        {
            var a = System.Diagnostics.FileVersionInfo.GetVersionInfo(first); var b = System.Diagnostics.FileVersionInfo.GetVersionInfo(second);
            return new Version(Math.Max(0, a.FileMajorPart), Math.Max(0, a.FileMinorPart), Math.Max(0, a.FileBuildPart), Math.Max(0, a.FilePrivatePart))
                .CompareTo(new Version(Math.Max(0, b.FileMajorPart), Math.Max(0, b.FileMinorPart), Math.Max(0, b.FileBuildPart), Math.Max(0, b.FilePrivatePart)));
        }
        public static void Extract(string path, string destination, IProgress<OperationProgress>? progress, CancellationToken token)
        {
            Check(MsiOpenDatabase(path, IntPtr.Zero, out var db));
            try
            {
                var cabinets = Rows(db, "SELECT `Cabinet` FROM `Media`", 1); var unpack = Path.Combine(Path.GetDirectoryName(destination)!, "cabinets"); Directory.CreateDirectory(unpack);
                foreach (var cabinet in cabinets.Where(c => c[0].StartsWith('#')))
                {
                    token.ThrowIfCancellationRequested(); var name = cabinet[0][1..];
                    if (name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))) throw new InvalidDataException("Güvensiz paket adı.");
                    var cab = Path.Combine(unpack, name);
                    Check(MsiDatabaseOpenView(db, "SELECT `Data` FROM `_Streams` WHERE `Name`='" + name + "'", out var view));
                    try
                    {
                        Check(MsiViewExecute(view, 0)); Check(MsiViewFetch(view, out var record));
                        try { using var file = new FileStream(cab, FileMode.CreateNew); var buffer = new byte[65536]; while (true) { uint size = (uint)buffer.Length; Check(MsiRecordReadStream(record, 1, buffer, ref size)); if (size == 0) break; token.ThrowIfCancellationRequested(); file.Write(buffer, 0, (int)size); } }
                        finally { MsiCloseHandle(record); }
                    }
                    finally { MsiCloseHandle(view); }
                    ProcessRunner.RunAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "expand.exe"), [cab, "-F:*", unpack], token).GetAwaiter().GetResult();
                    File.Delete(cab);
                }
                var dirs = Rows(db, "SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`", 3).ToDictionary(r => r[0]);
                var components = Rows(db, "SELECT `Component`, `Directory_` FROM `Component`", 2).ToDictionary(r => r[0], r => r[1]);
                string Relative(string id, int depth = 0)
                {
                    if (depth > 64) throw new InvalidDataException("Office klasör döngüsü.");
                    if (id == "TARGETDIR" || !dirs.TryGetValue(id, out var row)) return "";
                    var parent = Relative(row[1], depth + 1); var name = LongName(row[2]);
                    return name is "." or "" ? parent : Path.Combine(parent, name);
                }
                var files = Rows(db, "SELECT `File`, `Component_`, `FileName` FROM `File`", 3); var index = 0;
                foreach (var row in files)
                {
                    token.ThrowIfCancellationRequested();
                    var source = SafePaths.ArchiveEntry(unpack, row[0]);
                    if (!File.Exists(source)) throw new InvalidDataException("Office paketinde bir dosya eksik.");
                    var relative = Path.Combine(Relative(components[row[1]]), LongName(row[2]));
                    // Paket sistem klasörleri için x86 ve x64 çalışma zamanları içerir. Yalnızca doğrulanmış
                    // x64 DLL'ler uygulamanın yanında tutulur; Windows'a hiçbir şey yazılmaz.
                    var external = relative.Split(Path.DirectorySeparatorChar)[0];
                    if (external is "System64" or "System32" or "System")
                    {
                        if (Path.GetExtension(source).Equals(".dll", StringComparison.OrdinalIgnoreCase) || LongName(row[2]).EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        {
                            using var pe = File.OpenRead(source); using var reader = new BinaryReader(pe);
                            if (pe.Length >= 64 && reader.ReadUInt16() == 0x5a4d)
                            {
                                pe.Position = 60; var offset = reader.ReadUInt32();
                                if (offset <= pe.Length - 6)
                                {
                                    pe.Position = offset;
                                    if (reader.ReadUInt32() == 0x4550 && reader.ReadUInt16() == 0x8664)
                                    {
                                        var runtime = Path.Combine(destination, ".native-runtime"); Directory.CreateDirectory(runtime);
                                        var dll = SafePaths.ArchiveEntry(runtime, LongName(row[2]));
                                        if (!File.Exists(dll) || CompareVersion(source, dll) > 0) File.Copy(source, dll, true);
                                    }
                                }
                            }
                        }
                        continue;
                    }
                    var target = SafePaths.ArchiveEntry(destination, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (File.Exists(target))
                    {
                        using var existing = File.OpenRead(target); using var incoming = File.OpenRead(source);
                        if (!SHA256.HashData(existing).SequenceEqual(SHA256.HashData(incoming))) throw new InvalidDataException("Office paketinde çakışan dosyalar var: " + relative);
                    }
                    else File.Move(source, target);
                    progress?.Report(new(.77 + .22 * ++index / files.Count, "LibreOffice hazırlanıyor…"));
                }
            }
            finally { MsiCloseHandle(db); }
        }
    }
}
