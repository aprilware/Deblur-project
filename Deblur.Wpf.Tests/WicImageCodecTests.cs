using Deblur.Engine;
using Deblur.Services;
using Xunit;

namespace Deblur.Wpf.Tests;

public class WicImageCodecTests
{
    [Fact]
    public void EightBitPng_RoundTrip_WithinLsb()
    {
        var codec = new WicImageCodec();
        var src = new ImageBuffer(8, 8);
        for (int i = 0; i < src.PixelCount; i++) { src.R[i] = 0.2f; src.G[i] = 0.5f; src.B[i] = 0.8f; }
        var bytes = codec.EncodePng(src, BitDepth.Eight);
        var (rt, depth) = codec.Decode(bytes);
        Assert.Equal(BitDepth.Eight, depth);
        for (int i = 0; i < src.PixelCount; i++)
        {
            Assert.InRange(Math.Abs(rt.R[i] - src.R[i]), 0f, 1f / 255f);
            Assert.InRange(Math.Abs(rt.G[i] - src.G[i]), 0f, 1f / 255f);
            Assert.InRange(Math.Abs(rt.B[i] - src.B[i]), 0f, 1f / 255f);
        }
    }

    [Fact]
    public void SixteenBitPng_RoundTrip_WithinLsb()
    {
        var codec = new WicImageCodec();
        var src = new ImageBuffer(8, 8);
        for (int i = 0; i < src.PixelCount; i++) { src.R[i] = 0.20003f; src.G[i] = 0.50007f; src.B[i] = 0.80005f; }
        var bytes = codec.EncodePng(src, BitDepth.Sixteen);
        var (rt, depth) = codec.Decode(bytes);
        Assert.Equal(BitDepth.Sixteen, depth);
        for (int i = 0; i < src.PixelCount; i++)
        {
            Assert.InRange(Math.Abs(rt.R[i] - src.R[i]), 0f, 1f / 65535f);
            Assert.InRange(Math.Abs(rt.G[i] - src.G[i]), 0f, 1f / 65535f);
            Assert.InRange(Math.Abs(rt.B[i] - src.B[i]), 0f, 1f / 65535f);
        }
    }

    [Fact]
    public void UnknownFormat_Throws()
    {
        var codec = new WicImageCodec();
        Assert.ThrowsAny<Exception>(() => codec.Decode(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 }));
    }
}
