namespace Deblur.Engine;

public sealed record PipelineOptions(
    bool LinearLight,
    bool EdgeTaper,
    BoundaryMode BoundaryMode,
    bool LuminanceOnly)
{
    /// <summary>
    /// Optional cancellation for long-running iterative deconvolvers (Richardson-Lucy,
    /// Landweber). Frequency-domain deconvolvers ignore this — they complete in one shot.
    /// The runner threads its RenderFullAsync CancellationToken into options via `with`
    /// so mid-iteration cancellation on the full-res render actually stops the work.
    /// </summary>
    public CancellationToken CancellationToken { get; init; } = default;

    public static PipelineOptions Default { get; } =
        new(LinearLight: true, EdgeTaper: true, BoundaryMode: BoundaryMode.Reflect, LuminanceOnly: false);
}
