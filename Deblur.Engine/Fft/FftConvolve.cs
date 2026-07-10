using System.Numerics;

namespace Deblur.Engine.Fft;

public static class FftConvolve
{
    public static float[] Convolve(float[] channel, int w, int h, float[,] psf, BoundaryMode mode)
        => Apply(channel, w, h, psf, mode, conjugate: false);

    public static float[] Correlate(float[] channel, int w, int h, float[,] psf, BoundaryMode mode)
        => Apply(channel, w, h, psf, mode, conjugate: true);

    private static float[] Apply(float[] channel, int w, int h, float[,] psf, BoundaryMode mode, bool conjugate)
    {
        int psfH = psf.GetLength(0);
        int psfW = psf.GetLength(1);
        int pad = Math.Max(psfW, psfH) / 2 + 1;
        int paddedW = w + 2 * pad;
        int paddedH = h + 2 * pad;
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

        var padded = BoundaryFill.Pad(channel, w, h, pad, fftSize, mode);
        var G = FftAdapter.Forward2D(padded);
        var Fhat = new Complex[fftSize, fftSize];
        for (int y = 0; y < fftSize; y++)
            for (int x = 0; x < fftSize; x++)
                Fhat[y, x] = (conjugate ? Complex.Conjugate(H[y, x]) : H[y, x]) * G[y, x];

        var real = FftAdapter.Inverse2DReal(Fhat);
        var result = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float v = real[y + pad, x + pad];
                if (!float.IsFinite(v)) v = 0f;
                result[y * w + x] = v;   // no clamp — iterative callers handle it
            }
        return result;
    }
}
