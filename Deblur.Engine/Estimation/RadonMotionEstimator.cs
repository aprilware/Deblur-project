using System.Numerics;

namespace Deblur.Engine.Estimation;

public static class RadonMotionEstimator
{
    public const string Id = "radon-motion";
    public const string Version = "1.0";
    private const float Eps = 1e-8f;

    public static float EstimateAngleDegrees(float[] grayscale, int width, int height)
    {
        int fftSize = FftAdapter.NextPow2(Math.Max(width, height));

        var canvas = new float[fftSize, fftSize];
        int oy = (fftSize - height) / 2;
        int ox = (fftSize - width) / 2;
        double mean = 0; int n = grayscale.Length;
        for (int i = 0; i < n; i++) mean += grayscale[i];
        mean /= n;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                canvas[oy + y, ox + x] = (float)(grayscale[y * width + x] - mean);

        var F = FftAdapter.Forward2D(canvas);
        var logPS = new float[fftSize, fftSize];
        for (int y = 0; y < fftSize; y++)
            for (int x = 0; x < fftSize; x++)
            {
                double mag2 = F[y, x].Real * F[y, x].Real + F[y, x].Imaginary * F[y, x].Imaginary;
                logPS[y, x] = (float)Math.Log(mag2 + Eps);
            }

        // Radon integration over 180 candidate angles at 1-degree resolution.
        // Line through the FFT origin ([0, 0] in the FFT convention — not the
        // geometric center) with modular wraparound so negative and out-of-bounds
        // coordinates map correctly to the other half of the FFT plane. Bilinear
        // interpolation of fractional radial samples keeps the profile smooth
        // enough that argmin isn't dominated by nearest-neighbor jitter.
        //
        // Convention: MIN sum is the motion direction. Motion blur imprints
        // sinc-zeros as periodic dark stripes PERPENDICULAR to the motion angle,
        // so integrating along the motion direction crosses each dark stripe.
        // Empirical verification: MIN gives correct answers within tolerance;
        // MAX produces answers 90° off across the board.
        int maxR = fftSize / 2 - 2;
        double minSum = double.PositiveInfinity;
        float bestAngle = 0f;
        for (int deg = 0; deg < 180; deg++)
        {
            double rad = deg * Math.PI / 180.0;
            double dyU = Math.Sin(rad), dxU = Math.Cos(rad);
            double sum = 0; int count = 0;
            for (int r = -maxR; r <= maxR; r++)
            {
                double fy = r * dyU;
                double fx = r * dxU;
                sum += SampleBilinear(logPS, fftSize, fy, fx);
                count++;
            }
            double avg = sum / count;
            if (avg < minSum)
            {
                minSum = avg;
                bestAngle = deg;
            }
        }
        return bestAngle;
    }

    // Bilinear sample of logPS at fractional (fy, fx) with toroidal (modular)
    // wraparound — negative and out-of-bounds coordinates map to the other half
    // of the FFT plane, matching the DFT's periodic-extension convention.
    private static double SampleBilinear(float[,] logPS, int fftSize, double fy, double fx)
    {
        int y0 = (int)Math.Floor(fy);
        int x0 = (int)Math.Floor(fx);
        double ty = fy - y0;
        double tx = fx - x0;
        int y0m = Mod(y0, fftSize), y1m = Mod(y0 + 1, fftSize);
        int x0m = Mod(x0, fftSize), x1m = Mod(x0 + 1, fftSize);
        double v00 = logPS[y0m, x0m];
        double v01 = logPS[y0m, x1m];
        double v10 = logPS[y1m, x0m];
        double v11 = logPS[y1m, x1m];
        double v0 = v00 * (1 - tx) + v01 * tx;
        double v1 = v10 * (1 - tx) + v11 * tx;
        return v0 * (1 - ty) + v1 * ty;
    }

    private static int Mod(int a, int m) => ((a % m) + m) % m;
}
