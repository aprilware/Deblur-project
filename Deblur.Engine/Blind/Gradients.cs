namespace Deblur.Engine.Blind;

public static class Gradients
{
    /// <summary>Central-difference ∂/∂x with edge clamping. Result has same dimensions.</summary>
    public static float[] ComputeX(float[] image, int w, int h)
    {
        var result = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int xm = Math.Max(0, x - 1);
                int xp = Math.Min(w - 1, x + 1);
                result[y * w + x] = 0.5f * (image[y * w + xp] - image[y * w + xm]);
            }
        }
        return result;
    }

    public static float[] ComputeY(float[] image, int w, int h)
    {
        var result = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            int ym = Math.Max(0, y - 1);
            int yp = Math.Min(h - 1, y + 1);
            for (int x = 0; x < w; x++)
                result[y * w + x] = 0.5f * (image[yp * w + x] - image[ym * w + x]);
        }
        return result;
    }
}
