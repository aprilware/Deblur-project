namespace Deblur.Engine;

public sealed class GaussianBlurKernel : IBlurKernel
{
    public float[,] Build(KernelParams p)
    {
        if (p.Sigma < 0f) throw new ArgumentOutOfRangeException(nameof(p.Sigma));

        int r = Math.Max(0, (int)Math.Ceiling(3.0 * p.Sigma));
        int size = 2 * r + 1;
        var k = new float[size, size];

        if (r == 0)
        {
            k[0, 0] = 1f;
            return k;
        }

        double twoSigmaSq = 2.0 * p.Sigma * p.Sigma;
        float total = 0f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double dx = x - r;
                double dy = y - r;
                float w = (float)Math.Exp(-(dx * dx + dy * dy) / twoSigmaSq);
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
