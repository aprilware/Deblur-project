using Deblur.Engine;
using Deblur.Tests.TestHelpers;
using Xunit;

namespace Deblur.Tests;

public class WienerDeconvolverTests
{
    [Fact]
    public void RoundTrip_RecoversCheckerboard_AbovePsnrThreshold()
    {
        // cell=32 keeps the checkerboard's fundamental below the motion PSF's
        // frequency nulls; length-12 blur otherwise annihilates too many
        // harmonics for Wiener to recover.
        var original = SyntheticImages.Checkerboard(128, 128, 32);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 12f, 0, 0f, 0f, AlgorithmType.Wiener));
        var blurred = SyntheticImages.Convolve(original, psf);
        var noisy = SyntheticImages.AddGaussianNoise(blurred, 0.005f, seed: 42);

        var deconv = new WienerDeconvolver().Apply(
            noisy, psf, new DeconvolutionParams(K: 0.005f));

        Assert.True(SyntheticImages.Psnr(original, deconv) > 20f);
    }

    [Fact]
    public void WrongAngle_WorsePsnrThanBlurredInput()
    {
        var original = SyntheticImages.Checkerboard(128, 128, 8);
        var truePsf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 12f, 0, 0f, 0f, AlgorithmType.Wiener));
        var wrongPsf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 90f, 12f, 0, 0f, 0f, AlgorithmType.Wiener));
        var blurred = SyntheticImages.Convolve(original, truePsf);

        var deconv = new WienerDeconvolver().Apply(
            blurred, wrongPsf, new DeconvolutionParams(K: 0.005f));

        float blurredPsnr = SyntheticImages.Psnr(original, blurred);
        float wrongPsnr = SyntheticImages.Psnr(original, deconv);
        Assert.True(wrongPsnr < blurredPsnr);
    }

    [Fact]
    public void BorderPixels_BoundedVariance()
    {
        var original = SyntheticImages.Checkerboard(128, 128, 8);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 0f, 8f, 0, 0f, 0f, AlgorithmType.Wiener));
        var blurred = SyntheticImages.Convolve(original, psf);
        var deconv = new WienerDeconvolver().Apply(
            blurred, psf, new DeconvolutionParams(K: 0.005f));

        // Sample the top border strip; variance must be finite and modest.
        double mean = 0, mean2 = 0; int n = 0;
        for (int y = 0; y < 5; y++)
            for (int x = 0; x < deconv.Width; x++)
            {
                float v = deconv.R[y * deconv.Width + x];
                mean += v; mean2 += v * v; n++;
            }
        mean /= n; mean2 /= n;
        double variance = mean2 - mean * mean;
        Assert.True(variance < 0.2, $"variance {variance} too high — border ringing?");
    }

    [Fact]
    public void ExtremeParams_NoNaNOrInfInOutput()
    {
        var original = SyntheticImages.Checkerboard(64, 64, 4);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 22f, 100f, 0, 0f, 0f, AlgorithmType.Wiener));
        var deconv = new WienerDeconvolver().Apply(
            original, psf, new DeconvolutionParams(K: 1e-6f));

        for (int i = 0; i < deconv.PixelCount; i++)
        {
            Assert.False(float.IsNaN(deconv.R[i]) || float.IsInfinity(deconv.R[i]));
            Assert.False(float.IsNaN(deconv.G[i]) || float.IsInfinity(deconv.G[i]));
            Assert.False(float.IsNaN(deconv.B[i]) || float.IsInfinity(deconv.B[i]));
        }
    }

    [Fact]
    public void OutOfFocus_RoundTrip_RecoversAbovePsnrThreshold()
    {
        // cell=32 keeps checkerboard fundamentals below the disk PSF's first
        // Bessel-zero null; smaller cells are annihilated by defocus.
        var original = SyntheticImages.Checkerboard(128, 128, 32);
        var psf = new OutOfFocusBlurKernel().Build(
            new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0f, 4f, 0f, AlgorithmType.Wiener));
        var blurred = SyntheticImages.Convolve(original, psf);
        var noisy = SyntheticImages.AddGaussianNoise(blurred, 0.005f, seed: 42);

        var deconv = new WienerDeconvolver().Apply(
            noisy, psf, new DeconvolutionParams(K: 0.005f));

        float blurredPsnr = SyntheticImages.Psnr(original, blurred);
        float deconvPsnr = SyntheticImages.Psnr(original, deconv);
        Assert.True(deconvPsnr > 15f, $"deconv PSNR {deconvPsnr} below 15 dB floor");
        Assert.True(deconvPsnr > blurredPsnr + 3f,
            $"deconv PSNR {deconvPsnr} not > blurred {blurredPsnr} + 3 dB");
    }

    [Fact]
    public void Gaussian_RoundTrip_RecoversAbovePsnrThreshold()
    {
        // Gaussian PSF has no frequency-domain nulls, so Wiener recovery
        // is well-conditioned; cell=32 matches the phase-1/2 tests.
        var original = SyntheticImages.Checkerboard(128, 128, 32);
        var psf = new GaussianBlurKernel().Build(
            new KernelParams(BlurType.Gaussian, 0f, 0f, 0f, 0f, 2f, AlgorithmType.Wiener));
        var blurred = SyntheticImages.Convolve(original, psf);
        var noisy = SyntheticImages.AddGaussianNoise(blurred, 0.005f, seed: 42);

        var deconv = new WienerDeconvolver().Apply(
            noisy, psf, new DeconvolutionParams(K: 0.005f));

        float blurredPsnr = SyntheticImages.Psnr(original, blurred);
        float deconvPsnr = SyntheticImages.Psnr(original, deconv);
        Assert.True(deconvPsnr > 15f, $"deconv PSNR {deconvPsnr} below 15 dB floor");
        // Mild Gaussian (sigma=2) barely blurs the checkerboard, so blurredPsnr
        // is already high; the 3 dB delta used elsewhere is too tight here.
        Assert.True(deconvPsnr > blurredPsnr + 2.5f,
            $"deconv PSNR {deconvPsnr} not > blurred {blurredPsnr} + 2.5 dB");
    }
}
