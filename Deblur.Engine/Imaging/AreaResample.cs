namespace Deblur.Engine.Imaging;

public static class AreaResample
{
    public static ImageBuffer Box(ImageBuffer src, int newW, int newH)
    {
        if (newW <= 0 || newH <= 0) throw new ArgumentOutOfRangeException();
        if (newW > src.Width || newH > src.Height)
            throw new ArgumentException("Upscale is out of scope; downscale only.");
        var dst = new ImageBuffer(newW, newH);
        double sxScale = (double)src.Width / newW;
        double syScale = (double)src.Height / newH;

        for (int dy = 0; dy < newH; dy++)
        {
            double y0 = dy * syScale;
            double y1 = (dy + 1) * syScale;
            int iy0 = (int)Math.Floor(y0);
            int iy1 = (int)Math.Ceiling(y1);
            if (iy1 > src.Height) iy1 = src.Height;
            for (int dx = 0; dx < newW; dx++)
            {
                double x0 = dx * sxScale;
                double x1 = (dx + 1) * sxScale;
                int ix0 = (int)Math.Floor(x0);
                int ix1 = (int)Math.Ceiling(x1);
                if (ix1 > src.Width) ix1 = src.Width;

                double sumR = 0, sumG = 0, sumB = 0, sumW = 0;
                for (int sy = iy0; sy < iy1; sy++)
                {
                    double wy = Math.Min(sy + 1, y1) - Math.Max(sy, y0);
                    for (int sx = ix0; sx < ix1; sx++)
                    {
                        double wx = Math.Min(sx + 1, x1) - Math.Max(sx, x0);
                        double wt = wx * wy;
                        int si = sy * src.Width + sx;
                        sumR += src.R[si] * wt;
                        sumG += src.G[si] * wt;
                        sumB += src.B[si] * wt;
                        sumW += wt;
                    }
                }
                int di = dy * newW + dx;
                float inv = sumW > 0 ? (float)(1.0 / sumW) : 0f;
                dst.R[di] = (float)(sumR * inv);
                dst.G[di] = (float)(sumG * inv);
                dst.B[di] = (float)(sumB * inv);
            }
        }
        return dst;
    }
}
