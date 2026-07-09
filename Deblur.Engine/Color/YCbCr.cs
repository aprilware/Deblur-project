namespace Deblur.Engine.Color;

public static class YCbCr
{
    public static (float[] y, float[] cb, float[] cr) FromRgb(float[] r, float[] g, float[] b)
    {
        int n = r.Length;
        var y = new float[n]; var cb = new float[n]; var cr = new float[n];
        for (int i = 0; i < n; i++)
        {
            float yi = 0.299f * r[i] + 0.587f * g[i] + 0.114f * b[i];
            y[i]  = yi;
            cb[i] = 0.5f + (b[i] - yi) / 1.772f;
            cr[i] = 0.5f + (r[i] - yi) / 1.402f;
        }
        return (y, cb, cr);
    }

    public static (float[] r, float[] g, float[] b) ToRgb(float[] y, float[] cb, float[] cr)
    {
        int n = y.Length;
        var r = new float[n]; var g = new float[n]; var b = new float[n];
        for (int i = 0; i < n; i++)
        {
            float cbC = cb[i] - 0.5f;
            float crC = cr[i] - 0.5f;
            r[i] = y[i] + 1.402f * crC;
            b[i] = y[i] + 1.772f * cbC;
            g[i] = y[i] - (0.299f * 1.402f / 0.587f) * crC - (0.114f * 1.772f / 0.587f) * cbC;
        }
        return (r, g, b);
    }
}
