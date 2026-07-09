using Deblur.Engine;
using Deblur.Engine.Validation;
using Xunit;

namespace Deblur.Tests.Validation;

public class PsnrSsimTests
{
    [Fact]
    public void Identical_PsnrIsInfiniteOrLarge()
    {
        var a = MakeGradient(32, 32);
        var b = a.Clone();
        double psnr = Quality.Psnr(a, b);
        Assert.True(double.IsPositiveInfinity(psnr) || psnr > 100);
    }

    [Fact]
    public void Identical_SsimEqualsOne()
    {
        var a = MakeGradient(32, 32);
        double ssim = Quality.Ssim(a, a.Clone());
        Assert.InRange(ssim, 0.999, 1.0001);
    }

    [Fact]
    public void ShiftedNoise_PsnrKnownRange()
    {
        var a = MakeGradient(32, 32);
        var b = a.Clone();
        for (int i = 0; i < b.PixelCount; i++)
        { b.R[i] += 0.01f; b.G[i] += 0.01f; b.B[i] += 0.01f; }
        double psnr = Quality.Psnr(a, b);
        // MSE = 0.0001 → PSNR = 10 log10(1/0.0001) = 40 dB
        Assert.InRange(psnr, 39.5, 40.5);
    }

    [Fact]
    public void SyntheticBlur_ReducesGradientEnergy()
    {
        var src = MakeGradient(64, 64);
        var psf = new float[5, 5];
        for (int y = 0; y < 5; y++)
            for (int x = 0; x < 5; x++)
                psf[y, x] = 1f / 25f;
        var blurred = SyntheticBlur.Apply(src, psf, gaussianNoiseSigma: 0f, seed: 1);
        double srcEnergy = GradientEnergy(src);
        double blurEnergy = GradientEnergy(blurred);
        Assert.True(blurEnergy < srcEnergy * 0.5, $"blur did not reduce gradient energy: {blurEnergy}/{srcEnergy}");
    }

    private static ImageBuffer MakeGradient(int w, int h)
    {
        var b = new ImageBuffer(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                float v = (float)x / (w - 1);
                b.R[i] = v; b.G[i] = v; b.B[i] = v;
            }
        return b;
    }

    private static double GradientEnergy(ImageBuffer b)
    {
        double e = 0;
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width - 1; x++)
            {
                int i = y * b.Width + x;
                double d = b.R[i + 1] - b.R[i];
                e += d * d;
            }
        return e;
    }
}
