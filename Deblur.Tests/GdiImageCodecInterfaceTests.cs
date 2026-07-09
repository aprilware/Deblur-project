using Deblur.Engine;
using System.IO;
using Xunit;

namespace Deblur.Tests;

public class GdiImageCodecInterfaceTests
{
    [Fact]
    public void RoundTrip_Png_PreservesBytes()
    {
        var codec = new Gdi8BitImageCodec();
        // Build a tiny 4x4 image via existing helpers, encode, decode, compare.
        var src = new ImageBuffer(4, 4);
        for (int i = 0; i < src.PixelCount; i++)
        { src.R[i] = (i % 4) / 3f; src.G[i] = ((i / 4) % 4) / 3f; src.B[i] = 0.5f; }
        var bytes = codec.EncodePng(src, BitDepth.Eight);
        var (rt, depth) = codec.Decode(bytes);
        Assert.Equal(BitDepth.Eight, depth);
        Assert.Equal(src.Width, rt.Width);
        Assert.Equal(src.Height, rt.Height);
        for (int i = 0; i < src.PixelCount; i++)
        {
            Assert.InRange(Math.Abs(rt.R[i] - src.R[i]), 0f, 1f / 255f);
            Assert.InRange(Math.Abs(rt.G[i] - src.G[i]), 0f, 1f / 255f);
            Assert.InRange(Math.Abs(rt.B[i] - src.B[i]), 0f, 1f / 255f);
        }
    }
}
