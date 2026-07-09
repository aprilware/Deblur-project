# Deblur Phase 2 Implementation Plan (Out-of-Focus Blur)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the OutOfFocus dropdown option functional via a disk-kernel Wiener deconvolution driven by a Radius slider.

**Architecture:** Add `OutOfFocusBlurKernel` (anti-aliased disk PSF) alongside `MotionBlurKernel`; extend `KernelParams` with a `Radius` field; refactor `DeblurJobRunner` to route between kernels via a dictionary keyed on `BlurType`; extend `MainViewModel` and `MainWindow` with the OutOfFocus panel and swap the coming-soon panel to bind on Gaussian only. No Wiener math changes.

**Tech Stack:** .NET 8 (`net8.0-windows` WPF, `net8.0` Engine + Tests), WPF, CommunityToolkit.Mvvm 8.4.2, FftSharp 2.2.0, System.Drawing.Common, xUnit.

## Global Constraints

- Target framework: `net8.0` for `Deblur.Engine` and `Deblur.Tests`; `net8.0-windows` for the WPF `Deblur` project.
- `Nullable` and `ImplicitUsings` enabled everywhere.
- `Deblur.Engine` stays WPF-free (no `System.Windows` references).
- No new NuGet packages for phase 2.
- MVVM via `CommunityToolkit.Mvvm 8.4.2`.
- All 28 phase-1 tests remain green after every task.
- `Radius` is appended as the last field of `KernelParams` — every existing construction site takes a trailing `0f`.
- `IsComingSoon` is REPLACED by `IsGaussianSelected`; the XAML "coming soon" panel binds to `IsGaussianSelected` (so it disappears for both Motion and OutOfFocus).
- `OnSelectedBlurTypeChanged` resets ONLY the incoming type's params to 0; `Smoothness` is preserved across type switches.
- `Reset()` resets the currently-selected type's params (Motion → `Angle=0, Length=0`; OutOfFocus → `Radius=0`) and always sets `Smoothness=0.005`.
- `UpdateKernel(angle, length)` is a no-op when `SelectedBlurType != BlurType.Motion` (drag arrow does not drive OutOfFocus).
- Full-res render scales `Radius` by `1/proxyScale` for OutOfFocus, exactly as `Length` is scaled for Motion.
- Wiener short-circuits (emit input as-is) when `Motion && Length < 1` OR `OutOfFocus && Radius < 1` OR any other `BlurType`.
- Radius slider range: `Minimum=0`, `Maximum=50`.
- Phase 2 branches from tag `phase1` onto branch `phase2-out-of-focus`.

---

### Task 1: Extend `KernelParams` with a `Radius` field

**Files:**
- Modify: `Deblur.Engine/KernelParams.cs`
- Modify: `Deblur/ViewModels/MainViewModel.cs:88` and `:115`
- Modify: `Deblur.Tests/MotionBlurKernelTests.cs` (5 construction sites at lines 26, 34, 49, 51, 62)
- Modify: `Deblur.Tests/WienerDeconvolverTests.cs` (5 construction sites at lines 17, 32, 34, 50, 73)
- Modify: `Deblur.Tests/DeblurJobRunnerTests.cs` (2 construction sites at lines 47, 73)

**Interfaces:**
- Consumes: nothing new.
- Produces: `KernelParams` becomes `(BlurType Type, float Angle, float Length, float Smoothness, float Radius)`. Every existing call site adds a trailing `0f`. No behavior change.

- [ ] **Step 1: Extend `KernelParams`**

Replace `Deblur.Engine/KernelParams.cs`:
```csharp
namespace Deblur.Engine;

public readonly record struct KernelParams(
    BlurType Type,
    float Angle,
    float Length,
    float Smoothness,
    float Radius);
```

- [ ] **Step 2: Update the two production call sites**

In `Deblur/ViewModels/MainViewModel.cs`, edit line 88:
```csharp
        var current = new KernelParams(BlurType.Motion, Angle, Length, Smoothness, 0f);
```
And line 115:
```csharp
        _runner.Request(new KernelParams(BlurType.Motion, Angle, Length, Smoothness, 0f));
```
(These two lines still hardcode `BlurType.Motion` — Task 4 fixes that. Do not change the type here.)

