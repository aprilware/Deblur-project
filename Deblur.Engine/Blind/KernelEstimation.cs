using System.Numerics;

namespace Deblur.Engine.Blind;

public static class KernelEstimation
{
    /// <summary>
    /// Gradient-domain kernel estimation (Cho & Lee 2009):
    /// H(u,v) = ( conj(Fdx_L) · Fdx_B + conj(Fdy_L) · Fdy_B ) / ( |Fdx_L|² + |Fdy_L|² + λ )
    /// Returns the raw (unclipped, unprojected) kernel in the FFT canvas frame.
    /// Caller runs KernelProjection to clip to the desired window.
    /// </summary>
    public static float[,] EstimateGradientDomain(
        float[] dxLatent, float[] dyLatent,
        float[] dxBlurred, float[] dyBlurred,
        int w, int h,
        float lambda,
        int fftSize)
    {
        var dxL = PadToCanvas(dxLatent, w, h, fftSize);
        var dyL = PadToCanvas(dyLatent, w, h, fftSize);
        var dxB = PadToCanvas(dxBlurred, w, h, fftSize);
        var dyB = PadToCanvas(dyBlurred, w, h, fftSize);

        var FdxL = FftAdapter.Forward2D(dxL);
        var FdyL = FftAdapter.Forward2D(dyL);
        var FdxB = FftAdapter.Forward2D(dxB);
        var FdyB = FftAdapter.Forward2D(dyB);

        var Hfreq = new Complex[fftSize, fftSize];
        for (int y = 0; y < fftSize; y++)
        {
            for (int x = 0; x < fftSize; x++)
            {
                var conjDxL = Complex.Conjugate(FdxL[y, x]);
                var conjDyL = Complex.Conjugate(FdyL[y, x]);
                double magL2 = FdxL[y, x].Real * FdxL[y, x].Real + FdxL[y, x].Imaginary * FdxL[y, x].Imaginary
                             + FdyL[y, x].Real * FdyL[y, x].Real + FdyL[y, x].Imaginary * FdyL[y, x].Imaginary;
                var num = conjDxL * FdxB[y, x] + conjDyL * FdyB[y, x];
                Hfreq[y, x] = num / (magL2 + lambda);
            }
        }

        var raw = FftAdapter.Inverse2DReal(Hfreq);
        var result = new float[fftSize, fftSize];
        for (int y = 0; y < fftSize; y++)
            for (int x = 0; x < fftSize; x++)
            {
                float v = raw[y, x];
                if (!float.IsFinite(v)) v = 0f;
                result[y, x] = v;
            }
        return result;
    }

    private static float[,] PadToCanvas(float[] arr, int w, int h, int fftSize)
    {
        var canvas = new float[fftSize, fftSize];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                canvas[y, x] = arr[y * w + x];
        return canvas;
    }
}
