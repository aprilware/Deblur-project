namespace Deblur.Engine.Blind;

/// <summary>
/// Osher-Rudin shock filter, one pass: u_t = -sign(Δu) · |∇u|.
/// Sharpens edges while preserving flat regions. Stable for dt ≤ 0.25.
/// </summary>
public static class ShockFilter
{
    public static float[] ApplyOnce(float[] image, int w, int h, float dt)
    {
        var result = new float[image.Length];
        for (int y = 0; y < h; y++)
        {
            int ym = Math.Max(0, y - 1);
            int yp = Math.Min(h - 1, y + 1);
            for (int x = 0; x < w; x++)
            {
                int xm = Math.Max(0, x - 1);
                int xp = Math.Min(w - 1, x + 1);
                float c  = image[y * w + x];
                float dx = 0.5f * (image[y * w + xp] - image[y * w + xm]);
                float dy = 0.5f * (image[yp * w + x] - image[ym * w + x]);
                float lap = image[y * w + xp] + image[y * w + xm]
                          + image[yp * w + x] + image[ym * w + x] - 4f * c;
                float grad = MathF.Sqrt(dx * dx + dy * dy);
                float sign = lap > 0f ? 1f : (lap < 0f ? -1f : 0f);
                float v = c - dt * sign * grad;
                if (!float.IsFinite(v)) v = c;
                result[y * w + x] = Math.Clamp(v, 0f, 1f);
            }
        }
        return result;
    }
}
