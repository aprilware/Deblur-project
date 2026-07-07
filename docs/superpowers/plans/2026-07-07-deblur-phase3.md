# Deblur Phase 3 Implementation Plan (Gaussian Blur)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Gaussian dropdown option functional via a Wiener deconvolution against a 2D Gaussian PSF driven by a Sigma slider.

**Architecture:** Add `GaussianBlurKernel` (2D Gaussian PSF) as the third `IBlurKernel` implementation. Append `Sigma` to `KernelParams`. Extend `DeblurJobRunner.IsNoOp` with a `Gaussian => Sigma < 1f` case and `RenderFullAsync` to scale Sigma by `1/proxyScale`. `MainViewModel` gains a `Sigma` observable, `HasImage` computed, and a third dictionary entry. `MainWindow.xaml` is restructured so Smoothness + Reset + Render live in a shared footer below the three per-type Grids (no more triplication); the coming-soon TextBlock is deleted since all three types are now functional.

**Tech Stack:** .NET 8 (`net8.0-windows` WPF, `net8.0` Engine + Tests), WPF, CommunityToolkit.Mvvm 8.4.2, FftSharp 2.2.0, System.Drawing.Common, xUnit.

## Global Constraints

- Target framework: `net8.0` for `Deblur.Engine` and `Deblur.Tests`; `net8.0-windows` for the WPF `Deblur` project.
- `Nullable` and `ImplicitUsings` enabled everywhere.
- `Deblur.Engine` stays WPF-free (no `System.Windows` references).
- No new NuGet packages for phase 3.
- MVVM via `CommunityToolkit.Mvvm 8.4.2`.
- All 38 phase-2 tests remain green after every task.
- `Sigma` is appended as the last field of `KernelParams` — every existing construction site takes a trailing `0f`.
- `IsNoOp(p)`: `Motion && Length < 1` OR `OutOfFocus && Radius < 1` OR `Gaussian && Sigma < 1` OR any other `BlurType` → no-op.
- `RenderFullAsync` scales all three of `Length`, `Radius`, `Sigma` by `1/proxyScale`.
- Sigma slider range: `Minimum=0`, `Maximum=10`.
- Sidebar layout after phase 3: Motion Grid | OutOfFocus Grid | Gaussian Grid | Shared Footer (Smoothness + Reset + Render) | StatusMessage. The coming-soon TextBlock is deleted. Only one of the three per-type Grids is visible at a time (via `Is<Type>Selected`); the shared footer is visible when `HasImage` is true.
- `MainViewModel.HasImage` is a computed `bool` returning `_proxy is not null`. `LoadImageFromBytes` fires `OnPropertyChanged(nameof(HasImage))` after `_proxy` is assigned.
- `OnSelectedBlurTypeChanged` resets ONLY the incoming type's params (Gaussian sets `Sigma = 0f`); `Reset()` resets the currently-selected type's params plus always `Smoothness = 0.005f`.
- Phase 3 branches from tag `phase2` onto branch `phase3-gaussian`.

---

### Task 1: Extend `KernelParams` with a `Sigma` field

**Files:**
- Modify: `Deblur.Engine/KernelParams.cs`
- Modify: `Deblur/ViewModels/MainViewModel.cs:148`
- Modify: `Deblur.Tests/DeblurJobRunnerTests.cs` (6 sites at lines 48, 75, 91, 110, 138, 167)
- Modify: `Deblur.Tests/MotionBlurKernelTests.cs` (5 sites at lines 26, 34, 49, 51, 62)
- Modify: `Deblur.Tests/OutOfFocusBlurKernelTests.cs` (5 sites at lines 22, 29, 39, 47, 63)
- Modify: `Deblur.Tests/WienerDeconvolverTests.cs` (6 sites at lines 17, 32, 34, 50, 73, 92)

**Interfaces:**
- Consumes: nothing new.
- Produces: `KernelParams` becomes `(BlurType Type, float Angle, float Length, float Smoothness, float Radius, float Sigma)`. Every existing call site adds a trailing `0f`. No behavior change.

- [ ] **Step 1: Extend `KernelParams`**

