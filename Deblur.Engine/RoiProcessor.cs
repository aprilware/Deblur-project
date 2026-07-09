namespace Deblur.Engine;

public static class RoiProcessor
{
    public static ImageBuffer ApplyToRoi(
        ImageBuffer full,
        RegionOfInterest roi,
        int psfRadius,
        Func<ImageBuffer, ImageBuffer> deconvolve)
    {
        if (roi.Width <= 0 || roi.Height <= 0)
            throw new ArgumentException("ROI dimensions must be positive.");
        var clampedRoi = roi.ClampFeatherToHalfMinDim();
        int pad = Math.Max(psfRadius, clampedRoi.FeatherRadius);
        int ex = clampedRoi.X - pad;
        int ey = clampedRoi.Y - pad;
        int ew = clampedRoi.Width + 2 * pad;
        int eh = clampedRoi.Height + 2 * pad;

        var extract = new ImageBuffer(ew, eh);
        for (int dy = 0; dy < eh; dy++)
        {
            int sy = ReflectIndex(ey + dy, full.Height);
            for (int dx = 0; dx < ew; dx++)
            {
                int sx = ReflectIndex(ex + dx, full.Width);
                int si = sy * full.Width + sx;
                int di = dy * ew + dx;
                extract.R[di] = full.R[si];
                extract.G[di] = full.G[si];
                extract.B[di] = full.B[si];
            }
        }

        var deconvolved = deconvolve(extract);
        if (deconvolved.Width != ew || deconvolved.Height != eh)
            throw new InvalidOperationException(
                $"deconvolve returned {deconvolved.Width}x{deconvolved.Height}, expected {ew}x{eh}.");

        var result = full.Clone(); // preserves SourceBitDepth

        int F = clampedRoi.FeatherRadius;
        int rw = clampedRoi.Width, rh = clampedRoi.Height;
        int rx = clampedRoi.X, ry = clampedRoi.Y;
        for (int my = 0; my < rh; my++)
        {
            int fullY = ry + my;
            if (fullY < 0 || fullY >= full.Height) continue;
            for (int mx = 0; mx < rw; mx++)
            {
                int fullX = rx + mx;
                if (fullX < 0 || fullX >= full.Width) continue;

                float alpha;
                if (F <= 0)
                {
                    alpha = 1f;
                }
                else
                {
                    int d = Math.Min(Math.Min(mx, my), Math.Min(rw - 1 - mx, rh - 1 - my));
                    if (d >= F) alpha = 1f;
                    else alpha = 0.5f * (1f - MathF.Cos(MathF.PI * d / F));
                }

                int di = fullY * full.Width + fullX;
                int ei = (pad + my) * ew + (pad + mx);
                result.R[di] = alpha * deconvolved.R[ei] + (1f - alpha) * full.R[di];
                result.G[di] = alpha * deconvolved.G[ei] + (1f - alpha) * full.G[di];
                result.B[di] = alpha * deconvolved.B[ei] + (1f - alpha) * full.B[di];
            }
        }
        return result;
    }

    private static int ReflectIndex(int i, int len)
    {
        if (len <= 1) return 0;
        int period = 2 * (len - 1);
        int m = ((i % period) + period) % period;
        return m < len ? m : period - m;
    }
}
