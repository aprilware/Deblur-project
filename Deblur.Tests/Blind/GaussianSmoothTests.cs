using Deblur.Engine.Blind;
using Xunit;

namespace Deblur.Tests.Blind;

public class GaussianSmoothTests
{
    [Fact]
    public void Constant_Unchanged()
    {
        var img = new float[16 * 16];
        Array.Fill(img, 0.4f);
        var s = GaussianSmooth.Apply(img, 16, 16, 1.0f);
        foreach (var v in s) Assert.InRange(Math.Abs(v - 0.4f), 0f, 1e-4f);
    }

    [Fact]
    public void Impulse_SpreadsToNeighborhood()
    {
        var img = new float[16 * 16];
        img[8 * 16 + 8] = 1f;
        var s = GaussianSmooth.Apply(img, 16, 16, 1.0f);
        Assert.True(s[8 * 16 + 8] > 0.1f && s[8 * 16 + 8] < 0.3f, $"center: {s[8*16+8]}");
        Assert.True(s[8 * 16 + 9] > 0.05f, "neighbor should have positive weight");
    }
}