Replace `Deblur.Engine/KernelParams.cs`:
```csharp
namespace Deblur.Engine;

public readonly record struct KernelParams(
    BlurType Type,
    float Angle,
    float Length,
    float Smoothness,
    float Radius,
    float Sigma);
```

- [ ] **Step 2: Update the single production call site in `MainViewModel`**

In `Deblur/ViewModels/MainViewModel.cs`, edit line 148:
```csharp
        => new KernelParams(SelectedBlurType, Angle, Length, Smoothness, Radius, 0f);
```
Do NOT reference a `Sigma` property here — Task 4 will add the observable and update this line to use it.

- [ ] **Step 3: Update the 22 test call sites — add trailing `0f` to each**

In `Deblur.Tests/DeblurJobRunnerTests.cs`:
```csharp
// line 48
            runner.Request(new KernelParams(BlurType.Motion, Angle: i, Length: 5f, Smoothness: 0.005f, Radius: 0f, Sigma: 0f));
// line 75
            new KernelParams(BlurType.Motion, 45f, 10f, 0.005f, 0f, 0f), proxyScale: 0.25f);
// line 91
            new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0.005f, Radius: 10f, Sigma: 0f), proxyScale: 0.25f);
// line 110
            runner.Request(new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0.005f, Radius: 5f, Sigma: 0f));
// line 138
            runner.Request(new KernelParams(BlurType.Motion, 0f, Length: 0f, Smoothness: 0.005f, Radius: 0f, Sigma: 0f));
// line 167
            runner.Request(new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0.005f, Radius: 0f, Sigma: 0f));
```

In `Deblur.Tests/MotionBlurKernelTests.cs`:
```csharp
// line 26
            new KernelParams(BlurType.Motion, angleDeg, length, 0, 0f, 0f));
// line 34
            new KernelParams(BlurType.Motion, 45f, 1f, 0, 0f, 0f));
// line 49
            new KernelParams(BlurType.Motion, 30f, 15f, 0, 0f, 0f));
// line 51
            new KernelParams(BlurType.Motion, 30f + 180f, 15f, 0, 0f, 0f));
// line 62
            new KernelParams(BlurType.Motion, 45f, 10f, 0, 0f, 0f));
```

In `Deblur.Tests/OutOfFocusBlurKernelTests.cs`:
```csharp
// line 22
            () => kernel.Build(new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0f, -1f, 0f)));
// line 29
        var k = kernel.Build(new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0f, 0f, 0f));
// line 39
        var k = kernel.Build(new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0f, 8f, 0f));
// line 47
        var k = kernel.Build(new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0f, 6f, 0f));
// line 63
        var k = kernel.Build(new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0f, 5f, 0f));
```

In `Deblur.Tests/WienerDeconvolverTests.cs`:
```csharp
// line 17
            new KernelParams(BlurType.Motion, 30f, 12f, 0, 0f, 0f));
// line 32
            new KernelParams(BlurType.Motion, 30f, 12f, 0, 0f, 0f));
// line 34
            new KernelParams(BlurType.Motion, 90f, 12f, 0, 0f, 0f));
// line 50
            new KernelParams(BlurType.Motion, 0f, 8f, 0, 0f, 0f));
// line 73
            new KernelParams(BlurType.Motion, 22f, 100f, 0, 0f, 0f));
// line 92
            new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0f, 4f, 0f));
```

- [ ] **Step 4: Run the full test suite — confirm no regressions**

```bash
dotnet test Deblur.sln
```
Expected: `Passed: 38`.

- [ ] **Step 5: Commit**

```bash
git add Deblur.Engine/KernelParams.cs Deblur/ViewModels/MainViewModel.cs Deblur.Tests/DeblurJobRunnerTests.cs Deblur.Tests/MotionBlurKernelTests.cs Deblur.Tests/OutOfFocusBlurKernelTests.cs Deblur.Tests/WienerDeconvolverTests.cs
git commit -m "Add Sigma field to KernelParams (mechanical)"
```

---

### Task 2: `GaussianBlurKernel` + tests (TDD)

**Files:**
- Create: `Deblur.Engine/GaussianBlurKernel.cs`
- Create: `Deblur.Tests/GaussianBlurKernelTests.cs`
- Modify: `Deblur.Tests/WienerDeconvolverTests.cs` (append one Gaussian Wiener round-trip test)

