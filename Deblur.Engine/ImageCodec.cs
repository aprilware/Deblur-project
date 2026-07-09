using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace Deblur.Engine;

[SupportedOSPlatform("windows")]
public static class ImageCodec
{
    public static ImageBuffer DecodeFromBytes(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        Bitmap bmp;
        try
        {
            bmp = new Bitmap(ms);
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException)
        {
            throw new InvalidImageFormatException("Image bytes could not be decoded.", ex);
        }

        using (bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            var buf = new ImageBuffer(w, h);
            var rect = new Rectangle(0, 0, w, h);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                var scan = new byte[stride * h];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, scan, 0, scan.Length);
                for (int y = 0; y < h; y++)
                {
                    int rowBase = y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        int p = rowBase + x * 4;
                        // BGRA order in memory.
                        buf.B[y * w + x] = scan[p] / 255f;
                        buf.G[y * w + x] = scan[p + 1] / 255f;
                        buf.R[y * w + x] = scan[p + 2] / 255f;
                    }
                }
            }
            finally { bmp.UnlockBits(data); }
            return buf;
        }
    }

    public static byte[] EncodePng(ImageBuffer image)
        => EncodeInternal(image, ImageFormat.Png, quality: null);

    public static byte[] EncodeJpeg(ImageBuffer image, int quality)
    {
        if (quality < 1 || quality > 100)
            throw new ArgumentOutOfRangeException(nameof(quality));
        return EncodeInternal(image, ImageFormat.Jpeg, quality);
    }

    private static byte[] EncodeInternal(ImageBuffer image, ImageFormat format, int? quality)
    {
        using var bmp = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, image.Width, image.Height);
        var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            var scan = new byte[stride * image.Height];
            for (int y = 0; y < image.Height; y++)
            {
                int rowBase = y * stride;
                for (int x = 0; x < image.Width; x++)
                {
                    int p = rowBase + x * 4;
                    int idx = y * image.Width + x;
                    scan[p] = Clamp8(image.B[idx]);
                    scan[p + 1] = Clamp8(image.G[idx]);
                    scan[p + 2] = Clamp8(image.R[idx]);
                    scan[p + 3] = 255;
                }
            }
            System.Runtime.InteropServices.Marshal.Copy(scan, 0, data.Scan0, scan.Length);
        }
        finally { bmp.UnlockBits(data); }

        using var ms = new MemoryStream();
        if (quality is int q && format.Guid == ImageFormat.Jpeg.Guid)
        {
            var codec = GetEncoder(ImageFormat.Jpeg);
            var eps = new EncoderParameters(1);
            eps.Param[0] = new EncoderParameter(Encoder.Quality, (long)q);
            bmp.Save(ms, codec, eps);
        }
        else
        {
            bmp.Save(ms, format);
        }
        return ms.ToArray();
    }

    private static byte Clamp8(float v)
    {
        int i = (int)MathF.Round(v * 255f);
        return (byte)Math.Clamp(i, 0, 255);
    }

    private static ImageCodecInfo GetEncoder(ImageFormat format)
    {
        foreach (var c in ImageCodecInfo.GetImageEncoders())
            if (c.FormatID == format.Guid) return c;
        throw new InvalidOperationException($"No encoder for {format}.");
    }
}

[SupportedOSPlatform("windows")]
public sealed class Gdi8BitImageCodec : IImageCodec
{
    public (ImageBuffer image, BitDepth depth) Decode(byte[] bytes)
    {
        var img = ImageCodec.DecodeFromBytes(bytes);
        img.SourceBitDepth = BitDepth.Eight;
        return (img, BitDepth.Eight);
    }

    public byte[] EncodePng(ImageBuffer image, BitDepth depth) => ImageCodec.EncodePng(image);

    public byte[] EncodeJpeg(ImageBuffer image, int quality) => ImageCodec.EncodeJpeg(image, quality);
}
