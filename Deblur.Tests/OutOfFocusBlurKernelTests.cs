using Deblur.Engine;
using Xunit;

namespace Deblur.Tests;

public class OutOfFocusBlurKernelTests
{
    private static float Sum(float[,] k)
    {
        float total = 0f;
        for (int y = 0; y < k.GetLength(0); y++)
            for (int x = 0; x < k.GetLength(1); x++)
                total += k[y, x];
        return total;
    }

    [Fact]
    public void NegativeRadius_Throws()
    {
        var kernel = new OutOfFocusBlurKernel();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => kernel.Build(new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0f, -1f, 0f)));
    }

    [Fact]
    public void ZeroRadius_ReturnsSinglePixelIdentity()
    {
        var kernel = new OutOfFocusBlurKernel();
        var k = kernel.Build(new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0f, 0f, 0f));
        Assert.Equal(1, k.GetLength(0));
        Assert.Equal(1, k.GetLength(1));
        Assert.Equal(1f, k[0, 0], 5);
    }

    [Fact]
    public void Kernel_SumsToOne()
    {
        var kernel = new OutOfFocusBlurKernel();
        var k = kernel.Build(new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0f, 8f, 0f));
        Assert.Equal(1f, Sum(k), 4);
    }

    [Fact]
    public void Kernel_IsRadiallySymmetric()
    {
        var kernel = new OutOfFocusBlurKernel();
        var k = kernel.Build(new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0f, 6f, 0f));
        int size = k.GetLength(0);
        int c = size / 2;
        for (int d = 1; d <= c; d++)
        {
            // Four cardinal points at distance d from center must be equal.
            Assert.Equal(k[c, c + d], k[c, c - d], 5);
            Assert.Equal(k[c, c + d], k[c + d, c], 5);
            Assert.Equal(k[c, c + d], k[c - d, c], 5);
        }
    }

    [Fact]
    public void Kernel_HasAntiAliasedEdge()
    {
        var kernel = new OutOfFocusBlurKernel();
        var k = kernel.Build(new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0f, 5f, 0f));
        int size = k.GetLength(0);   // 11
        int c = size / 2;            // 5
        float center = k[c, c];
        float edge = k[c, c + 5];     // dist=5, exactly the Radius, expected weight before-normalize = 0.5
        float corner = k[0, 0];       // dist=sqrt(50)≈7.07, outside the disk, expected = 0

        Assert.True(center > 0f);
        Assert.True(edge > 0f && edge < center);
        Assert.Equal(0f, corner, 5);
    }
}
