using Deblur.Engine;
using Deblur.Engine.Blind;
using Deblur.Tests.TestHelpers;
using Xunit;

namespace Deblur.Tests.Blind;

public class KernelEstimationTests
{
    [Fact]
    public void RecoversDeltaKernel_FromIdenticalGradients()
    {
        // If latent == blurred, the kernel should be a delta.
        var gt = SyntheticImages.TexturedNoise(64, 64, seed: 42);
        var gray = ToGrayscale(gt);
        var dxL = Gradients.ComputeX(gray, 64, 64);
        var dyL = Gradients.ComputeY(gray, 64, 64);
        int fftSize = FftAdapter.NextPow2(64 + 30);

        var raw = KernelEstimation.EstimateGradientDomain(dxL, dyL, dxL, dyL, 64, 64, lambda: 1e-3f, fftSize);
        // Center of FFT canvas should hold the peak (or near it).
        float max = float.NegativeInfinity;
        int argY = 0, argX = 0;
        for (int y = 0; y < fftSize; y++)
            for (int x = 0; x < fftSize; x++)
                if (raw[y, x] > max) { max = raw[y, x]; argY = y; argX = x; }
        // Argmax should be at the origin (0, 0) under FFT convention (a delta).
        int distFromOrigin = Math.Min(argY, fftSize - argY) + Math.Min(argX, fftSize - argX);
        Assert.True(distFromOrigin <= 2, $"argmax at ({argY},{argX}), fftSize {fftSize}");
    }

    [Fact]
    public void NoNaNOnZeroLatent()
    {
        int w = 32, h = 32, fftSize = FftAdapter.NextPow2(w + 30);
        var zero = new float[w * h];
        var blurred = new float[w * h];
        for (int i = 0; i < blurred.Length; i++) blurred[i] = 0.5f;
        var dxL = Gradients.ComputeX(zero, w, h);
        var dyL = Gradients.ComputeY(zero, w, h);
        var dxB = Gradients.ComputeX(blurred, w, h);
        var dyB = Gradients.ComputeY(blurred, w, h);
        var raw = KernelEstimation.EstimateGradientDomain(dxL, dyL, dxB, dyB, w, h, lambda: 1e-3f, fftSize);
        for (int y = 0; y < fftSize; y++)
            for (int x = 0; x < fftSize; x++)
                Assert.True(float.IsFinite(raw[y, x]));
    }

    private static float[] ToGrayscale(ImageBuffer buf)
    {
        var g = new float[buf.PixelCount];
        for (int i = 0; i < g.Length; i++)
            g[i] = 0.299f * buf.R[i] + 0.587f * buf.G[i] + 0.114f * buf.B[i];
        return g;
    }
}
