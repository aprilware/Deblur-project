using Deblur.Engine.Blind;
using Xunit;

namespace Deblur.Tests.Blind;

public class ShockFilterTests
{
    [Fact]
    public void ConstantImage_Unchanged()
    {
        var img = new float[16 * 16];
        Array.Fill(img, 0.5f);
        var s = ShockFilter.ApplyOnce(img, 16, 16, dt: 0.25f);
        foreach (var v in s) Assert.InRange(Math.Abs(v - 0.5f), 0f, 1e-4f);
    }

    [Fact]
    public void SoftEdge_Sharpens()
    {
        // Sigmoid edge across x=8 in a 16-wide image.
        var img = new float[32 * 32];
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
                img[y * 32 + x] = 1f / (1f + MathF.Exp(-(x - 16) * 0.5f));

        // 3 passes at dt=0.25.
        var s = img;
        for (int i = 0; i < 3; i++) s = ShockFilter.ApplyOnce(s, 32, 32, dt: 0.25f);

        // Gradient magnitude at the edge should increase.
        int mid = 16 * 32 + 16;
        float srcGrad = Math.Abs(img[mid + 1] - img[mid - 1]) / 2f;
        float outGrad = Math.Abs(s[mid + 1] - s[mid - 1]) / 2f;
        Assert.True(outGrad > srcGrad, $"expected sharpened edge, src grad {srcGrad:F3} out {outGrad:F3}");
    }
}
