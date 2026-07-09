namespace Deblur.Engine;

public sealed record PipelineOptions(
    bool LinearLight,
    bool EdgeTaper,
    BoundaryMode BoundaryMode,
    bool LuminanceOnly)
{
    public static PipelineOptions Default { get; } =
        new(LinearLight: true, EdgeTaper: true, BoundaryMode: BoundaryMode.Reflect, LuminanceOnly: false);
}
