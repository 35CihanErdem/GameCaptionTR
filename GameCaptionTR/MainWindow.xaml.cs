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
    private const uint WdaNone = 0x00000000;
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
        Loaded += (_, _) =>
        {
            if (!ShowSafetyDisclaimerIfNeeded(force: false))
            {
                return;
            }

            // RDP otomatik algılandıysa uzak erişim modunu aç
            if (OverlayWindow.DetectRemoteSession())
            {
                _settings.RemoteAccessMode = true;
                RemoteAccessCheck.IsChecked = true;
            }

            ApplyMainWindowCaptureAffinity();
            EnsureOverlay();

            Show();
            WindowState = WindowState.Normal;
            Activate();

            StatusText.Text = UseRemoteCompatibleOverlay()
                ? "Uzak erişim modu açık — panel + menü uzaktan görünür."
                : "Çeviri paneli açıldı. Uzaktan bağlanacaksan 'Uzak erişim modu'nu aç.";
        };
        Closed += async (_, _) => await ShutdownAsync();
    }

    private void LoadUiFromSettings()
    {
        IntervalBox.Text = _settings.IntervalMs.ToString();
        FontSizeBox.Text = _settings.OverlayFontSize.ToString("0");
        ClickThroughCheck.IsChecked = _settings.ClickThrough;
        ShowInCapturesCheck.IsChecked = _settings.ShowOverlayInCaptures;
        RemoteAccessCheck.IsChecked = _settings.RemoteAccessMode || OverlayWindow.DetectRemoteSession();
        ShowInCapturesCheck.Checked += (_, _) => ApplyCaptureVisibility();
        ShowInCapturesCheck.Unchecked += (_, _) => ApplyCaptureVisibility();
        RemoteAccessCheck.Checked += (_, _) => ApplyRemoteAccessMode();
        RemoteAccessCheck.Unchecked += (_, _) => ApplyRemoteAccessMode();

        TranslationModeCombo.Items.Add("Otomatik (önerilen)");
        TranslationModeCombo.Items.Add("Çevrimiçi");
        TranslationModeCombo.Items.Add("Çevrimdışı");
        TranslationModeCombo.SelectedIndex = _settings.TranslationModeSetting switch
        {
            "Online" => 1,
            "Offline" => 2,
            _ => 0
        };

        _documentTranslator.StatusChanged += msg => Dispatcher.Invoke(() => StatusText.Text = msg);
        UpdateOfflineModelStatus();

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
        ApplyTranslationModeFromUi();
    }

    private void TranslationModeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        ApplyTranslationModeFromUi();
        _settings.TranslationModeSetting = GetSelectedTranslationModeSetting();
        _settings.Save();
        UpdateOfflineModelStatus();
    }

    private async void DownloadOfflineModel_Click(object sender, RoutedEventArgs e)
    {
        DownloadOfflineModelButton.IsEnabled = false;
        StatusText.Text = "Çevrimdışı model indiriliyor…";
        try
        {
            _documentTranslator.Mode = TranslationMode.Offline;
            await _documentTranslator.DownloadOfflineModelAsync(CancellationToken.None);
            UpdateOfflineModelStatus();
            StatusText.Text = "Çevrimdışı model hazır. İnternetsiz oynayabilirsin.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Model indirilemedi: " + ex.Message;
        }
        finally
        {
            DownloadOfflineModelButton.IsEnabled = true;
        }
    }

    private void UpdateOfflineModelStatus()
    {
        if (_documentTranslator.IsOfflineModelInstalled)
        {
            OfflineModelStatusText.Text = "Çevrimdışı model kurulu — İngilizce→Türkçe, internet gerekmez.";
            DownloadOfflineModelButton.Content = "Model güncelle / yeniden indir";
        }
        else
        {
            OfflineModelStatusText.Text = "Çevrimdışı mod için modeli bir kez indir (~530 MB).";
            DownloadOfflineModelButton.Content = "Çevrimdışı modeli indir (~530MB)";
        }
    }

    private void ApplyTranslationModeFromUi()
    {
        var mode = ParseTranslationMode(GetSelectedTranslationModeSetting());
        _pipeline.TranslationMode = mode;
        _documentTranslator.Mode = mode;
    }

    private string GetSelectedTranslationModeSetting() => TranslationModeCombo.SelectedIndex switch
    {
        1 => "Online",
        2 => "Offline",
        _ => "Auto"
    };

    private static TranslationMode ParseTranslationMode(string setting) => setting switch
    {
        "Online" => TranslationMode.Online,
        "Offline" => TranslationMode.Offline,
        _ => TranslationMode.Auto
    };

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

    private void ShowSafetyDisclaimer_Click(object sender, RoutedEventArgs e)
    {
        ShowSafetyDisclaimerIfNeeded(force: true);
    }

    /// <summary>
    /// İlk açılış veya Başlat öncesi güvenlik uyarısı. false = kullanıcı reddetti / kabul etmedi.
    /// </summary>
    private bool ShowSafetyDisclaimerIfNeeded(bool force)
    {
        if (!force && _settings.SafetyDisclaimerAccepted)
        {
            return true;
        }

        var dialog = new SafetyDisclaimerWindow
        {
            Owner = this
        };
        dialog.SetInformationalOnly(force && _settings.SafetyDisclaimerAccepted);

        var ok = dialog.ShowDialog() == true && (dialog.Accepted || dialog.InformationalOnly);
        if (!ok)
        {
            Application.Current.Shutdown();
            return false;
        }

        if (!dialog.InformationalOnly)
        {
            _settings.SafetyDisclaimerAccepted = true;
            if (dialog.DontShowAgain)
            {
                _settings.SafetyDisclaimerDismissed = true;
            }

            _settings.Save();
        }

        return true;
    }

    private bool EnsureSafetyAccepted()
    {
        if (_settings.SafetyDisclaimerAccepted)
        {
            return true;
        }

        StatusText.Text = "Devam etmek için güvenlik uyarısını kabul etmelisin.";
        return ShowSafetyDisclaimerIfNeeded(force: true);
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
        if (!EnsureSafetyAccepted())
        {
            return;
        }

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
        if (!EnsureSafetyAccepted())
        {
            return;
        }

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
        _settings.ShowOverlayInCaptures = ShowInCapturesCheck.IsChecked == true;
        _settings.RemoteAccessMode = RemoteAccessCheck.IsChecked == true;
        _settings.SourceLanguage = NormalizeLanguageTag(SourceLanguageCombo.SelectedItem as string ?? "en");
        _settings.TranslationModeSetting = GetSelectedTranslationModeSetting();
        _settings.Save();

        _pipeline.IntervalMs = _settings.IntervalMs;
        _pipeline.SourceLanguage = _settings.SourceLanguage;
        _pipeline.TargetLanguage = "tr";
        ApplyTranslationModeFromUi();

        if (_overlay is not null)
        {
            _overlay.SetFontSize(_settings.OverlayFontSize);
            _overlay.SetClickThrough(_settings.ClickThrough);
            _overlay.SetVisibleInCaptures(_settings.ShowOverlayInCaptures || _settings.RemoteAccessMode);
        }
    }

    private void ApplyCaptureVisibility()
    {
        _settings.ShowOverlayInCaptures = ShowInCapturesCheck.IsChecked == true;
        _settings.Save();
        _overlay?.SetVisibleInCaptures(_settings.ShowOverlayInCaptures || UseRemoteCompatibleOverlay());
        StatusText.Text = _settings.ShowOverlayInCaptures
            ? "Kayıt modu açık: panel Win+G / Discord'da görünür."
            : "Panel kayıttan gizli (OCR kendini çevirmesin diye).";
    }

    private void ApplyRemoteAccessMode()
    {
        _settings.RemoteAccessMode = RemoteAccessCheck.IsChecked == true;
        _settings.Save();
        ApplyMainWindowCaptureAffinity();

        // AllowsTransparency sonradan değişmez — paneli yeniden oluştur
        RecreateOverlay();
        RecreateDocumentWindowIfNeeded();
        Show();
        Activate();
        StatusText.Text = _settings.RemoteAccessMode
            ? "Uzak erişim modu açık. RDP/AnyDesk ile menü + tüm panelleri görebilirsin."
            : "Uzak erişim modu kapalı. Yerelde şeffaf panel.";
    }

    private bool UseRemoteCompatibleOverlay() =>
        _settings.RemoteAccessMode ||
        RemoteAccessCheck.IsChecked == true ||
        OverlayWindow.DetectRemoteSession();

    private void RecreateOverlay()
    {
        if (_overlay is not null)
        {
            _settings.OverlayLeft = _overlay.Left;
            _settings.OverlayTop = _overlay.Top;
            _settings.OverlayWidth = _overlay.Width;
            _overlay.Close();
            _overlay = null;
        }

        _overlaySuppressed = false;
        EnsureOverlay();
    }

    private void EnsureOverlay()
    {
        _overlaySuppressed = false;
        var remote = UseRemoteCompatibleOverlay();

        // Mod değiştiyse (şeffaf <-> opak) pencereyi yeniden yarat
        if (_overlay is not null && _overlay.IsRemoteCompatible != remote)
        {
            _settings.OverlayLeft = _overlay.Left;
            _settings.OverlayTop = _overlay.Top;
            _settings.OverlayWidth = _overlay.Width;
            _overlay.Close();
            _overlay = null;
        }

        if (_overlay is not null)
        {
            ClampOverlayToVisibleScreen(_overlay);
            _overlay.SetFontSize(_settings.OverlayFontSize);
            _overlay.SetClickThrough(_settings.ClickThrough);
            _overlay.SetVisibleInCaptures(_settings.ShowOverlayInCaptures || remote);

            if (!_overlay.IsVisible)
            {
                _overlay.Show();
            }

            BringOverlayToFront(_overlay);
            return;
        }

        _overlay = new OverlayWindow(remote)
        {
            Width = _settings.OverlayWidth > 200 ? _settings.OverlayWidth : 900,
            Height = 180
        };
        ClampOverlayToVisibleScreen(_overlay);
        _overlay.SetFontSize(_settings.OverlayFontSize);
        _overlay.SetClickThrough(_settings.ClickThrough);
        _overlay.HideRequested += () => _overlaySuppressed = true;
        _overlay.LocationChanged += (_, _) =>
        {
            if (_overlay is null)
            {
                return;
            }

            _settings.OverlayLeft = _overlay.Left;
            _settings.OverlayTop = _overlay.Top;
        };
        _overlay.SizeChanged += (_, _) =>
        {
            if (_overlay is not null)
            {
                _settings.OverlayWidth = _overlay.Width;
            }
        };
        _overlay.Closed += (_, _) => _overlay = null;
        _overlay.Show();
        _overlay.SetVisibleInCaptures(_settings.ShowOverlayInCaptures || remote);
        BringOverlayToFront(_overlay);
    }

    private void BringOverlayToFront(OverlayWindow overlay)
    {
        overlay.Show();
        overlay.WindowState = WindowState.Normal;
        overlay.Topmost = false;
        overlay.Topmost = true;
        overlay.Activate();
    }

    private void ClampOverlayToVisibleScreen(OverlayWindow overlay)
    {
        var width = overlay.Width > 0 ? overlay.Width : 900;
        var height = overlay.Height > 0 ? overlay.Height : 180;

        // DIP cinsinden çalışma alanı (görev çubuğu hariç)
        var work = SystemParameters.WorkArea;
        var left = _settings.OverlayLeft;
        var top = _settings.OverlayTop;

        var fullyVisible =
            !double.IsNaN(left) && !double.IsNaN(top) &&
            left >= work.Left &&
            top >= work.Top &&
            left + width <= work.Right &&
            top + height <= work.Bottom;

        if (!fullyVisible)
        {
            // Ekranın ortası-altı: her zaman görünür güvenli konum
            left = work.Left + Math.Max(20, (work.Width - width) / 2);
            top = work.Top + Math.Max(20, work.Height - height - 40);
        }

        left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - 160));
        top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - height - 8));

        overlay.Left = left;
        overlay.Top = top;
        _settings.OverlayLeft = left;
        _settings.OverlayTop = top;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowMessageHook);

        ApplyMainWindowCaptureAffinity();
        if (!RegisterHotKey(handle, HotkeyId, ModControl | ModShift, KeyT))
        {
            StatusText.Text = "Ctrl+Shift+T başka bir uygulama tarafından kullanılıyor.";
        }
    }

    private void ApplyMainWindowCaptureAffinity()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        // Uzak erişimde ExcludeFromCapture ana menüyü de görünmez yapıyor
        var affinity = UseRemoteCompatibleOverlay() ? WdaNone : WdaExcludeFromCapture;
        SetWindowDisplayAffinity(handle, affinity);
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
        if (!EnsureSafetyAccepted())
        {
            return;
        }

        if (_documentTranslationBusy)
        {
            _documentWindow?.Activate();
            return;
        }

        ApplyRuntimeSettings();
        _documentTranslator.Mode = _pipeline.TranslationMode;

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
        var remote = UseRemoteCompatibleOverlay();

        if (_documentWindow is not null && _documentWindow.IsRemoteCompatible != remote)
        {
            RecreateDocumentWindowIfNeeded();
        }

        if (_documentWindow is not null)
        {
            _documentWindow.SetRemoteCompatible(remote);
            return;
        }

        _documentWindow = new DocumentTranslationWindow(remote);
        _documentWindow.Closed += (_, _) =>
        {
            _documentCts?.Cancel();
            _documentWindow = null;
        };
    }

    private void RecreateDocumentWindowIfNeeded()
    {
        if (_documentWindow is null)
        {
            return;
        }

        _documentWindow.Close();
        _documentWindow = null;
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
