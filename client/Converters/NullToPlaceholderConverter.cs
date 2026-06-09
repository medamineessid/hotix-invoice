using System.Globalization;
using System.Windows.Data;

namespace Hotix.InvoiceClient.Converters;

public sealed class NullToPlaceholderConverter : IValueConverter
{
    private readonly string _placeholder = "—";
    private readonly string _convertBackMessage = "La conversion inverse n'est pas prise en charge.";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null)
        {
            return _placeholder;
        }

        if (value is string text && string.IsNullOrWhiteSpace(text))
        {
            return _placeholder;
        }

        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException(_convertBackMessage);
    }
}
