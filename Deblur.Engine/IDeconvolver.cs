namespace Deblur.Engine;

public interface IDeconvolver
{
    ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p);
}
