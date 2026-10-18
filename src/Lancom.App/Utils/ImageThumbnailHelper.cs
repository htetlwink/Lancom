using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lancom.App.Utils;

public static class ImageThumbnailHelper
{
    public static string? CreateThumbnailBase64(string imagePath, int maxDimension = 100)
    {
        try
        {
            if (!File.Exists(imagePath)) return null;

            using var fileStream = File.OpenRead(imagePath);
            var decoder = BitmapDecoder.Create(fileStream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];

            double scale = Math.Min((double)maxDimension / frame.PixelWidth, (double)maxDimension / frame.PixelHeight);
            if (scale > 1.0) scale = 1.0;

            var target = new TransformedBitmap(frame, new ScaleTransform(scale, scale));

            using var memStream = new MemoryStream();
            var encoder = new JpegBitmapEncoder { QualityLevel = 60 };
            encoder.Frames.Add(BitmapFrame.Create(target));
            encoder.Save(memStream);

            return Convert.ToBase64String(memStream.ToArray());
        }
        catch
        {
            return null;
        }
    }

    public static BitmapImage? LoadBitmapFromBase64(string base64)
    {
        try
        {
            byte[] bytes = Convert.FromBase64String(base64);
            using var ms = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = ms;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    public static BitmapImage? LoadBitmapFromFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(filePath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}
