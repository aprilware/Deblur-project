using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deblur.Engine;
using Deblur.Engine.Estimation;
using Deblur.Engine.Imaging;
using Deblur.Services;

namespace Deblur.ViewModels;

/// <summary>Snapshot of a motion-blur suggestion surfaced from <see cref="CepstralMotionEstimator"/>.</summary>
/// <summary>
/// Motion suggestion presented to the examiner. Angle/Length/Confidence come from
/// the cepstral estimator (the primary source). RadonAngle is the independent
/// cross-check — surfaced in the UI so the examiner can see whether the two
/// methods agree. Widely divergent estimates are the honest signal that the
/// estimation is unreliable and manual PSF entry is needed.
/// </summary>
public sealed record MotionSuggestion(float Angle, float Length, float Confidence, float RadonAngle)
{
    /// <summary>Absolute angular difference between cepstral and Radon, wrapped to [0, 90].</summary>
    public float RadonDisagreementDegrees
    {
        get
        {
            float a = ((Angle % 180f) + 180f) % 180f;
            float b = ((RadonAngle % 180f) + 180f) % 180f;
            float d = Math.Abs(a - b);
            return Math.Min(d, 180f - d);
        }
    }
}

/// <summary>Snapshot of a defocus-radius suggestion surfaced from <see cref="DefocusRadiusEstimator"/>.</summary>
public sealed record DefocusSuggestion(float Radius, float Confidence);

