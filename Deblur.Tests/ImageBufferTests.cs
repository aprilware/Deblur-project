using Deblur.Engine;
using Xunit;

namespace Deblur.Tests;

public class ImageBufferTests
{
    [Fact]
    public void Ctor_Dimensions_AllocatesChannels()
    {
        var buf = new ImageBuffer(4, 3);
        Assert.Equal(4, buf.Width);
        Assert.Equal(3, buf.Height);
        Assert.Equal(12, buf.R.Length);
        Assert.Equal(12, buf.G.Length);
        Assert.Equal(12, buf.B.Length);
        Assert.Equal(12, buf.PixelCount);
    }

    [Fact]
    public void Ctor_RejectsNonPositiveDimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageBuffer(0, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageBuffer(4, -1));
    }

    [Fact]
    public void Ctor_WithChannels_ValidatesLengths()
    {
        var r = new float[12]; var g = new float[12]; var b = new float[11];
        Assert.Throws<ArgumentException>(() => new ImageBuffer(4, 3, r, g, b));
    }

    [Fact]
    public void Clone_ProducesIndependentCopy()
    {
        var buf = new ImageBuffer(2, 2);
        buf.R[0] = 0.5f;
        var copy = buf.Clone();
        copy.R[0] = 0.9f;
        Assert.Equal(0.5f, buf.R[0]);
        Assert.Equal(0.9f, copy.R[0]);
    }
}
