using Deblur.Engine;
using Deblur.Tests.TestHelpers;
using Xunit;

namespace Deblur.Tests;

public class TotalVariationDeconvolverTests
{
    [Fact]
    public void RoundTrip_RecoversCheckerboard_AbovePsnrThreshold()
    {
        var original = SyntheticImages.Checkerboard(128, 128, 32);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 12f, 0, 0f, 0f, AlgorithmType.TotalVariation));
        var blurred = SyntheticImages.Convolve(original, psf);
        var noisy = SyntheticImages.AddGaussianNoise(blurred, 0.005f, seed: 42);

        var deconv = new TotalVariationDeconvolver().Apply(
            noisy, psf, new DeconvolutionParams(K: 0.005f));

        Assert.True(SyntheticImages.Psnr(original, deconv) > 15f);
    }

    [Fact]
    public void Gaussian_RoundTrip_RecoversAbovePsnrThreshold()
    {
        var original = SyntheticImages.Checkerboard(128, 128, 32);
        var psf = new GaussianBlurKernel().Build(
            new KernelParams(BlurType.Gaussian, 0f, 0f, 0f, 0f, 2f, AlgorithmType.TotalVariation));
        var blurred = SyntheticImages.Convolve(original, psf);
        var noisy = SyntheticImages.AddGaussianNoise(blurred, 0.005f, seed: 42);

        var deconv = new TotalVariationDeconvolver().Apply(
            noisy, psf, new DeconvolutionParams(K: 0.005f));

        float blurredPsnr = SyntheticImages.Psnr(original, blurred);
        float deconvPsnr = SyntheticImages.Psnr(original, deconv);
        Assert.True(deconvPsnr > 15f, $"deconv PSNR {deconvPsnr} below 15 dB floor");
        // TV as a post-filter on Wiener over-smooths Gaussian's already-smooth edge recovery,
        // so the delta over blurred is smaller than Wiener/Tikhonov achieve. 2.0 dB still
        // distinguishes real deconvolution from identity.
        Assert.True(deconvPsnr > blurredPsnr + 2.0f,
            $"deconv PSNR {deconvPsnr} not > blurred {blurredPsnr} + 2.0 dB");
    }

    [Fact]
    public void ExtremeParams_NoNaNOrInfInOutput()
    {
        var original = SyntheticImages.Checkerboard(64, 64, 4);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 22f, 100f, 0, 0f, 0f, AlgorithmType.TotalVariation));
        var deconv = new TotalVariationDeconvolver().Apply(
            original, psf, new DeconvolutionParams(K: 1e-6f));

        for (int i = 0; i < deconv.PixelCount; i++)
        {
            Assert.False(float.IsNaN(deconv.R[i]) || float.IsInfinity(deconv.R[i]));
            Assert.False(float.IsNaN(deconv.G[i]) || float.IsInfinity(deconv.G[i]));
            Assert.False(float.IsNaN(deconv.B[i]) || float.IsInfinity(deconv.B[i]));
        }
    }
}
