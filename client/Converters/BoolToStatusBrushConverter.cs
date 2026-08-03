using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Hotix.InvoiceClient.Converters;

/// <summary>
/// Converts a bool (e.g. HasGeminiKey / HasGrokKey) into a status brush:
/// <see cref="BrushSuccess"/> when true (configured), a neutral muted brush when false.
/// Used by the API-key status badge on the main screen (one-way binding only).
/// </summary>
public sealed class BoolToStatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool isConfigured = value is bool b && b;
        string resourceKey = isConfigured ? "BrushSuccess" : "BrushTextSecondary";
        return Application.Current.TryFindResource(resourceKey) as Brush ?? Brushes.Gray;
    }

    // One-way conversion only (status badge is display-only) — the reverse
    // direction has no meaningful interpretation, so it's intentionally not
    // implemented. Safe in practice: the badge is bound with the default
    // OneWay mode and WPF never pushes Foreground back through a converter.
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException("BoolToStatusBrushConverter is one-way only.");
}
