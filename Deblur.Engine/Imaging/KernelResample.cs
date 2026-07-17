namespace Deblur.Engine.Imaging;

public static class KernelResample
{
    /// <summary>
    /// Area-average downscale of a kernel by <paramref name="scale"/> (0 &lt; scale &lt;= 1);
    /// output size is round(size * scale) forced odd. Renormalizes to sum = 1 so the
    /// downscaled kernel remains a valid PSF.
    /// </summary>
    public static float[,] Downscale(float[,] src, float scale)
    {
        if (scale <= 0f || scale > 1f) throw new ArgumentOutOfRangeException(nameof(scale));
        int srcH = src.GetLength(0), srcW = src.GetLength(1);
        if (scale >= 0.9999f) return (float[,])src.Clone();

        int dstH = Math.Max(1, (int)Math.Round(srcH * scale));
        int dstW = Math.Max(1, (int)Math.Round(srcW * scale));
        if (dstH % 2 == 0) dstH++;
        if (dstW % 2 == 0) dstW++;
        double sxScale = (double)srcW / dstW;
        double syScale = (double)srcH / dstH;

        var dst = new float[dstH, dstW];
        for (int dy = 0; dy < dstH; dy++)
        {
            double y0 = dy * syScale;
            double y1 = (dy + 1) * syScale;
            int iy0 = (int)Math.Floor(y0);
            int iy1 = Math.Min(srcH, (int)Math.Ceiling(y1));
            for (int dx = 0; dx < dstW; dx++)
            {
                double x0 = dx * sxScale;
                double x1 = (dx + 1) * sxScale;
                int ix0 = (int)Math.Floor(x0);
                int ix1 = Math.Min(srcW, (int)Math.Ceiling(x1));
                double sum = 0, wt = 0;
                for (int sy = iy0; sy < iy1; sy++)
                {
                    double wy = Math.Min(sy + 1, y1) - Math.Max(sy, y0);
                    for (int sx = ix0; sx < ix1; sx++)
                    {
                        double wx = Math.Min(sx + 1, x1) - Math.Max(sx, x0);
                        double w = wx * wy;
                        sum += src[sy, sx] * w;
                        wt += w;
                    }
                }
                dst[dy, dx] = (float)(wt > 0 ? sum / wt : 0);
            }
        }

        // Renormalize sum to 1.
        double total = 0;
        for (int y = 0; y < dstH; y++)
            for (int x = 0; x < dstW; x++)
                total += dst[y, x];
        if (total > 0)
        {
            float inv = (float)(1.0 / total);
            for (int y = 0; y < dstH; y++)
                for (int x = 0; x < dstW; x++)
                    dst[y, x] *= inv;
        }
        return dst;
    }
}
