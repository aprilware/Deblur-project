using Deblur.Engine;
using Xunit;

namespace Deblur.Tests;

public class GaussianBlurKernelTests
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
    public void NegativeSigma_Throws()
    {
        var kernel = new GaussianBlurKernel();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => kernel.Build(new KernelParams(BlurType.Gaussian, 0f, 0f, 0f, 0f, -1f)));
    }

    [Fact]
    public void ZeroSigma_ReturnsSinglePixelIdentity()
    {
        var kernel = new GaussianBlurKernel();
        var k = kernel.Build(new KernelParams(BlurType.Gaussian, 0f, 0f, 0f, 0f, 0f));
        Assert.Equal(1, k.GetLength(0));
        Assert.Equal(1, k.GetLength(1));
        Assert.Equal(1f, k[0, 0], 5);
    }

    [Fact]
    public void Kernel_SumsToOne()
    {
        var kernel = new GaussianBlurKernel();
        var k = kernel.Build(new KernelParams(BlurType.Gaussian, 0f, 0f, 0f, 0f, 2f));
        Assert.Equal(1f, Sum(k), 4);
    }

    [Fact]
    public void Kernel_IsRadiallySymmetric()
    {
        var kernel = new GaussianBlurKernel();
        var k = kernel.Build(new KernelParams(BlurType.Gaussian, 0f, 0f, 0f, 0f, 2f));
        int size = k.GetLength(0);
        int c = size / 2;
        for (int d = 1; d <= c; d++)
        {
            Assert.Equal(k[c, c + d], k[c, c - d], 5);
            Assert.Equal(k[c, c + d], k[c + d, c], 5);
            Assert.Equal(k[c, c + d], k[c - d, c], 5);
        }
    }

    [Fact]
    public void Kernel_PeaksAtCenter_DecaysMonotonically()
    {
        var kernel = new GaussianBlurKernel();
        var k = kernel.Build(new KernelParams(BlurType.Gaussian, 0f, 0f, 0f, 0f, 2f));
        int size = k.GetLength(0);
        int c = size / 2;
        float center = k[c, c];

        // Center is the strict maximum.
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                if (y != c || x != c)
                    Assert.True(k[y, x] < center, $"k[{y},{x}]={k[y, x]} not strictly less than center {center}");

        // Along the +x axis, values decay monotonically.
        for (int d = 1; d < c; d++)
            Assert.True(k[c, c + d] > k[c, c + d + 1],
                $"k[c, c+{d}]={k[c, c + d]} not > k[c, c+{d + 1}]={k[c, c + d + 1]}");
    }
}
