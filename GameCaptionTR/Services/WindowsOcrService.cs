using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace GameCaptionTR.Services;

/// <summary>
/// Windows yerleşik OCR motorunu kullanır (yerel).
/// </summary>
public sealed class WindowsOcrService
{
    public async Task<string> RecognizeAsync(Bitmap bitmap, string languageTag, CancellationToken cancellationToken)
    {
        var engine = CreateEngine(languageTag);
        if (engine is null)
        {
            throw new InvalidOperationException(
                $"Windows OCR için '{languageTag}' dil paketi bulunamadı. " +
                "Ayarlar > Zaman ve dil > Dil ve bölge bölümünden OCR dil paketini yükleyin.");
        }

        using var softwareBitmap = await ToSoftwareBitmapAsync(bitmap, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var result = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken);
        return Normalize(result.Text);
    }

    public async Task<string> RecognizeDocumentAsync(
        Bitmap bitmap,
        string languageTag,
        CancellationToken cancellationToken)
    {
        var engine = CreateEngine(languageTag);
        if (engine is null)
        {
            throw new InvalidOperationException(
                $"Windows OCR için '{languageTag}' dil paketi bulunamadı. " +
                "Ayarlar > Zaman ve dil > Dil ve bölge bölümünden OCR dil paketini yükleyin.");
        }

        using var prepared = ResizeForDocumentOcr(bitmap);
        using var softwareBitmap = await ToSoftwareBitmapAsync(prepared, cancellationToken);
        var result = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken);

        return string.Join(
            Environment.NewLine,
            result.Lines
                .Select(line => line.Text.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    public static IReadOnlyList<string> GetAvailableLanguages()
    {
        return OcrEngine.AvailableRecognizerLanguages
            .Select(l => l.LanguageTag)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static OcrEngine? CreateEngine(string languageTag)
    {
        var requested = OcrEngine.AvailableRecognizerLanguages
            .FirstOrDefault(l => l.LanguageTag.Equals(languageTag, StringComparison.OrdinalIgnoreCase)
                                 || l.LanguageTag.StartsWith(languageTag + "-", StringComparison.OrdinalIgnoreCase));

        if (requested is not null)
        {
            return OcrEngine.TryCreateFromLanguage(requested);
        }

        return OcrEngine.TryCreateFromUserProfileLanguages();
    }

    private static Bitmap ResizeForDocumentOcr(Bitmap source)
    {
        var limit = Math.Max(1000, (int)OcrEngine.MaxImageDimension - 16);
        var largest = Math.Max(source.Width, source.Height);
        if (largest <= limit)
        {
            return new Bitmap(source);
        }

        var scale = limit / (double)largest;
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var resized = new Bitmap(width, height, PixelFormat.Format32bppArgb);

        using var graphics = Graphics.FromImage(resized);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, 0, 0, width, height);
        return resized;
    }

    private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(Bitmap bitmap, CancellationToken cancellationToken)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var rect = new Rectangle(0, 0, width, height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        try
        {
            var stride = Math.Abs(data.Stride);
            var source = new byte[stride * height];
            Marshal.Copy(data.Scan0, source, 0, source.Length);

            var packedStride = width * 4;
            var packed = source;
            if (stride != packedStride)
            {
                packed = new byte[packedStride * height];
                for (var y = 0; y < height; y++)
                {
                    System.Buffer.BlockCopy(source, y * stride, packed, y * packedStride, packedStride);
                }
            }

            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, stream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                (uint)width,
                (uint)height,
                96,
                96,
                packed);

            await encoder.FlushAsync().AsTask(cancellationToken);
            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream);
            return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var cleaned = text
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        while (cleaned.Contains("  ", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("  ", " ", StringComparison.Ordinal);
        }

        return cleaned;
    }
}
