using Deblur.Engine;
using Deblur.Engine.Validation;
using Deblur.Tests.TestHelpers;
using Xunit;

namespace Deblur.Tests;

public class BlindDeconvolutionDeconvolverTests
{
    [Fact]
    public void MotionKernelSimilarity_AboveThreshold()
    {
        var gt = SyntheticImages.TexturedNoise(256, 256, seed: 42);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 10f, 0f, 0f, 0f, AlgorithmType.BlindDeconvolution));
        var blurred = SyntheticBlur.Apply(gt, psf, gaussianNoiseSigma: 0f, seed: 42);

        var blind = new BlindDeconvolutionDeconvolver();
        _ = blind.Apply(blurred, new float[1, 1] { { 1f } }, new DeconvolutionParams(K: 1e-3f),
                        PipelineOptions.Default);

        var estimated = blind.LastEstimatedKernel;
        Assert.NotNull(estimated);
        float sim = CosineSimilarityAlignedByCentroid(psf, estimated!);
        Assert.True(sim > 0.6f, $"kernel cosine similarity {sim:F3} below 0.6");
    }

    [Fact]
    public void DefocusKernelSimilarity_AboveThreshold()
    {
        var gt = SyntheticImages.TexturedNoise(256, 256, seed: 42);
        var psf = new OutOfFocusBlurKernel().Build(
            new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0f, 5f, 0f, AlgorithmType.BlindDeconvolution));
        var blurred = SyntheticBlur.Apply(gt, psf, gaussianNoiseSigma: 0f, seed: 42);

        var blind = new BlindDeconvolutionDeconvolver();
        _ = blind.Apply(blurred, new float[1, 1] { { 1f } }, new DeconvolutionParams(K: 1e-3f),
                        PipelineOptions.Default);

        var estimated = blind.LastEstimatedKernel;
        Assert.NotNull(estimated);
        float sim = CosineSimilarityAlignedByCentroid(psf, estimated!);
        // Disc kernels have less directional structure; relax threshold slightly.
        Assert.True(sim > 0.5f, $"defocus kernel cosine similarity {sim:F3} below 0.5");
    }

    [Fact]
    public void SharpInput_RecoversNearDeltaKernel()
    {
        var gt = SyntheticImages.TexturedNoise(256, 256, seed: 42);
        var blind = new BlindDeconvolutionDeconvolver();
        _ = blind.Apply(gt, new float[1, 1] { { 1f } }, new DeconvolutionParams(K: 1e-3f),
                        PipelineOptions.Default);
        var k = blind.LastEstimatedKernel;
        Assert.NotNull(k);
        int center = k!.GetLength(0) / 2;
        float centerVal = k[center, center];
        float off = 0;
        for (int y = 0; y < k.GetLength(0); y++)
            for (int x = 0; x < k.GetLength(1); x++)
                if (y != center || x != center) off += k[y, x];
        Assert.True(centerVal > 0.5f, $"center pixel {centerVal:F3} not dominant");
        Assert.True(off < 0.5f, $"off-center sum {off:F3} too large");
    }

    [Fact]
    public void DeblurredImprovementOverBlurred_By3dB()
    {
        var gt = SyntheticImages.TexturedNoise(256, 256, seed: 42);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 45f, 8f, 0f, 0f, 0f, AlgorithmType.BlindDeconvolution));
        var blurred = SyntheticBlur.Apply(gt, psf, gaussianNoiseSigma: 0f, seed: 42);

        var deconv = new BlindDeconvolutionDeconvolver().Apply(
            blurred, new float[1, 1] { { 1f } }, new DeconvolutionParams(K: 1e-3f),
            PipelineOptions.Default);

        double blurredPsnr = Quality.Psnr(gt, blurred);
        double deconvPsnr  = Quality.Psnr(gt, deconv);
        Assert.True(deconvPsnr >= blurredPsnr + 3.0,
            $"blind did not improve by 3 dB: blurred {blurredPsnr:F2} -> deconv {deconvPsnr:F2}");
    }

    [Fact]
    public void KernelProperties_NonNegativeSumsToOne()
    {
        var gt = SyntheticImages.TexturedNoise(128, 128, seed: 42);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 0f, 6f, 0f, 0f, 0f, AlgorithmType.BlindDeconvolution));
        var blurred = SyntheticBlur.Apply(gt, psf, gaussianNoiseSigma: 0f, seed: 42);

        var blind = new BlindDeconvolutionDeconvolver();
        _ = blind.Apply(blurred, new float[1, 1] { { 1f } }, new DeconvolutionParams(K: 1e-3f),
                        PipelineOptions.Default);
        var k = blind.LastEstimatedKernel!;
        float sum = 0;
        for (int y = 0; y < k.GetLength(0); y++)
            for (int x = 0; x < k.GetLength(1); x++)
            {
                Assert.True(k[y, x] >= 0f);
                sum += k[y, x];
            }
        Assert.InRange(Math.Abs(sum - 1f), 0f, 1e-3f);
    }

    [Fact]
    public void PrecancelledToken_Throws()
    {
        var gt = SyntheticImages.TexturedNoise(128, 128, seed: 42);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var opts = PipelineOptions.Default with { CancellationToken = cts.Token };
        var blind = new BlindDeconvolutionDeconvolver();
        Assert.Throws<OperationCanceledException>(() =>
            blind.Apply(gt, new float[1, 1] { { 1f } }, new DeconvolutionParams(K: 1e-3f), opts));
    }

    private static float CosineSimilarityAlignedByCentroid(float[,] a, float[,] b)
    {
        // Center both by centroid, then cosine similarity on overlap.
        (float cyA, float cxA) = Centroid(a);
        (float cyB, float cxB) = Centroid(b);
        int radius = Math.Min(a.GetLength(0), b.GetLength(0)) / 2 - 1;

        double dot = 0, na = 0, nb = 0;
        for (int j = -radius; j <= radius; j++)
        {
            for (int i = -radius; i <= radius; i++)
            {
                int ay = (int)Math.Round(cyA + j), ax = (int)Math.Round(cxA + i);
                int by = (int)Math.Round(cyB + j), bx = (int)Math.Round(cxB + i);
                if (ay < 0 || ay >= a.GetLength(0) || ax < 0 || ax >= a.GetLength(1)) continue;
                if (by < 0 || by >= b.GetLength(0) || bx < 0 || bx >= b.GetLength(1)) continue;
                double va = a[ay, ax], vb = b[by, bx];
                dot += va * vb; na += va * va; nb += vb * vb;
            }
        }
        return na > 0 && nb > 0 ? (float)(dot / Math.Sqrt(na * nb)) : 0f;
    }

    private static (float y, float x) Centroid(float[,] k)
    {
        double sum = 0, wy = 0, wx = 0;
        for (int y = 0; y < k.GetLength(0); y++)
            for (int x = 0; x < k.GetLength(1); x++)
            {
                sum += k[y, x];
                wy += y * k[y, x];
                wx += x * k[y, x];
            }
        return sum > 0 ? ((float)(wy / sum), (float)(wx / sum))
                       : (k.GetLength(0) / 2f, k.GetLength(1) / 2f);
    }
}
