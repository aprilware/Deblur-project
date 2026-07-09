namespace Deblur.Engine;

public interface IImageCodec
{
    (ImageBuffer image, BitDepth depth) Decode(byte[] bytes);
    byte[] EncodePng(ImageBuffer image, BitDepth depth);
    byte[] EncodeJpeg(ImageBuffer image, int quality);
}
