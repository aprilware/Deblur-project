using System.Numerics;

namespace Deblur.Engine.Estimation;

public static class CepstralMotionEstimator
{
    public const string Id = "cepstral-motion";
    public const string Version = "1.0";

    /// <summary>
    /// Testimony-ready description of the estimator's method and limitations.
    /// The forensic audit log (Phase 2) reads this. Any change here must bump Version.
    /// </summary>
    public const string DescriptionMarkdown =
        "Cepstral motion blur estimation via the log power spectrum. Method: apply a Hann " +
        "window, take FFT, compute log |F|², take inverse FFT to produce the cepstrum, and " +
        "find the dominant negative peak away from the origin. The peak's polar coordinates " +
        "give the estimated motion angle and length in pixels.\n\n" +
        "LIMITATIONS: This method assumes the image's own cepstrum is broadband and " +
        "featureless — the motion PSF's periodic sinc zeros then show up as an isolated " +
        "dark peak. This assumption holds on textured or noisy content (film grain, dense " +
        "vegetation, water) but FAILS on natural photographs with strong regular " +
        "structure — buildings with repeating windows, brick patterns, textiles, printed " +
        "text. On such content the image's OWN cepstral peaks dominate the estimate and " +
        "the (angle, length) output is unreliable. The estimator's confidence field " +
        "reflects the peak's prominence relative to background cepstral energy — LOW " +
        "confidence (< 30%) means \"the peak is not distinguishable from image structure; " +
        "manual PSF entry recommended.\" Iterative blind deconvolution (future phase) " +
        "is the correct tool for high-quality kernel recovery on natural imagery.";

    private const float Eps = 1e-8f;
    private const int OriginExcludeRadius = 4;

    public static MotionEstimate Estimate(float[] grayscale, int width, int height)
    {
        int fftSize = FftAdapter.NextPow2(Math.Max(width, height));

        // Center + zero-pad into square canvas, apply Hann window.
        var canvas = new float[fftSize, fftSize];
        int oy = (fftSize - height) / 2;
        int ox = (fftSize - width) / 2;
        var winY = HannWindow(height);
        var winX = HannWindow(width);
        double mean = 0; int n = grayscale.Length;
        for (int i = 0; i < n; i++) mean += grayscale[i];
        mean /= n;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                canvas[oy + y, ox + x] = (float)((grayscale[y * width + x] - mean) * winY[y] * winX[x]);

        var F = FftAdapter.Forward2D(canvas);

        // Log power spectrum.
        var logPS = new float[fftSize, fftSize];
        for (int y = 0; y < fftSize; y++)
            for (int x = 0; x < fftSize; x++)
            {
                double mag2 = F[y, x].Real * F[y, x].Real + F[y, x].Imaginary * F[y, x].Imaginary;
                logPS[y, x] = (float)Math.Log(mag2 + Eps);
            }

        // Cepstrum = iFFT(log|F|^2).
        var cepFreq = new Complex[fftSize, fftSize];
        for (int y = 0; y < fftSize; y++)
            for (int x = 0; x < fftSize; x++)
                cepFreq[y, x] = new Complex(logPS[y, x], 0);
        var cep = FftAdapter.Inverse2DReal(cepFreq);

        // Find dominant negative peak, excluding a small disc around the origin.
        // Cepstrum uses circular indexing — center is (0, 0) in the FFT convention.
        float minVal = float.PositiveInfinity;
        int minY = 0, minX = 0;
        for (int y = 0; y < fftSize; y++)
        {
            int dy = y < fftSize / 2 ? y : y - fftSize;
            for (int x = 0; x < fftSize; x++)
            {
                int dx = x < fftSize / 2 ? x : x - fftSize;
                if (dy * dy + dx * dx <= OriginExcludeRadius * OriginExcludeRadius) continue;
                if (cep[y, x] < minVal)
                {
                    minVal = cep[y, x];
                    minY = dy;
                    minX = dx;
                }
            }
        }

        float length = MathF.Sqrt(minY * minY + minX * minX);
        float angle = MathF.Atan2(minY, minX) * 180f / MathF.PI;
        // Normalize to [0, 180) — motion is direction-ambiguous.
        while (angle < 0) angle += 180f;
        while (angle >= 180f) angle -= 180f;

        // Confidence: peak strength relative to overall cepstral energy.
        double absSum = 0;
        int m = fftSize * fftSize;
        for (int y = 0; y < fftSize; y++)
            for (int x = 0; x < fftSize; x++)
                absSum += Math.Abs(cep[y, x]);
        float meanAbs = (float)(absSum / m);
        float confidence = Math.Clamp(Math.Abs(minVal) / (meanAbs * 20f), 0f, 1f);

        return new MotionEstimate(angle, length, confidence);
    }

    private static double[] HannWindow(int n)
    {
        var w = new double[n];
        for (int i = 0; i < n; i++)
            w[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (n - 1)));
        return w;
    }
}
