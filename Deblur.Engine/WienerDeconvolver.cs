using System.Numerics;

namespace Deblur.Engine;

public sealed class WienerDeconvolver : FftDeconvolverBase
{
    public override AlgorithmMetadata Metadata { get; } = new(
        Id: "wiener",
        Version: "1.0",
        DisplayName: "Wiener filter",
        DescriptionMarkdown:
            "The Wiener filter is a linear frequency-domain deconvolver that " +
            "minimizes the expected squared error between the estimated and true image, " +
            "assuming known point spread function (PSF) and a scalar noise-to-signal " +
            "ratio parameter K. The filter response is conj(H) / (|H|^2 + K), where " +
            "H is the PSF's Fourier transform. Increasing K suppresses noise " +
            "amplification at the cost of retained blur.",
        LiteratureCitation:
            "Wiener, N. (1949). Extrapolation, Interpolation, and Smoothing of " +
            "Stationary Time Series. MIT Press / Wiley.");

    protected override Complex[,] BuildFilterResponse(Complex[,] H, DeconvolutionParams p, int fftSize)
    {
        var filter = new Complex[fftSize, fftSize];
        for (int y = 0; y < fftSize; y++)
            for (int x = 0; x < fftSize; x++)
            {
                var h = H[y, x];
                double mag2 = h.Real * h.Real + h.Imaginary * h.Imaginary;
                filter[y, x] = Complex.Conjugate(h) / (mag2 + p.K);
            }
        return filter;
    }
}
