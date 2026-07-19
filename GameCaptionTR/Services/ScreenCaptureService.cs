using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using GameCaptionTR.Models;

namespace GameCaptionTR.Services;

/// <summary>
/// Oyuna enjekte olmadan yalnızca ekran görüntüsü alır.
/// Bu yaklaşım DLL injection / bellek okuma kullanmaz.
/// </summary>
public sealed class ScreenCaptureService
{
    private const uint MonitorDefaultToNearest = 2;

    public Bitmap? Capture(CaptureRegion region)
    {
        if (!region.IsValid)
        {
            return null;
        }

        var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(region.X, region.Y, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    /// <summary>
    /// Farenin bulunduğu monitörün tamamını yakalar. Böylece çoklu monitörde
    /// kullanıcı hangi ekranı çevirmek istiyorsa imleci o ekrana götürebilir.
    /// </summary>
    public Bitmap? CaptureCursorMonitor()
    {
        if (!GetCursorPos(out var cursor))
        {
            return null;
        }

        var monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return null;
        }

        return Capture(new CaptureRegion
        {
            X = info.Monitor.Left,
            Y = info.Monitor.Top,
            Width = info.Monitor.Right - info.Monitor.Left,
            Height = info.Monitor.Bottom - info.Monitor.Top
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
