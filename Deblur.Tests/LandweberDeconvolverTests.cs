using Deblur.Engine;
using Deblur.Engine.Validation;
using Deblur.Tests.TestHelpers;
using Xunit;

namespace Deblur.Tests;

public class LandweberDeconvolverTests
{
    [Fact]
    public void MotionRoundTrip_BeatsBlurredBy3dB()
    {
        var gt = SyntheticImages.Checkerboard(128, 128, 16);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 8f, 0f, 0f, 0f, AlgorithmType.Landweber));
        var blurred = SyntheticBlur.Apply(gt, psf, gaussianNoiseSigma: 0f, seed: 42);
        var deconv = new LandweberDeconvolver().Apply(
            blurred, psf, new DeconvolutionParams(K: 0.005f), PipelineOptions.Default);

        double blurredPsnr = Quality.Psnr(gt, blurred);
        double deconvPsnr = Quality.Psnr(gt, deconv);
        Assert.True(deconvPsnr >= blurredPsnr + 3.0,
            $"Landweber did not improve by 3 dB: blurred {blurredPsnr:F2} → deconv {deconvPsnr:F2}");
    }

    [Fact]
    public void IdentityTransform_FailsImprovementCriterion()
    {
        var gt = SyntheticImages.Checkerboard(128, 128, 16);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 8f, 0f, 0f, 0f, AlgorithmType.Landweber));
        var blurred = SyntheticBlur.Apply(gt, psf, gaussianNoiseSigma: 0f, seed: 42);
        var identity = blurred.Clone();
        Assert.False(Quality.Psnr(gt, identity) >= Quality.Psnr(gt, blurred) + 3.0);
    }

    // Renamed from `HoldsAfterEveryIteration` — the test samples terminal
    // state at fixed iteration counts, and the final Math.Clamp(0,1) at
    // Landweber's exit means removing the in-loop non-negativity projection
    // would NOT fail this test. Proper mid-iteration coverage requires an
    // instrumented callback; deferred to Phase 1.d. This assertion still
    // proves the terminal invariant across a representative iteration range.
    [Fact]
    public void NonNegativity_HoldsAtSampledIterations()
    {
        var input = SyntheticImages.Checkerboard(64, 64, 8);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 6f, 0f, 0f, 0f, AlgorithmType.Landweber));
        foreach (int iters in new[] { 1, 10, 50, 100 })
        {
            var deconv = new LandweberDeconvolver(iterations: iters, step: 0.9f)
                .Apply(input, psf, new DeconvolutionParams(K: 0.005f), PipelineOptions.Default);
            for (int i = 0; i < deconv.PixelCount; i++)
            {
                Assert.True(deconv.R[i] >= 0f, $"R[{i}]={deconv.R[i]} at iters={iters}");
                Assert.True(deconv.G[i] >= 0f, $"G[{i}]={deconv.G[i]} at iters={iters}");
                Assert.True(deconv.B[i] >= 0f, $"B[{i}]={deconv.B[i]} at iters={iters}");
            }
        }
    }

    [Fact]
    public void ExtremeParams_NoNaNOrInfInOutput()
    {
        var input = SyntheticImages.Checkerboard(64, 64, 4);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 22f, 100f, 0f, 0f, 0f, AlgorithmType.Landweber));
        var deconv = new LandweberDeconvolver().Apply(
            input, psf, new DeconvolutionParams(K: 1e-6f), PipelineOptions.Default);
        for (int i = 0; i < deconv.PixelCount; i++)
        {
            Assert.False(float.IsNaN(deconv.R[i]) || float.IsInfinity(deconv.R[i]));
            Assert.False(float.IsNaN(deconv.G[i]) || float.IsInfinity(deconv.G[i]));
            Assert.False(float.IsNaN(deconv.B[i]) || float.IsInfinity(deconv.B[i]));
        }
    }
}
