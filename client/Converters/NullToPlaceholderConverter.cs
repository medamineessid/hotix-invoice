using System.Globalization;
using System.Windows.Data;
using Hotix.InvoiceClient;

namespace Hotix.InvoiceClient.Converters;

public sealed class NullToPlaceholderConverter : IValueConverter
{
    private const string DefaultPlaceholder = "—";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (IsEmpty(value))
        {
            // The compact grid keeps a dash; the detail view (Récapitulatif)
            // requests a descriptive "Not detected" placeholder via
            // ConverterParameter=detail so a missing field doesn't look like a
            // loading state.
            return string.Equals(parameter as string, "detail", StringComparison.OrdinalIgnoreCase)
                ? TranslationSource.Get("NotDetected")
                : DefaultPlaceholder;
        }

        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // Support DataGrid inline editing: convert the placeholder back to null,
        // and return any user-entered value as-is.
        if (value is string s)
        {
            if (IsEmpty(s) || s == DefaultPlaceholder || s == TranslationSource.Get("NotDetected"))
                return null!;
            return s;
        }

        return value ?? null!;
    }

    private static bool IsEmpty(object? value)
        => value is null || (value is string text && string.IsNullOrWhiteSpace(text));
}
