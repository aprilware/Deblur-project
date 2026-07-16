using Deblur.Engine;
using Xunit;

namespace Deblur.Tests;

public class CustomPsfKernelTests
{
    [Fact]
    public void Build_WithoutSetPsf_Throws()
    {
        var k = new CustomPsfKernel();
        Assert.Throws<System.InvalidOperationException>(() =>
            k.Build(new KernelParams(BlurType.Custom, 0f, 0f, 0f, 0f, 0f, AlgorithmType.Wiener)));
    }

    [Fact]
    public void Build_ReturnsStoredPsf()
    {
        var k = new CustomPsfKernel();
        var psf = new float[3, 3] { { 0f, 0.25f, 0f }, { 0.25f, 0f, 0.25f }, { 0f, 0.25f, 0f } };
        k.SetPsf(psf);
        var built = k.Build(new KernelParams(BlurType.Custom, 0f, 0f, 0f, 0f, 0f, AlgorithmType.Wiener));
        Assert.Equal(3, built.GetLength(0));
        Assert.Equal(3, built.GetLength(1));
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 3; x++)
                Assert.Equal(psf[y, x], built[y, x]);
    }

    [Fact]
    public void SetPsf_ReplacesPreviousPsf()
    {
        var k = new CustomPsfKernel();
        k.SetPsf(new float[1, 1] { { 1f } });
        var newPsf = new float[3, 3];
        newPsf[1, 1] = 1f;
        k.SetPsf(newPsf);
        var built = k.Build(new KernelParams(BlurType.Custom, 0f, 0f, 0f, 0f, 0f, AlgorithmType.Wiener));
        Assert.Equal(3, built.GetLength(0));
    }

    [Fact]
    public void SetPsf_Null_Throws()
    {
        var k = new CustomPsfKernel();
        Assert.Throws<System.ArgumentNullException>(() => k.SetPsf(null!));
    }
}
