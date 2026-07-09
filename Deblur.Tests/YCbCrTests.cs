using Deblur.Engine.Color;
using Xunit;

namespace Deblur.Tests;

public class YCbCrTests
{
    [Fact]
    public void RoundTrip_WithinTolerance()
    {
        var r = new float[] { 0f, 0.25f, 0.5f, 0.75f, 1f, 0.3f, 0.6f };
        var g = new float[] { 0f, 0.5f, 0.5f, 0.5f, 1f, 0.7f, 0.2f };
        var b = new float[] { 0f, 0.75f, 0.5f, 0.25f, 1f, 0.1f, 0.9f };
        var (y, cb, cr) = YCbCr.FromRgb(r, g, b);
        var (r2, g2, b2) = YCbCr.ToRgb(y, cb, cr);
        for (int i = 0; i < r.Length; i++)
        {
            Assert.InRange(Math.Abs(r2[i] - r[i]), 0f, 1e-5f);
            Assert.InRange(Math.Abs(g2[i] - g[i]), 0f, 1e-5f);
            Assert.InRange(Math.Abs(b2[i] - b[i]), 0f, 1e-5f);
        }
    }

    [Fact]
    public void Grayscale_YEqualsIntensity_CbCrHalf()
    {
        var (y, cb, cr) = YCbCr.FromRgb(new[] { 0.4f }, new[] { 0.4f }, new[] { 0.4f });
        Assert.InRange(Math.Abs(y[0] - 0.4f), 0f, 1e-5f);
        Assert.InRange(Math.Abs(cb[0] - 0.5f), 0f, 1e-5f);
        Assert.InRange(Math.Abs(cr[0] - 0.5f), 0f, 1e-5f);
    }
}
