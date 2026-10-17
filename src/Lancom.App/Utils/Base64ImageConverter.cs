using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Lancom.App.Utils;

public class Base64ImageConverter : IValueConverter
{
    public static Base64ImageConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string str && !string.IsNullOrWhiteSpace(str))
        {
            // If it's a local file path
            if (File.Exists(str))
            {
                return ImageThumbnailHelper.LoadBitmapFromFile(str);
            }
            // Otherwise it's base64 data
            return ImageThumbnailHelper.LoadBitmapFromBase64(str);
        }

        return null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
