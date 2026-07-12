namespace Deblur.Engine.Estimation;

public sealed record NoiseEstimate(float SigmaNoise, float SigmaSignal, float SuggestedK, float Confidence);
