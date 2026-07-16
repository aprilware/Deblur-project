namespace Deblur.Engine;

/// <summary>
/// IBlurKernel implementation that carries an arbitrary user-accepted PSF.
/// Used by the blind-kernel handoff flow: MainViewModel.AcceptBlindKernel
/// clones the estimated kernel and calls SetPsf; subsequent renders using
/// AlgorithmType != BlindDeconvolution + BlurType.Custom use this kernel.
///
/// Not thread-safe. Assumes single-threaded runner invocation (matches the
/// existing DeblurJobRunner discipline). Once set, the PSF stays until the
/// next SetPsf call — a null-PSF Build would indicate a VM/runner race and
/// throws.
/// </summary>
public sealed class CustomPsfKernel : IBlurKernel
{
    private float[,]? _psf;

    public void SetPsf(float[,] psf)
    {
        if (psf is null) throw new ArgumentNullException(nameof(psf));
        _psf = psf;
    }

    public float[,] Build(KernelParams p)
    {
        if (_psf is null)
            throw new InvalidOperationException(
                "CustomPsfKernel.Build called before SetPsf; VM/runner state has diverged.");
        return _psf;
    }
}
