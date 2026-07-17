using Deblur.Engine.Imaging;
using Xunit;

namespace Deblur.Tests.Imaging;

public class KernelResampleTests
{
    [Fact]
    public void Downscale_ScaleOne_ReturnsClone()
    {
        var src = new float[3, 3] { { 0.1f, 0.2f, 0.1f }, { 0.1f, 0.1f, 0.1f }, { 0.1f, 0.1f, 0.1f } };
        var dst = KernelResample.Downscale(src, 1.0f);
        Assert.Equal(3, dst.GetLength(0));
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 3; x++)
                Assert.InRange(Math.Abs(src[y, x] - dst[y, x]), 0f, 1e-6f);
    }

    [Fact]
    public void Downscale_HalfScale_SumsToOne()
    {
        var src = new float[7, 7];
        for (int y = 0; y < 7; y++)
            for (int x = 0; x < 7; x++)
                src[y, x] = 1f / 49f;
        var dst = KernelResample.Downscale(src, 0.5f);
        float sum = 0f;
        for (int y = 0; y < dst.GetLength(0); y++)
            for (int x = 0; x < dst.GetLength(1); x++)
                sum += dst[y, x];
        Assert.InRange(Math.Abs(sum - 1f), 0f, 1e-4f);
    }

    [Fact]
    public void Downscale_QuarterScale_ProducesOddSize()
    {
        // 31 * 0.25 = 7.75 → round up to nearest odd = 9. Or nearest odd of round = 7.
        // Implementation is expected to keep odd size for kernels; verify output is odd.
        var src = new float[31, 31];
        src[15, 15] = 1f;
        var dst = KernelResample.Downscale(src, 0.25f);
        Assert.Equal(1, dst.GetLength(0) % 2);
        Assert.Equal(1, dst.GetLength(1) % 2);
    }
}
