namespace Deblur.Engine.Estimation;

public static class DefocusRadiusEstimator
{
    public const string Id = "bessel-defocus";
    public const string Version = "1.0";
    private const float Eps = 1e-8f;

    // J_1's first positive zero is at 3.8317; the disc-PSF's Fourier transform is
    // proportional to 2*J_1(2*pi*R*rho)/(2*pi*R*rho), so its first zero-crossing
    // is at rho = 3.8317/(2*pi*R) ~= 0.6098/R. Therefore R ~= 0.6098 / rho_first_zero.
    private const float BesselFirstZeroOverTwoPi = 0.6098f;

    public static DefocusEstimate Estimate(float[] grayscale, int width, int height)
    {
        int fftSize = FftAdapter.NextPow2(Math.Max(width, height));
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

        // Radial average of the log power spectrum, using FFT-origin (0,0) with
        // modular indexing (dy = y or y - fftSize, dx = x or x - fftSize).
        int maxR = fftSize / 2 - 2;
        var sum = new double[maxR];
        var cnt = new int[maxR];
        for (int y = 0; y < fftSize; y++)
        {
            int dy = y < fftSize / 2 ? y : y - fftSize;
            for (int x = 0; x < fftSize; x++)
            {
                int dx = x < fftSize / 2 ? x : x - fftSize;
                int r = (int)Math.Round(Math.Sqrt(dy * dy + dx * dx));
                if (r >= maxR || r < 1) continue;
                double mag2 = F[y, x].Real * F[y, x].Real + F[y, x].Imaginary * F[y, x].Imaginary;
                sum[r] += Math.Log(mag2 + Eps);
                cnt[r]++;
            }
        }
        var profile = new float[maxR];
        for (int r = 0; r < maxR; r++)
            profile[r] = cnt[r] > 0 ? (float)(sum[r] / cnt[r]) : 0f;

        // 3-tap median filter to suppress bin-quantization noise before the
        // local-minimum scan.
        var smoothed = new float[maxR];
        smoothed[0] = profile[0];
        smoothed[maxR - 1] = profile[maxR - 1];
        for (int r = 1; r < maxR - 1; r++)
        {
            float a = profile[r - 1], b = profile[r], c = profile[r + 1];
            smoothed[r] = Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
        }

        // Scan outward from bin 4 (skip the DC region) for the first local minimum.
        // Two refinements over a plain smoothed[r] < smoothed[r-1] && smoothed[r] < smoothed[r+1]
        // scan are needed against real (non-synthetic-checkerboard) spectra:
        //  - the radial-average profile frequently contains exact-tie plateaus (adjacent
        //    bins with identical averaged log-power), which a strict "<" never matches on
        //    either side of the plateau, silently skipping genuine deep minima. The
        //    comparison below is <= on both sides with a strict "<" on at least one side,
        //    so it fires on the first bin of a tie plateau instead of never firing.
        //  - windowing the input (see HannWindow below) suppresses gross edge-leakage
        //    artifacts, but still leaves a small sidelobe ripple near the DC region that
        //    can register as a shallow "local minimum" well before the true, much deeper
        //    Bessel-zero dip for small radii (whose first zero sits far out in frequency).
        //    MinProminence rejects candidates that dip only a little below the highest
        //    point seen so far in the scan, continuing to look for the first *significant*
        //    minimum instead of the first incidental one.
        const float MinProminence = 3.0f;
        int zeroBin = -1;
        float peakSoFar = smoothed[4];
        for (int r = 4; r < maxR - 1; r++)
        {
            if (smoothed[r] > peakSoFar) peakSoFar = smoothed[r];
            bool isLocalMin = smoothed[r] <= smoothed[r - 1] && smoothed[r] <= smoothed[r + 1]
                               && (smoothed[r] < smoothed[r - 1] || smoothed[r] < smoothed[r + 1]);
            if (isLocalMin && peakSoFar - smoothed[r] >= MinProminence)
            {
                zeroBin = r;
                break;
            }
        }

        if (zeroBin < 0)
        {
            // Safe fallback: no local minimum found in the profile.
            return new DefocusEstimate(Radius: 0f, Confidence: 0f);
        }

        float rhoZero = zeroBin / (float)fftSize;
        float radius = BesselFirstZeroOverTwoPi / rhoZero;

        // Confidence: normalized depth of the minimum vs. the profile two bins
        // earlier. Deep dips -> high confidence; a flat profile -> near zero.
        float dipDepth = Math.Max(0f, smoothed[Math.Max(0, zeroBin - 2)] - smoothed[zeroBin]);
        float confidence = Math.Clamp(dipDepth / Math.Max(Math.Abs(smoothed[zeroBin]), 1e-3f), 0f, 1f);

        return new DefocusEstimate(radius, confidence);
    }

    private static double[] HannWindow(int n)
    {
        var w = new double[n];
        for (int i = 0; i < n; i++)
            w[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (n - 1)));
        return w;
    }
}
