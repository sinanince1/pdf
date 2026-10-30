using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace PanDaF.Core;

/// Bilgisayarda Microsoft Office kuruluysa Word, PowerPoint ve Excel'in kendi "PDF olarak kaydet"
/// özelliği kullanılır: biçim en doğru şekilde korunur ve ek indirme gerekmez.
/// Uygulama görünmez açılır, makrolar kapalıdır, kullanıcının açık belgelerine dokunulmaz.
[SupportedOSPlatform("windows")]
public static class MicrosoftOffice
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(4);
    private const int MsoAutomationSecurityForceDisable = 3;

    public static bool HasWord => Type.GetTypeFromProgID("Word.Application") is not null;
    public static bool HasPowerPoint => Type.GetTypeFromProgID("PowerPoint.Application") is not null;
    public static bool HasExcel => Type.GetTypeFromProgID("Excel.Application") is not null;

    /// Bu dosya Microsoft Office ile PDF'ye çevrilebilir mi?
    public static bool CanConvert(string path)
    {
        var ext = FileKinds.Extension(path);
        return FileKinds.WordDocs.Contains(ext) ? HasWord : FileKinds.Slides.Contains(ext) ? HasPowerPoint : FileKinds.Sheets.Contains(ext) && HasExcel;
    }

    public static Task ToPdfAsync(string input, string output, CancellationToken token)
    {
        var ext = FileKinds.Extension(input);
        if (FileKinds.WordDocs.Contains(ext)) return Sta(() => WordExport(input, output, pdfToDocx: false), token);
        if (FileKinds.Slides.Contains(ext)) return Sta(() => PowerPointExport(input, output), token);
        if (FileKinds.Sheets.Contains(ext)) return Sta(() => ExcelExport(input, output), token);
        throw new ArgumentException("Desteklenmeyen dosya türü.");
    }

    /// Word, PDF'yi düzenlenebilir bir belgeye kendisi dönüştürür (PDF Reflow).
    public static Task PdfToDocxAsync(string input, string output, CancellationToken token) => Sta(() => WordExport(input, output, pdfToDocx: true), token);

    /// Hazırlanmış HTML'yi Word belgesi olarak kaydeder (taranmış PDF'lerin metin yolu için).
    public static Task HtmlToDocxAsync(string input, string output, CancellationToken token) => Sta(() => WordExport(input, output, pdfToDocx: true), token);

    private static async Task Sta(Action work, CancellationToken token)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { work(); done.TrySetResult(); }
            catch (Exception error) { done.TrySetException(error); }
        }) { IsBackground = true, Name = "Microsoft Office" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await done.Task.WaitAsync(Timeout, token); }
        catch (TimeoutException) { throw new UserFacingException("Microsoft Office 4 dakika içinde yanıt vermedi. Office'te açık bir uyarı penceresi olabilir; kontrol edip tekrar deneyin."); }
        catch (COMException error) { throw new UserFacingException("Microsoft Office bu dosyayı dönüştüremedi: " + error.Message, error); }
        catch (System.Reflection.TargetInvocationException error) when (error.InnerException is COMException com) { throw new UserFacingException("Microsoft Office bu dosyayı dönüştüremedi: " + com.Message, error); }
    }

    private static object Create(string progId)
    {
        var type = Type.GetTypeFromProgID(progId) ?? throw new UserFacingException("Microsoft Office bulunamadı.");
        return Activator.CreateInstance(type) ?? throw new UserFacingException("Microsoft Office başlatılamadı.");
    }

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com);
    }

    private static void WordExport(string input, string output, bool pdfToDocx)
    {
        dynamic? app = null; dynamic? documents = null; dynamic? document = null;
        try
        {
            app = Create("Word.Application");
            app.Visible = false;
            app.DisplayAlerts = 0; // wdAlertsNone
            app.AutomationSecurity = MsoAutomationSecurityForceDisable;
            if (pdfToDocx) SilencePdfConversionPrompt((string)app.Version);
            documents = app.Documents;
            document = documents.Open(FileName: input, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false, Visible: false, NoEncodingDialog: true);
            if (pdfToDocx) document.SaveAs2(FileName: output, FileFormat: 16, AddToRecentFiles: false); // wdFormatDocumentDefault
            else document.ExportAsFixedFormat(OutputFileName: output, ExportFormat: 17, OpenAfterExport: false, OptimizeFor: 0); // wdExportFormatPDF, Print
        }
        finally
        {
            if (document is not null) { try { document.Close(SaveChanges: 0); } catch (COMException) { } Release(document); }
            // Kullanıcının o sırada açık Word belgeleri varsa Word kapatılmaz.
            if (app is not null) { try { if ((int)documents!.Count == 0) app.Quit(SaveChanges: 0); } catch (COMException) { } }
            Release(documents); Release(app);
        }
        RequireOutput(output, input);
    }

    // Word, PDF açarken "Word şimdi PDF'nizi düzenlenebilir belgeye dönüştürecek" diye sorar.
    // Görünmez Word'de bu soru işlemi kilitler; Word'ün kendi seçeneğiyle kapatılır.
    private static void SilencePdfConversionPrompt(string version)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(version) || version.Any(c => !char.IsAsciiDigit(c) && c != '.')) return;
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Microsoft\Office\{version}\Word\Options");
            key?.SetValue("DisableConvertPdfWarning", 1, RegistryValueKind.DWord);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
    }

    private static void PowerPointExport(string input, string output)
    {
        dynamic? app = null; dynamic? presentations = null; dynamic? presentation = null;
        try
        {
            app = Create("PowerPoint.Application");
            app.DisplayAlerts = 1; // ppAlertsNone
            app.AutomationSecurity = MsoAutomationSecurityForceDisable;
            presentations = app.Presentations;
            // ReadOnly, Untitled, WithWindow: msoTrue=-1, msoFalse=0
            presentation = presentations.Open(FileName: input, ReadOnly: -1, Untitled: 0, WithWindow: 0);
            presentation.SaveAs(FileName: output, FileFormat: 32); // ppSaveAsPDF
        }
        finally
        {
            if (presentation is not null) { try { presentation.Close(); } catch (COMException) { } Release(presentation); }
            // PowerPoint tek örnek çalışır; kullanıcının açık sunumu varsa kapatılmaz.
            if (app is not null) { try { if ((int)presentations!.Count == 0) app.Quit(); } catch (COMException) { } }
            Release(presentations); Release(app);
        }
        RequireOutput(output, input);
    }

    private static void ExcelExport(string input, string output)
    {
        dynamic? app = null; dynamic? workbooks = null; dynamic? workbook = null;
        try
        {
            app = Create("Excel.Application");
            app.Visible = false;
            app.DisplayAlerts = false;
            app.AutomationSecurity = MsoAutomationSecurityForceDisable;
            workbooks = app.Workbooks;
            workbook = workbooks.Open(Filename: input, UpdateLinks: 0, ReadOnly: true, AddToMru: false);
            workbook.ExportAsFixedFormat(Type: 0, Filename: output, Quality: 0, IncludeDocProperties: true, IgnorePrintAreas: false, OpenAfterPublish: false); // xlTypePDF
        }
        finally
        {
            if (workbook is not null) { try { workbook.Close(SaveChanges: false); } catch (COMException) { } Release(workbook); }
            if (app is not null) { try { if ((int)workbooks!.Count == 0) app.Quit(); } catch (COMException) { } }
            Release(workbooks); Release(app);
        }
        RequireOutput(output, input);
    }

    private static void RequireOutput(string output, string input)
    {
        if (!File.Exists(output) || new FileInfo(output).Length == 0)
            throw new UserFacingException($"Microsoft Office “{Path.GetFileName(input)}” dosyasını dönüştüremedi.");
    }
}
