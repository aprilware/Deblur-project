using System.Numerics;

namespace Deblur.Engine;

public sealed class WienerDeconvolver : IDeconvolver
{
    public ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p)
    {
        int psfH = psf.GetLength(0);
        int psfW = psf.GetLength(1);
        int pad = Math.Max(psfW, psfH) / 2 + 1;

        int paddedW = input.Width + 2 * pad;
        int paddedH = input.Height + 2 * pad;
        int fftSize = FftAdapter.NextPow2(Math.Max(paddedW, paddedH));

        // Build centered PSF in an fftSize x fftSize buffer with DC at (0,0).
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

        // Precompute Wiener denominator |H|^2 + K.
        var wienerNumer = new Complex[fftSize, fftSize];
        for (int y = 0; y < fftSize; y++)
        {
            for (int x = 0; x < fftSize; x++)
            {
                var h = H[y, x];
                double mag2 = h.Real * h.Real + h.Imaginary * h.Imaginary;
                wienerNumer[y, x] = Complex.Conjugate(h) / (mag2 + p.K);
            }
        }

        float[] outR = ProcessChannel(input.R, input.Width, input.Height, pad, fftSize, wienerNumer);
        float[] outG = ProcessChannel(input.G, input.Width, input.Height, pad, fftSize, wienerNumer);
        float[] outB = ProcessChannel(input.B, input.Width, input.Height, pad, fftSize, wienerNumer);
        return new ImageBuffer(input.Width, input.Height, outR, outG, outB);
    }

    private static float[] ProcessChannel(
        float[] channel, int w, int h, int pad, int fftSize, Complex[,] wienerNumer)
    {
        // Reflect-pad into fftSize x fftSize float buffer; zero-fill outside padded region.
        var padded = new float[fftSize, fftSize];
        for (int y = 0; y < h + 2 * pad; y++)
        {
            int sy = ReflectIndex(y - pad, h);
            for (int x = 0; x < w + 2 * pad; x++)
            {
                int sx = ReflectIndex(x - pad, w);
                padded[y, x] = channel[sy * w + sx];
            }
        }

        var G = FftAdapter.Forward2D(padded);
        var Fhat = new Complex[fftSize, fftSize];
        for (int y = 0; y < fftSize; y++)
            for (int x = 0; x < fftSize; x++)
                Fhat[y, x] = wienerNumer[y, x] * G[y, x];

        var real = FftAdapter.Inverse2DReal(Fhat);

        // Crop back to original dims from the padded region.
        // Guard against NaN/Inf: Math.Clamp(NaN, 0, 1) returns NaN because
        // both comparisons involving NaN are false, so filter explicitly.
        var result = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float v = real[y + pad, x + pad];
                if (!float.IsFinite(v)) v = 0f;
                result[y * w + x] = Math.Clamp(v, 0f, 1f);
            }
        }
        return result;
    }

    private static int ReflectIndex(int i, int len)
    {
        if (len <= 1) return 0;
        // Bounce back and forth off the edges.
        int period = 2 * (len - 1);
        int m = ((i % period) + period) % period;
        return m < len ? m : period - m;
    }
}
