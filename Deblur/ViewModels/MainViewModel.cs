using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Deblur.Engine;
using Deblur.Services;

namespace Deblur.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DeblurJobRunner _runner;
    private ImageBuffer? _originalFullRes;
    private ImageBuffer? _proxy;
    private float _proxyScale = 1f;
    private readonly ParamHistory _history = new();
    private bool _suppressHistory;

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

    public bool IsMotionSelected     => SelectedBlurType == BlurType.Motion;
    public bool IsOutOfFocusSelected => SelectedBlurType == BlurType.OutOfFocus;
    public bool IsGaussianSelected   => SelectedBlurType == BlurType.Gaussian;
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
        };
        var deconvolvers = new Dictionary<AlgorithmType, IDeconvolver>
        {
            [AlgorithmType.Wiener]   = new WienerDeconvolver(),
            [AlgorithmType.Tikhonov] = new TikhonovDeconvolver(),
        };
        _runner = new DeblurJobRunner(kernels, deconvolvers);
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

    partial void OnSelectedBlurTypeChanged(BlurType value)
    {
        OnPropertyChanged(nameof(IsMotionSelected));
        OnPropertyChanged(nameof(IsOutOfFocusSelected));
        OnPropertyChanged(nameof(IsGaussianSelected));

        // Preserve each type's own params across switches; the user can hit Reset
        // if they want to clear the active type.
        PushCurrentParams();
        PushSnapshot();
    }

    partial void OnSelectedAlgorithmChanged(AlgorithmType value)
    {
        OnPropertyChanged(nameof(IsWienerSelected));
        OnPropertyChanged(nameof(IsTikhonovSelected));
        InvalidateFullResCache();
        PushCurrentParams();
        PushSnapshot();
    }

    public void LoadImageFromBytes(byte[] bytes)
    {
        var full = ImageCodec.DecodeFromBytes(bytes);
        _originalFullRes = full;
        // Keep proxy dims under ~920 px so FFT pads to 1024 (not 2048) — 4x faster interactive preview.
        const int maxProxyPixels = 400_000;
        double scale = 1.0;
        int px = full.Width * full.Height;
        if (px > maxProxyPixels) scale = Math.Sqrt((double)maxProxyPixels / px);
        int pw = Math.Max(1, (int)Math.Round(full.Width * scale));
        int ph = Math.Max(1, (int)Math.Round(full.Height * scale));
        _proxy = Downscale(full, pw, ph);
        _proxyScale = (float)pw / full.Width;

        PreviewBitmap = ImageBufferInterop.NewCompatibleBitmap(pw, ph);
        _runner.SetProxy(_proxy);
        OnPropertyChanged(nameof(HasImage));
        _history.Clear();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
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
        _fullResBuffer = await _runner.RenderFullAsync(_originalFullRes, current, _proxyScale, progress, cancellationToken);
        _fullResParams = current;
    }

    public async Task<byte[]> RenderFullAsPngAsync(IProgress<double> progress, CancellationToken cancellationToken = default)
    {
        await EnsureFullResRenderedAsync(progress, cancellationToken);
        return ImageCodec.EncodePng(_fullResBuffer!);
    }

    public async Task<byte[]> RenderFullAsJpegAsync(int quality, IProgress<double> progress, CancellationToken cancellationToken = default)
    {
        await EnsureFullResRenderedAsync(progress, cancellationToken);
        return ImageCodec.EncodeJpeg(_fullResBuffer!, quality);
    }

    private void InvalidateFullResCache() => _fullResBuffer = null;

    private KernelParams BuildCurrentParams()
        => new KernelParams(SelectedBlurType, Angle, Length, Smoothness, Radius, Sigma, SelectedAlgorithm);

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

    private static ImageBuffer Downscale(ImageBuffer src, int newW, int newH)
    {
        var dst = new ImageBuffer(newW, newH);
        double sx = (double)src.Width / newW;
        double sy = (double)src.Height / newH;
        for (int y = 0; y < newH; y++)
        {
            int srcY = Math.Min(src.Height - 1, (int)(y * sy));
            for (int x = 0; x < newW; x++)
            {
                int srcX = Math.Min(src.Width - 1, (int)(x * sx));
                int si = srcY * src.Width + srcX;
                int di = y * newW + x;
                dst.R[di] = src.R[si];
                dst.G[di] = src.G[si];
                dst.B[di] = src.B[si];
            }
        }
        return dst;
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
