using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace GameCaptionTR.Views;

public partial class OverlayWindow : Window
{
    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolwindow = 0x00000080;
    private const int SmRemoteSession = 0x1000;
    private const uint WdaNone = 0x00000000;
    private const uint WdaExcludeFromCapture = 0x00000011;

    private readonly bool _remoteCompatible;
    private bool _clickThrough;
    private bool _visibleInCaptures;

    public event Action? HideRequested;

    /// <summary>
    /// Uzak erişimde görünsün diye opaque/RDP-uyumlu mı açıldı.
    /// </summary>
    public bool IsRemoteCompatible => _remoteCompatible;

    public OverlayWindow(bool remoteCompatible = false)
    {
        _remoteCompatible = remoteCompatible || DetectRemoteSession();

        // RDP / AnyDesk / uzak erişimde şeffaf pencere çoğu zaman görünmez.
        AllowsTransparency = !_remoteCompatible;
        Background = _remoteCompatible
            ? new SolidColorBrush(Color.FromRgb(16, 20, 24))
            : Brushes.Transparent;
        WindowStyle = WindowStyle.None;

        InitializeComponent();

        if (_remoteCompatible)
        {
            _visibleInCaptures = true;
            Title = "GameCaptionTR Overlay (Uzak erişim)";
        }

        SourceInitialized += (_, _) => ApplyExtendedStyles();
    }

    public static bool DetectRemoteSession()
    {
        try
        {
            if (GetSystemMetrics(SmRemoteSession) != 0)
            {
                return true;
            }

            var session = Environment.GetEnvironmentVariable("SESSIONNAME");
            if (!string.IsNullOrWhiteSpace(session) &&
                session.StartsWith("RDP-", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
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
        _clickThrough = enabled && !_remoteCompatible;
        ApplyExtendedStyles();
    }

    public void SetVisibleInCaptures(bool visible)
    {
        _visibleInCaptures = visible || _remoteCompatible;
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

        var affinity = (_visibleInCaptures || _remoteCompatible)
            ? WdaNone
            : WdaExcludeFromCapture;
        SetWindowDisplayAffinity(helper.Handle, affinity);
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
}
