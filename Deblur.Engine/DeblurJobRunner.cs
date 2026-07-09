namespace Deblur.Engine;

public sealed class ProxyReadyEventArgs : EventArgs
{
    public required byte[] Bgra { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
}

public sealed class DeblurJobRunner : IDisposable
{
    private readonly IReadOnlyDictionary<BlurType, IBlurKernel> _kernels;
    private readonly IReadOnlyDictionary<AlgorithmType, IDeconvolver> _deconvolvers;
    private readonly Thread _worker;
    private readonly ManualResetEventSlim _signal = new(false);
    private readonly object _lock = new();

    private ImageBuffer? _proxy;
    private KernelParams? _pending;
    private volatile bool _running = true;

    public event EventHandler<ProxyReadyEventArgs>? ProxyReady;

    /// <summary>Fires on the worker thread each time the pending queue drains to empty.</summary>
    public event EventHandler? Idle;

    public bool HasPending
    {
        get { lock (_lock) return _pending.HasValue; }
    }

    public DeblurJobRunner(
        IReadOnlyDictionary<BlurType, IBlurKernel> kernels,
        IReadOnlyDictionary<AlgorithmType, IDeconvolver> deconvolvers)
    {
        _kernels = kernels;
        _deconvolvers = deconvolvers;
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "DeblurWorker" };
        _worker.Start();
    }

    public void SetProxy(ImageBuffer proxy)
    {
        lock (_lock) _proxy = proxy;
    }

    public void Request(KernelParams p)
    {
        lock (_lock) _pending = p;
        _signal.Set();
    }

    public Task<ImageBuffer> RenderFullAsync(
        ImageBuffer fullRes, KernelParams p, float proxyScale,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.1);
            float scaleInv = 1f / Math.Max(proxyScale, 1e-6f);
            var scaledParams = p with
            {
                Length = p.Length * scaleInv,
                Radius = p.Radius * scaleInv,
                Sigma  = p.Sigma  * scaleInv,
            };
            if (IsNoOp(scaledParams))
            {
                progress?.Report(1.0);
                return fullRes.Clone();
            }
            cancellationToken.ThrowIfCancellationRequested();
            var psf = _kernels[scaledParams.Type].Build(scaledParams);
            progress?.Report(0.3);
            cancellationToken.ThrowIfCancellationRequested();
            var result = _deconvolvers[scaledParams.Algorithm].Apply(fullRes, psf, new DeconvolutionParams(K: p.Smoothness));
            progress?.Report(1.0);
            return result;
        });
    }

    /// <summary>
    /// Returns true for parameter sets that produce a raw-passthrough (no deconvolution) result.
    /// Any BlurType this switch treats as a no-op need not be present in the injected kernel
    /// dictionary; any type that reaches the else branch of WorkerLoop / RenderFullAsync MUST
    /// have a corresponding entry. Keep this switch in sync with the dictionary the caller
    /// injects in MainViewModel.
    /// </summary>
    private static bool IsNoOp(KernelParams p) => p.Type switch
    {
        BlurType.Motion     => p.Length < 1f,
        BlurType.OutOfFocus => p.Radius < 1f,
        BlurType.Gaussian   => p.Sigma  < 1f,
        _                   => true,
    };

    private void WorkerLoop()
    {
        while (_running)
        {
            _signal.Wait();
            _signal.Reset();

            while (true)
            {
                KernelParams p;
                ImageBuffer? proxy;
                lock (_lock)
                {
                    if (_pending is null || _proxy is null)
                    {
                        if (_running) Idle?.Invoke(this, EventArgs.Empty);
                        break;
                    }
                    p = _pending.Value;
                    proxy = _proxy;
                    _pending = null;
                }

                ImageBuffer deconv;
                if (IsNoOp(p))
                {
                    deconv = proxy;
                }
                else
                {
                    var psf = _kernels[p.Type].Build(p);
                    deconv = _deconvolvers[p.Algorithm].Apply(
                        proxy, psf, new DeconvolutionParams(K: p.Smoothness));
                }

                int w = deconv.Width, h = deconv.Height;
                var bgra = new byte[w * h * 4];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        int o = i * 4;
                        bgra[o] = Clamp8(deconv.B[i]);
                        bgra[o + 1] = Clamp8(deconv.G[i]);
                        bgra[o + 2] = Clamp8(deconv.R[i]);
                        bgra[o + 3] = 255;
                    }
                }

                ProxyReady?.Invoke(this, new ProxyReadyEventArgs
                {
                    Bgra = bgra, Width = w, Height = h,
                });
            }
        }
    }

    private static byte Clamp8(float v)
    {
        int i = (int)MathF.Round(v * 255f);
        return (byte)Math.Clamp(i, 0, 255);
    }

    public void Dispose()
    {
        _running = false;
        _signal.Set();
        _worker.Join(1000);
        _signal.Dispose();
    }
}