- [ ] **Step 3: Update the 12 test call sites — add trailing `0f` to each**

In `Deblur.Tests/MotionBlurKernelTests.cs`:
```csharp
// line 26
            new KernelParams(BlurType.Motion, angleDeg, length, 0, 0f));
// line 34
            new KernelParams(BlurType.Motion, 45f, 1f, 0, 0f));
// line 49
            new KernelParams(BlurType.Motion, 30f, 15f, 0, 0f));
// line 51
            new KernelParams(BlurType.Motion, 30f + 180f, 15f, 0, 0f));
// line 62
            new KernelParams(BlurType.Motion, 45f, 10f, 0, 0f));
```

In `Deblur.Tests/WienerDeconvolverTests.cs`:
```csharp
// line 17
            new KernelParams(BlurType.Motion, 30f, 12f, 0, 0f));
// line 32
            new KernelParams(BlurType.Motion, 30f, 12f, 0, 0f));
// line 34
            new KernelParams(BlurType.Motion, 90f, 12f, 0, 0f));
// line 50
            new KernelParams(BlurType.Motion, 0f, 8f, 0, 0f));
// line 73
            new KernelParams(BlurType.Motion, 22f, 100f, 0, 0f));
```

In `Deblur.Tests/DeblurJobRunnerTests.cs`:
```csharp
// line 47
            runner.Request(new KernelParams(BlurType.Motion, Angle: i, Length: 5f, Smoothness: 0.005f, Radius: 0f));
// line 73
            new KernelParams(BlurType.Motion, 45f, 10f, 0.005f, 0f), proxyScale: 0.25f);
```

- [ ] **Step 4: Run the full test suite — confirm no regressions**

```bash
dotnet test Deblur.sln
```
Expected: `Passed: 28`.

- [ ] **Step 5: Commit**

```bash
git add Deblur.Engine/KernelParams.cs Deblur/ViewModels/MainViewModel.cs Deblur.Tests/MotionBlurKernelTests.cs Deblur.Tests/WienerDeconvolverTests.cs Deblur.Tests/DeblurJobRunnerTests.cs
git commit -m "Add Radius field to KernelParams (mechanical)"
```

---

### Task 2: `OutOfFocusBlurKernel` + tests (TDD)

**Files:**
- Create: `Deblur.Engine/OutOfFocusBlurKernel.cs`
- Create: `Deblur.Tests/OutOfFocusBlurKernelTests.cs`
- Modify: `Deblur.Tests/WienerDeconvolverTests.cs` (append one Wiener round-trip test for OutOfFocus)

**Interfaces:**
- Consumes: `IBlurKernel`, `KernelParams`, `WienerDeconvolver`, `DeconvolutionParams`, `SyntheticImages` from `Deblur.Tests.TestHelpers`.
- Produces:
```csharp
public sealed class OutOfFocusBlurKernel : IBlurKernel
{
    public float[,] Build(KernelParams p);   // uses p.Radius; ignores Angle/Length; throws ArgumentOutOfRangeException for Radius < 0.
}
```

- [ ] **Step 1: Write the failing kernel unit tests**