**Interfaces:**
- Consumes: `IBlurKernel`, `KernelParams` (with `Sigma`), `WienerDeconvolver`, `DeconvolutionParams`, `SyntheticImages` from `Deblur.Tests.TestHelpers`.
- Produces:
```csharp
public sealed class GaussianBlurKernel : IBlurKernel
{
    public float[,] Build(KernelParams p);   // uses p.Sigma; throws ArgumentOutOfRangeException for Sigma < 0; returns 1x1 identity for Sigma == 0.
}
```

- [ ] **Step 1: Write the failing kernel unit tests**

Create `Deblur.Tests/GaussianBlurKernelTests.cs`:
```csharp
using Deblur.Engine;
using Xunit;

namespace Deblur.Tests;

public class GaussianBlurKernelTests
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
    public void NegativeSigma_Throws()
    {
        var kernel = new GaussianBlurKernel();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => kernel.Build(new KernelParams(BlurType.Gaussian, 0f, 0f, 0f, 0f, -1f)));
    }

    [Fact]
    public void ZeroSigma_ReturnsSinglePixelIdentity()
    {
        var kernel = new GaussianBlurKernel();
        var k = kernel.Build(new KernelParams(BlurType.Gaussian, 0f, 0f, 0f, 0f, 0f));
        Assert.Equal(1, k.GetLength(0));
        Assert.Equal(1, k.GetLength(1));
        Assert.Equal(1f, k[0, 0], 5);
    }

    [Fact]
    public void Kernel_SumsToOne()
    {
        var kernel = new GaussianBlurKernel();
        var k = kernel.Build(new KernelParams(BlurType.Gaussian, 0f, 0f, 0f, 0f, 2f));
        Assert.Equal(1f, Sum(k), 4);
    }

    [Fact]
    public void Kernel_IsRadiallySymmetric()
    {
        var kernel = new GaussianBlurKernel();
        var k = kernel.Build(new KernelParams(BlurType.Gaussian, 0f, 0f, 0f, 0f, 2f));
        int size = k.GetLength(0);
        int c = size / 2;
        for (int d = 1; d <= c; d++)
        {
            Assert.Equal(k[c, c + d], k[c, c - d], 5);
            Assert.Equal(k[c, c + d], k[c + d, c], 5);
            Assert.Equal(k[c, c + d], k[c - d, c], 5);
        }
    }

    [Fact]
    public void Kernel_PeaksAtCenter_DecaysMonotonically()
    {
        var kernel = new GaussianBlurKernel();
        var k = kernel.Build(new KernelParams(BlurType.Gaussian, 0f, 0f, 0f, 0f, 2f));
        int size = k.GetLength(0);
        int c = size / 2;
        float center = k[c, c];

        // Center is the strict maximum.
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                if (y != c || x != c)
                    Assert.True(k[y, x] < center, $"k[{y},{x}]={k[y, x]} not strictly less than center {center}");

        // Along the +x axis, values decay monotonically.
        for (int d = 1; d < c; d++)
            Assert.True(k[c, c + d] > k[c, c + d + 1],
                $"k[c, c+{d}]={k[c, c + d]} not > k[c, c+{d + 1}]={k[c, c + d + 1]}");
    }
}
```

- [ ] **Step 2: Write the failing Wiener round-trip test**

Append to `Deblur.Tests/WienerDeconvolverTests.cs` as a new `[Fact]` inside the existing `WienerDeconvolverTests` class:
```csharp
    [Fact]
    public void Gaussian_RoundTrip_RecoversAbovePsnrThreshold()
    {
        // Gaussian PSF has no frequency-domain nulls, so Wiener recovery
        // is well-conditioned; cell=32 matches the phase-1/2 tests.
        var original = SyntheticImages.Checkerboard(128, 128, 32);
        var psf = new GaussianBlurKernel().Build(
            new KernelParams(BlurType.Gaussian, 0f, 0f, 0f, 0f, 2f));
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
dotnet test Deblur.sln --filter "FullyQualifiedName~GaussianBlurKernelTests|FullyQualifiedName~Gaussian_RoundTrip"
```
Expected: compile errors — `GaussianBlurKernel` not defined.

