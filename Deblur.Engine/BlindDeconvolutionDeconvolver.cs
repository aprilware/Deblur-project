namespace Deblur.Engine;

public sealed class BlindDeconvolutionDeconvolver : IDeconvolver
{
    public AlgorithmMetadata Metadata { get; } = new(
        Id: "blind-cho-lee",
        Version: "1.0",
        DisplayName: "Blind deconvolution (MAP, multi-scale)",
        DescriptionMarkdown:
            "Multi-scale MAP-alternating blind deconvolution. Given a blurred image with " +
            "unknown point-spread function (PSF), estimates a general 2D kernel via a " +
            "coarse-to-fine pyramid: at each pyramid level, alternate between latent-image " +
            "recovery (Tikhonov given the current kernel) and kernel refinement in the " +
            "gradient domain (Cho & Lee 2009 formulation over dx and dy). An edge-prediction " +
            "step (Gaussian pre-smooth + Osher-Rudin shock filter) sharpens the latent image " +
            "between iterations as a surrogate for the sparse-gradient prior. Kernel projection " +
            "at each step enforces non-negativity and sum-to-1 with a 5%-of-max sparsity " +
            "threshold. Four pyramid levels at scales 1/8, 1/4, 1/2, 1/1 with kernel windows " +
            "5, 9, 17, 31 (odd, centered). Deterministic — no random initialization. Blind " +
            "recovery on natural imagery is inherently noisy; the recovered kernel should be " +
            "inspected visually for testimony validation, and the estimator is unreliable on " +
            "motion larger than ~15 px (finest kernel window is 31x31).",
        LiteratureCitation:
            "Cho, S. & Lee, S. (2009). Fast Motion Deblurring. ACM Transactions on Graphics " +
            "28(5), 145. Levin, A., Weiss, Y., Durand, F. & Freeman, W.T. (2011). " +
            "Understanding Blind Deconvolution Algorithms. IEEE PAMI 33(12), 2354-2367. " +
            "Osher, S. & Rudin, L.I. (1990). Feature-oriented image enhancement using shock " +
            "filters. SIAM Journal on Numerical Analysis 27(4), 919-940.");

    /// <summary>
    /// Kernel estimated on the last <see cref="Apply"/> call. Null before the first call.
    /// Not thread-safe; assumes single-threaded runner invocation.
    /// Live-preview WorkerLoop skips this algorithm, so only RenderFullAsync writes here.
    /// </summary>
    public float[,]? LastEstimatedKernel { get; private set; }

    public ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p, PipelineOptions? options = null)
    {
        _ = options ?? PipelineOptions.Default;
        // Task 6 will fill in the real algorithm. Skeleton returns input unchanged so the
        // enum + registration + metadata plumbing can land independently and be tested.
        LastEstimatedKernel = null;
        return input.Clone();
    }
}
