using System.Drawing;
using GameCaptionTR.Models;

namespace GameCaptionTR.Services;

public sealed class CaptionPipelineService : IDisposable
{
    private static readonly TimeSpan IdleClearAfter = TimeSpan.FromSeconds(5);

    private readonly ScreenCaptureService _capture = new();
    private readonly WindowsOcrService _ocr = new();
    private readonly TranslationService _translator = new();
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _busy;

    public CaptureRegion Region { get; set; } = new();
    public string SourceLanguage { get; set; } = "en";
    public string TargetLanguage { get; set; } = "tr";
    public int IntervalMs { get; set; } = 900;
    public TranslationMode TranslationMode { get; set; } = TranslationMode.Auto;

    public bool IsRunning { get; private set; }

    public event Action<string>? StatusChanged;
    public event Action<string, string>? CaptionChanged;
    public event Action? CaptionCleared;

    private string? _lastTranslated;
    private string? _lastSource;
    private DateTime _lastSubtitleUtc = DateTime.MinValue;
    private bool _panelCleared = true;

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        if (!Region.IsValid)
        {
            StatusChanged?.Invoke("Önce altyazı bölgesini seç.");
            return;
        }

        _cts = new CancellationTokenSource();
        IsRunning = true;
        _lastSubtitleUtc = DateTime.UtcNow;
        _panelCleared = true;
        StatusChanged?.Invoke("Canlı çeviri çalışıyor.");
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (!IsRunning || _cts is null)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            if (_loop is not null)
            {
                await _loop;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
            _loop = null;
            StatusChanged?.Invoke("Durduruldu.");
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await TickAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke("Hata: " + ex.Message);
            }

            await Task.Delay(Math.Clamp(IntervalMs, 400, 5000), token);
        }
    }

    private async Task TickAsync(CancellationToken token)
    {
        lock (_gate)
        {
            if (_busy)
            {
                return;
            }

            _busy = true;
        }

        try
        {
            using var bitmap = _capture.Capture(Region);
            if (bitmap is null)
            {
                await MaybeClearIfIdleAsync();
                return;
            }

            var sourceText = await _ocr.RecognizeAsync(bitmap, SourceLanguage, token);
            if (string.IsNullOrWhiteSpace(sourceText) || sourceText.Length < 2)
            {
                await MaybeClearIfIdleAsync();
                return;
            }

            // Overlay yakalandıysa: son Türkçe/orijinal metni tekrar işleme
            if ((!string.IsNullOrEmpty(_lastTranslated) && ContainsIgnoreCase(sourceText, _lastTranslated)) ||
                (!string.IsNullOrEmpty(_lastSource) && ContainsIgnoreCase(sourceText, _lastSource)))
            {
                _lastSubtitleUtc = DateTime.UtcNow;
                return;
            }

            _translator.Mode = TranslationMode;

            var translated = await _translator.TranslateAsync(sourceText, SourceLanguage, TargetLanguage, token);
            if (string.IsNullOrWhiteSpace(translated))
            {
                await MaybeClearIfIdleAsync();
                return;
            }

            _lastSource = sourceText;
            _lastTranslated = translated;
            _lastSubtitleUtc = DateTime.UtcNow;
            _panelCleared = false;
            CaptionChanged?.Invoke(sourceText, translated);
        }
        finally
        {
            lock (_gate)
            {
                _busy = false;
            }
        }
    }

    private Task MaybeClearIfIdleAsync()
    {
        if (_panelCleared)
        {
            return Task.CompletedTask;
        }

        if (DateTime.UtcNow - _lastSubtitleUtc < IdleClearAfter)
        {
            return Task.CompletedTask;
        }

        _panelCleared = true;
        _lastTranslated = null;
        _lastSource = null;
        CaptionCleared?.Invoke();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _translator.Dispose();
    }

    private static bool ContainsIgnoreCase(string haystack, string needle)
    {
        if (needle.Length < 8)
        {
            return haystack.Equals(needle, StringComparison.OrdinalIgnoreCase);
        }

        var sample = needle.Length > 40 ? needle[..40] : needle;
        return haystack.Contains(sample, StringComparison.OrdinalIgnoreCase);
    }
}
