using Deblur.Engine;
using Deblur.Engine.Fft;
using Xunit;

namespace Deblur.Tests;

public class FftConvolveTests
{
    [Fact]
    public void Convolve_IdentityKernel_ReturnsInput()
    {
        var input = MakeGradient(16, 16);
        var identity = new float[1, 1] { { 1f } };
        var result = FftConvolve.Convolve(input, 16, 16, identity, BoundaryMode.Reflect);
        for (int i = 0; i < input.Length; i++)
            Assert.InRange(Math.Abs(result[i] - input[i]), 0f, 1e-4f);
    }

    [Fact]
    public void Correlate_IdentityKernel_ReturnsInput()
    {
        var input = MakeGradient(16, 16);
        var identity = new float[1, 1] { { 1f } };
        var result = FftConvolve.Correlate(input, 16, 16, identity, BoundaryMode.Reflect);
        for (int i = 0; i < input.Length; i++)
            Assert.InRange(Math.Abs(result[i] - input[i]), 0f, 1e-4f);
    }

    [Fact]
    public void Convolve_UniformKernel_SmoothsInput()
    {
        var input = MakeGradient(32, 32);
        var box = new float[5, 5];
        for (int y = 0; y < 5; y++) for (int x = 0; x < 5; x++) box[y, x] = 1f / 25f;
        var result = FftConvolve.Convolve(input, 32, 32, box, BoundaryMode.Reflect);
        // Smoothing reduces gradient magnitude in the interior.
        double srcGrad = 0, resGrad = 0;
        for (int y = 8; y < 24; y++)
            for (int x = 8; x < 23; x++)
            {
                int i = y * 32 + x;
                srcGrad += Math.Abs(input[i + 1] - input[i]);
                resGrad += Math.Abs(result[i + 1] - result[i]);
            }
        Assert.True(resGrad < srcGrad * 0.7,
            $"box filter did not smooth: src {srcGrad:F3} → res {resGrad:F3}");
    }

    [Fact]
    public void ConvolveThenCorrelate_ApproximatesAutocorrelation()
    {
        // <Ah, h*A> = <h, A^T A h>  — convolve then correlate with the same PSF is A^T A.
        // For a shift-invariant PSF, this is an autocorrelation-shaped smoothing.
        // We just check the operation completes without NaN and is not the identity.
        var input = MakeGradient(32, 32);
        var psf = new float[5, 5];
        for (int y = 0; y < 5; y++) for (int x = 0; x < 5; x++)
            psf[y, x] = MathF.Exp(-((x - 2) * (x - 2) + (y - 2) * (y - 2)) / 4f);
        // Normalize.
        float sum = 0; for (int y = 0; y < 5; y++) for (int x = 0; x < 5; x++) sum += psf[y, x];
        for (int y = 0; y < 5; y++) for (int x = 0; x < 5; x++) psf[y, x] /= sum;

        var conv = FftConvolve.Convolve(input, 32, 32, psf, BoundaryMode.Reflect);
        var back = FftConvolve.Correlate(conv, 32, 32, psf, BoundaryMode.Reflect);
        for (int i = 0; i < back.Length; i++)
            Assert.True(float.IsFinite(back[i]), $"NaN/Inf at index {i}");
        // Not identity: some smoothing happened.
        double diff = 0;
        for (int i = 0; i < back.Length; i++) diff += Math.Abs(back[i] - input[i]);
        Assert.True(diff > 0.1, "convolve-then-correlate produced the identity");
    }

    private static float[] MakeGradient(int w, int h)
    {
        var b = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                b[y * w + x] = (float)(x + y) / (w + h - 2);
        return b;
    }
}