Create `Deblur.Tests/OutOfFocusBlurKernelTests.cs`:
```csharp
using Deblur.Engine;
using Xunit;

namespace Deblur.Tests;

public class OutOfFocusBlurKernelTests
{
    private static float Sum(float[,] k)
    {
        float total = 0f;
        for (int y = 0; y < k.GetLength(0); y++)
            for (int x = 0; x < k.GetLength(1); x++)
                total += k[y, x];
        return total;
    }

    [Fact]
    public void NegativeRadius_Throws()
    {
        var kernel = new OutOfFocusBlurKernel();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => kernel.Build(new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0f, -1f)));
    }

    [Fact]
    public void ZeroRadius_ReturnsSinglePixelIdentity()
    {
        var kernel = new OutOfFocusBlurKernel();
        var k = kernel.Build(new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0f, 0f));
        Assert.Equal(1, k.GetLength(0));
        Assert.Equal(1, k.GetLength(1));
        Assert.Equal(1f, k[0, 0], 5);
    }

    [Fact]
    public void Kernel_SumsToOne()
    {
        var kernel = new OutOfFocusBlurKernel();
        var k = kernel.Build(new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0f, 8f));
        Assert.Equal(1f, Sum(k), 4);
    }

    [Fact]
    public void Kernel_IsRadiallySymmetric()
    {
        var kernel = new OutOfFocusBlurKernel();
        var k = kernel.Build(new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0f, 6f));
        int size = k.GetLength(0);
        int c = size / 2;
        for (int d = 1; d <= c; d++)
        {
            // Four cardinal points at distance d from center must be equal.
            Assert.Equal(k[c, c + d], k[c, c - d], 5);
            Assert.Equal(k[c, c + d], k[c + d, c], 5);
            Assert.Equal(k[c, c + d], k[c - d, c], 5);
        }
    }

    [Fact]
    public void Kernel_HasAntiAliasedEdge()
    {
        var kernel = new OutOfFocusBlurKernel();
        var k = kernel.Build(new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0f, 5f));
        int size = k.GetLength(0);   // 11
        int c = size / 2;            // 5
        float center = k[c, c];
        float edge = k[c, c + 5];     // dist=5, exactly the Radius, expected weight before-normalize = 0.5
        float corner = k[0, 0];       // dist=sqrt(50)≈7.07, outside the disk, expected = 0

        Assert.True(center > 0f);
        Assert.True(edge > 0f && edge < center);
        Assert.Equal(0f, corner, 5);
    }
}
```

- [ ] **Step 2: Write the failing Wiener round-trip test**

Append to `Deblur.Tests/WienerDeconvolverTests.cs` (as a new `[Fact]` inside the existing `WienerDeconvolverTests` class):
```csharp
    [Fact]
    public void OutOfFocus_RoundTrip_RecoversAbovePsnrThreshold()
    {
        // cell=32 keeps checkerboard fundamentals below the disk PSF's first
        // Bessel-zero null; smaller cells are annihilated by defocus.
        var original = SyntheticImages.Checkerboard(128, 128, 32);
        var psf = new OutOfFocusBlurKernel().Build(
            new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0f, 4f));
        var blurred = SyntheticImages.Convolve(original, psf);
        var noisy = SyntheticImages.AddGaussianNoise(blurred, 0.005f, seed: 42);

        var deconv = new WienerDeconvolver().Apply(
            noisy, psf, new DeconvolutionParams(K: 0.005f));

        float blurredPsnr = SyntheticImages.Psnr(original, blurred);
        float deconvPsnr = SyntheticImages.Psnr(original, deconv);
        Assert.True(deconvPsnr > 15f, $"deconv PSNR {deconvPsnr} below 15 dB floor");
        Assert.True(deconvPsnr > blurredPsnr + 3f,
            $"deconv PSNR {deconvPsnr} not > blurred {blurredPsnr} + 3 dB");
    }
```

- [ ] **Step 3: Run tests to verify they fail (compile errors)**

```bash
dotnet test Deblur.sln --filter "FullyQualifiedName~OutOfFocusBlurKernelTests|FullyQualifiedName~OutOfFocus_RoundTrip"
```
Expected: compile errors — `OutOfFocusBlurKernel` not defined.

- [ ] **Step 4: Implement `OutOfFocusBlurKernel`**

Create `Deblur.Engine/OutOfFocusBlurKernel.cs`:
```csharp
namespace Deblur.Engine;

public sealed class OutOfFocusBlurKernel : IBlurKernel
{
    public float[,] Build(KernelParams p)
    {
        if (p.Radius < 0f) throw new ArgumentOutOfRangeException(nameof(p.Radius));

        int r = Math.Max(0, (int)Math.Ceiling(p.Radius));
        int size = 2 * r + 1;
        var k = new float[size, size];

        if (r == 0)
        {
            k[0, 0] = 1f;
            return k;
        }

        float total = 0f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double dx = x - r;
                double dy = y - r;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                float w = (float)Math.Clamp(p.Radius + 0.5 - dist, 0.0, 1.0);
                k[y, x] = w;
                total += w;
            }
        }

        if (total > 0f)
        {
            float inv = 1f / total;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    k[y, x] *= inv;
        }
        return k;
    }
}
```

- [ ] **Step 5: Run the filtered tests — verify green**

