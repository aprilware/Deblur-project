using Deblur.Engine.Estimation;
using Xunit;

namespace Deblur.Tests.Estimation;

public class WaveletNoiseEstimatorTests
{
    [Theory]
    [InlineData(0.005f)]
    [InlineData(0.01f)]
    [InlineData(0.02f)]
    [InlineData(0.05f)]
    public void RecoversKnownGaussianNoise_Within10Percent(float trueSigma)
    {
        int w = 256, h = 256;
        var img = MakeConstantWithNoise(w, h, mean: 0.5f, sigma: trueSigma, seed: 42);
        var est = WaveletNoiseEstimator.Estimate(img, w, h);
        Assert.InRange(est.SigmaNoise, trueSigma * 0.9f, trueSigma * 1.1f);
    }

    [Fact]
    public void NoiselessConstantImage_ReturnsNearZeroSigma()
    {
        int w = 128, h = 128;
        var img = new float[w * h];
        Array.Fill(img, 0.5f);
        var est = WaveletNoiseEstimator.Estimate(img, w, h);
        Assert.InRange(est.SigmaNoise, 0f, 1e-4f);
    }

    [Fact]
    public void HighSNR_Confidence_IsHigh()
    {
        int w = 128, h = 128;
        var img = MakeGradientWithNoise(w, h, sigma: 0.001f, seed: 42);
        var est = WaveletNoiseEstimator.Estimate(img, w, h);
        Assert.True(est.Confidence > 0.7f, $"expected high confidence, got {est.Confidence}");
    }

    [Fact]
    public void SuggestedK_IsPositiveAndBounded()
    {
        int w = 128, h = 128;
        var img = MakeGradientWithNoise(w, h, sigma: 0.02f, seed: 42);
        var est = WaveletNoiseEstimator.Estimate(img, w, h);
        Assert.InRange(est.SuggestedK, 1e-6f, 1.0f);
    }

    private static float[] MakeConstantWithNoise(int w, int h, float mean, float sigma, int seed)
    {
        var rng = new Random(seed);
        var img = new float[w * h];
        for (int i = 0; i < img.Length; i++)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = 1.0 - rng.NextDouble();
            float gauss = (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
            img[i] = mean + sigma * gauss;
        }
        return img;
    }

    private static float[] MakeGradientWithNoise(int w, int h, float sigma, int seed)
    {
        var rng = new Random(seed);
        var img = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float ramp = (float)x / (w - 1);
                double u1 = 1.0 - rng.NextDouble();
                double u2 = 1.0 - rng.NextDouble();
                float gauss = (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
                img[y * w + x] = ramp + sigma * gauss;
            }
        return img;
    }
}
