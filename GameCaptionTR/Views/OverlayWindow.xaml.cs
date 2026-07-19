using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace GameCaptionTR.Views;

public partial class OverlayWindow : Window
{
    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolwindow = 0x00000080;
    private const uint WdaExcludeFromCapture = 0x00000011;

    private bool _clickThrough;

    public event Action? HideRequested;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyExtendedStyles();
    }

    public void SetCaption(string text)
    {
        CaptionText.Text = text;
    }

    public void ClearCaption()
    {
        CaptionText.Text = string.Empty;
    }

    public void SetFontSize(double size)
    {
        CaptionText.FontSize = Math.Clamp(size, 14, 72);
    }

    public void SetClickThrough(bool enabled)
    {
        _clickThrough = enabled;
        ApplyExtendedStyles();
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_clickThrough)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // sürükleme sırasında bırakılırsa WPF hata verebilir
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        HideRequested?.Invoke();
        Hide();
    }

    private void ApplyExtendedStyles()
    {
        var helper = new WindowInteropHelper(this);
        if (helper.Handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(helper.Handle, GwlExstyle);
        style |= WsExToolwindow;
        if (_clickThrough)
        {
            style |= WsExTransparent;
        }
        else
        {
            style &= ~WsExTransparent;
        }

        SetWindowLong(helper.Handle, GwlExstyle, style);

        // Windows 10 2004+: bu pencere ekran yakalamaya dahil edilmez (göz kırpma yok)
        SetWindowDisplayAffinity(helper.Handle, WdaExcludeFromCapture);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
}