/// <summary>Snapshot of a noise suggestion surfaced from <see cref="WaveletNoiseEstimator"/>.</summary>
public sealed record NoiseSuggestionVm(float SigmaNoise, float SuggestedK, float Confidence);

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DeblurJobRunner _runner;
    private ImageBuffer? _originalFullRes;
    private ImageBuffer? _proxy;
    private float _proxyScale = 1f;
    private readonly ParamHistory _history = new();
    private bool _suppressHistory;
    private readonly IImageCodec _codec = new WicImageCodec();
    private readonly Gdi8BitImageCodec _fallbackCodec = new();
    private readonly BlindDeconvolutionDeconvolver _blindDeconvolver;
    private readonly CustomPsfKernel _customPsfKernel;
    private int _customPsfSequence;

    [ObservableProperty] private BlurType _selectedBlurType = BlurType.Motion;
    [ObservableProperty] private AlgorithmType _selectedAlgorithm = AlgorithmType.Wiener;
    [ObservableProperty] private float _angle;
    [ObservableProperty] private float _length;
    [ObservableProperty] private float _radius;
    [ObservableProperty] private float _sigma;
    [ObservableProperty] private float _smoothness = 0.005f;
    [ObservableProperty] private string? _currentFilePath;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private WriteableBitmap? _previewBitmap;
    [ObservableProperty] private bool _isPreviewComputing;
    [ObservableProperty] private bool _roiEnabled;
    [ObservableProperty] private int _roiFeatherRadius = 12;
    [ObservableProperty] private RegionOfInterest? _selectedRoi;
    [ObservableProperty] private System.Windows.Rect? _selectedRoiOverlayRect;
    [ObservableProperty] private MotionSuggestion? _motionSuggestion;
    [ObservableProperty] private DefocusSuggestion? _defocusSuggestion;
    [ObservableProperty] private NoiseSuggestionVm? _noiseSuggestion;
    [ObservableProperty] private float[,]? _estimatedKernel;

    // Set by AcceptBlindKernel: an independent audit clone of the accepted kernel, distinct
    // from the runtime clone handed to _customPsfKernel. Backs the Custom panel's PsfDisplay.
    [ObservableProperty] private float[,]? _acceptedCustomKernelDisplay;

    // The examiner-facing "Accepted from…" record for the most recent AcceptBlindKernel call.
    [ObservableProperty] private SuggestionRecord? _customPsfAcceptedRecord;

    // Wavelet-noise-estimator's accepted sigma^2, threaded into KernelParams.NoiseVariance
    // for CLS v2's discrepancy-principle gamma bisection. Null until the examiner accepts
    // a noise suggestion; reset to null on every new image load.
    private float? _acceptedNoiseVariance;

    /// <summary>
    /// In-memory audit trail of every estimator invocation and its accept/dismiss outcome.
    /// Not an [ObservableProperty] — it's a read-only list from XAML's perspective; the UI
    /// re-reads it after each Estimate/Accept/Dismiss call rather than binding to mutations.
    /// </summary>
    public List<SuggestionRecord> SuggestionHistory { get; } = new();

    public bool IsMotionSelected     => SelectedBlurType == BlurType.Motion;
    public bool IsOutOfFocusSelected => SelectedBlurType == BlurType.OutOfFocus;
    public bool IsGaussianSelected   => SelectedBlurType == BlurType.Gaussian;
    public bool IsCustomSelected     => SelectedBlurType == BlurType.Custom;
    public bool HasImage => _proxy is not null;
    public bool IsWienerSelected   => SelectedAlgorithm == AlgorithmType.Wiener;
    public bool IsTikhonovSelected => SelectedAlgorithm == AlgorithmType.Tikhonov;
    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;

    public MainViewModel()
    {
        _dispatcher = Application.Current.Dispatcher;
        var kernels = new Dictionary<BlurType, IBlurKernel>
        {
            [BlurType.Motion]     = new MotionBlurKernel(),
            [BlurType.OutOfFocus] = new OutOfFocusBlurKernel(),
            [BlurType.Gaussian]   = new GaussianBlurKernel(),
            [BlurType.Custom]     = _customPsfKernel = new CustomPsfKernel(),
        };
        var deconvolvers = new Dictionary<AlgorithmType, IDeconvolver>
        {
            [AlgorithmType.Wiener]                = new WienerDeconvolver(),
            [AlgorithmType.Tikhonov]              = new TikhonovDeconvolver(),
            [AlgorithmType.TotalVariation]        = new TotalVariationDeconvolver(),
            [AlgorithmType.RichardsonLucy]        = new RichardsonLucyDeconvolver(),
            [AlgorithmType.ConstrainedLeastSquares] = new ConstrainedLeastSquaresDeconvolver(),
            [AlgorithmType.Landweber]             = new LandweberDeconvolver(),
            [AlgorithmType.BlindDeconvolution]    = _blindDeconvolver = new BlindDeconvolutionDeconvolver(),
        };
        _runner = new DeblurJobRunner(kernels, deconvolvers, PipelineOptions.Default);
        _runner.ProxyReady += OnProxyReady;
        _runner.Idle += OnRunnerIdle;
    }

    // Short debounce so the bar doesn't wink off in the sliver between a
    // dispatched Idle callback and a new Request landing on the UI thread.
    private DispatcherTimer? _idleClearTimer;

    private void OnRunnerIdle(object? sender, EventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            _idleClearTimer?.Stop();
            _idleClearTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _idleClearTimer.Tick += (_, _) =>
            {
                _idleClearTimer?.Stop();
                _idleClearTimer = null;
                IsPreviewComputing = false;
            };
            _idleClearTimer.Start();
        });
    }

    // The three Accept commands' CanExecute predicates read the suggestion's Confidence,
    // so they need to be re-evaluated whenever the suggestion property changes.
    // [RelayCommand] with CanExecute doesn't auto-listen to ObservableProperty changes.
    partial void OnMotionSuggestionChanged(MotionSuggestion? value)
        => AcceptMotionSuggestionCommand.NotifyCanExecuteChanged();
    partial void OnDefocusSuggestionChanged(DefocusSuggestion? value)
        => AcceptDefocusSuggestionCommand.NotifyCanExecuteChanged();
    partial void OnNoiseSuggestionChanged(NoiseSuggestionVm? value)
        => AcceptNoiseSuggestionCommand.NotifyCanExecuteChanged();

    partial void OnSelectedBlurTypeChanged(BlurType value)
    {
        OnPropertyChanged(nameof(IsMotionSelected));
        OnPropertyChanged(nameof(IsOutOfFocusSelected));
        OnPropertyChanged(nameof(IsGaussianSelected));
        OnPropertyChanged(nameof(IsCustomSelected));

        // Preserve each type's own params across switches; the user can hit Reset
        // if they want to clear the active type.
        PushCurrentParams();
        PushSnapshot();
    }

    partial void OnSelectedAlgorithmChanged(AlgorithmType value)
    {
        OnPropertyChanged(nameof(IsWienerSelected));
        OnPropertyChanged(nameof(IsTikhonovSelected));
        // Clear the previous blind kernel — displaying a stale kernel from a prior
        // algorithm switch is testimony-adjacent misinformation (the PSF display
        // would show an old kernel that no longer relates to the current algorithm).
        // A fresh render under blind repopulates it via _blindDeconvolver.LastEstimatedKernel.
        EstimatedKernel = null;
        InvalidateFullResCache();
        PushCurrentParams();
        PushSnapshot();
    }

    public void LoadImageFromBytes(byte[] bytes)
    {
        ImageBuffer full;
        BitDepth depth;
        try
        {
            (full, depth) = _codec.Decode(bytes);
        }
        catch (Exception)
        {
            (full, depth) = _fallbackCodec.Decode(bytes);
        }
        full.SourceBitDepth = depth;
        _originalFullRes = full;
        // Keep proxy dims under ~920 px so FFT pads to 1024 (not 2048) — 4x faster interactive preview.
        const int maxProxyPixels = 400_000;
        double scale = 1.0;
        int px = full.Width * full.Height;
        if (px > maxProxyPixels) scale = Math.Sqrt((double)maxProxyPixels / px);
        int pw = Math.Max(1, (int)Math.Round(full.Width * scale));
        int ph = Math.Max(1, (int)Math.Round(full.Height * scale));
        _proxy = AreaResample.Box(full, pw, ph);
        _proxyScale = (float)pw / full.Width;

        // Clear ROI selection BEFORE swapping PreviewBitmap so PreviewCanvas.OnSourceChanged
        // does not paint a stale overlay against the newly-sized bitmap for one dispatcher tick.
        SelectedRoi = null;
        SelectedRoiOverlayRect = null;
        PreviewBitmap = ImageBufferInterop.NewCompatibleBitmap(pw, ph);
        _runner.SetProxy(_proxy);
        _runner.SetProxyScale(_proxyScale);
        OnPropertyChanged(nameof(HasImage));
        _history.Clear();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));

        // New image invalidates any prior estimator suggestions/acceptance.
        MotionSuggestion = null;
        DefocusSuggestion = null;
        NoiseSuggestion = null;
        _acceptedNoiseVariance = null;
        EstimatedKernel = null;
        CustomPsfAcceptedRecord = null;
        AcceptedCustomKernelDisplay = null;

        Reset();
    }

    public void UpdateKernel(float angle, float length)
    {
        // Drag arrow only drives motion blur.
        if (SelectedBlurType != BlurType.Motion) return;
        Angle = angle;
        Length = length;
        PushCurrentParams();
    }

    public void CommitArrowDrag(float angle, float length)
    {
        if (SelectedBlurType != BlurType.Motion) return;
        Angle = angle;
        Length = length;
        PushCurrentParams();
        PushSnapshot();
    }

    partial void OnSmoothnessChanged(float value) { InvalidateFullResCache(); PushCurrentParams(); }
    partial void OnAngleChanged(float value)      { InvalidateFullResCache(); PushCurrentParams(); }
    partial void OnLengthChanged(float value)     { InvalidateFullResCache(); PushCurrentParams(); }
    partial void OnRadiusChanged(float value)     { InvalidateFullResCache(); PushCurrentParams(); }
    partial void OnSigmaChanged(float value)      { InvalidateFullResCache(); PushCurrentParams(); }
    partial void OnRoiEnabledChanged(bool value) => InvalidateFullResCache();
    partial void OnRoiFeatherRadiusChanged(int value) => InvalidateFullResCache();

    public void CommitRoi(int proxyX, int proxyY, int proxyW, int proxyH)
    {
        if (_originalFullRes is null) return;
        // Proxy → full-res: multiply by 1/_proxyScale (same convention as Length/Radius/Sigma).
        float inv = 1f / Math.Max(_proxyScale, 1e-6f);
        int fx = (int)Math.Round(proxyX * inv);
        int fy = (int)Math.Round(proxyY * inv);
        int fw = (int)Math.Round(proxyW * inv);
        int fh = (int)Math.Round(proxyH * inv);
        // Clamp to image bounds.
        fx = Math.Clamp(fx, 0, _originalFullRes.Width - 1);
        fy = Math.Clamp(fy, 0, _originalFullRes.Height - 1);
        fw = Math.Min(fw, _originalFullRes.Width - fx);
        fh = Math.Min(fh, _originalFullRes.Height - fy);
        if (fw < 2 || fh < 2) return;

        SelectedRoi = new RegionOfInterest(fx, fy, fw, fh, RoiFeatherRadius);
        // Round-trip the clamped full-res rect back into proxy space so the overlay
        // shows exactly the region the runner will process — no visual drift when the
        // user drags past the image edge.
        SelectedRoiOverlayRect = new System.Windows.Rect(
            fx * _proxyScale, fy * _proxyScale, fw * _proxyScale, fh * _proxyScale);
        InvalidateFullResCache();
    }

    public void Reset()
    {
        // Reset the currently-selected type's params to defaults.
        switch (SelectedBlurType)
        {
            case BlurType.Motion:
                Angle = 0f;
                Length = 0f;
                break;
            case BlurType.OutOfFocus:
                Radius = 0f;
                break;
            case BlurType.Gaussian:
                Sigma = 0f;
                break;
        }
        Smoothness = 0.005f;
        PushCurrentParams();
        PushSnapshot();
    }

    // Cached full-resolution render; invalidated on any param change.
    private ImageBuffer? _fullResBuffer;
    private KernelParams? _fullResParams;

    public async Task EnsureFullResRenderedAsync(IProgress<double> progress, CancellationToken cancellationToken = default)
    {
        if (_originalFullRes is null) throw new InvalidOperationException("No image loaded.");
        var current = BuildCurrentParams();
        if (_fullResBuffer is not null && _fullResParams.Equals(current))
        {
            progress.Report(1.0);
            return;
        }
        _runner.Roi = (RoiEnabled && SelectedRoi is { } r)
            ? r with { FeatherRadius = RoiFeatherRadius }
            : null;
        _fullResBuffer = await _runner.RenderFullAsync(_originalFullRes, current, _proxyScale, progress, cancellationToken);
        _fullResParams = current;
        if (SelectedAlgorithm == AlgorithmType.BlindDeconvolution)
            EstimatedKernel = _blindDeconvolver.LastEstimatedKernel;
    }

    public async Task<byte[]> RenderFullAsPngAsync(IProgress<double> progress, CancellationToken cancellationToken = default)
    {
        await EnsureFullResRenderedAsync(progress, cancellationToken);
        return _codec.EncodePng(_fullResBuffer!, _fullResBuffer!.SourceBitDepth);
    }

    public async Task<byte[]> RenderFullAsJpegAsync(int quality, IProgress<double> progress, CancellationToken cancellationToken = default)
    {
        await EnsureFullResRenderedAsync(progress, cancellationToken);
        return _codec.EncodeJpeg(_fullResBuffer!, quality);
    }

    private void InvalidateFullResCache() => _fullResBuffer = null;

    private KernelParams BuildCurrentParams()
        => new KernelParams(SelectedBlurType, Angle, Length, Smoothness, Radius, Sigma, SelectedAlgorithm,
                             NoiseVariance: _acceptedNoiseVariance,
                             KernelId: SelectedBlurType == BlurType.Custom ? _customPsfSequence : null);

    // ---- Automatic parameter estimation (Phase 1.d) -----------------------------------
    //
    // Estimators run on _originalFullRes in LINEAR LIGHT, never the proxy: motion length
    // and defocus radius are pixel-scaled quantities that are only correct at full-res.

    [RelayCommand]
    private void EstimateMotion()
    {
        if (_originalFullRes is null) return;
        var gray = ToLinearGrayscale(_originalFullRes);
        var est = CepstralMotionEstimator.Estimate(gray, _originalFullRes.Width, _originalFullRes.Height);
        var radonAngle = RadonMotionEstimator.EstimateAngleDegrees(gray, _originalFullRes.Width, _originalFullRes.Height);
        // Record both estimator provenances; the cepstral entry is the one an Accept
        // marks (it supplies the accepted angle/length). Radon supplies a cross-check
        // angle only — no independent Accept/Dismiss surface.
        SuggestionHistory.Add(new SuggestionRecord(
            CepstralMotionEstimator.Id, CepstralMotionEstimator.Version, est, est.Confidence, DateTime.UtcNow));
        SuggestionHistory.Add(new SuggestionRecord(
            RadonMotionEstimator.Id, RadonMotionEstimator.Version, radonAngle, est.Confidence, DateTime.UtcNow));
        MotionSuggestion = new MotionSuggestion(est.Angle, est.Length, est.Confidence, radonAngle);
    }

    // Confidence gate for Accept: cepstral / defocus / wavelet-noise estimators can
    // produce plausible-looking numbers on natural imagery that they cannot actually
    // support (the cepstrum is dominated by image structure, not the injected PSF).
    // Threshold locked with CepstralMotionEstimatorTests.SharpImage_LowConfidence and
    // LowConfidenceToVisibilityConverter — a genuinely sharp image measures ≈ 0.347
    // (recorded in that test), so the gate sits just above at 0.35 to reject it.
    // Users who need to override — say, on a low-confidence suggestion they've
    // cross-checked visually — should use the sliders directly.
    private const float AcceptConfidenceThreshold = 0.35f;

    [RelayCommand(CanExecute = nameof(CanAcceptMotionSuggestion))]
    private void AcceptMotionSuggestion()
    {
        if (MotionSuggestion is null) return;
        // Standard slider-change flow (OnAngleChanged/OnLengthChanged) pushes into
        // ParamHistory and invalidates the full-res cache via the existing partials.
        Angle = MotionSuggestion.Angle;
        Length = MotionSuggestion.Length;
        MarkSuggestionAccepted(CepstralMotionEstimator.Id);
        MotionSuggestion = null;
    }

    private bool CanAcceptMotionSuggestion()
        => MotionSuggestion is { } s && s.Confidence >= AcceptConfidenceThreshold;

    [RelayCommand]
    private void DismissMotionSuggestion()
    {
        if (MotionSuggestion is null) return;
        MarkSuggestionDismissed(CepstralMotionEstimator.Id);
        MotionSuggestion = null;
    }

    [RelayCommand]
    private void EstimateDefocus()
    {
        if (_originalFullRes is null) return;
        var gray = ToLinearGrayscale(_originalFullRes);
        var est = DefocusRadiusEstimator.Estimate(gray, _originalFullRes.Width, _originalFullRes.Height);
        SuggestionHistory.Add(new SuggestionRecord(
            DefocusRadiusEstimator.Id, DefocusRadiusEstimator.Version, est, est.Confidence, DateTime.UtcNow));
        DefocusSuggestion = new DefocusSuggestion(est.Radius, est.Confidence);
    }

    [RelayCommand(CanExecute = nameof(CanAcceptDefocusSuggestion))]
    private void AcceptDefocusSuggestion()
    {
        if (DefocusSuggestion is null) return;
        Radius = DefocusSuggestion.Radius; // OnRadiusChanged invalidates the full-res cache.
        MarkSuggestionAccepted(DefocusRadiusEstimator.Id);
        DefocusSuggestion = null;
    }

    private bool CanAcceptDefocusSuggestion()
        => DefocusSuggestion is { } s && s.Confidence >= AcceptConfidenceThreshold;

    [RelayCommand]
    private void DismissDefocusSuggestion()
    {
        if (DefocusSuggestion is null) return;
        MarkSuggestionDismissed(DefocusRadiusEstimator.Id);
        DefocusSuggestion = null;
    }

    [RelayCommand]
    private void EstimateNoise()
    {
        if (_originalFullRes is null) return;
        var gray = ToLinearGrayscale(_originalFullRes);
        var est = WaveletNoiseEstimator.Estimate(gray, _originalFullRes.Width, _originalFullRes.Height);
        SuggestionHistory.Add(new SuggestionRecord(
            WaveletNoiseEstimator.Id, WaveletNoiseEstimator.Version, est, est.Confidence, DateTime.UtcNow));
        NoiseSuggestion = new NoiseSuggestionVm(est.SigmaNoise, est.SuggestedK, est.Confidence);
    }

    [RelayCommand(CanExecute = nameof(CanAcceptNoiseSuggestion))]
    private void AcceptNoiseSuggestion()
    {
        if (NoiseSuggestion is null) return;
        // Set _acceptedNoiseVariance BEFORE Smoothness so the OnSmoothnessChanged
        // partial (which fires PushCurrentParams) sees the new noise variance in
        // BuildCurrentParams. Assigning Smoothness first queued a preview job with
        // the OLD (null) noise variance, so the live preview stayed on fixed-γ
        // CLS until the next slider tweak.
        _acceptedNoiseVariance = NoiseSuggestion.SigmaNoise * NoiseSuggestion.SigmaNoise;
        Smoothness = NoiseSuggestion.SuggestedK; // OnSmoothnessChanged invalidates the cache and requests preview.
        MarkSuggestionAccepted(WaveletNoiseEstimator.Id);
        NoiseSuggestion = null;
        // Belt-and-suspenders: invalidate explicitly in case SuggestedK happens to
        // equal the current Smoothness (then OnSmoothnessChanged wouldn't fire).
        InvalidateFullResCache();
    }

    private bool CanAcceptNoiseSuggestion()
        => NoiseSuggestion is { } s && s.Confidence >= AcceptConfidenceThreshold;

    [RelayCommand]
    private void DismissNoiseSuggestion()
    {
        if (NoiseSuggestion is null) return;
        MarkSuggestionDismissed(WaveletNoiseEstimator.Id);
        NoiseSuggestion = null;
    }

    // ---- Blind-kernel handoff (Phase 1.f-1) --------------------------------------------
    //
    // Accept clones the blind-estimated kernel TWICE: once as the runtime PSF handed to
    // _customPsfKernel (consumed by future renders), once as an independent audit copy
    // stored on the SuggestionRecord / AcceptedCustomKernelDisplay. Neither clone aliases
    // EstimatedKernel or each other, so a later algorithm switch nulling EstimatedKernel
    // (see OnSelectedAlgorithmChanged) cannot mutate what was accepted.

    private static float[,] CloneKernel(float[,] src)
    {
        int h = src.GetLength(0), w = src.GetLength(1);
        var dst = new float[h, w];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                dst[y, x] = src[y, x];
        return dst;
    }

    private bool CanAcceptBlindKernel() => EstimatedKernel is not null;

    // EstimatedKernel isn't consumed by CanAcceptBlindKernel's own generated dependency
    // wiring ([RelayCommand] doesn't auto-listen to ObservableProperty changes), so this
    // partial re-evaluates CanExecute explicitly, matching the OnMotionSuggestionChanged
    // pattern above.
    partial void OnEstimatedKernelChanged(float[,]? value)
        => AcceptBlindKernelCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanAcceptBlindKernel))]
    private void AcceptBlindKernel()
    {
        if (EstimatedKernel is null) return;
        var runtimeCopy = CloneKernel(EstimatedKernel);
        var auditCopy   = CloneKernel(EstimatedKernel);

        _customPsfKernel.SetPsf(runtimeCopy);
        // Bump BEFORE the SelectedBlurType/SelectedAlgorithm switches below — those trigger
        // PushCurrentParams/PushSnapshot via existing partials, and BuildCurrentParams reads
        // _customPsfSequence into KernelParams.KernelId, so the id must already be current.
        _customPsfSequence++;

        var record = new SuggestionRecord(
            BlindDeconvolutionDeconvolver.MetadataId,
            BlindDeconvolutionDeconvolver.MetadataVersion,
            (float[,]?)auditCopy,
            Confidence: (float?)null, // examiner acceptance is encoded by AcceptedAtUtc, not a fabricated confidence.
            SuggestedAtUtc: DateTime.UtcNow)
            with { AcceptedAtUtc = DateTime.UtcNow };
        SuggestionHistory.Add(record);
        CustomPsfAcceptedRecord = record;
        AcceptedCustomKernelDisplay = auditCopy;

        SelectedBlurType = BlurType.Custom;
        SelectedAlgorithm = AlgorithmType.Wiener;
        InvalidateFullResCache();
    }

    [RelayCommand]
    private void ClearCustomPsf()
    {
        // Stored PSF stays in _customPsfKernel — prevents a null race with an in-flight
        // WorkerLoop preview. Only switch the type back. Next Accept replaces the stored
        // PSF; a switch back to Custom without a re-Accept would apply the OLD stored PSF,
        // which is why the type combobox never surfaces Custom directly (it's only
        // reachable via Accept, and the Custom panel is only visible when IsCustomSelected).
        SelectedBlurType = BlurType.Motion;
        InvalidateFullResCache();
    }

    // Searches backwards for the most recent SuggestionHistory entry from the given
    // estimator. Backward search (rather than positional [^N] indexing) is required
    // because EstimateMotion appends TWO records (cepstral + Radon) per invocation, and
    // repeated Estimate calls without an intervening Accept/Dismiss would otherwise make
    // positional indexing target the wrong entry.
    private void MarkSuggestionAccepted(string estimatorId)
    {
        for (int i = SuggestionHistory.Count - 1; i >= 0; i--)
        {
            if (SuggestionHistory[i].EstimatorId == estimatorId)
            {
                SuggestionHistory[i] = SuggestionHistory[i] with { AcceptedAtUtc = DateTime.UtcNow };
                return;
            }
        }
    }

    private void MarkSuggestionDismissed(string estimatorId)
    {
        for (int i = SuggestionHistory.Count - 1; i >= 0; i--)
        {
            if (SuggestionHistory[i].EstimatorId == estimatorId)
            {
                SuggestionHistory[i] = SuggestionHistory[i] with { DismissedAtUtc = DateTime.UtcNow };
                return;
            }
        }
    }

    // BT.601 luma weighting applied AFTER per-channel sRGB->linear decode. ImageBuffer
    // channels are sRGB-encoded floats in [0,1] (RunDeconvolve conditionally linearizes
    // them the same way for LinearLight processing) — estimators need linear-light input
    // so their pixel-scaled outputs (motion length, defocus radius) aren't skewed by gamma.
    private static float[] ToLinearGrayscale(ImageBuffer buf)
    {
        int n = buf.PixelCount;
        var g = new float[n];
        for (int i = 0; i < n; i++)
        {
            float linR = Deblur.Engine.Color.SrgbLinear.ToLinear(buf.R[i]);
            float linG = Deblur.Engine.Color.SrgbLinear.ToLinear(buf.G[i]);
            float linB = Deblur.Engine.Color.SrgbLinear.ToLinear(buf.B[i]);
            g[i] = 0.299f * linR + 0.587f * linG + 0.114f * linB;
        }
        return g;
    }

    private void PushCurrentParams()
    {
        if (_proxy is null) return;
        _idleClearTimer?.Stop();
        _idleClearTimer = null;
        IsPreviewComputing = true;
        _runner.Request(BuildCurrentParams());
    }

    private void OnProxyReady(object? sender, ProxyReadyEventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (PreviewBitmap is null || PreviewBitmap.PixelWidth != e.Width || PreviewBitmap.PixelHeight != e.Height)
                PreviewBitmap = ImageBufferInterop.NewCompatibleBitmap(e.Width, e.Height);
            ImageBufferInterop.ApplyBgraToWriteableBitmap(e.Bgra, e.Width, e.Height, PreviewBitmap);
        });
    }

    public void Undo()
    {
        if (!_history.TryUndo(out var previous)) return;
        ApplySnapshot(previous);
    }

    public void Redo()
    {
        if (!_history.TryRedo(out var next)) return;
        ApplySnapshot(next);
    }

    private void ApplySnapshot(KernelParams p)
    {
        _suppressHistory = true;
        try
        {
            SelectedBlurType  = p.Type;
            SelectedAlgorithm = p.Algorithm;
            Angle             = p.Angle;
            Length            = p.Length;
            Radius            = p.Radius;
            Sigma             = p.Sigma;
            Smoothness        = p.Smoothness;
        }
        finally
        {
            _suppressHistory = false;
        }
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    private void PushSnapshot()
    {
        if (_suppressHistory || _proxy is null) return;
        _history.Push(BuildCurrentParams());
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    public void Dispose() => _runner.Dispose();
}
