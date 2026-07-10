using Deblur.Engine.Fft;

namespace Deblur.Engine;

public sealed class LandweberDeconvolver : IDeconvolver
{
    private readonly int _iterations;
    private readonly float _step;

    public AlgorithmMetadata Metadata { get; } = new(
        Id: "landweber",
        Version: "1.0",
        DisplayName: "Landweber (non-negativity-projected)",
        DescriptionMarkdown:
            "Landweber deconvolution is an iterative gradient-descent method on the least- " +
            "squares residual with a non-negativity projection. Each iteration applies " +
            "x_{k+1} = max(0, x_k + tau * H^T * (y - H*x_k)), where tau in (0, 2/lambda_max) " +
            "is the step size (lambda_max being the largest eigenvalue of H^T H, ~1 for " +
            "normalized PSFs). The non-negativity projection matches the physical assumption " +
            "that intensities are non-negative and restrains overshoot at strong edges.",
        LiteratureCitation:
            "Landweber, L. (1951). An iteration formula for Fredholm integral equations of " +
            "the first kind. American Journal of Mathematics 73(3), 615-624.");

    public LandweberDeconvolver(int iterations = 100, float step = 0.9f)
    {
        if (iterations < 1) throw new ArgumentOutOfRangeException(nameof(iterations));
        if (step <= 0f || step >= 2f) throw new ArgumentOutOfRangeException(nameof(step));
        _iterations = iterations;
        _step = step;
    }

    public ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p, PipelineOptions? options = null)
    {
        _ = options ?? PipelineOptions.Default;
        int w = input.Width, h = input.Height;
        return new ImageBuffer(w, h,
            ProcessChannel(input.R, w, h, psf),
            ProcessChannel(input.G, w, h, psf),
            ProcessChannel(input.B, w, h, psf));
    }

    private float[] ProcessChannel(float[] y, int w, int h, float[,] psf)
    {
        int n = y.Length;
        var x = (float[])y.Clone();
        for (int k = 0; k < _iterations; k++)
        {
            var Hx = FftConvolve.Convolve(x, w, h, psf, BoundaryMode.Reflect);
            var residual = new float[n];
            for (int i = 0; i < n; i++) residual[i] = y[i] - Hx[i];

            var grad = FftConvolve.Correlate(residual, w, h, psf, BoundaryMode.Reflect);
            for (int i = 0; i < n; i++)
            {
                float v = x[i] + _step * grad[i];
                if (!float.IsFinite(v)) v = 0f;
                x[i] = Math.Max(0f, v);
            }
        }
        // Clamp final output to [0,1] for consistency with other deconvolvers.
        for (int i = 0; i < n; i++) x[i] = Math.Clamp(x[i], 0f, 1f);
        return x;
    }
}
