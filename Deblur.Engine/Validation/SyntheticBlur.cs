namespace Deblur.Engine.Validation;

public static class SyntheticBlur
{
    public static ImageBuffer Apply(ImageBuffer src, float[,] psf, float gaussianNoiseSigma, int seed)
    {
        int kh = psf.GetLength(0);
        int kw = psf.GetLength(1);
        int cy = kh / 2, cx = kw / 2;
        int w = src.Width, h = src.Height;
        var dst = new ImageBuffer(w, h);
        var rng = new Random(seed);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float r = 0, g = 0, b = 0;
                for (int ky = 0; ky < kh; ky++)
                {
                    int sy = ReflectIndex(y + ky - cy, h);
                    for (int kx = 0; kx < kw; kx++)
                    {
                        int sx = ReflectIndex(x + kx - cx, w);
                        float p = psf[ky, kx];
                        int si = sy * w + sx;
                        r += src.R[si] * p;
                        g += src.G[si] * p;
                        b += src.B[si] * p;
                    }
                }
                if (gaussianNoiseSigma > 0f)
                {
                    r += (float)(gaussianNoiseSigma * Gaussian(rng));
                    g += (float)(gaussianNoiseSigma * Gaussian(rng));
                    b += (float)(gaussianNoiseSigma * Gaussian(rng));
                }
                int di = y * w + x;
                dst.R[di] = Math.Clamp(r, 0f, 1f);
                dst.G[di] = Math.Clamp(g, 0f, 1f);
                dst.B[di] = Math.Clamp(b, 0f, 1f);
            }
        }
        return dst;
    }

    private static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static int ReflectIndex(int i, int len)
    {
        if (len <= 1) return 0;
        int period = 2 * (len - 1);
        int m = ((i % period) + period) % period;
        return m < len ? m : period - m;
    }
}
