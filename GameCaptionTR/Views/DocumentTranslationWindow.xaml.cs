using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GameCaptionTR.Views;

public partial class DocumentTranslationWindow : Window
{
    private const uint WdaNone = 0x00000000;
    private const uint WdaExcludeFromCapture = 0x00000011;

    private readonly bool _remoteCompatible;

    public bool IsRemoteCompatible => _remoteCompatible;

    public DocumentTranslationWindow(bool remoteCompatible = false)
    {
        _remoteCompatible = remoteCompatible || OverlayWindow.DetectRemoteSession();
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyCaptureAffinity();
    }

    public void SetRemoteCompatible(bool remoteCompatible)
    {
        ApplyCaptureAffinity(forceVisible: remoteCompatible || _remoteCompatible);
    }

    public void ShowLoading()
    {
        SourceText.Text = string.Empty;
        TranslatedText.Text = string.Empty;
        StatusText.Text = "Ekran okunuyor ve çevriliyor…";
    }

    public void ShowResult(string source, string translated)
    {
        SourceText.Text = source;
        TranslatedText.Text = translated;
        StatusText.Text = "Tamamlandı. Yeniden taramak için Ctrl + Shift + T.";
    }

    public void ShowError(string message)
    {
        StatusText.Text = message;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ApplyCaptureAffinity(bool? forceVisible = null)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var visible = forceVisible ?? _remoteCompatible;
        SetWindowDisplayAffinity(handle, visible ? WdaNone : WdaExcludeFromCapture);
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
}
