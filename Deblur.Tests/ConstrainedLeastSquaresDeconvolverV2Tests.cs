using Deblur.Engine;
using Deblur.Engine.Validation;
using Deblur.Tests.TestHelpers;
using Xunit;

namespace Deblur.Tests;

public class ConstrainedLeastSquaresDeconvolverV2Tests
{
    [Fact]
    public void Metadata_VersionBumpedTo2_0()
    {
        var m = new ConstrainedLeastSquaresDeconvolver().Metadata;
        Assert.Equal("2.0", m.Version);
        Assert.Equal("cls-laplacian", m.Id);
    }

    [Fact]
    public void NoiseVariance_Null_ByteIdenticalToV1Behavior()
    {
        // Reference: pinned v1.0 code path.
        var input = SyntheticImages.Checkerboard(64, 64, 8);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 8f, 0f, 0f, 0f, AlgorithmType.ConstrainedLeastSquares));
        var opts = PipelineOptions.Default with { LinearLight = false, EdgeTaper = false };
        var p = new DeconvolutionParams(K: 1e-5f); // NoiseVariance = null

        var v1Ref = LegacyClsV1Reference.Apply(input, psf, p, opts);
        var v2Actual = new ConstrainedLeastSquaresDeconvolver().Apply(input, psf, p, opts);

        float maxDiff = 0f;
        for (int i = 0; i < input.PixelCount; i++)
        {
            maxDiff = Math.Max(maxDiff, Math.Abs(v1Ref.R[i] - v2Actual.R[i]));
            maxDiff = Math.Max(maxDiff, Math.Abs(v1Ref.G[i] - v2Actual.G[i]));
            maxDiff = Math.Max(maxDiff, Math.Abs(v1Ref.B[i] - v2Actual.B[i]));
        }
        Assert.True(maxDiff <= 1e-5f, $"v2 with null NoiseVariance diverges from v1: {maxDiff:E}");
    }

    [Fact]
    public void AdaptiveGamma_WithCorrectNoiseVariance_MatchesOrBeatsFixedGamma()
    {
        // Same setup as phase-1.c CLS MotionRoundTrip, but with noise injected + variance provided.
        var gt = SyntheticImages.Checkerboard(128, 128, 16);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 5f, 0f, 0f, 0f, AlgorithmType.ConstrainedLeastSquares));
        float sigma = 0.01f;
        var blurred = SyntheticBlur.Apply(gt, psf, gaussianNoiseSigma: sigma, seed: 42);

        var opts = PipelineOptions.Default;
        var pFixed = new DeconvolutionParams(K: 1e-5f);
        var pAdapt = new DeconvolutionParams(K: 1e-5f, NoiseVariance: sigma * sigma);

        var fixedResult = new ConstrainedLeastSquaresDeconvolver().Apply(blurred, psf, pFixed, opts);
        var adaptResult = new ConstrainedLeastSquaresDeconvolver().Apply(blurred, psf, pAdapt, opts);

        double fixedPsnr = Quality.Psnr(gt, fixedResult);
        double adaptPsnr = Quality.Psnr(gt, adaptResult);

        // Adaptive should not be materially worse than fixed; ideally better under noise.
        Assert.True(adaptPsnr >= fixedPsnr - 0.5, $"adaptive {adaptPsnr:F2} < fixed {fixedPsnr:F2} - 0.5");
    }

    // Pinned pre-v2 CLS implementation — this is the actual reference the v2
    // null-path must match. Do NOT edit to fix a failure; if this needs changing,
    // you're changing algorithm behavior and CLS's Version should bump.
    //
    // Mirrors the Wiener/Tikhonov approach in FftDeconvolverRefactorRegressionTests.
    private static class LegacyClsV1Reference
    {
        public static ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p, PipelineOptions opt)
        {
            int psfH = psf.GetLength(0);
            int psfW = psf.GetLength(1);
            int pad = Math.Max(psfW, psfH) / 2 + 1;

            int paddedW = input.Width + 2 * pad;
            int paddedH = input.Height + 2 * pad;
            int fftSize = FftAdapter.NextPow2(Math.Max(paddedW, paddedH));

            var psfBuf = new float[fftSize, fftSize];
            int cy = psfH / 2, cx = psfW / 2;
            for (int y = 0; y < psfH; y++)
                for (int x = 0; x < psfW; x++)
                {
                    int dy = (y - cy + fftSize) % fftSize;
                    int dx = (x - cx + fftSize) % fftSize;
                    psfBuf[dy, dx] = psf[y, x];
                }
            var H = FftAdapter.Forward2D(psfBuf);

            int n = fftSize * fftSize;
            double sumH2 = 0, sumC2 = 0;
            var cSq = new double[fftSize, fftSize];
            for (int y = 0; y < fftSize; y++)
            {
                double Cv = 2.0 - 2.0 * Math.Cos(2.0 * Math.PI * y / fftSize);
                for (int x = 0; x < fftSize; x++)
                {
                    double Cu = 2.0 - 2.0 * Math.Cos(2.0 * Math.PI * x / fftSize);
                    double cs = (Cu + Cv) * (Cu + Cv);
                    cSq[y, x] = cs;
                    sumC2 += cs;
                    var h = H[y, x];
                    sumH2 += h.Real * h.Real + h.Imaginary * h.Imaginary;
                }
            }
            double meanH2 = sumH2 / n;
            double meanC2 = sumC2 / n;
            double gamma = p.K * (meanC2 / Math.Max(meanH2, 1e-12));

            var filter = new System.Numerics.Complex[fftSize, fftSize];
            for (int y = 0; y < fftSize; y++)
                for (int x = 0; x < fftSize; x++)
                {
                    var h = H[y, x];
                    double mag2 = h.Real * h.Real + h.Imaginary * h.Imaginary;
                    filter[y, x] = System.Numerics.Complex.Conjugate(h) / (mag2 + gamma * cSq[y, x]);
                }

            float[] outR = ProcessChannel(input.R, input.Width, input.Height, pad, fftSize, filter, opt);
            float[] outG = ProcessChannel(input.G, input.Width, input.Height, pad, fftSize, filter, opt);
            float[] outB = ProcessChannel(input.B, input.Width, input.Height, pad, fftSize, filter, opt);
            return new ImageBuffer(input.Width, input.Height, outR, outG, outB);
        }

        private static float[] ProcessChannel(float[] channel, int w, int h, int pad, int fftSize,
            System.Numerics.Complex[,] filter, PipelineOptions opt)
        {
            var padded = BoundaryFill.Pad(channel, w, h, pad, fftSize, opt.BoundaryMode);
            if (opt.EdgeTaper) EdgeTaper.ApplyInPlace(padded, pad);

            var G = FftAdapter.Forward2D(padded);
            var Fhat = new System.Numerics.Complex[fftSize, fftSize];
            for (int y = 0; y < fftSize; y++)
                for (int x = 0; x < fftSize; x++)
                    Fhat[y, x] = filter[y, x] * G[y, x];

            var real = FftAdapter.Inverse2DReal(Fhat);
            var result = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float v = real[y + pad, x + pad];
                    if (!float.IsFinite(v)) v = 0f;
                    result[y * w + x] = Math.Clamp(v, 0f, 1f);
                }
            return result;
        }
    }
}
