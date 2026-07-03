namespace Deblur.Engine;

public interface IBlurKernel
{
    float[,] Build(KernelParams p);
}
