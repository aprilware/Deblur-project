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

    [ObservableProperty] private BlurType _selectedBlurType = BlurType.Motion;
    [ObservableProperty] private float _angle;
    [ObservableProperty] private float _length = 10f;
    [ObservableProperty] private float _smoothness = 0.005f;
    [ObservableProperty] private string? _currentFilePath;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private WriteableBitmap? _previewBitmap;

    public bool IsMotionSelected => SelectedBlurType == BlurType.Motion;
    public bool IsComingSoon => !IsMotionSelected;

    public MainViewModel()
    {
        _dispatcher = Application.Current.Dispatcher;
        _runner = new DeblurJobRunner(new MotionBlurKernel(), new WienerDeconvolver());
        _runner.ProxyReady += OnProxyReady;
    }

    partial void OnSelectedBlurTypeChanged(BlurType value)
    {
        OnPropertyChanged(nameof(IsMotionSelected));
        OnPropertyChanged(nameof(IsComingSoon));
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
        Reset();
    }

    public void UpdateKernel(float angle, float length)
    {
        Angle = angle;
        Length = length;
        PushCurrentParams();
    }

    partial void OnSmoothnessChanged(float value) { InvalidateFullResCache(); PushCurrentParams(); }
    partial void OnAngleChanged(float value)      { InvalidateFullResCache(); PushCurrentParams(); }
    partial void OnLengthChanged(float value)     { InvalidateFullResCache(); PushCurrentParams(); }

    public void Reset()
    {
        Angle = 0f;
        Length = 10f;
        Smoothness = 0.005f;
        PushCurrentParams();
    }

    // Cached full-resolution render; invalidated on any param change.
    private ImageBuffer? _fullResBuffer;
    private KernelParams? _fullResParams;

    public async Task EnsureFullResRenderedAsync(IProgress<double> progress)
    {
        if (_originalFullRes is null) throw new InvalidOperationException("No image loaded.");
        var current = new KernelParams(BlurType.Motion, Angle, Length, Smoothness);
        if (_fullResBuffer is not null && _fullResParams.Equals(current))
        {
            progress.Report(1.0);
            return;
        }
        _fullResBuffer = await _runner.RenderFullAsync(_originalFullRes, current, _proxyScale, progress);
        _fullResParams = current;
    }

    public async Task<byte[]> RenderFullAsPngAsync(IProgress<double> progress)
    {
        await EnsureFullResRenderedAsync(progress);
        return ImageCodec.EncodePng(_fullResBuffer!);
    }

    public async Task<byte[]> RenderFullAsJpegAsync(int quality, IProgress<double> progress)
    {
        await EnsureFullResRenderedAsync(progress);
        return ImageCodec.EncodeJpeg(_fullResBuffer!, quality);
    }

    private void InvalidateFullResCache() => _fullResBuffer = null;

    private void PushCurrentParams()
    {
        if (_proxy is null) return;
        _runner.Request(new KernelParams(BlurType.Motion, Angle, Length, Smoothness));
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

    public void Dispose() => _runner.Dispose();
}
