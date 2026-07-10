using System.Numerics;

namespace Deblur.Engine;

public abstract class FftDeconvolverBase : IDeconvolver
{
    public abstract AlgorithmMetadata Metadata { get; }

    /// <summary>
    /// Compute the per-frequency multiplier applied to the input's Fourier transform.
    /// Called once per Apply() with the PSF's Fourier transform H and the fftSize.
    /// </summary>
    protected abstract Complex[,] BuildFilterResponse(Complex[,] H, DeconvolutionParams p, int fftSize);

    public ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p, PipelineOptions? options = null)
    {
        var opt = options ?? PipelineOptions.Default;
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
        var filter = BuildFilterResponse(H, p, fftSize);

        float[] outR = ProcessChannel(input.R, input.Width, input.Height, pad, fftSize, filter, opt);
        float[] outG = ProcessChannel(input.G, input.Width, input.Height, pad, fftSize, filter, opt);
        float[] outB = ProcessChannel(input.B, input.Width, input.Height, pad, fftSize, filter, opt);
        return new ImageBuffer(input.Width, input.Height, outR, outG, outB);
    }

    private static float[] ProcessChannel(
        float[] channel, int w, int h, int pad, int fftSize, Complex[,] filter, PipelineOptions opt)
    {
        var padded = BoundaryFill.Pad(channel, w, h, pad, fftSize, opt.BoundaryMode);
        if (opt.EdgeTaper) EdgeTaper.ApplyInPlace(padded, pad);

        var G = FftAdapter.Forward2D(padded);
        var Fhat = new Complex[fftSize, fftSize];
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
