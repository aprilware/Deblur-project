using Deblur.Engine;

namespace Deblur.Tests.TestHelpers;

public static class SyntheticImages
{
    /// <summary>
    /// A non-periodic textured image — pure white-noise pattern, optionally smoothed
    /// with N 3×3 box passes. Use this (not Checkerboard) as ground truth for tests
    /// that inspect the cepstrum or spectrum for injected periodicities: periodic
    /// patterns like the checkerboard produce their own strong cepstral/spectral
    /// peaks that can dominate whatever the test is trying to measure.
    ///
    /// smoothPasses = 0 (default): pure white noise — spectrally flat, ideal for
    /// cepstral peak-detection tests where the cepstrum should be near-zero except
    /// where injected structure places a peak.
    ///
    /// smoothPasses > 0: lightly low-pass filtered — reduces the per-bin variance
    /// of pure white noise, giving Radon-style spectrum-line-integration tests a
    /// smoother angular profile. Still broadband, still non-periodic.
    /// </summary>
    public static ImageBuffer TexturedNoise(int width, int height, int seed, int smoothPasses = 0)
    {
        var rng = new Random(seed);
        var raw = new float[width * height];
        for (int i = 0; i < raw.Length; i++) raw[i] = (float)rng.NextDouble();

        var smoothed = new float[width * height];
        for (int pass = 0; pass < smoothPasses; pass++)
        {
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    float sum = 0f; int count = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int sy = Math.Clamp(y + dy, 0, height - 1);
                            int sx = Math.Clamp(x + dx, 0, width - 1);
                            sum += raw[sy * width + sx];
                            count++;
                        }
                    smoothed[y * width + x] = sum / count;
                }
            (raw, smoothed) = (smoothed, raw);
        }

        var buf = new ImageBuffer(width, height);
        for (int i = 0; i < raw.Length; i++)
        {
            buf.R[i] = raw[i]; buf.G[i] = raw[i]; buf.B[i] = raw[i];
        }
        return buf;
    }

    public static ImageBuffer Checkerboard(int width, int height, int cellSize)
    {
        var buf = new ImageBuffer(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool on = ((x / cellSize) + (y / cellSize)) % 2 == 0;
                float v = on ? 0.9f : 0.1f;
                int i = y * width + x;
                buf.R[i] = v; buf.G[i] = v; buf.B[i] = v;
            }
        }
        return buf;
    }

    public static ImageBuffer AddGaussianNoise(ImageBuffer input, float sigma, int seed)
    {
        var rng = new Random(seed);
        var copy = input.Clone();
        for (int i = 0; i < copy.PixelCount; i++)
        {
            copy.R[i] = Math.Clamp(copy.R[i] + (float)NextGaussian(rng, sigma), 0f, 1f);
            copy.G[i] = Math.Clamp(copy.G[i] + (float)NextGaussian(rng, sigma), 0f, 1f);
            copy.B[i] = Math.Clamp(copy.B[i] + (float)NextGaussian(rng, sigma), 0f, 1f);
        }
        return copy;
    }

    private static double NextGaussian(Random rng, float sigma)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return sigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    public static ImageBuffer Convolve(ImageBuffer input, float[,] kernel)
    {
        int kh = kernel.GetLength(0), kw = kernel.GetLength(1);
        int kry = kh / 2, krx = kw / 2;
        var outBuf = new ImageBuffer(input.Width, input.Height);
        for (int y = 0; y < input.Height; y++)
        {
            for (int x = 0; x < input.Width; x++)
            {
                float sr = 0, sg = 0, sb = 0;
                for (int ky = 0; ky < kh; ky++)
                {
                    int sy = Math.Clamp(y + ky - kry, 0, input.Height - 1);
                    for (int kx = 0; kx < kw; kx++)
                    {
                        int sx = Math.Clamp(x + kx - krx, 0, input.Width - 1);
                        float w = kernel[ky, kx];
                        int si = sy * input.Width + sx;
                        sr += input.R[si] * w;
                        sg += input.G[si] * w;
                        sb += input.B[si] * w;
                    }
                }
                int oi = y * input.Width + x;
                outBuf.R[oi] = sr; outBuf.G[oi] = sg; outBuf.B[oi] = sb;
            }
        }
        return outBuf;
    }

    public static float Psnr(ImageBuffer a, ImageBuffer b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
            throw new ArgumentException("size mismatch");
        double mse = 0;
        long n = a.PixelCount * 3L;
        for (int i = 0; i < a.PixelCount; i++)
        {
            double dr = a.R[i] - b.R[i];
            double dg = a.G[i] - b.G[i];
            double db = a.B[i] - b.B[i];
            mse += dr * dr + dg * dg + db * db;
        }
        mse /= n;
        if (mse <= 1e-12) return 200f;
        return (float)(10.0 * Math.Log10(1.0 / mse));
    }
}