- [ ] **Step 4: Implement `GaussianBlurKernel`**

Create `Deblur.Engine/GaussianBlurKernel.cs`:
```csharp
namespace Deblur.Engine;

public sealed class GaussianBlurKernel : IBlurKernel
{
    public float[,] Build(KernelParams p)
    {
        if (p.Sigma < 0f) throw new ArgumentOutOfRangeException(nameof(p.Sigma));

        int r = Math.Max(0, (int)Math.Ceiling(3.0 * p.Sigma));
        int size = 2 * r + 1;
        var k = new float[size, size];

        if (r == 0)
        {
            k[0, 0] = 1f;
            return k;
        }

        double twoSigmaSq = 2.0 * p.Sigma * p.Sigma;
        float total = 0f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double dx = x - r;
                double dy = y - r;
                float w = (float)Math.Exp(-(dx * dx + dy * dy) / twoSigmaSq);
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
dotnet test Deblur.sln --filter "FullyQualifiedName~GaussianBlurKernelTests|FullyQualifiedName~Gaussian_RoundTrip"
```
Expected: 6 passing (5 kernel tests + 1 Wiener round-trip).

- [ ] **Step 6: Run the full suite to confirm no regression**

```bash
dotnet test Deblur.sln
```
Expected: `Passed: 44` (38 phase-2 + 6 new).

- [ ] **Step 7: Commit**

```bash
git add Deblur.Engine/GaussianBlurKernel.cs Deblur.Tests/GaussianBlurKernelTests.cs Deblur.Tests/WienerDeconvolverTests.cs
git commit -m "Add GaussianBlurKernel with 2D Gaussian PSF and Wiener round-trip"
```

---

### Task 3: Extend `DeblurJobRunner` with Gaussian routing + Sigma scaling + IsNoOp doc

**Files:**
- Modify: `Deblur.Engine/DeblurJobRunner.cs`
- Modify: `Deblur.Tests/DeblurJobRunnerTests.cs`

**Interfaces:**
- Consumes: `GaussianBlurKernel` (Task 2), `KernelParams.Sigma` (Task 1).
- Produces: `DeblurJobRunner`'s `IsNoOp` treats `Gaussian && Sigma < 1f` as no-op. `RenderFullAsync` scales `Sigma` by `1/proxyScale` alongside `Length` and `Radius`. `IsNoOp` gains an XML doc comment stating the invariant with the kernel dictionary.

- [ ] **Step 1: Write the failing tests**

Modify `Deblur.Tests/DeblurJobRunnerTests.cs`. Add these three new `[Fact]` methods inside the existing `DeblurJobRunnerTests` class:

```csharp
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
        using var runner = new DeblurJobRunner(kernels, deconv);
        runner.SetProxy(SyntheticImages.Checkerboard(32, 32, 4));

        runner.Request(new KernelParams(BlurType.Gaussian, 0f, 0f, 0.005f, 0f, Sigma: 3f));

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
        using var runner = new DeblurJobRunner(kernels, deconv);
        runner.SetProxy(SyntheticImages.Checkerboard(32, 32, 4));

        int received = 0;
        runner.ProxyReady += (_, __) => Interlocked.Increment(ref received);

        runner.Request(new KernelParams(BlurType.Gaussian, 0f, 0f, 0.005f, 0f, Sigma: 0f));

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
        using var runner = new DeblurJobRunner(kernels, deconv);

        var full = SyntheticImages.Checkerboard(200, 200, 10);
        // proxyScale = 0.25 → sigma multiplier = 4x (3 → 12).
        await runner.RenderFullAsync(full,
            new KernelParams(BlurType.Gaussian, 0f, 0f, 0.005f, 0f, Sigma: 3f), proxyScale: 0.25f);

        Assert.Contains(kernel.Seen, p => Math.Abs(p.Sigma - 12f) < 0.001f);
    }
```

- [ ] **Step 2: Run tests to verify they fail (compile errors on missing IsNoOp/scaling for Gaussian)**

