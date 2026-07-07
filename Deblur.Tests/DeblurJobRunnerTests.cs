using System.Collections.Concurrent;
using Deblur.Engine;
using Deblur.Tests.TestHelpers;
using Xunit;

namespace Deblur.Tests;

public class DeblurJobRunnerTests
{
    private sealed class SlowStubDeconvolver : IDeconvolver
    {
        public int CallCount;
        public readonly ConcurrentBag<float> ObservedAngles = new();
        public int SleepMs { get; init; } = 10;

        public ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p)
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

    [Fact]
    public void Rapid_Requests_Coalesce_And_LastParamsWin()
    {
        var kernel = new RecordingStubKernel();
        var deconv = new SlowStubDeconvolver { SleepMs = 15 };
        var kernels = new Dictionary<BlurType, IBlurKernel> { [BlurType.Motion] = kernel };
        using var runner = new DeblurJobRunner(kernels, deconv);
        runner.SetProxy(SyntheticImages.Checkerboard(32, 32, 4));

        int received = 0;
        var lastEvent = new ManualResetEventSlim();
        runner.ProxyReady += (_, __) =>
        {
            Interlocked.Increment(ref received);
            lastEvent.Set();
        };

        for (int i = 0; i < 100; i++)
            runner.Request(new KernelParams(BlurType.Motion, Angle: i, Length: 5f, Smoothness: 0.005f, Radius: 0f, Sigma: 0f));

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
        using var runner = new DeblurJobRunner(kernels, deconv);

        var full = SyntheticImages.Checkerboard(200, 200, 10);
        // proxyScale = proxyW / fullW = 50 / 200 = 0.25 → length multiplier = 4x
        await runner.RenderFullAsync(full,
            new KernelParams(BlurType.Motion, 45f, 10f, 0.005f, 0f, 0f), proxyScale: 0.25f);

        Assert.Contains(kernel.Seen, p => Math.Abs(p.Length - 40f) < 0.001f);
    }

    [Fact]
    public async Task RenderFullAsync_ScalesKernelRadiusByInverseProxyScale()
    {
        var kernel = new RecordingStubKernel();
        var deconv = new SlowStubDeconvolver { SleepMs = 0 };
        var kernels = new Dictionary<BlurType, IBlurKernel> { [BlurType.OutOfFocus] = kernel };
        using var runner = new DeblurJobRunner(kernels, deconv);

        var full = SyntheticImages.Checkerboard(200, 200, 10);
        // proxyScale = 0.25 → radius multiplier = 4x (10 → 40).
        await runner.RenderFullAsync(full,
            new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0.005f, Radius: 10f, Sigma: 0f), proxyScale: 0.25f);

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
        using var runner = new DeblurJobRunner(kernels, deconv);
        runner.SetProxy(SyntheticImages.Checkerboard(32, 32, 4));

        runner.Request(new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0.005f, Radius: 5f, Sigma: 0f));

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
        using var runner = new DeblurJobRunner(kernels, deconv);
        runner.SetProxy(SyntheticImages.Checkerboard(32, 32, 4));

        int received = 0;
        runner.ProxyReady += (_, __) => Interlocked.Increment(ref received);

        runner.Request(new KernelParams(BlurType.Motion, 0f, Length: 0f, Smoothness: 0.005f, Radius: 0f, Sigma: 0f));

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
        using var runner = new DeblurJobRunner(kernels, deconv);
        runner.SetProxy(SyntheticImages.Checkerboard(32, 32, 4));

        int received = 0;
        runner.ProxyReady += (_, __) => Interlocked.Increment(ref received);

        runner.Request(new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0.005f, Radius: 0f, Sigma: 0f));

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
}
