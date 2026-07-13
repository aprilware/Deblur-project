using Deblur.Engine;
using Deblur.Engine.Estimation;
using Deblur.Engine.Validation;
using Deblur.Tests.TestHelpers;
using Xunit;

namespace Deblur.Tests.Estimation;

public class DefocusRadiusEstimatorTests
{
    // TexturedNoise (broadband, non-periodic) instead of Checkerboard: a periodic
    // pattern injects its own strong spectral structure that would dominate the
    // radial log-power profile this estimator scans for a first local minimum.
    [Theory]
    [InlineData(3f)]
    [InlineData(5f)]
    [InlineData(8f)]
    [InlineData(12f)]
    public void RecoversDiscPsfRadius_Within15Percent(float trueRadius)
    {
        var gt = SyntheticImages.TexturedNoise(256, 256, seed: 42);
        var psf = new OutOfFocusBlurKernel().Build(
            new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0f, trueRadius, 0f, AlgorithmType.Wiener));
        var blurred = SyntheticBlur.Apply(gt, psf, gaussianNoiseSigma: 0f, seed: 42);
        var gray = ToGrayscale(blurred);

        var est = DefocusRadiusEstimator.Estimate(gray, blurred.Width, blurred.Height);
        Assert.InRange(est.Radius, trueRadius * 0.85f, trueRadius * 1.15f);
    }

    private static float[] ToGrayscale(ImageBuffer buf)
    {
        var g = new float[buf.PixelCount];
        for (int i = 0; i < g.Length; i++)
            g[i] = 0.299f * buf.R[i] + 0.587f * buf.G[i] + 0.114f * buf.B[i];
        return g;
    }
}
