namespace Deblur.Engine.Estimation;

public static class WaveletNoiseEstimator
{
    public const string Id = "wavelet-mad-noise";
    public const string Version = "1.0";

    public static NoiseEstimate Estimate(float[] grayscale, int width, int height)
    {
        if (width < 2 || height < 2)
            throw new ArgumentException("image must be at least 2x2");

        int hw = width / 2, hh = height / 2;
        var ll = new float[hw * hh];
        var hh_ = new float[hw * hh];
        for (int y = 0; y < hh; y++)
        {
            for (int x = 0; x < hw; x++)
            {
                float a = grayscale[(2 * y) * width + (2 * x)];
                float b = grayscale[(2 * y) * width + (2 * x + 1)];
                float c = grayscale[(2 * y + 1) * width + (2 * x)];
                float d = grayscale[(2 * y + 1) * width + (2 * x + 1)];
                int i = y * hw + x;
                ll[i] = (a + b + c + d) * 0.5f;
                hh_[i] = (a - b - c + d) * 0.5f;
            }
        }

        float medHH = Median(hh_);
        var absDev = new float[hh_.Length];
        for (int i = 0; i < hh_.Length; i++) absDev[i] = Math.Abs(hh_[i] - medHH);
        float mad = Median(absDev);
        float sigmaNoise = mad / 0.6745f;

        double meanLL = 0;
        for (int i = 0; i < ll.Length; i++) meanLL += ll[i];
        meanLL /= ll.Length;
        double varLL = 0;
        for (int i = 0; i < ll.Length; i++)
        {
            double d = ll[i] - meanLL;
            varLL += d * d;
        }
        varLL /= ll.Length;
        // Haar 2D LL = (a + b + c + d) / 2, so var(LL) ≈ 4·var(signal) + var(noise).
        // Undo the 4× amplification before subtracting noise power so sigmaSignal
        // reflects per-pixel signal variance. Without this, sigmaSignal is 2× low
        // and suggestedK = σ²_noise / σ²_signal runs ~4× low — Wiener/Tikhonov
        // users who accept the K under-regularize.
        float sigmaSignal = (float)Math.Sqrt(Math.Max(varLL / 4.0 - sigmaNoise * sigmaNoise, 1e-8));

        float noiseVar = sigmaNoise * sigmaNoise;
        float signalVar = sigmaSignal * sigmaSignal;
        float suggestedK = Math.Clamp(noiseVar / Math.Max(signalVar, 1e-8f), 1e-6f, 1f);
        float confidence = Math.Clamp(1f - sigmaNoise / Math.Max(sigmaSignal, 1e-6f), 0f, 1f);

        return new NoiseEstimate(sigmaNoise, sigmaSignal, suggestedK, confidence);
    }

    private static float Median(float[] arr)
    {
        var copy = (float[])arr.Clone();
        Array.Sort(copy);
        int n = copy.Length;
        return n % 2 == 1 ? copy[n / 2] : 0.5f * (copy[n / 2 - 1] + copy[n / 2]);
    }
}
