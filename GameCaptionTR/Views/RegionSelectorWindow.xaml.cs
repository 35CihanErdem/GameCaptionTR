using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using GameCaptionTR.Models;

namespace GameCaptionTR.Views;

public partial class RegionSelectorWindow : Window
{
    private Point _start;
    private bool _dragging;
    private readonly Rectangle _selection;

    public CaptureRegion? SelectedRegion { get; private set; }

    public RegionSelectorWindow()
    {
        InitializeComponent();

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        _selection = new Rectangle
        {
            Stroke = Brushes.Lime,
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(60, 0, 255, 0)),
            Visibility = Visibility.Collapsed
        };

        CanvasRoot.Children.Add(_selection);
    }

    private void Window_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
    }

    private void Window_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _start = e.GetPosition(CanvasRoot);
        Canvas.SetLeft(_selection, _start.X);
        Canvas.SetTop(_selection, _start.Y);
        _selection.Width = 0;
        _selection.Height = 0;
        _selection.Visibility = Visibility.Visible;
        CaptureMouse();
    }

    private void Window_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var pos = e.GetPosition(CanvasRoot);
        var x = Math.Min(pos.X, _start.X);
        var y = Math.Min(pos.Y, _start.Y);
        var w = Math.Abs(pos.X - _start.X);
        var h = Math.Abs(pos.Y - _start.Y);

        Canvas.SetLeft(_selection, x);
        Canvas.SetTop(_selection, y);
        _selection.Width = w;
        _selection.Height = h;
    }

    private void Window_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();

        var x = Canvas.GetLeft(_selection);
        var y = Canvas.GetTop(_selection);
        var w = _selection.Width;
        var h = _selection.Height;

        if (w < 40 || h < 20)
        {
            MessageBox.Show("Bölge çok küçük. Altyazı alanını daha geniş seç.", "GameCaptionTR");
            return;
        }

        // WPF mantıksal birim -> ekran pikseli
        var dpi = VisualTreeHelper.GetDpi(this);
        SelectedRegion = new CaptureRegion
        {
            X = (int)Math.Round((SystemParameters.VirtualScreenLeft + x) * dpi.DpiScaleX),
            Y = (int)Math.Round((SystemParameters.VirtualScreenTop + y) * dpi.DpiScaleY),
            Width = (int)Math.Round(w * dpi.DpiScaleX),
            Height = (int)Math.Round(h * dpi.DpiScaleY)
        };

        DialogResult = true;
        Close();
    }
}
