using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Deblur.Services;

public static class ImageBufferInterop
{
    public static WriteableBitmap NewCompatibleBitmap(int width, int height)
        => new(width, height, 96, 96, PixelFormats.Bgra32, null);

    public static void ApplyBgraToWriteableBitmap(byte[] bgra, int w, int h, WriteableBitmap target)
    {
        if (target.PixelWidth != w || target.PixelHeight != h)
            throw new ArgumentException("target dimensions do not match source.");
        var rect = new Int32Rect(0, 0, w, h);
        target.WritePixels(rect, bgra, w * 4, 0);
    }
}
