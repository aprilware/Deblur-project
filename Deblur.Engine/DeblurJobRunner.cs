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
    private readonly PipelineOptions _options;
    private readonly Thread _worker;
    private readonly ManualResetEventSlim _signal = new(false);
    private readonly object _lock = new();

    private ImageBuffer? _proxy;
    private KernelParams? _pending;
    private volatile bool _running = true;

    public RegionOfInterest? Roi { get; set; }

    // Matches the deconvolvers' internal FFT pad, max(psfW, psfH)/2 + 1, so the
    // ROI extract has enough context for Wiener's spatial-inversion tail to
    // converge. Undersizing (e.g., ceil(Length/2) for Motion) leaves the extract's
    // boundary too close to the deconvolution core and produces measurably worse
    // recovery than the whole-image path.
    private static int EstimatePsfRadius(KernelParams p)
    {
        // BlindDeconvolution's finest kernel window is 31x31 (radius 15). Blind ignores
        // the blur-type sliders, so basing the ROI pad on Length/Radius/Sigma leaves
        // only a few pixels of context and the 31x31 kernel can't recover meaningfully.
        // Match the finest kernel radius so the ROI extract has enough context for the
        // multi-scale MAP loop.
        if (p.Algorithm == AlgorithmType.BlindDeconvolution) return 32;

        return p.Type switch
        {
            // MotionBlurKernel: size = 2*ceil(Length)+1 → half-size = ceil(Length),
            // deconvolver pad = ceil(Length) + 1.
            BlurType.Motion     => (int)Math.Ceiling(p.Length) + 1,
            // OutOfFocusBlurKernel: size = 2*ceil(Radius)+1 → deconvolver pad = ceil(Radius) + 1.
            BlurType.OutOfFocus => (int)Math.Ceiling((double)p.Radius) + 1,
            // GaussianBlurKernel: size ≈ 2*ceil(3*Sigma)+1 → deconvolver pad ≈ ceil(3*Sigma) + 1.
            BlurType.Gaussian   => (int)Math.Ceiling(3.0 * p.Sigma) + 1,
            _                   => 1,
        };
    }

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
        : this(kernels, deconvolvers, PipelineOptions.Default)
    {
    }

    public DeblurJobRunner(
        IReadOnlyDictionary<BlurType, IBlurKernel> kernels,
        IReadOnlyDictionary<AlgorithmType, IDeconvolver> deconvolvers,
        PipelineOptions options)
    {
        _kernels = kernels;
        _deconvolvers = deconvolvers;
        _options = options;
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
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.3);
            cancellationToken.ThrowIfCancellationRequested();
            // Snapshot Roi once so a concurrent setter cannot cause a torn read between
            // the null-check and the argument. Safe today (IsBusy serializes callers)
            // but the invariant costs nothing to make structural.
            var roi = Roi;
            ImageBuffer result;
            if (roi is null)
            {
                result = IsNoOp(scaledParams) ? fullRes.Clone() : RunDeconvolve(fullRes, scaledParams, cancellationToken);
            }
            else
            {
                result = RoiProcessor.ApplyToRoi(
                    fullRes,
                    roi,
                    psfRadius: EstimatePsfRadius(scaledParams),
                    deconvolve: extract => IsNoOp(scaledParams)
                        ? extract.Clone()
                        : RunDeconvolve(extract, scaledParams, cancellationToken));
            }
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
    private static bool IsNoOp(KernelParams p)
    {
        // BlindDeconvolution estimates its own kernel — the blur-type sliders are
        // hint values only (currently unused). Never short-circuit it based on the
        // slider defaults, or blind renders would silently return the raw input.
        if (p.Algorithm == AlgorithmType.BlindDeconvolution) return false;

        return p.Type switch
        {
            BlurType.Motion     => p.Length < 1f,
            BlurType.OutOfFocus => p.Radius < 1f,
            BlurType.Gaussian   => p.Sigma  < 1f,
            _                   => true,
        };
    }

    /// <summary>
    /// Runs the configured deconvolver against <paramref name="input"/> for kernel parameters
    /// <paramref name="p"/>, applying linear-light decode/encode and luminance-only routing per
    /// <see cref="_options"/>. Order: sRGB -> linear -> YCbCr -> deconvolve Y -> recompose to
    /// linear RGB -> sRGB.
    /// </summary>
    private ImageBuffer RunDeconvolve(ImageBuffer input, KernelParams p, CancellationToken cancellationToken = default)
    {
        // Thread the CancellationToken into options so iterative deconvolvers
        // (Richardson-Lucy, Landweber) can check it every iteration. Frequency-domain
        // deconvolvers ignore it — they finish in one shot.
        var options = _options with { CancellationToken = cancellationToken };
        // BlindDeconvolution estimates its own PSF and ignores the one passed to
        // Apply(). Skip building it here — otherwise MotionBlurKernel.Build throws
        // on Length=0 (the default when a user picks blind without touching the
        // slider) even though the value is never used.
        var psf = p.Algorithm == AlgorithmType.BlindDeconvolution
            ? new float[1, 1] { { 1f } }
            : _kernels[p.Type].Build(p);
        var deconvIn = input;

        if (options.LinearLight)
        {
            deconvIn = input.Clone();
            Deblur.Engine.Color.SrgbLinear.ToLinearInPlace(deconvIn.R);
            Deblur.Engine.Color.SrgbLinear.ToLinearInPlace(deconvIn.G);
            Deblur.Engine.Color.SrgbLinear.ToLinearInPlace(deconvIn.B);
        }

        ImageBuffer result;
        if (options.LuminanceOnly)
        {
            var (y, cb, cr) = Deblur.Engine.Color.YCbCr.FromRgb(deconvIn.R, deconvIn.G, deconvIn.B);
            var yBuf = new ImageBuffer(deconvIn.Width, deconvIn.Height, y, (float[])y.Clone(), (float[])y.Clone());
            var deconvY = _deconvolvers[p.Algorithm].Apply(yBuf, psf, new DeconvolutionParams(K: p.Smoothness, NoiseVariance: p.NoiseVariance), options);
            var (r, g, b) = Deblur.Engine.Color.YCbCr.ToRgb(deconvY.R, cb, cr);
            result = new ImageBuffer(deconvIn.Width, deconvIn.Height, r, g, b);
        }
        else
        {
            result = _deconvolvers[p.Algorithm].Apply(deconvIn, psf, new DeconvolutionParams(K: p.Smoothness, NoiseVariance: p.NoiseVariance), options);
        }

        if (options.LinearLight)
        {
            // Encode result back to sRGB; result may share arrays with deconvIn — clone to be safe.
            var enc = new ImageBuffer(result.Width, result.Height,
                (float[])result.R.Clone(), (float[])result.G.Clone(), (float[])result.B.Clone());
            Deblur.Engine.Color.SrgbLinear.ToSrgbInPlace(enc.R);
            Deblur.Engine.Color.SrgbLinear.ToSrgbInPlace(enc.G);
            Deblur.Engine.Color.SrgbLinear.ToSrgbInPlace(enc.B);
            result = enc;
        }
        // Deconvolvers and YCbCr recompose construct fresh ImageBuffers via the raw
        // ctor, which resets SourceBitDepth to the default Eight. Propagate the input's
        // depth so the runner is the single choke point that enforces the forensic
        // 16-bit-preservation invariant end-to-end.
        result.SourceBitDepth = input.SourceBitDepth;
        return result;
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
                    if (_pending is null || _proxy is null)
                    {
                        if (_running) Idle?.Invoke(this, EventArgs.Empty);
                        break;
                    }
                    p = _pending.Value;
                    proxy = _proxy;
                    _pending = null;
                }

                // Iterative algorithms (Richardson-Lucy: 30 iters × 2 FFTs × 3 channels,
                // Landweber: 100 × 2 × 3) take seconds per invocation. Running them on
                // the live-preview WorkerLoop means every slider tick queues another
                // multi-second job and the worker never drains; the UI's IsPreviewComputing
                // flag stays lit and the app appears hung. Skip them in preview — show the
                // raw proxy — and let the user see the actual iterative result on
                // full-render (Save-As / press-Render), where the delay is expected.
                bool isIterativePreview = p.Algorithm is
                    AlgorithmType.RichardsonLucy
                    or AlgorithmType.Landweber
                    or AlgorithmType.BlindDeconvolution;
                ImageBuffer deconv = (IsNoOp(p) || isIterativePreview) ? proxy : RunDeconvolve(proxy, p);

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