```bash
dotnet test Deblur.sln --filter "FullyQualifiedName~OutOfFocusBlurKernelTests|FullyQualifiedName~OutOfFocus_RoundTrip"
```
Expected: 6 passing (5 kernel tests + 1 Wiener round-trip).

- [ ] **Step 6: Run the full suite to confirm no regression**

```bash
dotnet test Deblur.sln
```
Expected: `Passed: 34` (28 phase-1 + 6 new).

- [ ] **Step 7: Commit**

```bash
git add Deblur.Engine/OutOfFocusBlurKernel.cs Deblur.Tests/OutOfFocusBlurKernelTests.cs Deblur.Tests/WienerDeconvolverTests.cs
git commit -m "Add OutOfFocusBlurKernel with disk PSF and Wiener round-trip"
```

---

### Task 3: Route `DeblurJobRunner` by `BlurType` via a kernel dictionary

**Files:**
- Modify: `Deblur.Engine/DeblurJobRunner.cs`
- Modify: `Deblur.Tests/DeblurJobRunnerTests.cs`

**Interfaces:**
- Consumes: `IBlurKernel`, `IDeconvolver`, `KernelParams`, `BlurType`, `OutOfFocusBlurKernel`.
- Produces:
```csharp
public sealed class DeblurJobRunner : IDisposable
{
    public DeblurJobRunner(
        IReadOnlyDictionary<BlurType, IBlurKernel> kernels,
        IDeconvolver deconvolver);
    // remainder unchanged (SetProxy, Request, RenderFullAsync, ProxyReady, HasPending, Dispose)
}
```
Runner short-circuits (emits the input as-is) when the params represent a no-op:
- `Motion && Length < 1` → no-op
- `OutOfFocus && Radius < 1` → no-op
- Any other `BlurType` (i.e. Gaussian) → no-op

- [ ] **Step 1: Write the failing routing + short-circuit tests**

Modify `Deblur.Tests/DeblurJobRunnerTests.cs`. Add these two new tests inside the existing `DeblurJobRunnerTests` class:

```csharp
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

        runner.Request(new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0.005f, Radius: 5f));

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

        runner.Request(new KernelParams(BlurType.Motion, 0f, Length: 0f, Smoothness: 0.005f, Radius: 0f));

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

        runner.Request(new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0.005f, Radius: 0f));

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
```

Also update the two existing `[Fact]` methods in the same file to construct the runner with a dictionary. Replace `new DeblurJobRunner(kernel, deconv)` with:
```csharp
        var kernels = new Dictionary<BlurType, IBlurKernel> { [BlurType.Motion] = kernel };
        using var runner = new DeblurJobRunner(kernels, deconv);
```

- [ ] **Step 2: Run the tests to see them fail (compile errors + old ctor)**

```bash
dotnet test Deblur.sln --filter "FullyQualifiedName~DeblurJobRunnerTests"
```
Expected: compile errors on the dictionary constructor.

- [ ] **Step 3: Update `DeblurJobRunner` to take a dictionary and route by type**

Replace `Deblur.Engine/DeblurJobRunner.cs`:
```csharp
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

    public DeblurJobRunner(
        IReadOnlyDictionary<BlurType, IBlurKernel> kernels,
        IDeconvolver deconvolver)
    {
        _kernels = kernels;
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
        lock (_lock) _pending = p;
        _signal.Set();
    }

    public Task<ImageBuffer> RenderFullAsync(
        ImageBuffer fullRes, KernelParams p, float proxyScale, IProgress<double>? progress = null)
    {
        return Task.Run(() =>
        {
            progress?.Report(0.1);
            float scaleInv = 1f / Math.Max(proxyScale, 1e-6f);
            var scaledParams = p with
            {
                Length = p.Length * scaleInv,
                Radius = p.Radius * scaleInv,
            };
            if (IsNoOp(scaledParams))
            {
                progress?.Report(1.0);
                return fullRes.Clone();
            }
            var psf = _kernels[scaledParams.Type].Build(scaledParams);
            progress?.Report(0.3);
            var result = _deconvolver.Apply(fullRes, psf, new DeconvolutionParams(K: p.Smoothness));
            progress?.Report(1.0);
            return result;
        });
    }

    private static bool IsNoOp(KernelParams p) => p.Type switch
    {
        BlurType.Motion     => p.Length < 1f,
        BlurType.OutOfFocus => p.Radius < 1f,
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
                    if (_pending is null || _proxy is null) break;
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
                    deconv = _deconvolver.Apply(
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
```

