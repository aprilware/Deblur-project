using Deblur.Engine;
using Xunit;

namespace Deblur.Tests;

public class EdgeTaperTests
{
    [Fact]
    public void CenterUnchanged()
    {
        var padded = new float[16, 16];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                padded[y, x] = 0.5f;
        // Add a spike well inside the interior.
        padded[8, 8] = 1f;
        EdgeTaper.ApplyInPlace(padded, pad: 4);
        Assert.Equal(1f, padded[8, 8]);
        Assert.Equal(0.5f, padded[8, 9]);
    }

    [Fact]
    public void BorderBlendsTowardInteriorMean()
    {
        var padded = new float[16, 16];
        // interior = 0.8, ring reflected but we can pretend the ring is 0.0
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                padded[y, x] = (y < 4 || y >= 12 || x < 4 || x >= 12) ? 0.0f : 0.8f;
        EdgeTaper.ApplyInPlace(padded, pad: 4);
        // Corner should be closer to interior mean 0.8 than raw 0.0.
        Assert.True(padded[0, 0] > 0.05f, $"corner {padded[0, 0]} not blended toward mean");
    }
}
