using Deblur.Engine;
using Xunit;

namespace Deblur.Tests;

public class RegionOfInterestTests
{
    [Fact]
    public void Contains_InteriorPoint_True()
    {
        var roi = new RegionOfInterest(10, 20, 100, 50, 12);
        Assert.True(roi.Contains(10, 20));
        Assert.True(roi.Contains(109, 69));
    }

    [Fact]
    public void Contains_BoundaryAndOutsidePoints()
    {
        var roi = new RegionOfInterest(10, 20, 100, 50, 12);
        Assert.False(roi.Contains(9, 20));    // just outside left
        Assert.False(roi.Contains(10, 19));   // just outside top
        Assert.False(roi.Contains(110, 20));  // right edge exclusive
        Assert.False(roi.Contains(10, 70));   // bottom edge exclusive
    }

    [Fact]
    public void ClampFeatherToHalfMinDim_LimitsWhenExcessive()
    {
        var small = new RegionOfInterest(0, 0, 10, 20, 100);
        var clamped = small.ClampFeatherToHalfMinDim();
        Assert.Equal(5, clamped.FeatherRadius); // min(10,20)/2 = 5
    }

    [Fact]
    public void ClampFeatherToHalfMinDim_NoOpWhenSmall()
    {
        var roi = new RegionOfInterest(0, 0, 100, 100, 12);
        var clamped = roi.ClampFeatherToHalfMinDim();
        Assert.Equal(12, clamped.FeatherRadius);
    }
}
