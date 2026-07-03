using System.Numerics;
using Deblur.Engine;
using Xunit;

namespace Deblur.Tests;

public class FftAdapterTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(129)]
    public void NextPow2_RoundsUp(int input)
    {
        int result = FftAdapter.NextPow2(input);
        Assert.True(result >= input);
        // result must be a power of two
        Assert.True((result & (result - 1)) == 0);
        // and result / 2 must be less than input (i.e. it's the *next* one)
        Assert.True(result / 2 < input);
    }

    [Fact]
    public void RoundTrip_RecoversOriginalWithinTolerance()
    {
        // 8x8 input with a couple of arbitrary non-zero values.
        var input = new float[8, 8];
        input[3, 4] = 1.0f;
        input[5, 1] = 0.5f;
        input[0, 0] = -0.25f;

        var freq = FftAdapter.Forward2D(input);
        var recovered = FftAdapter.Inverse2DReal(freq);

        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                Assert.Equal(input[y, x], recovered[y, x], 4);
    }
}
