namespace Deblur.Engine;

public sealed class ProxyReadyEventArgs : EventArgs
{
    public required byte[] Bgra { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
}

public sealed class DeblurJobRunner : IDisposable
{
    private readonly IBlurKernel _kernel;
    private readonly IDeconvolver _deconvolver;
    private readonly Thread _worker;
    private readonly ManualResetEventSlim _signal = new(false);
    private readonly object _lock = new();

    private ImageBuffer? _proxy;
    private KernelParams? _pending;
    private volatile bool _running = true;

    public event EventHandler<ProxyReadyEventArgs>? ProxyReady;

    public bool HasPending
    {
        get { lock (_lock) return _pending.HasValue; }
    }

    public DeblurJobRunner(IBlurKernel kernel, IDeconvolver deconvolver)
    {
        _kernel = kernel;
        _deconvolver = deconvolver;
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "DeblurWorker" };
        _worker.Start();
    }

    public void SetProxy(ImageBuffer proxy)
    {
        lock (_lock) _proxy = proxy;
    }

    public void Request(KernelParams p)
    {
        lock (_lock) _pending = p;   // overwrite: only latest matters
        _signal.Set();
    }

    public Task<ImageBuffer> RenderFullAsync(
        ImageBuffer fullRes, KernelParams p, float proxyScale, IProgress<double>? progress = null)
    {
        return Task.Run(() =>
        {
            progress?.Report(0.1);
            var scaledLength = p.Length / Math.Max(proxyScale, 1e-6f);
            if (scaledLength < 1f)
            {
                progress?.Report(1.0);
                return fullRes.Clone();
            }
            var psf = _kernel.Build(p with { Length = scaledLength });
            progress?.Report(0.3);
            var result = _deconvolver.Apply(fullRes, psf, new DeconvolutionParams(K: p.Smoothness));
            progress?.Report(1.0);
            return result;
        });
    }

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
                    if (_pending is null || _proxy is null) break;
                    p = _pending.Value;
                    proxy = _proxy;
                    _pending = null;
                }

                ImageBuffer deconv;
                if (p.Length < 1f)
                {
                    // No motion → skip Wiener; show the untouched proxy.
                    deconv = proxy;
                }
                else
                {
                    var psf = _kernel.Build(p);
                    deconv = _deconvolver.Apply(
                        proxy, psf, new DeconvolutionParams(K: p.Smoothness));
                }

                // Convert to BGRA.
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
