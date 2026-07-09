using Deblur.Engine;
using Xunit;

namespace Deblur.Tests;

public class BoundaryFillTests
{
    [Fact]
    public void Reflect_MatchesLegacyReflectIndex()
    {
        var channel = new float[] { 1, 2, 3, 4 }; // 4x1
        var padded = BoundaryFill.Pad(channel, w: 4, h: 1, pad: 2, fftSize: 8, BoundaryMode.Reflect);
        // fftSize row 0: reflect across [1,2,3,4] with pad=2:
        // index in fftSize: 0..7, source position = i - pad => -2,-1,0,1,2,3,4,5
        // reflect over [0..3] period 6: 2,1,0,1,2,3,4? no — validate via ReflectIndex bounce math
        Assert.Equal(3f, padded[0, 0]); // reflect(-2)=2 → channel[2]=3
        Assert.Equal(2f, padded[0, 1]); // reflect(-1)=1 → channel[1]=2
        Assert.Equal(1f, padded[0, 2]); // reflect(0)=0  → channel[0]=1
        Assert.Equal(4f, padded[0, 5]); // reflect(3)=3  → channel[3]=4
        Assert.Equal(3f, padded[0, 6]); // reflect(4)=2  → channel[2]=3
    }

    [Fact]
    public void Replicate_ClampsToEdges()
    {
        var channel = new float[] { 1, 2, 3, 4 };
        var padded = BoundaryFill.Pad(channel, 4, 1, pad: 2, fftSize: 8, BoundaryMode.Replicate);
        Assert.Equal(1f, padded[0, 0]);
        Assert.Equal(1f, padded[0, 1]);
        Assert.Equal(4f, padded[0, 6]);
        Assert.Equal(4f, padded[0, 7]);
    }

    [Fact]
    public void Periodic_WrapsModulo()
    {
        var channel = new float[] { 1, 2, 3, 4 };
        var padded = BoundaryFill.Pad(channel, 4, 1, pad: 2, fftSize: 8, BoundaryMode.Periodic);
        Assert.Equal(3f, padded[0, 0]); // (-2 mod 4) = 2 → channel[2]=3
        Assert.Equal(4f, padded[0, 1]); // (-1 mod 4) = 3 → channel[3]=4
    }
}
