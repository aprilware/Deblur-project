using Deblur.Engine;
using Deblur.Engine.Imaging;
using Xunit;

namespace Deblur.Tests;

public class AreaResampleTests
{
    [Fact]
    public void Checkerboard_2To1_YieldsUniformMean()
    {
        // 4x4 checkerboard (0 or 1); every 2x2 tile averages to 0.5.
        var src = new ImageBuffer(4, 4);
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                float v = ((x + y) & 1) == 0 ? 1f : 0f;
                int i = y * 4 + x;
                src.R[i] = v; src.G[i] = v; src.B[i] = v;
            }
        var dst = AreaResample.Box(src, 2, 2);
        for (int i = 0; i < 4; i++)
        {
            Assert.InRange(dst.R[i], 0.49f, 0.51f);
            Assert.InRange(dst.G[i], 0.49f, 0.51f);
            Assert.InRange(dst.B[i], 0.49f, 0.51f);
        }
    }

    [Fact]
    public void Dimensions_Correct()
    {
        var src = new ImageBuffer(100, 60);
        var dst = AreaResample.Box(src, 33, 20);
        Assert.Equal(33, dst.Width);
        Assert.Equal(20, dst.Height);
    }

    [Fact]
    public void Upscale_Throws()
    {
        var src = new ImageBuffer(10, 10);
        Assert.Throws<ArgumentException>(() => AreaResample.Box(src, 20, 20));
    }

    [Fact]
    public void ConstantInput_ConstantOutput()
    {
        var src = new ImageBuffer(50, 30);
        for (int i = 0; i < src.PixelCount; i++)
        { src.R[i] = 0.3f; src.G[i] = 0.6f; src.B[i] = 0.9f; }
        var dst = AreaResample.Box(src, 25, 15);
        for (int i = 0; i < dst.PixelCount; i++)
        {
            Assert.InRange(Math.Abs(dst.R[i] - 0.3f), 0f, 1e-5f);
            Assert.InRange(Math.Abs(dst.G[i] - 0.6f), 0f, 1e-5f);
            Assert.InRange(Math.Abs(dst.B[i] - 0.9f), 0f, 1e-5f);
        }
    }
}
