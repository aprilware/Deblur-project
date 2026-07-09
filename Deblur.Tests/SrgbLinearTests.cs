using Deblur.Engine.Color;
using Xunit;

namespace Deblur.Tests;

public class SrgbLinearTests
{
    [Fact]
    public void ByteRoundTrip_WithinOneLsb()
    {
        for (int v = 0; v < 256; v++)
        {
            byte b = (byte)v;
            float lin = SrgbLinear.ToLinear(b);
            byte round = SrgbLinear.ToSrgb8(lin);
            Assert.InRange(Math.Abs(round - v), 0, 1);
        }
    }

    [Fact]
    public void FloatRoundTrip_WithinTolerance()
    {
        for (int i = 0; i <= 1000; i++)
        {
            float v = i / 1000f;
            float lin = SrgbLinear.ToLinear(v);
            float back = SrgbLinear.ToSrgbFloat(lin);
            Assert.InRange(Math.Abs(back - v), 0f, 1e-4f);
        }
    }

    [Fact]
    public void KnownPoints()
    {
        // At v=0.04045 sRGB the piecewise switches: linear = 0.04045/12.92 = 0.003130804...
        float lin = SrgbLinear.ToLinear(0.04045f);
        Assert.InRange(lin, 0.003130f, 0.003132f);
        // sRGB(0.5) linear ~= 0.21404
        Assert.InRange(SrgbLinear.ToLinear(0.5f), 0.213f, 0.216f);
    }

    [Fact]
    public void InPlaceMonotonic()
    {
        var arr = new float[] { 0f, 0.25f, 0.5f, 0.75f, 1f };
        SrgbLinear.ToLinearInPlace(arr);
        for (int i = 1; i < arr.Length; i++)
            Assert.True(arr[i] > arr[i - 1]);
    }

    [Fact]
    public void UshortRoundTrip_WithinOneLsb()
    {
        int[] samples = { 0, 1, 1000, 32767, 65534, 65535 };
        foreach (int v in samples)
        {
            ushort u = (ushort)v;
            float lin = SrgbLinear.ToLinear(u);
            ushort round = SrgbLinear.ToSrgb16(lin);
            Assert.InRange(Math.Abs((int)round - v), 0, 1);
        }
    }
}
