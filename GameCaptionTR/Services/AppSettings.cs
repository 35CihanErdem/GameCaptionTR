using System.IO;
using System.Text.Json;

namespace GameCaptionTR.Services;

public sealed class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GameCaptionTR",
        "settings.json");

    public int CaptureX { get; set; }
    public int CaptureY { get; set; }
    public int CaptureWidth { get; set; } = 900;
    public int CaptureHeight { get; set; } = 120;
    public string SourceLanguage { get; set; } = "en";
    public string TargetLanguage { get; set; } = "tr";
    public int IntervalMs { get; set; } = 900;
    public double OverlayFontSize { get; set; } = 28;
    public double OverlayLeft { get; set; } = 200;
    public double OverlayTop { get; set; } = 700;
    public double OverlayWidth { get; set; } = 900;
    public bool ClickThrough { get; set; }

    /// <summary>
    /// true ise overlay Win+G / Discord kaydında görünür.
    /// false ise OCR feedback'ini önlemek için kayıttan gizlenir.
    /// </summary>
    public bool ShowOverlayInCaptures { get; set; }

    /// <summary>
    /// RDP / AnyDesk / uzak bağlanırken panelin görünmesi için opak mod.
    /// </summary>
    public bool RemoteAccessMode { get; set; } = true;

    /// <summary>Kullanıcı güvenlik uyarısını okuyup kabul etti mi.</summary>
    public bool SafetyDisclaimerAccepted { get; set; }

    /// <summary>İlk uyarı bir daha gösterilmesin.</summary>
    public bool SafetyDisclaimerDismissed { get; set; }

    /// <summary>Online, Offline veya Auto</summary>
    public string TranslationModeSetting { get; set; } = "Auto";

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}
