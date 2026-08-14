using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Hotix.InvoiceClient;

/// <summary>
/// Full-window viewer for an invoice image. Opened by the "View invoice" row
/// action so the scan can be read comfortably instead of being crammed into the
/// narrow side preview panel. Supports zoom (buttons or Ctrl+wheel), fit-to-
/// window, and Escape/✕ to close.
/// </summary>
public partial class ImageViewerWindow : Window
{
    private double _zoom = 1.0;

    public ImageViewerWindow(ImageSource image, string title)
    {
        InitializeComponent();
        Image.Source = image;
        Title = title;
        TitleText.Text = title;
        StatusText.Text = string.Empty;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Defer until layout has measured the scroll viewer so its viewport
        // dimensions are meaningful for the initial fit.
        Dispatcher.BeginInvoke(new System.Action(FitToWindow));
    }

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 0.1, 8.0);
        ZoomTransform.ScaleX = _zoom;
        ZoomTransform.ScaleY = _zoom;
        ZoomText.Text = $"{(int)Math.Round(_zoom * 100)}%";
    }

    private void FitToWindow()
    {
        if (Image.Source is not BitmapSource bmp || bmp.PixelWidth <= 0 || bmp.PixelHeight <= 0)
            return;

        double availableW = Scroller.ViewportWidth;
        double availableH = Scroller.ViewportHeight;
        if (availableW <= 0 || availableH <= 0)
            return;

        double scale = Math.Min(availableW / bmp.PixelWidth, availableH / bmp.PixelHeight);
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
            scale = 1.0;

        // Don't upscale a tiny image beyond its native resolution.
        SetZoom(Math.Min(scale, 1.0));
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom * 1.25);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom / 1.25);

    private void Fit_Click(object sender, RoutedEventArgs e) => FitToWindow();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Ctrl+wheel zooms; a plain wheel scrolls the image normally.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SetZoom(_zoom * (e.Delta > 0 ? 1.1 : 1.0 / 1.1));
            e.Handled = true;
        }
    }
}
