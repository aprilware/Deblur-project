using System.Collections.Concurrent;
using Deblur.Engine;
using Deblur.Engine.Imaging;
using Deblur.Engine.Validation;
using Deblur.Tests.TestHelpers;
using Xunit;

namespace Deblur.Tests;

public class DeblurJobRunnerTests
{
    private sealed class SlowStubDeconvolver : IDeconvolver
    {
        public AlgorithmMetadata Metadata { get; } = new(
            Id: "stub", Version: "0", DisplayName: "Stub", DescriptionMarkdown: "test-only stub",
            LiteratureCitation: "n/a");

        public int CallCount;
        public readonly ConcurrentBag<float> ObservedAngles = new();
        public int SleepMs { get; init; } = 10;

        public ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p, PipelineOptions? options = null)
        {
            Interlocked.Increment(ref CallCount);
            Thread.Sleep(SleepMs);
            return input.Clone();
        }
    }

    private sealed class RecordingStubKernel : IBlurKernel
    {
        public readonly ConcurrentBag<KernelParams> Seen = new();
        public float[,] Build(KernelParams p) { Seen.Add(p); return new float[1, 1] { { 1f } }; }
    }

    private sealed class RecordingStubDeconvolver : IDeconvolver
    {
        public AlgorithmMetadata Metadata { get; } = new(
            Id: "stub", Version: "0", DisplayName: "Stub", DescriptionMarkdown: "test-only stub",
            LiteratureCitation: "n/a");

        public readonly System.Collections.Concurrent.ConcurrentBag<KernelParams> Applied = new();
        public int SleepMs { get; init; } = 0;

        public ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p, PipelineOptions? options = null)
        {
            // We only need the algorithm for routing; the caller's KernelParams isn't
            // reachable here, so we record something distinguishable via the PSF hash.
            // In practice, the routing test uses a stub kernel that echoes p.Type into
            // the psf, but simplest is to just record we were called.
            Applied.Add(new KernelParams(BlurType.Motion, 0f, 0f, p.K, 0f, 0f, AlgorithmType.Wiener));
            if (SleepMs > 0) Thread.Sleep(SleepMs);
            return input.Clone();
        }
    }

    [Fact]
    public void Rapid_Requests_Coalesce_And_LastParamsWin()
    {
        var kernel = new RecordingStubKernel();
        var deconv = new SlowStubDeconvolver { SleepMs = 15 };
        var kernels = new Dictionary<BlurType, IBlurKernel> { [BlurType.Motion] = kernel };
        var deconvolvers = new Dictionary<AlgorithmType, IDeconvolver>
        {
            [AlgorithmType.Wiener]   = deconv,
            [AlgorithmType.Tikhonov] = deconv,
        };
        using var runner = new DeblurJobRunner(kernels, deconvolvers);
        runner.SetProxy(SyntheticImages.Checkerboard(32, 32, 4));

        int received = 0;
        var lastEvent = new ManualResetEventSlim();
        runner.ProxyReady += (_, __) =>
        {
            Interlocked.Increment(ref received);
            lastEvent.Set();
        };

        for (int i = 0; i < 100; i++)
            runner.Request(new KernelParams(BlurType.Motion, Angle: i, Length: 5f, Smoothness: 0.005f, Radius: 0f, Sigma: 0f, Algorithm: AlgorithmType.Wiener));

        // Wait for the last coalesced job to complete.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
            if (deconv.CallCount > 0 && !runner.HasPending) break;
        }

        Assert.True(deconv.CallCount < 100, $"expected coalescing; ran {deconv.CallCount} jobs");
        Assert.True(deconv.CallCount >= 1);
        // Latest param (angle 99) must appear in the observed kernel calls.
        Assert.Contains(kernel.Seen, p => (int)p.Angle == 99);
    }

    [Fact]
    public async Task RenderFullAsync_ScalesKernelLengthByInverseProxyScale()
    {
        var kernel = new RecordingStubKernel();
        var deconv = new SlowStubDeconvolver { SleepMs = 0 };
        var kernels = new Dictionary<BlurType, IBlurKernel> { [BlurType.Motion] = kernel };
        var deconvolvers = new Dictionary<AlgorithmType, IDeconvolver>
        {
            [AlgorithmType.Wiener]   = deconv,
            [AlgorithmType.Tikhonov] = deconv,
        };
        using var runner = new DeblurJobRunner(kernels, deconvolvers);

        var full = SyntheticImages.Checkerboard(200, 200, 10);
        // proxyScale = proxyW / fullW = 50 / 200 = 0.25 → length multiplier = 4x
        await runner.RenderFullAsync(full,
            new KernelParams(BlurType.Motion, 45f, 10f, 0.005f, 0f, 0f, AlgorithmType.Wiener), proxyScale: 0.25f);

        Assert.Contains(kernel.Seen, p => Math.Abs(p.Length - 40f) < 0.001f);
    }

    [Fact]
    public async Task RenderFullAsync_ScalesKernelRadiusByInverseProxyScale()
    {
        var kernel = new RecordingStubKernel();
        var deconv = new SlowStubDeconvolver { SleepMs = 0 };
        var kernels = new Dictionary<BlurType, IBlurKernel> { [BlurType.OutOfFocus] = kernel };
        var deconvolvers = new Dictionary<AlgorithmType, IDeconvolver>
        {
            [AlgorithmType.Wiener]   = deconv,
            [AlgorithmType.Tikhonov] = deconv,
        };
        using var runner = new DeblurJobRunner(kernels, deconvolvers);

        var full = SyntheticImages.Checkerboard(200, 200, 10);
        // proxyScale = 0.25 → radius multiplier = 4x (10 → 40).
        await runner.RenderFullAsync(full,
            new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0.005f, Radius: 10f, Sigma: 0f, Algorithm: AlgorithmType.Wiener), proxyScale: 0.25f);

        Assert.Contains(kernel.Seen, p => Math.Abs(p.Radius - 40f) < 0.001f);
    }

    [Fact]
    public void Request_WithOutOfFocusType_DispatchesToOutOfFocusKernel()
    {
        var motionKernel = new RecordingStubKernel();
        var outOfFocusKernel = new RecordingStubKernel();
        var deconv = new SlowStubDeconvolver { SleepMs = 5 };
        var kernels = new Dictionary<BlurType, IBlurKernel>
        {
            [BlurType.Motion]     = motionKernel,
            [BlurType.OutOfFocus] = outOfFocusKernel,
        };
        var deconvolvers = new Dictionary<AlgorithmType, IDeconvolver>
        {
            [AlgorithmType.Wiener]   = deconv,
            [AlgorithmType.Tikhonov] = deconv,
        };
        using var runner = new DeblurJobRunner(kernels, deconvolvers);
        runner.SetProxy(SyntheticImages.Checkerboard(32, 32, 4));

        runner.Request(new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0.005f, Radius: 5f, Sigma: 0f, Algorithm: AlgorithmType.Wiener));

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
            if (deconv.CallCount > 0 && !runner.HasPending) break;
        }

        Assert.Contains(outOfFocusKernel.Seen, p => p.Type == BlurType.OutOfFocus);
        Assert.Empty(motionKernel.Seen);
    }

    [Fact]
    public void Request_WithMotionLengthBelow1_EmitsRawProxyWithoutCallingDeconvolver()
    {
        var motionKernel = new RecordingStubKernel();
        var deconv = new SlowStubDeconvolver { SleepMs = 5 };
        var kernels = new Dictionary<BlurType, IBlurKernel>
        {
            [BlurType.Motion] = motionKernel,
        };
        var deconvolvers = new Dictionary<AlgorithmType, IDeconvolver>
        {
            [AlgorithmType.Wiener]   = deconv,
            [AlgorithmType.Tikhonov] = deconv,
        };
        using var runner = new DeblurJobRunner(kernels, deconvolvers);
        runner.SetProxy(SyntheticImages.Checkerboard(32, 32, 4));

        int received = 0;
        runner.ProxyReady += (_, __) => Interlocked.Increment(ref received);

        runner.Request(new KernelParams(BlurType.Motion, 0f, Length: 0f, Smoothness: 0.005f, Radius: 0f, Sigma: 0f, Algorithm: AlgorithmType.Wiener));

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
            if (received > 0 && !runner.HasPending) break;
        }

        Assert.True(received > 0);
        Assert.Equal(0, deconv.CallCount);
        Assert.Empty(motionKernel.Seen);
    }

    [Fact]
    public void Request_WithOutOfFocusRadiusBelow1_EmitsRawProxyWithoutCallingDeconvolver()
    {
        var outOfFocusKernel = new RecordingStubKernel();
        var deconv = new SlowStubDeconvolver { SleepMs = 5 };
        var kernels = new Dictionary<BlurType, IBlurKernel>
        {
            [BlurType.OutOfFocus] = outOfFocusKernel,
        };
        var deconvolvers = new Dictionary<AlgorithmType, IDeconvolver>
        {
            [AlgorithmType.Wiener]   = deconv,
            [AlgorithmType.Tikhonov] = deconv,
        };
        using var runner = new DeblurJobRunner(kernels, deconvolvers);
        runner.SetProxy(SyntheticImages.Checkerboard(32, 32, 4));

        int received = 0;
        runner.ProxyReady += (_, __) => Interlocked.Increment(ref received);

        runner.Request(new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0.005f, Radius: 0f, Sigma: 0f, Algorithm: AlgorithmType.Wiener));

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
            if (received > 0 && !runner.HasPending) break;
        }

        Assert.True(received > 0);
        Assert.Equal(0, deconv.CallCount);
        Assert.Empty(outOfFocusKernel.Seen);
    }

    [Fact]
    public void Request_WithGaussianType_DispatchesToGaussianKernel()
    {
        var motionKernel = new RecordingStubKernel();
        var outOfFocusKernel = new RecordingStubKernel();
        var gaussianKernel = new RecordingStubKernel();
        var deconv = new SlowStubDeconvolver { SleepMs = 5 };
        var kernels = new Dictionary<BlurType, IBlurKernel>
        {
            [BlurType.Motion]     = motionKernel,
            [BlurType.OutOfFocus] = outOfFocusKernel,
            [BlurType.Gaussian]   = gaussianKernel,
        };
        var deconvolvers = new Dictionary<AlgorithmType, IDeconvolver>
        {
            [AlgorithmType.Wiener]   = deconv,
            [AlgorithmType.Tikhonov] = deconv,
        };
        using var runner = new DeblurJobRunner(kernels, deconvolvers);
        runner.SetProxy(SyntheticImages.Checkerboard(32, 32, 4));

        runner.Request(new KernelParams(BlurType.Gaussian, 0f, 0f, 0.005f, 0f, Sigma: 3f, Algorithm: AlgorithmType.Wiener));

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
            if (deconv.CallCount > 0 && !runner.HasPending) break;
        }

        Assert.Contains(gaussianKernel.Seen, p => p.Type == BlurType.Gaussian);
        Assert.Empty(motionKernel.Seen);
        Assert.Empty(outOfFocusKernel.Seen);
    }

    [Fact]
    public void Request_WithGaussianSigmaBelow1_EmitsRawProxyWithoutCallingDeconvolver()
    {
        var gaussianKernel = new RecordingStubKernel();
        var deconv = new SlowStubDeconvolver { SleepMs = 5 };
        var kernels = new Dictionary<BlurType, IBlurKernel>
        {
            [BlurType.Gaussian] = gaussianKernel,
        };
        var deconvolvers = new Dictionary<AlgorithmType, IDeconvolver>
        {
            [AlgorithmType.Wiener]   = deconv,
            [AlgorithmType.Tikhonov] = deconv,
        };
        using var runner = new DeblurJobRunner(kernels, deconvolvers);
        runner.SetProxy(SyntheticImages.Checkerboard(32, 32, 4));

        int received = 0;
        runner.ProxyReady += (_, __) => Interlocked.Increment(ref received);

        runner.Request(new KernelParams(BlurType.Gaussian, 0f, 0f, 0.005f, 0f, Sigma: 0f, Algorithm: AlgorithmType.Wiener));

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
            if (received > 0 && !runner.HasPending) break;
        }

        Assert.True(received > 0);
        Assert.Equal(0, deconv.CallCount);
        Assert.Empty(gaussianKernel.Seen);
    }

    [Fact]
    public async Task RenderFullAsync_ScalesKernelSigmaByInverseProxyScale()
    {
        var kernel = new RecordingStubKernel();
        var deconv = new SlowStubDeconvolver { SleepMs = 0 };
        var kernels = new Dictionary<BlurType, IBlurKernel> { [BlurType.Gaussian] = kernel };
        var deconvolvers = new Dictionary<AlgorithmType, IDeconvolver>
        {
            [AlgorithmType.Wiener]   = deconv,
            [AlgorithmType.Tikhonov] = deconv,
        };
        using var runner = new DeblurJobRunner(kernels, deconvolvers);

        var full = SyntheticImages.Checkerboard(200, 200, 10);
        // proxyScale = 0.25 → sigma multiplier = 4x (3 → 12).
        await runner.RenderFullAsync(full,
            new KernelParams(BlurType.Gaussian, 0f, 0f, 0.005f, 0f, Sigma: 3f, Algorithm: AlgorithmType.Wiener), proxyScale: 0.25f);

        Assert.Contains(kernel.Seen, p => Math.Abs(p.Sigma - 12f) < 0.001f);
    }

    [Fact]
    public void Request_WithTikhonovAlgorithm_DispatchesToTikhonovDeconvolver()
    {
        var kernel = new RecordingStubKernel();
        var wienerDeconv = new RecordingStubDeconvolver();
        var tikhonovDeconv = new RecordingStubDeconvolver();
        var kernels = new Dictionary<BlurType, IBlurKernel> { [BlurType.Motion] = kernel };
        var deconvolvers = new Dictionary<AlgorithmType, IDeconvolver>
        {
            [AlgorithmType.Wiener]   = wienerDeconv,
            [AlgorithmType.Tikhonov] = tikhonovDeconv,
        };
        using var runner = new DeblurJobRunner(kernels, deconvolvers);
        runner.SetProxy(SyntheticImages.Checkerboard(32, 32, 4));

        runner.Request(new KernelParams(BlurType.Motion, 0f, Length: 5f, Smoothness: 0.005f, Radius: 0f, Sigma: 0f, Algorithm: AlgorithmType.Tikhonov));

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
            if (tikhonovDeconv.Applied.Count > 0 && !runner.HasPending) break;
        }

        Assert.NotEmpty(tikhonovDeconv.Applied);
        Assert.Empty(wienerDeconv.Applied);
    }

    // Regression: production deconvolvers construct their result via the raw
    // ImageBuffer(w,h,r,g,b) ctor, which resets SourceBitDepth to the default
    // Eight. The runner is the choke point that must re-stamp the input's depth
    // onto the result. Without that, 16-bit source images silently exported as
    // 8-bit after any non-noop deconvolution.
    private sealed class FreshBufferStubDeconvolver : IDeconvolver
    {
        public AlgorithmMetadata Metadata { get; } = new(
            Id: "stub", Version: "0", DisplayName: "Stub", DescriptionMarkdown: "test-only stub",
            LiteratureCitation: "n/a");

        public ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p, PipelineOptions? options = null)
            => new ImageBuffer(input.Width, input.Height,
                (float[])input.R.Clone(), (float[])input.G.Clone(), (float[])input.B.Clone());
    }

    [Fact]
    public async Task RenderFullAsync_Preserves16BitSourceDepth_ThroughDeconvolution()
    {
        var kernel = new RecordingStubKernel();
        var deconv = new FreshBufferStubDeconvolver();
        var kernels = new Dictionary<BlurType, IBlurKernel> { [BlurType.Motion] = kernel };
        var deconvolvers = new Dictionary<AlgorithmType, IDeconvolver>
        {
            [AlgorithmType.Wiener]   = deconv,
            [AlgorithmType.Tikhonov] = deconv,
        };
        using var runner = new DeblurJobRunner(kernels, deconvolvers);

        var full = SyntheticImages.Checkerboard(64, 64, 8);
        full.SourceBitDepth = BitDepth.Sixteen;

        var result = await runner.RenderFullAsync(
            full,
            new KernelParams(BlurType.Motion, 45f, 10f, 0.005f, 0f, 0f, AlgorithmType.Wiener),
            proxyScale: 1f);

        Assert.Equal(BitDepth.Sixteen, result.SourceBitDepth);
    }

    [Fact]
    public async Task RenderFullAsync_PrecancelledToken_ThrowsOperationCanceled()
    {
        var kernel = new RecordingStubKernel();
        var deconv = new SlowStubDeconvolver { SleepMs = 0 };
        var kernels = new Dictionary<BlurType, IBlurKernel> { [BlurType.Motion] = kernel };
        var deconvolvers = new Dictionary<AlgorithmType, IDeconvolver>
        {
            [AlgorithmType.Wiener]   = deconv,
            [AlgorithmType.Tikhonov] = deconv,
        };
        using var runner = new DeblurJobRunner(kernels, deconvolvers);

        var full = SyntheticImages.Checkerboard(200, 200, 10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await runner.RenderFullAsync(
                full,
                new KernelParams(BlurType.Motion, 45f, 10f, 0.005f, 0f, 0f, AlgorithmType.Wiener),
                proxyScale: 0.25f,
                progress: null,
                cancellationToken: cts.Token));
    }

    [Fact]
    public async Task RenderFullAsync_CustomBlurType_DispatchesToCustomKernel()
    {
        var customKernel = new CustomPsfKernel();
        customKernel.SetPsf(new float[3, 3] { { 0f, 0.25f, 0f }, { 0.25f, 0f, 0.25f }, { 0f, 0.25f, 0f } });
        var recordingDeconv = new RecordingStubDeconvolver();
        var kernels = new Dictionary<BlurType, IBlurKernel>
        {
            [BlurType.Motion] = new RecordingStubKernel(),
            [BlurType.Custom] = customKernel,
        };
        var deconvs = new Dictionary<AlgorithmType, IDeconvolver>
        {
            [AlgorithmType.Wiener] = recordingDeconv,
            [AlgorithmType.Tikhonov] = recordingDeconv,
        };
        using var runner = new DeblurJobRunner(kernels, deconvs);
        var img = SyntheticImages.Checkerboard(32, 32, 4);

        await runner.RenderFullAsync(img,
            new KernelParams(BlurType.Custom, 0f, 0f, 0.005f, 0f, 0f, AlgorithmType.Wiener, KernelId: 1),
            proxyScale: 1f);

        Assert.NotEmpty(recordingDeconv.Applied);
    }

    [Fact]
    public void CustomKernel_ProxyPreview_MatchesFullResResampled_WithinTolerance()
    {
        // Deterministic small kernel (Gaussian-shaped, 5x5).
        var kernel = new float[5, 5];
        float sum = 0;
        for (int y = 0; y < 5; y++)
            for (int x = 0; x < 5; x++)
            {
                kernel[y, x] = MathF.Exp(-((y - 2) * (y - 2) + (x - 2) * (x - 2)) / 2f);
                sum += kernel[y, x];
            }
        for (int y = 0; y < 5; y++)
            for (int x = 0; x < 5; x++)
                kernel[y, x] /= sum;

        // Apply to full-res via Wiener + Custom.
        var full = SyntheticImages.Checkerboard(128, 128, 16);
        var custom = new CustomPsfKernel();
        custom.SetPsf(kernel);
        var kernels = new Dictionary<BlurType, IBlurKernel>
        {
            [BlurType.Custom] = custom,
            [BlurType.Motion] = new MotionBlurKernel(),
        };
        var deconvs = new Dictionary<AlgorithmType, IDeconvolver>
        {
            [AlgorithmType.Wiener] = new WienerDeconvolver(),
        };
        using var runner = new DeblurJobRunner(kernels, deconvs);

        // Proxy is 1/4 scale.
        var proxy = AreaResample.Box(full, 32, 32);
        runner.SetProxy(proxy);
        runner.SetProxyScale(0.25f);

        var p = new KernelParams(BlurType.Custom, 0f, 0f, 0.005f, 0f, 0f, AlgorithmType.Wiener, KernelId: 1);
        // Full-res render — synchronous by awaiting the task inline.
        var fullOut = runner.RenderFullAsync(full, p, proxyScale: 0.25f).GetAwaiter().GetResult();

        // Proxy preview — dispatch via Request then wait for ProxyReady.
        ImageBuffer? previewOut = null;
        using var got = new ManualResetEventSlim(false);
        runner.ProxyReady += (_, e) =>
        {
            previewOut = new ImageBuffer(e.Width, e.Height);
            // Bgra → back to float R/G/B just for comparison.
            for (int y = 0; y < e.Height; y++)
                for (int x = 0; x < e.Width; x++)
                {
                    int o = (y * e.Width + x) * 4;
                    previewOut.B[y * e.Width + x] = e.Bgra[o] / 255f;
                    previewOut.G[y * e.Width + x] = e.Bgra[o + 1] / 255f;
                    previewOut.R[y * e.Width + x] = e.Bgra[o + 2] / 255f;
                }
            got.Set();
        };
        runner.Request(p);
        Assert.True(got.Wait(TimeSpan.FromSeconds(5)));

        var fullResampled = AreaResample.Box(fullOut, 32, 32);
        var psnr = Quality.Psnr(previewOut!, fullResampled);
        Assert.True(psnr >= 30.0, $"proxy preview PSNR vs full resampled: {psnr:F2} dB < 30");
    }
}
