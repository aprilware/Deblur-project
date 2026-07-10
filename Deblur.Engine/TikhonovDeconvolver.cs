using System.Numerics;

namespace Deblur.Engine;

public sealed class TikhonovDeconvolver : FftDeconvolverBase
{
    public override AlgorithmMetadata Metadata { get; } = new(
        Id: "tikhonov-laplacian",
        Version: "1.0",
        DisplayName: "Tikhonov regularization (Laplacian)",
        DescriptionMarkdown:
            "Tikhonov regularization adds a smoothness penalty to the deconvolution " +
            "objective: minimize ||H*x - y||^2 + K * ||C*x||^2, where C is the discrete " +
            "5-point Laplacian operator. The closed-form frequency-domain solution is " +
            "conj(H) / (|H|^2 + K * |C|^2). K controls the trade-off between fit and " +
            "smoothness; larger K produces smoother, less noise-amplifying reconstructions.",
        LiteratureCitation:
            "Tikhonov, A. N. (1963). Solution of incorrectly formulated problems and " +
            "the regularization method. Dokl. Akad. Nauk SSSR, 151, 501-504.");

    protected override Complex[,] BuildFilterResponse(Complex[,] H, DeconvolutionParams p, int fftSize)
    {
        var filter = new Complex[fftSize, fftSize];
        for (int y = 0; y < fftSize; y++)
        {
            double Cv = 2.0 - 2.0 * Math.Cos(2.0 * Math.PI * y / fftSize);
            for (int x = 0; x < fftSize; x++)
            {
                double Cu = 2.0 - 2.0 * Math.Cos(2.0 * Math.PI * x / fftSize);
                double cSq = (Cu + Cv) * (Cu + Cv);
                var h = H[y, x];
                double mag2 = h.Real * h.Real + h.Imaginary * h.Imaginary;
                filter[y, x] = Complex.Conjugate(h) / (mag2 + p.K * cSq);
            }
        }
        return filter;
    }
}
