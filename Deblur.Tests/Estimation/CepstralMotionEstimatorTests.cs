using Deblur.Engine;
using Deblur.Engine.Estimation;
using Deblur.Engine.Validation;
using Deblur.Tests.TestHelpers;
using Xunit;

namespace Deblur.Tests.Estimation;

public class CepstralMotionEstimatorTests
{
    [Theory]
    [InlineData(30f, 12f)]
    [InlineData(0f, 10f)]
    [InlineData(45f, 20f)]
    [InlineData(90f, 8f)]
    public void RecoversMotionAngleAndLength_WithinTolerance(float trueAngle, float trueLength)
    {
        // TexturedNoise (broadband, non-periodic) instead of Checkerboard: a
        // 16-px checkerboard produces a strong self-cepstrum peak at quefrency
        // 16 that dominates any motion-blur peak we can inject, making the
        // estimator's output test-artifact-driven rather than motion-driven.
        var gt = SyntheticImages.TexturedNoise(256, 256, seed: 42);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, trueAngle, trueLength, 0f, 0f, 0f, AlgorithmType.Wiener));
        var blurred = SyntheticBlur.Apply(gt, psf, gaussianNoiseSigma: 0f, seed: 42);
        var gray = ToGrayscale(blurred);

        var est = CepstralMotionEstimator.Estimate(gray, blurred.Width, blurred.Height);

        // Angle within +/-5 deg. Motion angle is ambiguous mod 180 — normalize.
        float estAngle = est.Angle % 180f;
        float trueAngleNorm = ((trueAngle % 180f) + 180f) % 180f;
        float angleDiff = Math.Min(Math.Abs(estAngle - trueAngleNorm),
                                    Math.Abs(estAngle - trueAngleNorm - 180f));
        angleDiff = Math.Min(angleDiff, Math.Abs(estAngle - trueAngleNorm + 180f));
        Assert.True(angleDiff < 5f, $"angle: est {estAngle:F1} vs true {trueAngleNorm:F1}, diff {angleDiff:F1}");

        // Length within +/-20%.
        Assert.InRange(est.Length, trueLength * 0.8f, trueLength * 1.2f);
    }

    [Fact]
    public void SharpImage_LowConfidence()
    {
        // TexturedNoise (broadband, non-periodic) instead of Checkerboard: a
        // 16-px checkerboard produces a strong self-cepstrum peak at quefrency
        // 16 that dominates any motion-blur peak we can inject, making the
        // estimator's output test-artifact-driven rather than motion-driven.
        var gt = SyntheticImages.TexturedNoise(256, 256, seed: 42);
        var gray = ToGrayscale(gt);
        var est = CepstralMotionEstimator.Estimate(gray, gt.Width, gt.Height);
        Assert.True(est.Confidence < 0.5f, $"expected low confidence on sharp image, got {est.Confidence}");
    }

    private static float[] ToGrayscale(ImageBuffer buf)
    {
        var g = new float[buf.PixelCount];
        for (int i = 0; i < g.Length; i++)
            g[i] = 0.299f * buf.R[i] + 0.587f * buf.G[i] + 0.114f * buf.B[i];
        return g;
    }
}
