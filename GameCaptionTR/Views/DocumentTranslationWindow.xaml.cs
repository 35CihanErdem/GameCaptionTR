using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GameCaptionTR.Views;

public partial class DocumentTranslationWindow : Window
{
    private const uint WdaExcludeFromCapture = 0x00000011;

    public DocumentTranslationWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowDisplayAffinity(handle, WdaExcludeFromCapture);
        };
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

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
}
