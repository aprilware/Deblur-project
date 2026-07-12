namespace Deblur.Engine;

public readonly record struct KernelParams(
    BlurType Type,
    float Angle,
    float Length,
    float Smoothness,
    float Radius,
    float Sigma,
    AlgorithmType Algorithm,
    float? NoiseVariance = null);