Actually, the first test will PASS at this point (Task 2's routing works because `_kernels[p.Type]` handles the lookup) — the failing behavior lives in the other two: without Task 3's IsNoOp Gaussian case, `Request_WithGaussianSigmaBelow1_...` calls the deconvolver (violating `deconv.CallCount == 0`). Without Task 3's Sigma scaling, `RenderFullAsync_ScalesKernelSigmaByInverseProxyScale` sees `p.Sigma == 3` (unscaled) instead of `12`.

Run:
```bash
dotnet test Deblur.sln --filter "FullyQualifiedName~DeblurJobRunnerTests"
```
Expected: 7 passing (6 pre-existing + the new `Request_WithGaussianType_DispatchesToGaussianKernel`), 2 failing (short-circuit + scaling).

- [ ] **Step 3: Extend `DeblurJobRunner`**

In `Deblur.Engine/DeblurJobRunner.cs`, replace the `IsNoOp` static method (currently around line 75) with:

```csharp
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
```

Also inside `RenderFullAsync`, extend the `with` expression to scale `Sigma` too:

```csharp
            var scaledParams = p with
            {
                Length = p.Length * scaleInv,
                Radius = p.Radius * scaleInv,
                Sigma  = p.Sigma  * scaleInv,
            };
```

- [ ] **Step 4: Run the runner tests — verify green**

```bash
dotnet test Deblur.sln --filter "FullyQualifiedName~DeblurJobRunnerTests"
```
Expected: 9 passing (6 pre-existing + 3 new).

- [ ] **Step 5: Run the full suite — verify no regression**

```bash
dotnet test Deblur.sln
```
Expected: `Passed: 47` (38 phase-2 + 6 from Task 2 + 3 new).

- [ ] **Step 6: Commit**

```bash
git add Deblur.Engine/DeblurJobRunner.cs Deblur.Tests/DeblurJobRunnerTests.cs
git commit -m "Route Gaussian through DeblurJobRunner with Sigma short-circuit and scaling"
```

---

### Task 4: `MainViewModel` — Sigma observable, HasImage, dictionary entry, per-type reset arm

**Files:**
- Modify: `Deblur/ViewModels/MainViewModel.cs`

**Interfaces:**
- Consumes: `GaussianBlurKernel` (Task 2), `KernelParams.Sigma` (Task 1).
- Produces: `MainViewModel` gains `Sigma` observable, `IsGaussianSelected` becomes reachable through the dictionary, `HasImage` computed fires when an image loads, per-type reset covers Gaussian, and `BuildCurrentParams` now includes `Sigma`.

- [ ] **Step 1: Add the `Sigma` observable and its OnChanged partial**

In `Deblur/ViewModels/MainViewModel.cs`, add after the existing `_radius` observable (line 21):
```csharp
    [ObservableProperty] private float _sigma;
```

And after `partial void OnRadiusChanged(float value)` (line 97), add:
```csharp
    partial void OnSigmaChanged(float value)      { InvalidateFullResCache(); PushCurrentParams(); }
```

- [ ] **Step 2: Add the `HasImage` computed property**

Add after the existing `IsGaussianSelected` line (~line 31):
```csharp
    public bool HasImage => _proxy is not null;
```

- [ ] **Step 3: Add the Gaussian kernel to the dictionary**

Replace the `kernels` initializer inside the `MainViewModel` constructor (lines ~36-40):
```csharp
        var kernels = new Dictionary<BlurType, IBlurKernel>
        {
            [BlurType.Motion]     = new MotionBlurKernel(),
            [BlurType.OutOfFocus] = new OutOfFocusBlurKernel(),
            [BlurType.Gaussian]   = new GaussianBlurKernel(),
        };
```

- [ ] **Step 4: Fire `HasImage` when the proxy is loaded**

In `LoadImageFromBytes` (currently around line 66-83), after the line `_runner.SetProxy(_proxy);` insert:
```csharp
        OnPropertyChanged(nameof(HasImage));
```
Then leave the existing `Reset();` call after it.

- [ ] **Step 5: Add the Gaussian reset arm in both switch statements**

In `OnSelectedBlurTypeChanged` (currently lines ~50-64), extend the reset `switch (value)` so it reads:
```csharp
        switch (value)
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
```

In `Reset()` (currently lines ~99-114), extend the `switch (SelectedBlurType)` similarly:
```csharp
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
```

- [ ] **Step 6: Update `BuildCurrentParams` to include `Sigma`**

Replace `BuildCurrentParams()` (line 148 as of Task 1) with:
```csharp
    private KernelParams BuildCurrentParams()
        => new KernelParams(SelectedBlurType, Angle, Length, Smoothness, Radius, Sigma);
```

- [ ] **Step 7: Build the whole solution — confirm the WPF project still compiles**

```bash
dotnet build Deblur.sln
```
Expected: 0 errors. The XAML still binds to `IsGaussianSelected` on the coming-soon TextBlock; that binding will resolve at runtime and show the "coming soon" text if the user selects Gaussian — a temporary regression that Task 5 fixes.

- [ ] **Step 8: Run the full test suite — no regressions**

```bash
dotnet test Deblur.sln
```
Expected: `Passed: 47`.

- [ ] **Step 9: Commit**

```bash
git add Deblur/ViewModels/MainViewModel.cs
git commit -m "Wire MainViewModel for Gaussian: Sigma observable, HasImage, per-type reset"
```

---

### Task 5: `MainWindow.xaml` — restructure sidebar with Gaussian panel + shared footer; delete coming-soon

**Files:**
- Modify: `Deblur/MainWindow.xaml`

**Interfaces:**
- Consumes: `MainViewModel.Sigma`, `MainViewModel.IsGaussianSelected`, `MainViewModel.HasImage`, and the existing `IsMotionSelected` / `IsOutOfFocusSelected` / `Reset()` / `OnRenderFullClick` handlers.
- Produces: Sidebar with three per-type Grids (each holding only its unique slider(s)) + one shared footer StackPanel (Smoothness + Reset + Render) below them, gated on `HasImage`. Coming-soon TextBlock removed.

- [ ] **Step 1: Replace the sidebar's per-type panels + coming-soon TextBlock with the new structure**

In `Deblur/MainWindow.xaml`, locate the span from the Motion `<Grid Margin="0,12,0,0" Visibility="{Binding IsMotionSelected...}">` opening (currently around line 46) through the closing tag of the coming-soon TextBlock (currently around line 82). Replace that entire span with:

```xml
                    <Grid Margin="0,12,0,0" Visibility="{Binding IsMotionSelected, Converter={StaticResource BoolToVis}}">
                        <StackPanel>
                            <TextBlock Text="Angle (°)" Margin="0,4,0,0"/>
                            <Slider Minimum="0" Maximum="360" Value="{Binding Angle}"/>
                            <TextBlock Text="{Binding Angle, StringFormat={}{0:0.0}}" HorizontalAlignment="Right"/>

                            <TextBlock Text="Length (px, proxy)" Margin="0,8,0,0"/>
                            <Slider Minimum="0" Maximum="100" Value="{Binding Length}"/>
                            <TextBlock Text="{Binding Length, StringFormat={}{0:0.0}}" HorizontalAlignment="Right"/>
                        </StackPanel>
                    </Grid>

                    <Grid Margin="0,12,0,0" Visibility="{Binding IsOutOfFocusSelected, Converter={StaticResource BoolToVis}}">
                        <StackPanel>
                            <TextBlock Text="Radius (px, proxy)" Margin="0,4,0,0"/>
                            <Slider Minimum="0" Maximum="50" Value="{Binding Radius}"/>
                            <TextBlock Text="{Binding Radius, StringFormat={}{0:0.0}}" HorizontalAlignment="Right"/>
                        </StackPanel>
                    </Grid>

                    <Grid Margin="0,12,0,0" Visibility="{Binding IsGaussianSelected, Converter={StaticResource BoolToVis}}">
                        <StackPanel>
                            <TextBlock Text="Sigma (px, proxy)" Margin="0,4,0,0"/>
                            <Slider Minimum="0" Maximum="10" Value="{Binding Sigma}"/>
                            <TextBlock Text="{Binding Sigma, StringFormat={}{0:0.0}}" HorizontalAlignment="Right"/>
                        </StackPanel>
                    </Grid>

                    <StackPanel Margin="0,12,0,0" Visibility="{Binding HasImage, Converter={StaticResource BoolToVis}}">
                        <TextBlock Text="Smoothness" Margin="0,4,0,0"/>
                        <Slider Minimum="0.0001" Maximum="0.1" SmallChange="0.0005" Value="{Binding Smoothness}"/>
                        <TextBlock Text="{Binding Smoothness, StringFormat={}{0:0.0000}}" HorizontalAlignment="Right"/>

                        <Button Content="Reset" Margin="0,12,0,0" Click="OnResetClick"/>
                        <Button Content="Render full resolution" Margin="0,8,0,0" Click="OnRenderFullClick"/>
                    </StackPanel>
```

Leave the `<TextBlock Margin="0,20,0,0" Text="{Binding StatusMessage}"...>` line beneath unchanged. Do NOT touch anything outside this span — Menu, PreviewCanvas Grid, ComboBox, DataContext, BusyOverlay all stay verbatim.

- [ ] **Step 2: Build**

```bash
dotnet build Deblur.sln
```
Expected: 0 errors, 0 new warnings.

- [ ] **Step 3: Run the full test suite — still green**

```bash
dotnet test Deblur.sln
```
Expected: `Passed: 47`.

- [ ] **Step 4: Commit**

```bash
git add Deblur/MainWindow.xaml
git commit -m "Restructure sidebar: three per-type panels + shared footer; delete coming-soon"
```

---

### Task 6: Manual smoke test pass + tag `phase3`

**Files:** none.

**Interfaces:** none.

- [ ] **Step 1: Run the app**

```bash
dotnet run --project Deblur/Deblur.csproj
```

Walk through the checklist:

- [ ] Launch app without an image loaded. Sidebar shows the ComboBox only — no Smoothness/Reset/Render footer (HasImage=false).
- [ ] Open a PNG via File → Open. Sidebar now shows the Motion Grid (default) AND the shared footer beneath (Smoothness + Reset + Render).
- [ ] Switch dropdown to "OutOfFocus". OutOfFocus Grid replaces Motion Grid; shared footer stays.
- [ ] Switch dropdown to "Gaussian". Sidebar shows the Sigma slider; shared footer still present. Preview shows the raw image (Sigma=0).
- [ ] Drag the Sigma slider up to ~3. Preview softens (Wiener with Gaussian PSF). Drag back to 0 → preview returns to raw.
- [ ] Move Smoothness — preview responds.
- [ ] Click Reset — sliders return to defaults for the selected type (Sigma=0, Smoothness=0.005); preview returns to raw immediately.
- [ ] Click "Render full resolution" on a Gaussian setting > 0 — busy overlay appears; status shows "Full-resolution render ready".
- [ ] File → Save As → PNG. Reopen the saved file externally; it should be a full-resolution Gaussian-deblurred image.
- [ ] Switch to Motion → Motion Grid returns; previously-set Motion Angle/Length preserved.
- [ ] Switch back to Gaussian → Sigma resets to 0 (per-type raw-on-switch semantics).
- [ ] Coming-soon TextBlock is gone entirely.
- [ ] Drop a corrupt file (rename a `.txt` to `.jpg`) — error modal appears; app state unchanged; shared footer still visible if an image was previously loaded.
- [ ] Under Gaussian, drag on the preview — the arrow may render but does NOT change the image (Motion-only; accepted phase-2 cosmetic).

- [ ] **Step 2: Commit any smoke-test-triggered fixes**

If the smoke test surfaces bugs, fix them and commit each fix separately with a message describing the failure and the fix. If nothing was wrong, no commit is needed for this step.

- [ ] **Step 3: Tag phase 3 complete**

```bash
git tag phase3
```

---

## Summary

Six tasks, each an independently reviewable commit. Task 1 appends `Sigma` to `KernelParams` and updates 23 construction sites mechanically. Task 2 adds `GaussianBlurKernel` and a Wiener round-trip test (TDD). Task 3 extends `DeblurJobRunner`'s `IsNoOp` and Sigma scaling with three new tests, and adds the XML doc comment closing the phase-2 dictionary-invariant finding. Task 4 wires `MainViewModel` with the Sigma observable, `HasImage` computed, dictionary entry, and per-type reset arm. Task 5 restructures the sidebar so Smoothness + Reset + Render live in a single shared footer and deletes the coming-soon TextBlock. Task 6 smoke-tests end-to-end and tags `phase3`.
