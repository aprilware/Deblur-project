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
    public void Build_ReturnsExactStoredPsfReference()
    {
        // Reference equality — CustomPsfKernel is a passive holder; any Phase 1.f-2
        // editor path is expected to clone BEFORE mutating. If someone adds a
        // defensive clone here it silently doubles the memory cost per render and
        // would slip past a value-only assertion.
        var k = new CustomPsfKernel();
        var psf = new float[3, 3] { { 0f, 0.25f, 0f }, { 0.25f, 0f, 0.25f }, { 0f, 0.25f, 0f } };
        k.SetPsf(psf);
        var built = k.Build(new KernelParams(BlurType.Custom, 0f, 0f, 0f, 0f, 0f, AlgorithmType.Wiener));
        Assert.Same(psf, built);
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
