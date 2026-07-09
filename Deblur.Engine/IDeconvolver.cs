namespace Deblur.Engine;

public interface IDeconvolver
{
    AlgorithmMetadata Metadata { get; }

    ImageBuffer Apply(
        ImageBuffer input,
        float[,] psf,
        DeconvolutionParams p,
        PipelineOptions? options = null);
}
