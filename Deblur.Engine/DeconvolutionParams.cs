namespace Deblur.Engine;

public readonly record struct DeconvolutionParams(float K, float? NoiseVariance = null);
