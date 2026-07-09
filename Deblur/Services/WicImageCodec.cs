using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Deblur.Engine;

namespace Deblur.Services;

public sealed class WicImageCodec : IImageCodec
{
    public (ImageBuffer image, BitDepth depth) Decode(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var srcFmt = frame.Format;
        bool is16 = srcFmt == PixelFormats.Rgb48 || srcFmt == PixelFormats.Rgba64 || srcFmt == PixelFormats.Gray16;

        int w = frame.PixelWidth, h = frame.PixelHeight;
        var img = new ImageBuffer(w, h);
        img.SourceBitDepth = is16 ? BitDepth.Sixteen : BitDepth.Eight;

        if (is16)
        {
            var conv = new FormatConvertedBitmap(frame, PixelFormats.Rgb48, null, 0);
            int stride = w * 6;
            var pixels = new ushort[w * h * 3];
            conv.CopyPixels(pixels, stride, 0);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int p = (y * w + x) * 3;
                    int di = y * w + x;
                    img.R[di] = pixels[p]     / 65535f;
                    img.G[di] = pixels[p + 1] / 65535f;
                    img.B[di] = pixels[p + 2] / 65535f;
                }
        }
        else
        {
            var conv = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int stride = w * 4;
            var pixels = new byte[w * h * 4];
            conv.CopyPixels(pixels, stride, 0);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int p = (y * w + x) * 4;
                    int di = y * w + x;
                    img.B[di] = pixels[p]     / 255f;
                    img.G[di] = pixels[p + 1] / 255f;
                    img.R[di] = pixels[p + 2] / 255f;
                }
        }
        return (img, img.SourceBitDepth);
    }

    public byte[] EncodePng(ImageBuffer image, BitDepth depth)
    {
        var bitmap = depth == BitDepth.Sixteen ? To48bpp(image) : To32bpp(image);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    public byte[] EncodeJpeg(ImageBuffer image, int quality)
    {
        if (quality < 1 || quality > 100) throw new ArgumentOutOfRangeException(nameof(quality));
        var bitmap = To32bpp(image);
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static BitmapSource To32bpp(ImageBuffer image)
    {
        int w = image.Width, h = image.Height;
        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                int p = i * 4;
                pixels[p]     = Clamp8(image.B[i]);
                pixels[p + 1] = Clamp8(image.G[i]);
                pixels[p + 2] = Clamp8(image.R[i]);
                pixels[p + 3] = 255;
            }
        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
    }

    private static BitmapSource To48bpp(ImageBuffer image)
    {
        int w = image.Width, h = image.Height;
        var pixels = new ushort[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                int p = i * 3;
                pixels[p]     = Clamp16(image.R[i]);
                pixels[p + 1] = Clamp16(image.G[i]);
                pixels[p + 2] = Clamp16(image.B[i]);
            }
        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Rgb48, null, pixels, w * 6);
    }

    private static byte Clamp8(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
    private static ushort Clamp16(float v) => (ushort)Math.Clamp((int)MathF.Round(v * 65535f), 0, 65535);
}
