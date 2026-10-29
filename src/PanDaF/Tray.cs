using Forms = System.Windows.Forms;

namespace PanDaF;

/// Sistem tepsisindeki (saatin yanındaki) panda simgesi. Pencere kapatılınca PanDaF burada
/// çalışmaya devam eder; Shift paneli bu sayede her zaman hazırdır.
public sealed class Tray : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ToolStripMenuItem shiftItem, startupItem;

    public Tray(Action open, Func<bool> shiftPanel, Action<bool> setShiftPanel, Func<bool> startup, Action<bool> setStartup, Action exit)
    {
        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false, Font = new System.Drawing.Font("Segoe UI", 10f) };
        var openItem = new Forms.ToolStripMenuItem("PanDaF'ı aç") { Font = new System.Drawing.Font("Segoe UI", 10f, System.Drawing.FontStyle.Bold) };
        openItem.Click += (_, _) => open();
        shiftItem = new Forms.ToolStripMenuItem("Shift paneli") { CheckOnClick = true };
        shiftItem.Click += (_, _) => setShiftPanel(shiftItem.Checked);
        startupItem = new Forms.ToolStripMenuItem("Bilgisayar açılınca başlat") { CheckOnClick = true };
        startupItem.Click += (_, _) => setStartup(startupItem.Checked);
        var exitItem = new Forms.ToolStripMenuItem("PanDaF'tan çık");
        exitItem.Click += (_, _) => exit();
        menu.Items.AddRange([openItem, new Forms.ToolStripSeparator(), shiftItem, startupItem, new Forms.ToolStripSeparator(), exitItem]);
        menu.Opening += (_, _) => { shiftItem.Checked = shiftPanel(); startupItem.Checked = startup(); };

        icon = new Forms.NotifyIcon
        {
            Text = "PanDaF · dosyaları sürüklerken Shift'e basın",
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) open(); };
    }

    public void Hint(string title, string text) => icon.ShowBalloonTip(6000, title, text, Forms.ToolTipIcon.Info);

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && System.Drawing.Icon.ExtractAssociatedIcon(exe) is { } extracted) return extracted;
        }
        catch (Exception error) when (error is ArgumentException or IOException) { }
        return System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
    }
}
