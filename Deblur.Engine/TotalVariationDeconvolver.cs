namespace Deblur.Engine;

public sealed class TotalVariationDeconvolver : IDeconvolver
{
    private const int Iterations = 20;
    private const float Tau = 0.125f;
    private const float LambdaScale = 50f;

    public AlgorithmMetadata Metadata { get; } = new(
        Id: "tv-chambolle",
        Version: "1.0",
        DisplayName: "Total Variation (Chambolle post-filter)",
        DescriptionMarkdown:
            "Total Variation denoising via Chambolle's projected dual algorithm, " +
            "applied as a post-filter over a Wiener warm-start. Solves " +
            "argmin_u ||u - f||^2 / (2*lambda) + TV(u), preserving edges while " +
            "suppressing noise. Twenty iterations of the dual projection with " +
            "step size tau = 0.125; lambda is derived from the smoothness slider " +
            "(K * 50) to map the UI range into a visible TV effect.",
        LiteratureCitation:
            "Chambolle, A. (2004). An algorithm for total variation minimization " +
            "and applications. Journal of Mathematical Imaging and Vision, 20, 89-97.");

    public ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p, PipelineOptions? options = null)
    {
        // Warm start: Wiener gives us the initial deblurred estimate.
        var wiener = new WienerDeconvolver().Apply(input, psf, p, options);

        // Then apply Chambolle-Pock TV denoising per channel.
        float lambda = MathF.Max(p.K * LambdaScale, 1e-6f);
        int w = wiener.Width, h = wiener.Height;
        float[] r = ChambolleTV(wiener.R, w, h, lambda);
        float[] g = ChambolleTV(wiener.G, w, h, lambda);
        float[] b = ChambolleTV(wiener.B, w, h, lambda);
        return new ImageBuffer(w, h, r, g, b);
    }

    // Chambolle projected-gradient dual formulation of TV denoising.
    // Solves argmin_u ||u - f||^2 / (2*lambda) + TV(u).
    private static float[] ChambolleTV(float[] f, int w, int h, float lambda)
    {
        var px = new float[w * h];
        var py = new float[w * h];
        var u = new float[w * h];

        for (int iter = 0; iter < Iterations; iter++)
        {
            // u = f - lambda * div(p)
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    float dpx = px[i] - (x > 0 ? px[i - 1] : 0f);
                    float dpy = py[i] - (y > 0 ? py[i - w] : 0f);
                    u[i] = f[i] - lambda * (dpx + dpy);
                }
            }

            // Chambolle-Pock dual step: p_new = P_C(p + tau * grad(div(p) - f/lambda)).
            // Since u = f - lambda*div(p), grad(u)/lambda = -grad(div(p) - f/lambda),
            // so the update in terms of u is p_new = P_C(p - (tau/lambda) * grad(u)).
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    float gx = (x < w - 1) ? u[i + 1] - u[i] : 0f;
                    float gy = (y < h - 1) ? u[i + w] - u[i] : 0f;
                    float pxNew = px[i] - (Tau / lambda) * gx;
                    float pyNew = py[i] - (Tau / lambda) * gy;
                    float norm = MathF.Max(1f, MathF.Sqrt(pxNew * pxNew + pyNew * pyNew));
                    px[i] = pxNew / norm;
                    py[i] = pyNew / norm;
                }
            }
        }

        // Final u = f - lambda * div(p), NaN/Inf guarded and clamped.
        var result = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                float dpx = px[i] - (x > 0 ? px[i - 1] : 0f);
                float dpy = py[i] - (y > 0 ? py[i - w] : 0f);
                float v = f[i] - lambda * (dpx + dpy);
                if (!float.IsFinite(v)) v = 0f;
                result[i] = Math.Clamp(v, 0f, 1f);
            }
        }
        return result;
    }
}