- [ ] **Step 4: Run the runner tests — verify green**

```bash
dotnet test Deblur.Tests/Deblur.Tests.csproj --filter "FullyQualifiedName~DeblurJobRunnerTests"
```
Expected: 5 passing (2 existing + 3 new).

Note: target the test project directly (`Deblur.Tests/Deblur.Tests.csproj`), not the solution. `MainViewModel` still calls the old `DeblurJobRunner(kernel, deconv)` constructor and the WPF project will not build until Task 4. `dotnet build Deblur.sln` or `dotnet test Deblur.sln` at the solution level WILL fail on `Deblur/ViewModels/MainViewModel.cs`. That is expected; do not attempt them here.

- [ ] **Step 5: Run the full engine test suite — verify no regression**

```bash
dotnet test Deblur.Tests/Deblur.Tests.csproj
```
Expected: `Passed: 37` (28 phase-1 + 6 from Task 2 + 3 new).

- [ ] **Step 6: Commit**

```bash
git add Deblur.Engine/DeblurJobRunner.cs Deblur.Tests/DeblurJobRunnerTests.cs
git commit -m "Route DeblurJobRunner by BlurType via kernel dictionary"
```

---

### Task 4: `MainViewModel` — Radius property, computed props, routing, reset semantics

**Files:**
- Modify: `Deblur/ViewModels/MainViewModel.cs`

**Interfaces:**
- Consumes: `OutOfFocusBlurKernel`, `MotionBlurKernel`, `DeblurJobRunner`'s new dictionary constructor, `KernelParams` with `Radius`.
- Produces: `MainViewModel` gains public observable `Radius` and computed `IsOutOfFocusSelected`, `IsGaussianSelected`. `IsComingSoon` is removed. `PushCurrentParams` and `EnsureFullResRenderedAsync` build `KernelParams` from `SelectedBlurType` (no more hardcoded `BlurType.Motion`).

- [ ] **Step 1: Replace `MainViewModel.cs`**

Overwrite `Deblur/ViewModels/MainViewModel.cs` with:
```csharp
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

    [ObservableProperty] private BlurType _selectedBlurType = BlurType.Motion;
    [ObservableProperty] private float _angle;
    [ObservableProperty] private float _length;
    [ObservableProperty] private float _radius;
    [ObservableProperty] private float _smoothness = 0.005f;
    [ObservableProperty] private string? _currentFilePath;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private WriteableBitmap? _previewBitmap;

    public bool IsMotionSelected     => SelectedBlurType == BlurType.Motion;
    public bool IsOutOfFocusSelected => SelectedBlurType == BlurType.OutOfFocus;
    public bool IsGaussianSelected   => SelectedBlurType == BlurType.Gaussian;

    public MainViewModel()
    {
        _dispatcher = Application.Current.Dispatcher;
        var kernels = new Dictionary<BlurType, IBlurKernel>
        {
            [BlurType.Motion]     = new MotionBlurKernel(),
            [BlurType.OutOfFocus] = new OutOfFocusBlurKernel(),
        };
        _runner = new DeblurJobRunner(kernels, new WienerDeconvolver());
        _runner.ProxyReady += OnProxyReady;
    }

    partial void OnSelectedBlurTypeChanged(BlurType value)
    {
        OnPropertyChanged(nameof(IsMotionSelected));
        OnPropertyChanged(nameof(IsOutOfFocusSelected));
        OnPropertyChanged(nameof(IsGaussianSelected));

        // Reset only the incoming type's params so switching shows the raw image.
        // Smoothness is preserved across type switches (it's a Wiener param, not a blur param).
        switch (value)
        {
            case BlurType.Motion:
                Angle = 0f;
                Length = 0f;
                break;
            case BlurType.OutOfFocus:
                Radius = 0f;
                break;
        }
        PushCurrentParams();
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
        // Drag arrow only drives motion blur.
        if (SelectedBlurType != BlurType.Motion) return;
        Angle = angle;
        Length = length;
        PushCurrentParams();
    }

    partial void OnSmoothnessChanged(float value) { InvalidateFullResCache(); PushCurrentParams(); }
    partial void OnAngleChanged(float value)      { InvalidateFullResCache(); PushCurrentParams(); }
    partial void OnLengthChanged(float value)     { InvalidateFullResCache(); PushCurrentParams(); }
    partial void OnRadiusChanged(float value)     { InvalidateFullResCache(); PushCurrentParams(); }

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
        }
        Smoothness = 0.005f;
        PushCurrentParams();
    }

    // Cached full-resolution render; invalidated on any param change.
    private ImageBuffer? _fullResBuffer;
    private KernelParams? _fullResParams;

    public async Task EnsureFullResRenderedAsync(IProgress<double> progress)
    {
        if (_originalFullRes is null) throw new InvalidOperationException("No image loaded.");
        var current = BuildCurrentParams();
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

    private KernelParams BuildCurrentParams()
        => new KernelParams(SelectedBlurType, Angle, Length, Smoothness, Radius);

    private void PushCurrentParams()
    {
        if (_proxy is null) return;
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

    public void Dispose() => _runner.Dispose();
}
```

