namespace Deblur.Engine;

public sealed record RegionOfInterest(int X, int Y, int Width, int Height, int FeatherRadius)
{
    public bool Contains(int px, int py)
        => px >= X && px < X + Width && py >= Y && py < Y + Height;

    /// <summary>
    /// Returns a copy with FeatherRadius clamped to at most half the smaller dimension.
    /// Prevents the feather band from consuming the entire ROI.
    /// </summary>
    public RegionOfInterest ClampFeatherToHalfMinDim()
    {
        int cap = Math.Min(Width, Height) / 2;
        return FeatherRadius <= cap ? this : this with { FeatherRadius = cap };
    }
}
