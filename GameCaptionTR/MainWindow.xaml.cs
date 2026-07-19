using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using GameCaptionTR.Models;
using GameCaptionTR.Services;
using GameCaptionTR.Views;

namespace GameCaptionTR;

public partial class MainWindow : Window
{
    private const int HotkeyId = 7101;
    private const int WmHotkey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint KeyT = 0x54;
    private const uint WdaExcludeFromCapture = 0x00000011;

    private readonly AppSettings _settings;
    private readonly CaptionPipelineService _pipeline = new();
    private readonly ScreenCaptureService _screenCapture = new();
    private readonly WindowsOcrService _documentOcr = new();
    private readonly TranslationService _documentTranslator = new();
    private OverlayWindow? _overlay;
    private DocumentTranslationWindow? _documentWindow;
    private CancellationTokenSource? _documentCts;
    private HwndSource? _windowSource;
    private bool _overlaySuppressed;
    private bool _documentTranslationBusy;
    private readonly ObservableCollection<string> _languages = new();

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        LoadUiFromSettings();
        WirePipeline();
        SourceInitialized += MainWindow_SourceInitialized;
        Closed += async (_, _) => await ShutdownAsync();
    }

    private void LoadUiFromSettings()
    {
        IntervalBox.Text = _settings.IntervalMs.ToString();
        FontSizeBox.Text = _settings.OverlayFontSize.ToString("0");
        ClickThroughCheck.IsChecked = _settings.ClickThrough;

        foreach (var lang in WindowsOcrService.GetAvailableLanguages())
        {
            _languages.Add(lang);
        }

        SourceLanguageCombo.ItemsSource = _languages;
        if (_languages.Count == 0)
        {
            StatusText.Text = "Windows OCR dil paketi bulunamadı. Ayarlar > Dil bölümünden yükle.";
        }
        else
        {
            var preferred = _languages.FirstOrDefault(l =>
                l.Equals(_settings.SourceLanguage, StringComparison.OrdinalIgnoreCase) ||
                l.StartsWith(_settings.SourceLanguage + "-", StringComparison.OrdinalIgnoreCase));
            SourceLanguageCombo.SelectedItem = preferred ?? _languages.FirstOrDefault(l => l.StartsWith("en", StringComparison.OrdinalIgnoreCase)) ?? _languages[0];
        }

        RegionText.Text = _settings.CaptureWidth > 0
            ? $"{_settings.CaptureX},{_settings.CaptureY} {_settings.CaptureWidth}x{_settings.CaptureHeight}"
            : "Henüz seçilmedi";

        _pipeline.Region = new CaptureRegion
        {
            X = _settings.CaptureX,
            Y = _settings.CaptureY,
            Width = _settings.CaptureWidth,
            Height = _settings.CaptureHeight
        };
        _pipeline.SourceLanguage = NormalizeLanguageTag(SourceLanguageCombo.SelectedItem as string ?? "en");
        _pipeline.TargetLanguage = "tr";
        _pipeline.IntervalMs = _settings.IntervalMs;
    }

    private void WirePipeline()
    {
        _pipeline.StatusChanged += status => Dispatcher.Invoke(() => StatusText.Text = status);
        _pipeline.CaptionChanged += (source, translated) => Dispatcher.Invoke(() =>
        {
            SourcePreview.Text = source;
            TranslatedPreview.Text = translated;
            if (!_overlaySuppressed)
            {
                EnsureOverlay();
                _overlay!.SetCaption(translated);
            }
        });
        _pipeline.CaptionCleared += () => Dispatcher.Invoke(() =>
        {
            SourcePreview.Text = string.Empty;
            TranslatedPreview.Text = string.Empty;
            _overlay?.ClearCaption();
        });
    }

    private void SelectRegion_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        try
        {
            var selector = new RegionSelectorWindow();
            var ok = selector.ShowDialog() == true && selector.SelectedRegion is not null;
            if (!ok)
            {
                return;
            }

            _pipeline.Region = selector.SelectedRegion!;
            _settings.CaptureX = _pipeline.Region.X;
            _settings.CaptureY = _pipeline.Region.Y;
            _settings.CaptureWidth = _pipeline.Region.Width;
            _settings.CaptureHeight = _pipeline.Region.Height;
            _settings.Save();
            RegionText.Text = _pipeline.Region.ToString();
            StatusText.Text = "Bölge kaydedildi.";
        }
        finally
        {
            Show();
            Activate();
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        ApplyRuntimeSettings();
        _overlaySuppressed = false;
        EnsureOverlay();
        _pipeline.Start();
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        await Task.CompletedTask;
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        await _pipeline.StopAsync();
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
    }

    private void ShowOverlay_Click(object sender, RoutedEventArgs e)
    {
        _overlaySuppressed = false;
        EnsureOverlay();
        _overlay!.Activate();
    }

    private async void TranslateScreen_Click(object sender, RoutedEventArgs e)
    {
        await TranslateCursorScreenAsync();
    }

    private void ApplyRuntimeSettings()
    {
        if (int.TryParse(IntervalBox.Text.Trim(), out var interval))
        {
            _settings.IntervalMs = Math.Clamp(interval, 400, 5000);
            IntervalBox.Text = _settings.IntervalMs.ToString();
        }

        if (double.TryParse(FontSizeBox.Text.Trim(), out var font))
        {
            _settings.OverlayFontSize = Math.Clamp(font, 14, 72);
            FontSizeBox.Text = _settings.OverlayFontSize.ToString("0");
        }

        _settings.ClickThrough = ClickThroughCheck.IsChecked == true;
        _settings.SourceLanguage = NormalizeLanguageTag(SourceLanguageCombo.SelectedItem as string ?? "en");
        _settings.Save();

        _pipeline.IntervalMs = _settings.IntervalMs;
        _pipeline.SourceLanguage = _settings.SourceLanguage;
        _pipeline.TargetLanguage = "tr";

        if (_overlay is not null)
        {
            _overlay.SetFontSize(_settings.OverlayFontSize);
            _overlay.SetClickThrough(_settings.ClickThrough);
        }
    }

    private void EnsureOverlay()
    {
        if (_overlay is not null)
        {
            if (!_overlay.IsVisible)
            {
                _overlay.Show();
            }

            return;
        }

        _overlay = new OverlayWindow
        {
            Left = _settings.OverlayLeft,
            Top = _settings.OverlayTop,
            Width = _settings.OverlayWidth
        };
        _overlay.SetFontSize(_settings.OverlayFontSize);
        _overlay.SetClickThrough(_settings.ClickThrough);
        _overlay.HideRequested += () => _overlaySuppressed = true;
        _overlay.LocationChanged += (_, _) =>
        {
            _settings.OverlayLeft = _overlay.Left;
            _settings.OverlayTop = _overlay.Top;
        };
        _overlay.SizeChanged += (_, _) => _settings.OverlayWidth = _overlay.Width;
        _overlay.Closed += (_, _) => _overlay = null;
        _overlay.Show();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowMessageHook);

        SetWindowDisplayAffinity(handle, WdaExcludeFromCapture);
        if (!RegisterHotKey(handle, HotkeyId, ModControl | ModShift, KeyT))
        {
            StatusText.Text = "Ctrl+Shift+T başka bir uygulama tarafından kullanılıyor.";
        }
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            _ = TranslateCursorScreenAsync();
        }

        return IntPtr.Zero;
    }

    private async Task TranslateCursorScreenAsync()
    {
        if (_documentTranslationBusy)
        {
            _documentWindow?.Activate();
            return;
        }

        ApplyRuntimeSettings();
        _documentTranslationBusy = true;
        _documentCts?.Cancel();
        _documentCts?.Dispose();
        _documentCts = new CancellationTokenSource();
        var token = _documentCts.Token;

        EnsureDocumentWindow();
        _documentWindow!.ShowLoading();
        _documentWindow.Show();
        _documentWindow.Activate();
        StatusText.Text = "Farenin bulunduğu ekran okunuyor…";

        try
        {
            using var bitmap = _screenCapture.CaptureCursorMonitor();
            if (bitmap is null)
            {
                throw new InvalidOperationException("Ekran görüntüsü alınamadı.");
            }

            var sourceLanguage = NormalizeLanguageTag(
                SourceLanguageCombo.SelectedItem as string ?? "en");
            var source = await _documentOcr.RecognizeDocumentAsync(
                bitmap,
                sourceLanguage,
                token);

            if (string.IsNullOrWhiteSpace(source))
            {
                _documentWindow.ShowError("Bu ekranda okunabilir metin bulunamadı.");
                StatusText.Text = "Ekranda okunabilir metin bulunamadı.";
                return;
            }

            _documentWindow.ShowError("Metin bulundu, Türkçeye çevriliyor…");
            var translated = await _documentTranslator.TranslateDocumentAsync(
                source,
                sourceLanguage,
                "tr",
                token);

            _documentWindow.ShowResult(source, translated);
            StatusText.Text = "Ekran çevirisi tamamlandı.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _documentWindow?.ShowError("Hata: " + ex.Message);
            StatusText.Text = "Ekran çevirisi başarısız: " + ex.Message;
        }
        finally
        {
            _documentTranslationBusy = false;
        }
    }

    private void EnsureDocumentWindow()
    {
        if (_documentWindow is not null)
        {
            return;
        }

        _documentWindow = new DocumentTranslationWindow();
        _documentWindow.Closed += (_, _) =>
        {
            _documentCts?.Cancel();
            _documentWindow = null;
        };
    }

    private async Task ShutdownAsync()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            UnregisterHotKey(handle, HotkeyId);
        }

        _windowSource?.RemoveHook(WindowMessageHook);
        _documentCts?.Cancel();
        _documentCts?.Dispose();
        _documentTranslator.Dispose();

        if (_overlay is not null)
        {
            _settings.OverlayLeft = _overlay.Left;
            _settings.OverlayTop = _overlay.Top;
            _settings.OverlayWidth = _overlay.Width;
        }

        ApplyRuntimeSettings();
        await _pipeline.StopAsync();
        _pipeline.Dispose();
        _overlay?.Close();
    }

    private static string NormalizeLanguageTag(string tag)
    {
        var dash = tag.IndexOf('-');
        return dash > 0 ? tag[..dash].ToLowerInvariant() : tag.ToLowerInvariant();
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
}
