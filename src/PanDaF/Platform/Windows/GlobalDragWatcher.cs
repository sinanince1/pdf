#if WINDOWS
// Nashi'nin "sürüklerken tuşa bas, çark açılsın" yaklaşımından uyarlanmıştır
// (https://github.com/orkunisitmak/nashi-app, Copyright (c) 2026 Orkun Işıtmak and Nashi contributors, MIT).
using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace PanDaF;

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
        var handle = owner.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle != IntPtr.Zero && GetForegroundWindow() == handle && owner.IsVisible) { PanelLog.Write("Sürükleme PanDaF penceresinin içinde; panel açılmadı."); return; }
        PanelLog.Write($"Shift ile sürükleme algılandı, panel açılıyor ({point.X},{point.Y}).");
        wheel = new PandaWheel((id, files) => owner.QuickAction(id, files));
        wheel.Show();
        wheel.PlaceAt(point.X, point.Y);
    }

    private void Reset()
    {
        wheel?.Close(); wheel = null; down = false; opened = false;
    }

    public void Dispose() { timer.Stop(); wheel?.Close(); }
}
#endif
