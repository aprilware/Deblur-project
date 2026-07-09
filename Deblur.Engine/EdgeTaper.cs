namespace Deblur.Engine;

public static class EdgeTaper
{
    /// <summary>
    /// Applies a separable Tukey (cosine) taper over the outer <paramref name="pad"/> pixels
    /// of a padded FFT canvas, blending them toward the interior mean so periodic-convolution
    /// wrap does not ring at the boundary. In place.
    /// </summary>
    public static void ApplyInPlace(float[,] padded, int pad)
    {
        int h = padded.GetLength(0);
        int w = padded.GetLength(1);
        if (pad <= 0 || w <= 2 * pad || h <= 2 * pad) return;

        double sum = 0; long count = 0;
        for (int y = pad; y < h - pad; y++)
            for (int x = pad; x < w - pad; x++)
            { sum += padded[y, x]; count++; }
        float mean = count > 0 ? (float)(sum / count) : 0f;

        var wx = new float[w];
        var wy = new float[h];
        for (int i = 0; i < w; i++) wx[i] = Taper(i, w, pad);
        for (int i = 0; i < h; i++) wy[i] = Taper(i, h, pad);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float m = wx[x] * wy[y];
                padded[y, x] = m * padded[y, x] + (1f - m) * mean;
            }
        }
    }

    private static float Taper(int i, int len, int pad)
    {
        if (i < pad)
            return 0.5f * (1f - MathF.Cos(MathF.PI * i / pad));
        int right = len - 1 - i;
        if (right < pad)
            return 0.5f * (1f - MathF.Cos(MathF.PI * right / pad));
        return 1f;
    }
}
