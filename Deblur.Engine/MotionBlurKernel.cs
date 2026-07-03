namespace Deblur.Engine;

public sealed class MotionBlurKernel : IBlurKernel
{
    public float[,] Build(KernelParams p)
    {
        if (p.Length < 1f) throw new ArgumentOutOfRangeException(nameof(p.Length));

        int r = (int)Math.Ceiling(p.Length);
        int size = 2 * r + 1;
        var k = new float[size, size];

        if (p.Length <= 1f)
        {
            k[r, r] = 1f;
            return k;
        }

        // Line segment from -halfLen*dir to +halfLen*dir, through the kernel center.
        double halfLen = p.Length / 2.0;
        double rad = p.Angle * Math.PI / 180.0;
        double dx = Math.Cos(rad);
        double dy = Math.Sin(rad);
        double ax = -halfLen * dx, ay = -halfLen * dy;
        double bx = +halfLen * dx, by = +halfLen * dy;

        float total = 0f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                // Sample point in kernel-centered coords.
                double sx = x - r;
                double sy = y - r;
                double dist = PointToSegmentDistance(sx, sy, ax, ay, bx, by);
                float w = (float)Math.Max(0.0, 1.0 - dist);
                k[y, x] = w;
                total += w;
            }
        }

        // Normalize to sum = 1.
        if (total > 0f)
        {
            float inv = 1f / total;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    k[y, x] *= inv;
        }
        return k;
    }

    private static double PointToSegmentDistance(
        double px, double py, double ax, double ay, double bx, double by)
    {
        double vx = bx - ax, vy = by - ay;
        double wx = px - ax, wy = py - ay;
        double c1 = vx * wx + vy * wy;
        double c2 = vx * vx + vy * vy;
        double t = c2 > 0 ? Math.Clamp(c1 / c2, 0.0, 1.0) : 0.0;
        double cx = ax + t * vx;
        double cy = ay + t * vy;
        return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }
}