- [ ] **Step 2: Build the whole solution — confirm the WPF project now compiles**

```bash
dotnet build Deblur.sln
```
Expected: 0 errors.

- [ ] **Step 3: Run the full test suite — no regressions**

```bash
dotnet test Deblur.sln
```
Expected: `Passed: 37` (same as after Task 3).

- [ ] **Step 4: Commit**

```bash
git add Deblur/ViewModels/MainViewModel.cs
git commit -m "Wire MainViewModel for OutOfFocus: Radius, IsOutOfFocusSelected, per-type reset"
```

---

### Task 5: `MainWindow.xaml` — add OutOfFocus sidebar panel; rebind coming-soon to Gaussian

**Files:**
- Modify: `Deblur/MainWindow.xaml`

**Interfaces:**
- Consumes: `MainViewModel.Radius`, `MainViewModel.IsOutOfFocusSelected`, `MainViewModel.IsGaussianSelected`, existing `MainViewModel.Reset` and Render/Save buttons.
- Produces: A working WPF window whose sidebar swaps between Motion / OutOfFocus / (coming-soon Gaussian) panels based on the dropdown.

- [ ] **Step 1: Add the OutOfFocus panel and rebind the coming-soon TextBlock**

Modify `Deblur/MainWindow.xaml`. Locate the block between the Motion `<Grid>` and the "Coming soon" `<TextBlock>` (currently lines 46 to 67). Replace that entire span with:

```xml
                    <Grid Margin="0,12,0,0" Visibility="{Binding IsMotionSelected, Converter={StaticResource BoolToVis}}">
                        <StackPanel>
                            <TextBlock Text="Angle (°)" Margin="0,4,0,0"/>
                            <Slider Minimum="0" Maximum="360" Value="{Binding Angle}"/>
                            <TextBlock Text="{Binding Angle, StringFormat={}{0:0.0}}" HorizontalAlignment="Right"/>

                            <TextBlock Text="Length (px, proxy)" Margin="0,8,0,0"/>
                            <Slider Minimum="0" Maximum="100" Value="{Binding Length}"/>
                            <TextBlock Text="{Binding Length, StringFormat={}{0:0.0}}" HorizontalAlignment="Right"/>

                            <TextBlock Text="Smoothness" Margin="0,8,0,0"/>
                            <Slider Minimum="0.0001" Maximum="0.1" SmallChange="0.0005" Value="{Binding Smoothness}"/>
                            <TextBlock Text="{Binding Smoothness, StringFormat={}{0:0.0000}}" HorizontalAlignment="Right"/>

                            <Button Content="Reset" Margin="0,12,0,0" Click="OnResetClick"/>
                            <Button Content="Render full resolution" Margin="0,8,0,0" Click="OnRenderFullClick"/>
                        </StackPanel>
                    </Grid>

                    <Grid Margin="0,12,0,0" Visibility="{Binding IsOutOfFocusSelected, Converter={StaticResource BoolToVis}}">
                        <StackPanel>
                            <TextBlock Text="Radius (px, proxy)" Margin="0,4,0,0"/>
                            <Slider Minimum="0" Maximum="50" Value="{Binding Radius}"/>
                            <TextBlock Text="{Binding Radius, StringFormat={}{0:0.0}}" HorizontalAlignment="Right"/>

                            <TextBlock Text="Smoothness" Margin="0,8,0,0"/>
                            <Slider Minimum="0.0001" Maximum="0.1" SmallChange="0.0005" Value="{Binding Smoothness}"/>
                            <TextBlock Text="{Binding Smoothness, StringFormat={}{0:0.0000}}" HorizontalAlignment="Right"/>

                            <Button Content="Reset" Margin="0,12,0,0" Click="OnResetClick"/>
                            <Button Content="Render full resolution" Margin="0,8,0,0" Click="OnRenderFullClick"/>
                        </StackPanel>
                    </Grid>

                    <TextBlock Margin="0,12,0,0" TextWrapping="Wrap"
                               Visibility="{Binding IsGaussianSelected, Converter={StaticResource BoolToVis}}"
                               Text="This blur type will be supported in a future phase. Select 'Motion' or 'OutOfFocus' to try the current build."/>
```

