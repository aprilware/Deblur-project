namespace Deblur.Engine;

public enum BoundaryMode { Reflect, Replicate, Periodic }

public static class BoundaryFill
{
    public static float[,] Pad(float[] channel, int w, int h, int pad, int fftSize, BoundaryMode mode)
    {
        var padded = new float[fftSize, fftSize];
        for (int y = 0; y < fftSize; y++)
        {
            int sy = MapIndex(y - pad, h, mode);
            for (int x = 0; x < fftSize; x++)
            {
                int sx = MapIndex(x - pad, w, mode);
                padded[y, x] = channel[sy * w + sx];
            }
        }
        return padded;
    }

    private static int MapIndex(int i, int len, BoundaryMode mode)
    {
        if (len <= 1) return 0;
        return mode switch
        {
            BoundaryMode.Reflect   => Reflect(i, len),
            BoundaryMode.Replicate => Math.Clamp(i, 0, len - 1),
            BoundaryMode.Periodic  => ((i % len) + len) % len,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    private static int Reflect(int i, int len)
    {
        int period = 2 * (len - 1);
        int m = ((i % period) + period) % period;
        return m < len ? m : period - m;
    }
}
