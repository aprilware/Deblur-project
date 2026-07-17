namespace Deblur.Engine.Estimation;

public sealed record SuggestionRecord(
    string EstimatorId,
    string EstimatorVersion,
    object Value,
    float? Confidence,
    DateTime SuggestedAtUtc)
{
    public DateTime? AcceptedAtUtc { get; init; }
    public DateTime? DismissedAtUtc { get; init; }
}
