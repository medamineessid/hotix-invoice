using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Hotix.InvoiceClient.Converters;

public sealed class ConfidenceToColorConverter : IValueConverter
{
    private const double HighThreshold   = 0.75;
    private const double MediumThreshold = 0.40;

    // Pastel background colors
    private static readonly Brush HighBrush   = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E8F5E9"));
    private static readonly Brush MediumBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFF3E0"));
    private static readonly Brush LowBrush    = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFEBEE"));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double confidence = value is double d ? d
            : value is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double p) ? p
            : 0.0;

        if (confidence >= HighThreshold)   return HighBrush;
        if (confidence >= MediumThreshold) return MediumBrush;
        return LowBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
