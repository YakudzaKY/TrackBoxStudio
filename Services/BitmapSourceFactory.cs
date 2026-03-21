using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace TrackBoxStudio.Services;

public static class BitmapSourceFactory
{
    public static BitmapSource ToBitmapSource(Mat source)
    {
        using var converted = new Mat();
        switch (source.Channels())
        {
            case 1:
                Cv2.CvtColor(source, converted, ColorConversionCodes.GRAY2BGRA);
                break;
            case 3:
                Cv2.CvtColor(source, converted, ColorConversionCodes.BGR2BGRA);
                break;
            case 4:
                source.CopyTo(converted);
                break;
            default:
                throw new InvalidOperationException($"Unsupported channel count: {source.Channels()}");
        }

        var stride = (int)converted.Step();
        var bufferSize = stride * converted.Rows;
        var buffer = new byte[bufferSize];
        System.Runtime.InteropServices.Marshal.Copy(converted.Data, buffer, 0, bufferSize);

        var bitmap = BitmapSource.Create(
            converted.Width,
            converted.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            buffer,
            stride);
        bitmap.Freeze();
        return bitmap;
    }
}
