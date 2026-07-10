using Deblur.Engine;
using Deblur.Engine.Validation;
using Deblur.Tests.TestHelpers;
using Xunit;

namespace Deblur.Tests;

public class ConstrainedLeastSquaresDeconvolverTests
{
    [Fact]
    public void MotionRoundTrip_BeatsBlurredBy3dB()
    {
        var gt = SyntheticImages.Checkerboard(128, 128, 16);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 8f, 0f, 0f, 0f, AlgorithmType.ConstrainedLeastSquares));
        var blurred = SyntheticBlur.Apply(gt, psf, gaussianNoiseSigma: 0f, seed: 42);
        var deconv = new ConstrainedLeastSquaresDeconvolver().Apply(
            blurred, psf, new DeconvolutionParams(K: 0.005f), PipelineOptions.Default);

        double blurredPsnr = Quality.Psnr(gt, blurred);
        double deconvPsnr = Quality.Psnr(gt, deconv);
        Assert.True(deconvPsnr >= blurredPsnr + 3.0,
            $"CLS did not improve by 3 dB: blurred {blurredPsnr:F2} → deconv {deconvPsnr:F2}");
    }

    [Fact]
    public void IdentityTransform_FailsImprovementCriterion()
    {
        var gt = SyntheticImages.Checkerboard(128, 128, 16);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 8f, 0f, 0f, 0f, AlgorithmType.ConstrainedLeastSquares));
        var blurred = SyntheticBlur.Apply(gt, psf, gaussianNoiseSigma: 0f, seed: 42);
        var identity = blurred.Clone();
        Assert.False(Quality.Psnr(gt, identity) >= Quality.Psnr(gt, blurred) + 3.0);
    }

    [Fact]
    public void KNormalization_ProducesMoreConsistentRecoveryAcrossPsfSizes()
    {
        // Fixed K on length-5 and length-15 motion PSFs.
        // CLS's PSNR-vs-GT range across the two PSFs should be TIGHTER than Tikhonov's.
        var gt = SyntheticImages.Checkerboard(128, 128, 16);
        var p = new DeconvolutionParams(K: 0.005f);
        var opts = PipelineOptions.Default with { LinearLight = false, EdgeTaper = false };

        double[] clsPsnrs = new double[2];
        double[] tikPsnrs = new double[2];
        int idx = 0;
        foreach (float length in new[] { 5f, 15f })
        {
            var psf = new MotionBlurKernel().Build(
                new KernelParams(BlurType.Motion, 30f, length, 0f, 0f, 0f, AlgorithmType.ConstrainedLeastSquares));
            var blurred = SyntheticBlur.Apply(gt, psf, gaussianNoiseSigma: 0f, seed: 42);
            clsPsnrs[idx] = Quality.Psnr(gt, new ConstrainedLeastSquaresDeconvolver().Apply(blurred, psf, p, opts));
            tikPsnrs[idx] = Quality.Psnr(gt, new TikhonovDeconvolver().Apply(blurred, psf, p, opts));
            idx++;
        }
        double clsRange = Math.Abs(clsPsnrs[0] - clsPsnrs[1]);
        double tikRange = Math.Abs(tikPsnrs[0] - tikPsnrs[1]);
        Assert.True(clsRange <= tikRange,
            $"CLS K-normalization did not tighten PSNR range: cls Δ={clsRange:F2}, tik Δ={tikRange:F2}");
    }

    [Fact]
    public void ExtremeParams_NoNaNOrInfInOutput()
    {
        var input = SyntheticImages.Checkerboard(64, 64, 4);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 22f, 100f, 0f, 0f, 0f, AlgorithmType.ConstrainedLeastSquares));
        var deconv = new ConstrainedLeastSquaresDeconvolver().Apply(
            input, psf, new DeconvolutionParams(K: 1e-6f), PipelineOptions.Default);
        for (int i = 0; i < deconv.PixelCount; i++)
        {
            Assert.False(float.IsNaN(deconv.R[i]) || float.IsInfinity(deconv.R[i]));
            Assert.False(float.IsNaN(deconv.G[i]) || float.IsInfinity(deconv.G[i]));
            Assert.False(float.IsNaN(deconv.B[i]) || float.IsInfinity(deconv.B[i]));
        }
    }
}
