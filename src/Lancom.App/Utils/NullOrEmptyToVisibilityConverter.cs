using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Lancom.App.Utils;

public class NullOrEmptyToVisibilityConverter : IValueConverter
{
    public static NullOrEmptyToVisibilityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isVisible = false;
        if (value is string s)
        {
            isVisible = !string.IsNullOrWhiteSpace(s);
        }
        else if (value is bool b)
        {
            isVisible = b;
        }
        else if (value != null)
        {
            isVisible = true;
        }

        if (parameter is string paramStr && paramStr.Equals("Invert", StringComparison.OrdinalIgnoreCase))
        {
            isVisible = !isVisible;
        }

        return isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
