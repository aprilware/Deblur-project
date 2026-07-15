using Deblur.Engine.Blind;
using Deblur.Engine.Imaging;

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
        var opt = options ?? PipelineOptions.Default;
        var ct = opt.CancellationToken;
        ct.ThrowIfCancellationRequested();

        // Blind estimates kernel from luminance; deblurs each color channel with it.
        var luma = ExtractLuma(input);

        int[] windowSizes = new[] { 5, 9, 17, 31 };
        float[] scales    = new[] { 1f / 8f, 1f / 4f, 1f / 2f, 1f };
        const int outerIters = 5;
        const float lambdaI = 1e-3f;
        const float lambdaK = 1e-3f;
        const float smoothSigma = 1.0f;
        const float shockDt = 0.25f;
        const int shockPasses = 3;
        const float sparsityThreshold = 0.05f;

        float[,] kernel = InitDeltaKernel(windowSizes[0]);

        for (int level = 0; level < scales.Length; level++)
        {
            ct.ThrowIfCancellationRequested();
            float scale = scales[level];
            int windowSize = windowSizes[level];

            int lw = Math.Max(8, (int)Math.Round(input.Width * scale));
            int lh = Math.Max(8, (int)Math.Round(input.Height * scale));
            var lumaAtScale = DownscaleLuma(luma, input.Width, input.Height, lw, lh);

            var dxBlurred = Gradients.ComputeX(lumaAtScale, lw, lh);
            var dyBlurred = Gradients.ComputeY(lumaAtScale, lw, lh);
            int fftSize = FftAdapter.NextPow2(Math.Max(lw, lh) + windowSize * 2);

            for (int iter = 0; iter < outerIters; iter++)
            {
                ct.ThrowIfCancellationRequested();

                // (a) Latent image via Tikhonov given the current kernel.
                var singleChannel = new ImageBuffer(lw, lh,
                    (float[])lumaAtScale.Clone(), (float[])lumaAtScale.Clone(), (float[])lumaAtScale.Clone());
                var latentImg = new TikhonovDeconvolver().Apply(
                    singleChannel, kernel, new DeconvolutionParams(K: lambdaI),
                    PipelineOptions.Default with { LinearLight = false, EdgeTaper = false });
                var latent = latentImg.R; // all three channels are equal

                // (b) Edge prediction — Gaussian pre-smooth + shock filter.
                var predicted = GaussianSmooth.Apply(latent, lw, lh, smoothSigma);
                for (int pass = 0; pass < shockPasses; pass++)
                    predicted = ShockFilter.ApplyOnce(predicted, lw, lh, shockDt);

                // (c) Gradient-domain kernel estimation.
                var dxLatent = Gradients.ComputeX(predicted, lw, lh);
                var dyLatent = Gradients.ComputeY(predicted, lw, lh);
                var rawKernel = KernelEstimation.EstimateGradientDomain(
                    dxLatent, dyLatent, dxBlurred, dyBlurred, lw, lh, lambdaK, fftSize);

                // EstimateGradientDomain returns the kernel in unshifted FFT convention:
                // zero offset at index (0,0), negative offsets wrapped to the far edge
                // (fftSize - d). KernelProjection.Project does a plain contiguous-window
                // crop with no wraparound awareness, so a kernel whose support straddles
                // zero offset (i.e. anything but a purely-positive shift) must be
                // fftshifted first so its support becomes a single contiguous blob
                // centered in the canvas.
                var shiftedKernel = FftShiftCenter(rawKernel, fftSize);

                // (d) Projection.
                kernel = KernelProjection.Project(shiftedKernel, windowSize, sparsityThreshold);
            }

            // Upscale kernel for the next level.
            if (level < scales.Length - 1)
            {
                int nextSize = windowSizes[level + 1];
                var upscaled = BilinearUpscaleKernel(kernel, windowSize, nextSize);
                kernel = KernelProjection.Project(upscaled, nextSize, sparsityThreshold);
            }
        }

        LastEstimatedKernel = kernel;

        // Final deblur: apply the recovered kernel to each color channel via Tikhonov.
        return new TikhonovDeconvolver().Apply(input, kernel, new DeconvolutionParams(K: lambdaI), opt);
    }

    /// <summary>BT.601 luma: 0.299R + 0.587G + 0.114B.</summary>
    private static float[] ExtractLuma(ImageBuffer input)
    {
        var luma = new float[input.PixelCount];
        for (int i = 0; i < luma.Length; i++)
            luma[i] = 0.299f * input.R[i] + 0.587f * input.G[i] + 0.114f * input.B[i];
        return luma;
    }

    private static float[] DownscaleLuma(float[] luma, int srcW, int srcH, int dstW, int dstH)
    {
        if (srcW == dstW && srcH == dstH) return (float[])luma.Clone();
        var wrapped = new ImageBuffer(srcW, srcH, (float[])luma.Clone(), (float[])luma.Clone(), (float[])luma.Clone());
        var resampled = AreaResample.Box(wrapped, dstW, dstH);
        return resampled.R;
    }

    /// <summary>Centered plus-shaped delta: center 0.9, four 4-connected neighbors 0.025 each.</summary>
    private static float[,] InitDeltaKernel(int size)
    {
        var k = new float[size, size];
        int c = size / 2;
        k[c, c] = 0.9f;
        if (c - 1 >= 0) k[c - 1, c] = 0.025f;
        if (c + 1 < size) k[c + 1, c] = 0.025f;
        if (c - 1 >= 0) k[c, c - 1] = 0.025f;
        if (c + 1 < size) k[c, c + 1] = 0.025f;

        float sum = 0;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                sum += k[y, x];
        if (sum > 0f)
        {
            float inv = 1f / sum;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    k[y, x] *= inv;
        }
        return k;
    }

    /// <summary>
    /// Standard fftshift: rotates quadrants so index 0 (zero offset) moves to the
    /// canvas center, making a kernel whose support straddles zero offset (i.e. has
    /// both positive and negative spatial displacements) appear as one contiguous
    /// blob instead of being split between the near-0 and near-fftSize edges.
    /// </summary>
    private static float[,] FftShiftCenter(float[,] raw, int fftSize)
    {
        int half = fftSize / 2;
        var shifted = new float[fftSize, fftSize];
        for (int y = 0; y < fftSize; y++)
        {
            int sy = (y + half) % fftSize;
            for (int x = 0; x < fftSize; x++)
            {
                int sx = (x + half) % fftSize;
                shifted[y, x] = raw[sy, sx];
            }
        }
        return shifted;
    }

    /// <summary>Bilinear resample of a srcSize×srcSize kernel to dstSize×dstSize.</summary>
    private static float[,] BilinearUpscaleKernel(float[,] kernel, int srcSize, int dstSize)
    {
        var result = new float[dstSize, dstSize];
        if (srcSize == 1)
        {
            float v = kernel[0, 0];
            for (int y = 0; y < dstSize; y++)
                for (int x = 0; x < dstSize; x++)
                    result[y, x] = v;
            return result;
        }

        double scale = (double)(srcSize - 1) / (dstSize - 1);
        for (int y = 0; y < dstSize; y++)
        {
            double sy = y * scale;
            int y0 = (int)Math.Floor(sy);
            int y1 = Math.Min(y0 + 1, srcSize - 1);
            double fy = sy - y0;
            for (int x = 0; x < dstSize; x++)
            {
                double sx = x * scale;
                int x0 = (int)Math.Floor(sx);
                int x1 = Math.Min(x0 + 1, srcSize - 1);
                double fx = sx - x0;

                double v00 = kernel[y0, x0];
                double v01 = kernel[y0, x1];
                double v10 = kernel[y1, x0];
                double v11 = kernel[y1, x1];
                double top = v00 + (v01 - v00) * fx;
                double bot = v10 + (v11 - v10) * fx;
                result[y, x] = (float)(top + (bot - top) * fy);
            }
        }
        return result;
    }
}
