using Deblur.Engine.Fft;

namespace Deblur.Engine;

public sealed class RichardsonLucyDeconvolver : IDeconvolver
{
    private const float Eps = 1e-6f;
    private readonly int _iterations;
    private readonly float _alpha;
    private readonly bool _accelerate;

    public AlgorithmMetadata Metadata { get; } = new(
        Id: "richardson-lucy",
        Version: "1.0",
        DisplayName: "Richardson-Lucy (accelerated, under-relaxed)",
        DescriptionMarkdown:
            "Richardson-Lucy is an iterative maximum-likelihood deconvolver under a " +
            "Poisson-noise model. Each iteration applies a multiplicative correction " +
            "x_{k+1} = x_k * H^T(y / (H*x_k))^alpha, where alpha in (0, 1] under-relaxes " +
            "the update to reduce noise amplification. This is fractional-power " +
            "under-relaxation, NOT White (1994) damped RL (which uses a residual-thresholded " +
            "damping mask). Biggs-Andrews momentum-style extrapolation accelerates convergence.",
        LiteratureCitation:
            "Richardson, W.H. (1972). Bayesian-based iterative method of image restoration. " +
            "J. Opt. Soc. Am. 62, 55-59. Lucy, L.B. (1974). Astron. J. 79, 745. " +
            "Biggs, D.S.C. & Andrews, M. (1997). Applied Optics 36, 1766.");

    public RichardsonLucyDeconvolver(int iterations = 30, float alpha = 0.5f, bool accelerate = true)
    {
        if (iterations < 1) throw new ArgumentOutOfRangeException(nameof(iterations));
        if (alpha <= 0f || alpha > 1f) throw new ArgumentOutOfRangeException(nameof(alpha));
        _iterations = iterations;
        _alpha = alpha;
        _accelerate = accelerate;
    }

    public ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p, PipelineOptions? options = null)
    {
        var opt = options ?? PipelineOptions.Default;
        int w = input.Width, h = input.Height;
        return new ImageBuffer(w, h,
            ProcessChannel(input.R, w, h, psf, opt.CancellationToken),
            ProcessChannel(input.G, w, h, psf, opt.CancellationToken),
            ProcessChannel(input.B, w, h, psf, opt.CancellationToken));
    }

    private float[] ProcessChannel(float[] y, int w, int h, float[,] psf, CancellationToken cancellationToken)
    {
        int n = y.Length;
        var x = (float[])y.Clone();
        var xPrev = (float[])x.Clone();
        var xPrevPrev = (float[])x.Clone();

        for (int k = 0; k < _iterations; k++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Biggs-Andrews extrapolation for k >= 2.
            // beta = <x_k - x_{k-1}, x_{k-1} - x_{k-2}> / <x_{k-1} - x_{k-2}, x_{k-1} - x_{k-2}>
            // Requires tracking two iterations back (xPrevPrev), not just one.
            float[] xStart;
            if (_accelerate && k >= 2)
            {
                double num = 0, den = 0;
                for (int i = 0; i < n; i++)
                {
                    float d  = x[i]     - xPrev[i];        // x_k - x_{k-1}
                    float dP = xPrev[i] - xPrevPrev[i];    // x_{k-1} - x_{k-2}
                    num += d  * dP;
                    den += dP * dP;
                }
                float beta = den > 0 ? (float)Math.Clamp(num / den, 0.0, 1.0) : 0f;
                xStart = new float[n];
                for (int i = 0; i < n; i++)
                    xStart[i] = Math.Clamp(x[i] + beta * (x[i] - xPrev[i]), 0f, 1f);
            }
            else
            {
                xStart = x;
            }

            var Hx = FftConvolve.Convolve(xStart, w, h, psf, BoundaryMode.Reflect);
            var ratio = new float[n];
            for (int i = 0; i < n; i++) ratio[i] = y[i] / MathF.Max(Hx[i], Eps);

            var correction = FftConvolve.Correlate(ratio, w, h, psf, BoundaryMode.Reflect);
            var xNext = new float[n];
            for (int i = 0; i < n; i++)
            {
                float c = MathF.Max(correction[i], Eps);
                float relaxed = _alpha == 1f ? c : MathF.Pow(c, _alpha);
                float v = xStart[i] * relaxed;
                if (!float.IsFinite(v)) v = 0f;
                xNext[i] = Math.Clamp(v, 0f, 1f);
            }

            xPrevPrev = xPrev;
            xPrev = x;
            x = xNext;
        }
        return x;
    }
}
