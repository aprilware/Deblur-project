using System.Numerics;

namespace Deblur.Engine;

public sealed class ConstrainedLeastSquaresDeconvolver : FftDeconvolverBase
{
    public override AlgorithmMetadata Metadata { get; } = new(
        Id: "cls-laplacian",
        Version: "1.0",
        DisplayName: "Constrained Least Squares (Laplacian, PSF-normalized)",
        DescriptionMarkdown:
            "Constrained Least Squares deconvolution with a discrete-Laplacian smoothness " +
            "constraint. The regularization strength gamma is scaled by the ratio of the " +
            "Laplacian's average spectral energy to the PSF's average spectral energy so " +
            "that the K slider produces comparable regularization across different PSF sizes. " +
            "Because of this normalization, K's effective magnitude is roughly two orders " +
            "of magnitude larger than in Wiener/Tikhonov — the CLS K slider operates in the " +
            "~1e-5 to 1e-3 range for comparable output quality, not the 1e-3 to 1e-1 range. " +
            "This is a pragmatic substitute for the classical CLS formulation, which chooses " +
            "gamma adaptively via the discrepancy principle. The classical adaptive gamma " +
            "requires independent noise-variance estimation and lands in a later phase; this " +
            "version's behavior is honest: fixed gamma scaled by PSF energy, not noise-adaptive.",
        LiteratureCitation:
            "Hunt, B.R. (1973). The application of constrained least squares estimation to " +
            "image restoration by digital computer. IEEE Trans. Comput. C-22(9), 805-812. " +
            "Gonzalez, R.C. & Woods, R.E. Digital Image Processing (4th ed.), sec. 5.9.");

    protected override Complex[,] BuildFilterResponse(Complex[,] H, DeconvolutionParams p, int fftSize)
    {
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
        // Bigger blur -> smaller meanH2 -> larger gamma -> more regularization.
        double gamma = p.K * (meanC2 / Math.Max(meanH2, 1e-12));

        var filter = new Complex[fftSize, fftSize];
        for (int y = 0; y < fftSize; y++)
            for (int x = 0; x < fftSize; x++)
            {
                var h = H[y, x];
                double mag2 = h.Real * h.Real + h.Imaginary * h.Imaginary;
                filter[y, x] = Complex.Conjugate(h) / (mag2 + gamma * cSq[y, x]);
            }
        return filter;
    }
}
