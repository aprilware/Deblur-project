using System.Numerics;

namespace Deblur.Engine.Estimation;

public static class CepstralMotionEstimator
{
    public const string Id = "cepstral-motion";
    public const string Version = "1.0";
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
