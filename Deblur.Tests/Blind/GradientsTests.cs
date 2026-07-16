using Deblur.Engine.Blind;
using Xunit;

namespace Deblur.Tests.Blind;

public class GradientsTests
{
    [Fact]
    public void ComputeX_ConstantImage_ReturnsZeros()
    {
        var img = new float[16 * 16];
        Array.Fill(img, 0.5f);
        var dx = Gradients.ComputeX(img, 16, 16);
        foreach (var v in dx) Assert.InRange(Math.Abs(v), 0f, 1e-6f);
    }

    [Fact]
    public void ComputeX_LinearRamp_ReturnsSlope()
    {
        var img = new float[16 * 16];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                img[y * 16 + x] = x / 15f;
        var dx = Gradients.ComputeX(img, 16, 16);
        // Interior slope = 1/15 = 0.0667.
        for (int y = 0; y < 16; y++)
            for (int x = 1; x < 15; x++)
                Assert.InRange(dx[y * 16 + x], 0.06f, 0.075f);
    }

    [Fact]
    public void ComputeY_LinearRamp_ReturnsSlope()
    {
        var img = new float[16 * 16];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                img[y * 16 + x] = y / 15f;
        var dy = Gradients.ComputeY(img, 16, 16);
        for (int y = 1; y < 15; y++)
            for (int x = 0; x < 16; x++)
                Assert.InRange(dy[y * 16 + x], 0.06f, 0.075f);
    }

    [Fact]
    public void ComputeX_EdgeClamp_NoNaN()
    {
        var img = new float[16 * 16];
        for (int i = 0; i < img.Length; i++) img[i] = (float)i / img.Length;
        var dx = Gradients.ComputeX(img, 16, 16);
        foreach (var v in dx) Assert.True(float.IsFinite(v));
    }
}
