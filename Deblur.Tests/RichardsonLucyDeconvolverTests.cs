using Deblur.Engine;
using Deblur.Engine.Validation;
using Deblur.Tests.TestHelpers;
using Xunit;

namespace Deblur.Tests;

public class RichardsonLucyDeconvolverTests
{
    [Fact]
    public void MotionRoundTrip_BeatsBlurredBy3dB()
    {
        var gt = SyntheticImages.Checkerboard(128, 128, 16);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 8f, 0f, 0f, 0f, AlgorithmType.RichardsonLucy));
        var blurred = SyntheticBlur.Apply(gt, psf, gaussianNoiseSigma: 0f, seed: 42);
        var deconv = new RichardsonLucyDeconvolver().Apply(
            blurred, psf, new DeconvolutionParams(K: 0.005f), PipelineOptions.Default);

        double blurredPsnr = Quality.Psnr(gt, blurred);
        double deconvPsnr = Quality.Psnr(gt, deconv);
        Assert.True(deconvPsnr >= blurredPsnr + 3.0,
            $"RL did not improve by 3 dB: blurred {blurredPsnr:F2} → deconv {deconvPsnr:F2}");
    }

    [Fact]
    public void IdentityTransform_FailsImprovementCriterion()
    {
        // Test-methodology integrity: verify the 3-dB improvement criterion
        // correctly REJECTS a no-op. If this ever passes with an identity
        // transform, the criterion is broken.
        var gt = SyntheticImages.Checkerboard(128, 128, 16);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 8f, 0f, 0f, 0f, AlgorithmType.RichardsonLucy));
        var blurred = SyntheticBlur.Apply(gt, psf, gaussianNoiseSigma: 0f, seed: 42);
        var identity = blurred.Clone();

        double blurredPsnr = Quality.Psnr(gt, blurred);
        double identityPsnr = Quality.Psnr(gt, identity);
        // Identity should give the SAME PSNR — NOT an improvement.
        Assert.False(identityPsnr >= blurredPsnr + 3.0,
            $"criterion accepted an identity transform: {identityPsnr:F2} vs {blurredPsnr:F2}");
    }

    [Fact]
    public void NoAcceleration_MonotonicConvergenceOnPsnr()
    {
        // Basic RL is provably monotonic in the log-likelihood; PSNR-vs-GT should
        // be non-decreasing when noise is absent and the model matches.
        var gt = SyntheticImages.Checkerboard(64, 64, 8);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 6f, 0f, 0f, 0f, AlgorithmType.RichardsonLucy));
        var blurred = SyntheticBlur.Apply(gt, psf, gaussianNoiseSigma: 0f, seed: 7);

        double prev = double.NegativeInfinity;
        foreach (int iters in new[] { 1, 3, 5, 10, 20 })
        {
            var deconv = new RichardsonLucyDeconvolver(iterations: iters, alpha: 1.0f, accelerate: false)
                .Apply(blurred, psf, new DeconvolutionParams(K: 0.005f), PipelineOptions.Default);
            double psnr = Quality.Psnr(gt, deconv);
            Assert.True(psnr >= prev - 1e-3, $"non-monotonic at iters={iters}: {prev:F3} → {psnr:F3}");
            prev = psnr;
        }
    }

    [Fact]
    public void Accelerated_Iter30BeatsIter5BeatsIter1()
    {
        // Accelerated RL can zigzag between adjacent iterations, so we only assert
        // the ordering iter30 > iter5 > iter1 (loose long-term progression).
        var gt = SyntheticImages.Checkerboard(64, 64, 8);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 6f, 0f, 0f, 0f, AlgorithmType.RichardsonLucy));
        var blurred = SyntheticBlur.Apply(gt, psf, gaussianNoiseSigma: 0f, seed: 7);

        double PsnrAt(int iters) => Quality.Psnr(
            gt,
            new RichardsonLucyDeconvolver(iterations: iters, alpha: 0.5f, accelerate: true)
                .Apply(blurred, psf, new DeconvolutionParams(K: 0.005f), PipelineOptions.Default));

        double p1 = PsnrAt(1), p5 = PsnrAt(5), p30 = PsnrAt(30);
        Assert.True(p30 > p5, $"iter30 ({p30:F2}) not > iter5 ({p5:F2})");
        Assert.True(p5 > p1, $"iter5 ({p5:F2}) not > iter1 ({p1:F2})");
    }

    [Fact]
    public void ExtremeParams_NoNaNOrInfInOutput()
    {
        var input = SyntheticImages.Checkerboard(64, 64, 4);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 22f, 100f, 0f, 0f, 0f, AlgorithmType.RichardsonLucy));
        var deconv = new RichardsonLucyDeconvolver().Apply(
            input, psf, new DeconvolutionParams(K: 1e-6f), PipelineOptions.Default);
        for (int i = 0; i < deconv.PixelCount; i++)
        {
            Assert.False(float.IsNaN(deconv.R[i]) || float.IsInfinity(deconv.R[i]));
            Assert.False(float.IsNaN(deconv.G[i]) || float.IsInfinity(deconv.G[i]));
            Assert.False(float.IsNaN(deconv.B[i]) || float.IsInfinity(deconv.B[i]));
        }
    }
}
