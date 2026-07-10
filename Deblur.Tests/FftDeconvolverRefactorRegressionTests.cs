using System.Numerics;
using Deblur.Engine;
using Deblur.Engine.Color;
using Deblur.Tests.TestHelpers;
using Xunit;

namespace Deblur.Tests;

public class FftDeconvolverRefactorRegressionTests
{
    [Fact]
    public void WienerRefactor_ProducesNearExactSameOutput()
    {
        var input = SyntheticImages.Checkerboard(64, 64, 8);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 8f, 0f, 0f, 0f, AlgorithmType.Wiener));
        var opts = PipelineOptions.Default with { LinearLight = false, EdgeTaper = false };
        var p = new DeconvolutionParams(K: 0.005f);

        var expected = LegacyWienerReference.Apply(input, psf, p, opts);
        var actual = new WienerDeconvolver().Apply(input, psf, p, opts);
        AssertNearExact(expected, actual, tol: 1e-5f);
    }

    [Fact]
    public void TikhonovRefactor_ProducesNearExactSameOutput()
    {
        var input = SyntheticImages.Checkerboard(64, 64, 8);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 8f, 0f, 0f, 0f, AlgorithmType.Tikhonov));
        var opts = PipelineOptions.Default with { LinearLight = false, EdgeTaper = false };
        var p = new DeconvolutionParams(K: 0.005f);

        var expected = LegacyTikhonovReference.Apply(input, psf, p, opts);
        var actual = new TikhonovDeconvolver().Apply(input, psf, p, opts);
        AssertNearExact(expected, actual, tol: 1e-5f);
    }

    private static void AssertNearExact(ImageBuffer expected, ImageBuffer actual, float tol)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        float maxDiff = 0f;
        for (int i = 0; i < expected.PixelCount; i++)
        {
            maxDiff = Math.Max(maxDiff, Math.Abs(expected.R[i] - actual.R[i]));
            maxDiff = Math.Max(maxDiff, Math.Abs(expected.G[i] - actual.G[i]));
            maxDiff = Math.Max(maxDiff, Math.Abs(expected.B[i] - actual.B[i]));
        }
        Assert.True(maxDiff <= tol, $"max abs diff {maxDiff:E} > tolerance {tol:E}");
    }

    // Pinned copy of the pre-refactor WienerDeconvolver — the reference the
    // refactored implementation must match. Do NOT edit this to fix a failure;
    // if you're changing this, you're changing algorithm behavior and the
    // phase-1.a linear-light-gain test would tell you the same thing.
    private static class LegacyWienerReference
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

            var wienerNumer = new Complex[fftSize, fftSize];
            for (int y = 0; y < fftSize; y++)
                for (int x = 0; x < fftSize; x++)
                {
                    var h = H[y, x];
                    double mag2 = h.Real * h.Real + h.Imaginary * h.Imaginary;
                    wienerNumer[y, x] = Complex.Conjugate(h) / (mag2 + p.K);
                }

            float[] outR = ProcessChannel(input.R, input.Width, input.Height, pad, fftSize, wienerNumer, opt);
            float[] outG = ProcessChannel(input.G, input.Width, input.Height, pad, fftSize, wienerNumer, opt);
            float[] outB = ProcessChannel(input.B, input.Width, input.Height, pad, fftSize, wienerNumer, opt);
            return new ImageBuffer(input.Width, input.Height, outR, outG, outB);
        }

        private static float[] ProcessChannel(float[] channel, int w, int h, int pad, int fftSize, Complex[,] wienerNumer, PipelineOptions opt)
        {
            var padded = BoundaryFill.Pad(channel, w, h, pad, fftSize, opt.BoundaryMode);
            if (opt.EdgeTaper) EdgeTaper.ApplyInPlace(padded, pad);

            var G = FftAdapter.Forward2D(padded);
            var Fhat = new Complex[fftSize, fftSize];
            for (int y = 0; y < fftSize; y++)
                for (int x = 0; x < fftSize; x++)
                    Fhat[y, x] = wienerNumer[y, x] * G[y, x];

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

    // Pinned copy of the pre-refactor TikhonovDeconvolver.
    private static class LegacyTikhonovReference
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

            var tikhonovNumer = new Complex[fftSize, fftSize];
            for (int y = 0; y < fftSize; y++)
            {
                double Cv = 2.0 - 2.0 * Math.Cos(2.0 * Math.PI * y / fftSize);
                for (int x = 0; x < fftSize; x++)
                {
                    double Cu = 2.0 - 2.0 * Math.Cos(2.0 * Math.PI * x / fftSize);
                    double cSq = (Cu + Cv) * (Cu + Cv);
                    var h = H[y, x];
                    double mag2 = h.Real * h.Real + h.Imaginary * h.Imaginary;
                    tikhonovNumer[y, x] = Complex.Conjugate(h) / (mag2 + p.K * cSq);
                }
            }

            float[] outR = ProcessChannel(input.R, input.Width, input.Height, pad, fftSize, tikhonovNumer, opt);
            float[] outG = ProcessChannel(input.G, input.Width, input.Height, pad, fftSize, tikhonovNumer, opt);
            float[] outB = ProcessChannel(input.B, input.Width, input.Height, pad, fftSize, tikhonovNumer, opt);
            return new ImageBuffer(input.Width, input.Height, outR, outG, outB);
        }

        private static float[] ProcessChannel(float[] channel, int w, int h, int pad, int fftSize, Complex[,] tikhonovNumer, PipelineOptions opt)
        {
            var padded = BoundaryFill.Pad(channel, w, h, pad, fftSize, opt.BoundaryMode);
            if (opt.EdgeTaper) EdgeTaper.ApplyInPlace(padded, pad);

            var G = FftAdapter.Forward2D(padded);
            var Fhat = new Complex[fftSize, fftSize];
            for (int y = 0; y < fftSize; y++)
                for (int x = 0; x < fftSize; x++)
                    Fhat[y, x] = tikhonovNumer[y, x] * G[y, x];

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
