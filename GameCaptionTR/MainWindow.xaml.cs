using System.Collections.ObjectModel;
using System.Windows;
using GameCaptionTR.Models;
using GameCaptionTR.Services;
using GameCaptionTR.Views;

namespace GameCaptionTR;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly CaptionPipelineService _pipeline = new();
    private OverlayWindow? _overlay;
    private readonly ObservableCollection<string> _languages = new();

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        LoadUiFromSettings();
        WirePipeline();
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
            EnsureOverlay();
            _overlay!.SetCaption(translated);
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
        EnsureOverlay();
        _overlay!.Activate();
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
        _overlay.LocationChanged += (_, _) =>
        {
            _settings.OverlayLeft = _overlay.Left;
            _settings.OverlayTop = _overlay.Top;
        };
        _overlay.SizeChanged += (_, _) => _settings.OverlayWidth = _overlay.Width;
        _overlay.Closed += (_, _) => _overlay = null;
        _overlay.Show();
    }

    private async Task ShutdownAsync()
    {
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
}