Leave the `<TextBlock Margin="0,20,0,0" Text="{Binding StatusMessage}"...>` line beneath unchanged.

- [ ] **Step 2: Build**

```bash
dotnet build Deblur.sln
```
Expected: 0 errors, 0 new warnings.

- [ ] **Step 3: Run the full test suite — must still be green**

```bash
dotnet test Deblur.sln
```
Expected: `Passed: 37`.

- [ ] **Step 4: Commit**

```bash
git add Deblur/MainWindow.xaml
git commit -m "Add OutOfFocus sidebar panel; rebind coming-soon to IsGaussianSelected"
```

---

### Task 6: Manual smoke test pass + tag `phase2`

**Files:** none.

**Interfaces:** none.

- [ ] **Step 1: Run the app**

```bash
dotnet run --project Deblur/Deblur.csproj
```

Walk through the checklist:

- [ ] Open a PNG via File → Open. Preview shows the raw image; sliders read Angle=0.0, Length=0.0, Smoothness=0.0050.
- [ ] Switch dropdown to "OutOfFocus". Sidebar swaps: Radius + Smoothness + Reset + Render buttons. Preview stays on raw image.
- [ ] Drag the Radius slider up to ~5. Preview updates within a beat, softer than a pure raw. Drag it back to 0. Preview returns to raw.
- [ ] Move Smoothness — preview responds.
- [ ] Click Reset — sliders return to defaults; preview returns to raw image immediately.
- [ ] Click "Render full resolution" — busy overlay shows, closes; status shows "Full-resolution render ready".
- [ ] File → Save As → PNG. Reopen the saved file externally; it should be a full-resolution deblurred image matching the Radius setting.
- [ ] Switch dropdown to "Gaussian". Sidebar hides everything; the "coming soon" text appears.
- [ ] Switch back to "Motion". Motion panel returns. Angle/Length are still at their previous Motion state (preserved across type switches).
- [ ] Switch to "OutOfFocus" a second time — Radius resets to 0 (per the "raw image on switch" behavior).
- [ ] Drop a corrupt file (rename a `.txt` to `.jpg`) — error modal appears; app state unchanged.
- [ ] Under OutOfFocus, drag on the preview — the arrow may render but does NOT change the image (arrow is Motion-only; this is acceptable phase-2 cosmetic).

- [ ] **Step 2: Commit any smoke-test-triggered fixes**

If the smoke test surfaces bugs, fix them and commit each fix separately with a message describing the failure and the fix. If nothing was wrong, no commit is needed for this step.

- [ ] **Step 3: Tag phase 2 complete**

```bash
git tag phase2
```

---

## Summary

Six tasks, each an independently reviewable commit. Task 1 is a mechanical field addition. Task 2 adds the disk kernel with TDD and a Wiener round-trip integration check. Task 3 refactors the runner for kernel routing and locks in the extended short-circuit with three new tests. Task 4 rewires the ViewModel for both blur types and closes the phase-1 `BlurType.Motion` hardcoding finding. Task 5 adds the OutOfFocus sidebar panel and rebinds the coming-soon panel to Gaussian only. Task 6 smoke-tests end-to-end and tags `phase2`.
