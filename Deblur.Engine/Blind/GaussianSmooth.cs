namespace Deblur.Engine.Blind;

public static class GaussianSmooth
{
    public static float[] Apply(float[] image, int w, int h, float sigma)
    {
        if (sigma <= 0f) return (float[])image.Clone();
        int radius = (int)Math.Ceiling(3.0 * sigma);
        int size = 2 * radius + 1;
        var kernel = new float[size];
        float sum = 0;
        for (int i = 0; i < size; i++)
        {
            float d = i - radius;
            kernel[i] = MathF.Exp(-d * d / (2f * sigma * sigma));
            sum += kernel[i];
        }
        for (int i = 0; i < size; i++) kernel[i] /= sum;

        // Separable: pass along x, then y.
        var tmp = new float[image.Length];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float acc = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    int xk = Math.Clamp(x + k, 0, w - 1);
                    acc += image[y * w + xk] * kernel[k + radius];
                }
                tmp[y * w + x] = acc;
            }
        }
        var result = new float[image.Length];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float acc = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    int yk = Math.Clamp(y + k, 0, h - 1);
                    acc += tmp[yk * w + x] * kernel[k + radius];
                }
                result[y * w + x] = acc;
            }
        }
        return result;
    }
}
