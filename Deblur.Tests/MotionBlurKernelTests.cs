using Deblur.Engine;
using Xunit;

namespace Deblur.Tests;

public class MotionBlurKernelTests
{
    private static float Sum(float[,] k)
    {
        float s = 0;
        for (int y = 0; y < k.GetLength(0); y++)
            for (int x = 0; x < k.GetLength(1); x++)
                s += k[y, x];
        return s;
    }

    [Theory]
    [InlineData(0f, 5f)]
    [InlineData(45f, 10f)]
    [InlineData(90f, 20f)]
    [InlineData(137f, 33f)]
    [InlineData(270f, 50f)]
    public void Kernel_SumsToOne(float angleDeg, float length)
    {
        var k = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, angleDeg, length, 0, 0f, 0f));
        Assert.InRange(Sum(k), 0.999999f, 1.000001f);
    }

    [Fact]
    public void Length1_ProducesIdentityKernel()
    {
        var k = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 45f, 1f, 0, 0f, 0f));
        // 3x3 with only center non-zero and equal to 1
        Assert.Equal(3, k.GetLength(0));
        Assert.Equal(3, k.GetLength(1));
        Assert.Equal(1f, k[1, 1], 6);
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 3; x++)
                if (!(x == 1 && y == 1))
                    Assert.Equal(0f, k[y, x], 6);
    }

    [Fact]
    public void AngleFlip_180Degrees_ProducesEquivalentKernel()
    {
        var a = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 15f, 0, 0f, 0f));
        var b = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f + 180f, 15f, 0, 0f, 0f));
        Assert.Equal(a.GetLength(0), b.GetLength(0));
        for (int y = 0; y < a.GetLength(0); y++)
            for (int x = 0; x < a.GetLength(1); x++)
                Assert.Equal(a[y, x], b[y, x], 5);
    }

    [Fact]
    public void FortyFiveDegrees_HasNonZeroOffAxisWeights()
    {
        var k = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 45f, 10f, 0, 0f, 0f));
        // Somewhere in the kernel there must be a pixel that is neither on the
        // horizontal nor vertical axis through center that carries weight;
        // this fails if we fell back to axis-aligned rasterization.
        int c = k.GetLength(0) / 2;
        bool foundOffAxis = false;
        for (int y = 0; y < k.GetLength(0); y++)
            for (int x = 0; x < k.GetLength(1); x++)
                if (x != c && y != c && k[y, x] > 0.001f)
                    foundOffAxis = true;
        Assert.True(foundOffAxis);
    }
}
