namespace Deblur.Engine;

public sealed class OutOfFocusBlurKernel : IBlurKernel
{
    public float[,] Build(KernelParams p)
    {
        if (p.Radius < 0f) throw new ArgumentOutOfRangeException(nameof(p.Radius));

        int r = Math.Max(0, (int)Math.Ceiling(p.Radius));
        int size = 2 * r + 1;
        var k = new float[size, size];

        if (r == 0)
        {
            k[0, 0] = 1f;
            return k;
        }

        float total = 0f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double dx = x - r;
                double dy = y - r;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                float w = (float)Math.Clamp(p.Radius + 0.5 - dist, 0.0, 1.0);
                k[y, x] = w;
                total += w;
            }
        }

        if (total > 0f)
        {
            float inv = 1f / total;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    k[y, x] *= inv;
        }
        return k;
    }
}
