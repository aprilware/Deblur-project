using Deblur.Engine.Blind;
using Xunit;

namespace Deblur.Tests.Blind;

public class KernelProjectionTests
{
    [Fact]
    public void OutputDimensionsMatchWindowSize()
    {
        var raw = new float[64, 64];
        raw[32, 32] = 1f;
        var k = KernelProjection.Project(raw, 5, 0.05f);
        Assert.Equal(5, k.GetLength(0));
        Assert.Equal(5, k.GetLength(1));
    }

    [Fact]
    public void CroppedAroundArgmax()
    {
        var raw = new float[64, 64];
        raw[10, 20] = 1f;
        raw[10, 21] = 0.5f;
        raw[11, 20] = 0.5f;
        var k = KernelProjection.Project(raw, 5, 0.05f);
        // Center pixel should be argmax value (normalized).
        Assert.True(k[2, 2] > k[0, 0]);
        Assert.True(k[2, 2] > k[4, 4]);
    }

    [Fact]
    public void SparsityThreshold_ZeroesSmallValues()
    {
        var raw = new float[5, 5];
        raw[2, 2] = 1f;
        raw[0, 0] = 0.04f; // 4% of max — below 5% threshold
        raw[4, 4] = 0.06f; // 6% of max — above
        var k = KernelProjection.Project(raw, 5, 0.05f);
        // k[0,0] would be normalized. Its RAW pre-normalization value came from
        // 0.04 which is below the 5% threshold, so it should be 0.
        Assert.Equal(0f, k[0, 0]);
        Assert.True(k[4, 4] > 0f);
    }

    [Fact]
    public void NonNegativity()
    {
        var raw = new float[5, 5];
        raw[2, 2] = 1f;
        raw[1, 1] = -0.5f;
        raw[3, 3] = 0.3f;
        var k = KernelProjection.Project(raw, 5, 0.05f);
        for (int y = 0; y < 5; y++)
            for (int x = 0; x < 5; x++)
                Assert.True(k[y, x] >= 0f);
    }

    [Fact]
    public void SumsToOne()
    {
        var raw = new float[5, 5];
        raw[2, 2] = 0.7f;
        raw[2, 3] = 0.3f;
        raw[3, 2] = 0.1f;
        var k = KernelProjection.Project(raw, 5, 0.05f);
        float sum = 0;
        for (int y = 0; y < 5; y++)
            for (int x = 0; x < 5; x++)
                sum += k[y, x];
        Assert.InRange(Math.Abs(sum - 1f), 0f, 1e-5f);
    }
}
