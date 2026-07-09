namespace Deblur.Engine.Color;

public static class SrgbLinear
{
    private static readonly float[] _byteToLinear = BuildByteLut();
    private static readonly float[] _ushortToLinear = BuildUshortLut();

    public static float ToLinear(byte v) => _byteToLinear[v];
    public static float ToLinear(ushort v) => _ushortToLinear[v];

    public static float ToLinear(float srgb)
    {
        // srgb in [0,1]; piecewise IEC 61966-2-1.
        if (srgb <= 0.04045f) return srgb / 12.92f;
        return MathF.Pow((srgb + 0.055f) / 1.055f, 2.4f);
    }

    public static float ToSrgbFloat(float linear)
    {
        if (linear <= 0.0031308f) return linear * 12.92f;
        return 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
    }

    public static byte ToSrgb8(float linear)
    {
        float s = ToSrgbFloat(linear);
        int i = (int)MathF.Round(s * 255f);
        return (byte)Math.Clamp(i, 0, 255);
    }

    public static ushort ToSrgb16(float linear)
    {
        float s = ToSrgbFloat(linear);
        int i = (int)MathF.Round(s * 65535f);
        return (ushort)Math.Clamp(i, 0, 65535);
    }

    public static void ToLinearInPlace(float[] srgbNormalized)
    {
        for (int i = 0; i < srgbNormalized.Length; i++)
            srgbNormalized[i] = ToLinear(srgbNormalized[i]);
    }

    public static void ToSrgbInPlace(float[] linear)
    {
        for (int i = 0; i < linear.Length; i++)
            linear[i] = ToSrgbFloat(linear[i]);
    }

    private static float[] BuildByteLut()
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++) t[i] = ToLinear(i / 255f);
        return t;
    }

    private static float[] BuildUshortLut()
    {
        var t = new float[65536];
        for (int i = 0; i < 65536; i++) t[i] = ToLinear(i / 65535f);
        return t;
    }
}
