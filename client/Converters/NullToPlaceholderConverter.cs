using System.Globalization;
using System.Windows.Data;

namespace Hotix.InvoiceClient.Converters;

public sealed class NullToPlaceholderConverter : IValueConverter
{
    private const string Placeholder = "—";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null)
            return Placeholder;

        if (value is string text && string.IsNullOrWhiteSpace(text))
            return Placeholder;

        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // Support DataGrid inline editing: convert the placeholder back to null,
        // and return any user-entered value as-is.
        if (value is string s)
        {
            if (s == Placeholder || string.IsNullOrWhiteSpace(s))
                return null!;
            return s;
        }

        return value ?? null!;
    }
}
