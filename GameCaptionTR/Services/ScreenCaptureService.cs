using System.Drawing;
using System.Drawing.Imaging;
using GameCaptionTR.Models;

namespace GameCaptionTR.Services;

/// <summary>
/// Oyuna enjekte olmadan yalnızca ekran görüntüsü alır.
/// Bu yaklaşım DLL injection / bellek okuma kullanmaz.
/// </summary>
public sealed class ScreenCaptureService
{
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
}
