# Deblur Phase 1.f-1 Implementation Plan — Blind Kernel Accept + Handoff

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the roadmap §3.2 handoff gap: examiner accepts a blind-estimated kernel and applies it via any non-blind deconvolver (Wiener/Tikhonov/RL/CLS/TV/Landweber) with tunable parameters. Adds `BlurType.Custom` + `CustomPsfKernel` so any existing deconvolver consumes an arbitrary kernel.

**Architecture:** `CustomPsfKernel : IBlurKernel` holds an accepted PSF (cloned at accept time). `MainViewModel.AcceptBlindKernelCommand` clones the estimated kernel twice — once for the runtime slot, once for the audit `SuggestionRecord` — bumps `_customPsfSequence`, switches `SelectedBlurType = Custom` and `SelectedAlgorithm = Wiener`. Live preview uses area-resampled kernel (via `_proxyScale`); full-res uses the accepted kernel as-is. Clear switches type only; stored PSF stays put to avoid null-race with WorkerLoop.

**Tech Stack:** .NET 8; `FftSharp`; `CommunityToolkit.Mvvm`; WPF (`net8.0-windows`, `UseWPF`); xUnit.

## Global Constraints

- .NET 8. `net8.0` for `Deblur.Engine` + `Deblur.Tests`. `net8.0-windows` + `UseWPF` for `Deblur` and `Deblur.Wpf.Tests`. Nullable + ImplicitUsings enabled.
- No new NuGet packages.
- `Deblur.Engine` stays UI-free.
- All 174 Phase 1.e tests remain green. Test count target after 1.f-1: ~186.
- **Kernel cloned at accept time** — runtime clone into `CustomPsfKernel`, audit clone into `SuggestionRecord`. Audit records immutable by construction.
- **`KernelParams.KernelId`** — nullable `int?` field, additive. Only populated for `Type == Custom`. Cache-equality and undo history distinguish accepted kernels.
- **`SuggestionRecord.Confidence`** — becomes `float?` (nullable). Existing estimator paths continue non-null; blind's accept records store null.
- **Clear does not null the stored PSF** — switches `SelectedBlurType` back to Motion only. Prevents null-race with in-flight WorkerLoop preview.
- **Live preview area-resamples the Custom kernel by `_proxyScale`** with sum-to-1 renormalize. Preview must match full-res render (locked by `ProxyScaling_MatchesFullResWithinTolerance` test at PSNR ≥ 30 dB).
- **Restored determinism test** (from Phase 1.e deferred): two consecutive blind runs on the same input produce byte-identical kernel and output.
- Phase 1.f-1 branches from tag `phase1e` onto `phase1f1-blind-kernel-handoff` (already created).

---

### Task 1: Enum + record + const scaffold

**Files:**
- Modify: `Deblur.Engine/BlurType.cs` — append `Custom`.
- Modify: `Deblur.Engine/KernelParams.cs` — additive nullable `KernelId` field.
- Modify: `Deblur.Engine/Estimation/SuggestionRecord.cs` — `Confidence` becomes `float?`.
- Modify: `Deblur.Engine/BlindDeconvolutionDeconvolver.cs` — expose `MetadataId = "blind-cho-lee"` and `MetadataVersion = "1.0"` as `public const string`.
- Modify (mechanical fallout): every existing `new SuggestionRecord(...)` construction site to update the `Confidence` argument type from `float` to `float?` (implicit conversion for existing callers).
- Test:  `Deblur.Tests/BlindKernelHandoffScaffoldTests.cs` — a few new tests to pin the field additions.

**Interfaces:**
- Produces:
  - `BlurType.Custom` enum value.
  - `KernelParams` with additive `int? KernelId = null` (default null).
  - `SuggestionRecord` with `Confidence` typed `float?`.
  - `BlindDeconvolutionDeconvolver.MetadataId` and `MetadataVersion` public consts (matching the Metadata property values verbatim).

- [ ] **Step 1: Add `BlurType.Custom`**

```csharp
// Deblur.Engine/BlurType.cs
public enum BlurType { Motion, OutOfFocus, Gaussian, Custom }
```

- [ ] **Step 2: Add `KernelId` to `KernelParams`**

```csharp
// Deblur.Engine/KernelParams.cs — nullable additive
public readonly record struct KernelParams(
    BlurType Type,
    float Angle,
    float Length,
    float Smoothness,
    float Radius,
    float Sigma,
    AlgorithmType Algorithm,
    float? NoiseVariance = null,
    int? KernelId = null);
```

- [ ] **Step 3: Change `SuggestionRecord.Confidence` to `float?`**

Locate `Deblur.Engine/Estimation/SuggestionRecord.cs`. Change `float Confidence` to `float? Confidence` in the positional record definition.

- [ ] **Step 4: Fix all construction sites for SuggestionRecord**

Run `dotnet build`; the compiler will surface every callsite where `Confidence` was passed as `float`. Grep `new SuggestionRecord(` — the callsites in `MainViewModel` (existing estimators: cepstral, defocus, wavelet-noise) will all still compile because `float → float?` is implicit. Verify each still passes a non-null float.

- [ ] **Step 5: Expose MetadataId/Version consts on BlindDeconvolutionDeconvolver**

At the top of `BlindDeconvolutionDeconvolver.cs`:

```csharp
public sealed class BlindDeconvolutionDeconvolver : IDeconvolver
{
    public const string MetadataId = "blind-cho-lee";
    public const string MetadataVersion = "1.0";

    public AlgorithmMetadata Metadata { get; } = new(
        Id: MetadataId,
        Version: MetadataVersion,
        // ... rest unchanged
```

- [ ] **Step 6: Add scaffold tests**

```csharp
// Deblur.Tests/BlindKernelHandoffScaffoldTests.cs
using Deblur.Engine;
using Deblur.Engine.Estimation;
using Xunit;

namespace Deblur.Tests;

public class BlindKernelHandoffScaffoldTests
{
    [Fact]
    public void BlurTypeCustom_Exists() => Assert.Equal(3, (int)BlurType.Custom);

    [Fact]
    public void KernelParams_KernelId_DefaultsToNull()
    {
        var p = new KernelParams(BlurType.Motion, 0f, 0f, 0f, 0f, 0f, AlgorithmType.Wiener);
        Assert.Null(p.KernelId);
    }

    [Fact]
    public void KernelParams_DifferentKernelIds_AreNotEqual()
    {
        var p1 = new KernelParams(BlurType.Custom, 0f, 0f, 0f, 0f, 0f, AlgorithmType.Wiener, KernelId: 1);
        var p2 = p1 with { KernelId = 2 };
        Assert.NotEqual(p1, p2);
    }

    [Fact]
    public void SuggestionRecord_Confidence_AcceptsNull()
    {
        var r = new SuggestionRecord("x", "1.0", 42, confidence: null, System.DateTime.UtcNow);
        Assert.Null(r.Confidence);
    }

    [Fact]
    public void BlindDeconvolutionDeconvolver_MetadataConsts_MatchProperty()
    {
        var d = new BlindDeconvolutionDeconvolver();
        Assert.Equal(BlindDeconvolutionDeconvolver.MetadataId, d.Metadata.Id);
        Assert.Equal(BlindDeconvolutionDeconvolver.MetadataVersion, d.Metadata.Version);
    }
}
```

- [ ] **Step 7: Verify + commit**

Run: `dotnet build Deblur.sln` → 0 errors. `dotnet test Deblur.sln` → 179 total pass.

```bash
git add Deblur.Engine/BlurType.cs Deblur.Engine/KernelParams.cs Deblur.Engine/Estimation/SuggestionRecord.cs Deblur.Engine/BlindDeconvolutionDeconvolver.cs Deblur.Tests/BlindKernelHandoffScaffoldTests.cs
git commit -m "Scaffold for blind kernel handoff: BlurType.Custom, KernelParams.KernelId, SuggestionRecord.Confidence float?, blind Metadata consts"
```

---

### Task 2: `CustomPsfKernel` engine helper

**Files:**
- Create: `Deblur.Engine/CustomPsfKernel.cs`
- Test:   `Deblur.Tests/CustomPsfKernelTests.cs`

**Interfaces:**
- Produces:
  - `sealed class CustomPsfKernel : IBlurKernel` with `void SetPsf(float[,] psf)` and `float[,] Build(KernelParams p)`.
  - `Build` returns the STORED full-res kernel regardless of `p.Type` (proxy scaling is the runner's responsibility, Task 3).
  - `SetPsf(null)` throws `ArgumentNullException` — the null-race prevention lives in the VM (Clear doesn't null the stored PSF).

- [ ] **Step 1: Write failing tests**

```csharp
// Deblur.Tests/CustomPsfKernelTests.cs
using Deblur.Engine;
using Xunit;

namespace Deblur.Tests;

public class CustomPsfKernelTests
{
    [Fact]
    public void Build_WithoutSetPsf_Throws()
    {
        var k = new CustomPsfKernel();
        Assert.Throws<System.InvalidOperationException>(() =>
            k.Build(new KernelParams(BlurType.Custom, 0f, 0f, 0f, 0f, 0f, AlgorithmType.Wiener)));
    }

    [Fact]
    public void Build_ReturnsStoredPsf()
    {
        var k = new CustomPsfKernel();
        var psf = new float[3, 3] { { 0f, 0.25f, 0f }, { 0.25f, 0f, 0.25f }, { 0f, 0.25f, 0f } };
        k.SetPsf(psf);
        var built = k.Build(new KernelParams(BlurType.Custom, 0f, 0f, 0f, 0f, 0f, AlgorithmType.Wiener));
        Assert.Equal(3, built.GetLength(0));
        Assert.Equal(3, built.GetLength(1));
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 3; x++)
                Assert.Equal(psf[y, x], built[y, x]);
    }

    [Fact]
    public void SetPsf_ReplacesPreviousPsf()
    {
        var k = new CustomPsfKernel();
        k.SetPsf(new float[1, 1] { { 1f } });
        var newPsf = new float[3, 3];
        newPsf[1, 1] = 1f;
        k.SetPsf(newPsf);
        var built = k.Build(new KernelParams(BlurType.Custom, 0f, 0f, 0f, 0f, 0f, AlgorithmType.Wiener));
        Assert.Equal(3, built.GetLength(0));
    }

    [Fact]
    public void SetPsf_Null_Throws()
    {
        var k = new CustomPsfKernel();
        Assert.Throws<System.ArgumentNullException>(() => k.SetPsf(null!));
    }
}
```

- [ ] **Step 2: Implement**

```csharp
// Deblur.Engine/CustomPsfKernel.cs
namespace Deblur.Engine;

/// <summary>
/// IBlurKernel implementation that carries an arbitrary user-accepted PSF.
/// Used by the blind-kernel handoff flow: MainViewModel.AcceptBlindKernel
/// clones the estimated kernel and calls SetPsf; subsequent renders using
/// AlgorithmType != BlindDeconvolution + BlurType.Custom use this kernel.
///
/// Not thread-safe. Assumes single-threaded runner invocation (matches the
/// existing DeblurJobRunner discipline). Once set, the PSF stays until the
/// next SetPsf call — a null-PSF Build would indicate a VM/runner race and
/// throws.
/// </summary>
public sealed class CustomPsfKernel : IBlurKernel
{
    private float[,]? _psf;

    public void SetPsf(float[,] psf)
    {
        if (psf is null) throw new ArgumentNullException(nameof(psf));
        _psf = psf;
    }

    public float[,] Build(KernelParams p)
    {
        if (_psf is null)
            throw new InvalidOperationException(
                "CustomPsfKernel.Build called before SetPsf; VM/runner state has diverged.");
        return _psf;
    }
}
```

- [ ] **Step 3: Verify + commit**

Run: `dotnet test Deblur.sln --filter FullyQualifiedName~CustomPsfKernel` → 4 pass. Full → 183.

```bash
git add Deblur.Engine/CustomPsfKernel.cs Deblur.Tests/CustomPsfKernelTests.cs
git commit -m "Add CustomPsfKernel: stateful IBlurKernel carrying an examiner-accepted PSF"
```

---

### Task 3: Runner integration + proxy-scale awareness

**Files:**
- Modify: `Deblur.Engine/DeblurJobRunner.cs`
- Create: `Deblur.Engine/Imaging/KernelResample.cs` — area-resample a `float[,]` kernel by a scale factor + renormalize to sum=1.
- Test:   `Deblur.Tests/Imaging/KernelResampleTests.cs`
- Test:   `Deblur.Tests/DeblurJobRunnerTests.cs` (extend) — Custom dispatch + proxy/full agreement.

**Interfaces:**
- Produces:
  - `static class KernelResample { public static float[,] Downscale(float[,] src, float scale); }` — area-average downscale via existing `AreaResample.Box`-shaped logic adapted to kernel array; renormalizes sum to 1.
  - `DeblurJobRunner.SetProxyScale(float scale)` — VM calls after `SetProxy` to keep the runner aware of the current scale.
  - `IsNoOp` early-returns false for `BlurType.Custom`.
  - `WorkerLoop` (live-preview) dispatches Custom with kernel downsampled by `_proxyScale`. `RenderFullAsync` uses Custom kernel as-is.

- [ ] **Step 1: Write KernelResample failing tests**

```csharp
// Deblur.Tests/Imaging/KernelResampleTests.cs
using Deblur.Engine.Imaging;
using Xunit;

namespace Deblur.Tests.Imaging;

public class KernelResampleTests
{
    [Fact]
    public void Downscale_ScaleOne_ReturnsClone()
    {
        var src = new float[3, 3] { { 0.1f, 0.2f, 0.1f }, { 0.1f, 0.1f, 0.1f }, { 0.1f, 0.1f, 0.1f } };
        var dst = KernelResample.Downscale(src, 1.0f);
        Assert.Equal(3, dst.GetLength(0));
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 3; x++)
                Assert.InRange(Math.Abs(src[y, x] - dst[y, x]), 0f, 1e-6f);
    }

    [Fact]
    public void Downscale_HalfScale_SumsToOne()
    {
        var src = new float[7, 7];
        for (int y = 0; y < 7; y++)
            for (int x = 0; x < 7; x++)
                src[y, x] = 1f / 49f;
        var dst = KernelResample.Downscale(src, 0.5f);
        float sum = 0f;
        for (int y = 0; y < dst.GetLength(0); y++)
            for (int x = 0; x < dst.GetLength(1); x++)
                sum += dst[y, x];
        Assert.InRange(Math.Abs(sum - 1f), 0f, 1e-4f);
    }

    [Fact]
    public void Downscale_QuarterScale_ProducesOddSize()
    {
        // 31 * 0.25 = 7.75 → round up to nearest odd = 9. Or nearest odd of round = 7.
        // Implementation is expected to keep odd size for kernels; verify output is odd.
        var src = new float[31, 31];
        src[15, 15] = 1f;
        var dst = KernelResample.Downscale(src, 0.25f);
        Assert.Equal(1, dst.GetLength(0) % 2);
        Assert.Equal(1, dst.GetLength(1) % 2);
    }
}
```

- [ ] **Step 2: Implement KernelResample**

```csharp
// Deblur.Engine/Imaging/KernelResample.cs
namespace Deblur.Engine.Imaging;

public static class KernelResample
{
    /// <summary>
    /// Area-average downscale of a kernel by <paramref name="scale"/> (0 < scale ≤ 1);
    /// output size is round(size * scale) forced odd. Renormalizes to sum = 1 so the
    /// downscaled kernel remains a valid PSF.
    /// </summary>
    public static float[,] Downscale(float[,] src, float scale)
    {
        if (scale <= 0f || scale > 1f) throw new ArgumentOutOfRangeException(nameof(scale));
        int srcH = src.GetLength(0), srcW = src.GetLength(1);
        if (scale >= 0.9999f) return (float[,])src.Clone();

        int dstH = Math.Max(1, (int)Math.Round(srcH * scale));
        int dstW = Math.Max(1, (int)Math.Round(srcW * scale));
        if (dstH % 2 == 0) dstH++;
        if (dstW % 2 == 0) dstW++;
        double sxScale = (double)srcW / dstW;
        double syScale = (double)srcH / dstH;

        var dst = new float[dstH, dstW];
        for (int dy = 0; dy < dstH; dy++)
        {
            double y0 = dy * syScale;
            double y1 = (dy + 1) * syScale;
            int iy0 = (int)Math.Floor(y0);
            int iy1 = Math.Min(srcH, (int)Math.Ceiling(y1));
            for (int dx = 0; dx < dstW; dx++)
            {
                double x0 = dx * sxScale;
                double x1 = (dx + 1) * sxScale;
                int ix0 = (int)Math.Floor(x0);
                int ix1 = Math.Min(srcW, (int)Math.Ceiling(x1));
                double sum = 0, wt = 0;
                for (int sy = iy0; sy < iy1; sy++)
                {
                    double wy = Math.Min(sy + 1, y1) - Math.Max(sy, y0);
                    for (int sx = ix0; sx < ix1; sx++)
                    {
                        double wx = Math.Min(sx + 1, x1) - Math.Max(sx, x0);
                        double w = wx * wy;
                        sum += src[sy, sx] * w;
                        wt += w;
                    }
                }
                dst[dy, dx] = (float)(wt > 0 ? sum / wt : 0);
            }
        }

        // Renormalize sum to 1.
        double total = 0;
        for (int y = 0; y < dstH; y++)
            for (int x = 0; x < dstW; x++)
                total += dst[y, x];
        if (total > 0)
        {
            float inv = (float)(1.0 / total);
            for (int y = 0; y < dstH; y++)
                for (int x = 0; x < dstW; x++)
                    dst[y, x] *= inv;
        }
        return dst;
    }
}
```

- [ ] **Step 3: DeblurJobRunner — SetProxyScale + IsNoOp + Custom dispatch**

Add a private field `private float _proxyScale = 1f;` and a public `SetProxyScale(float scale)` method that sets it.

Extend `IsNoOp`:

```csharp
private static bool IsNoOp(KernelParams p)
{
    if (p.Algorithm == AlgorithmType.BlindDeconvolution) return false;
    if (p.Type == BlurType.Custom) return false;  // Custom's presence implies a kernel is set
    return p.Type switch { /* existing */ };
}
```

In `WorkerLoop`, after `_kernels[p.Type].Build(...)` and before `Apply(...)`, if `p.Type == BlurType.Custom`, downscale the built kernel by `_proxyScale` via `KernelResample.Downscale`. In `RenderFullAsync`, use the built kernel as-is (already full-res).

The cleanest place is inside `RunDeconvolve` itself, since both `WorkerLoop` and `RenderFullAsync` route through it. But `RunDeconvolve` doesn't currently know whether it's a preview or a full-res call. Two paths:

- **A.** Add a `bool isPreview` parameter to `RunDeconvolve`.
- **B.** Only WorkerLoop downscales; RenderFullAsync leaves alone. Requires a small refactor to expose the "get the kernel" step separately.

**Choose A.** Simpler; small signature change.

Modify `RunDeconvolve(ImageBuffer input, KernelParams p, CancellationToken ct = default, bool isPreview = false)`. Inside, after the existing `psf = ...Build(p)` line, add:

```csharp
if (p.Type == BlurType.Custom && isPreview && _proxyScale < 1f)
    psf = KernelResample.Downscale(psf, _proxyScale);
```

WorkerLoop dispatches with `isPreview: true`; RenderFullAsync with `isPreview: false`.

- [ ] **Step 4: MainViewModel — call SetProxyScale after SetProxy**

In `LoadImageFromBytes`, after `_runner.SetProxy(_proxy)`, add `_runner.SetProxyScale(_proxyScale);`.

- [ ] **Step 5: Runner integration tests**

```csharp
// Deblur.Tests/DeblurJobRunnerTests.cs — add to existing file
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
```

Plus one preview/full agreement test — proxy-render on the same input matches full-res-render resampled to proxy dimensions within PSNR ≥ 30 dB. Use `WienerDeconvolver` (fast, deterministic) with a small fixed custom kernel:

```csharp
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
    // Simpler: call RunDeconvolve-equivalent path via a manual invocation on a stub or expose a preview helper.
    // For this test, use the proxy dispatch through WorkerLoop signaling.
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
```

If PSNR falls below 30 dB, tune — the acceptance target is empirical.

- [ ] **Step 6: Verify + commit**

Run: `dotnet test Deblur.sln` → 188 or so pass (183 + KernelResample 3 + Runner 2).

```bash
git add Deblur.Engine/Imaging/KernelResample.cs Deblur.Engine/DeblurJobRunner.cs Deblur.Tests/Imaging/KernelResampleTests.cs Deblur.Tests/DeblurJobRunnerTests.cs Deblur/ViewModels/MainViewModel.cs
git commit -m "Runner integration for Custom PSF: SetProxyScale, IsNoOp Custom, WorkerLoop kernel downscale"
```

---

### Task 4: MainViewModel — AcceptBlindKernel, Clear, sequence, wiring

**Files:**
- Modify: `Deblur/ViewModels/MainViewModel.cs`

**Interfaces:**
- Adds:
  - `private readonly CustomPsfKernel _customPsfKernel;` — held for lifetime, registered in `_kernels` dict.
  - `private int _customPsfSequence;` — monotonic id starting at 0.
  - `[ObservableProperty] private SuggestionRecord? _customPsfAcceptedRecord;` — for the "Accepted from…" display.
  - `[RelayCommand(CanExecute = nameof(CanAcceptBlindKernel))] private void AcceptBlindKernel();` — clones twice, sets Custom kernel, bumps sequence, switches type to Custom, switches algorithm to Wiener, appends `SuggestionRecord`.
  - `[RelayCommand] private void ClearCustomPsf();` — switches type back to Motion; does NOT null the stored PSF.
  - `IsCustomSelected` computed property (mirrors `IsMotionSelected`).
  - `partial void OnSelectedBlurTypeChanged` extended to notify `IsCustomSelected`.
  - `BuildCurrentParams` includes `KernelId: _customPsfSequence` when Type == Custom.

- [ ] **Step 1: Add field + dictionary entry**

Register `[BlurType.Custom] = _customPsfKernel = new CustomPsfKernel()` in the kernels dictionary. Add field:

```csharp
private readonly CustomPsfKernel _customPsfKernel;
private int _customPsfSequence;
```

- [ ] **Step 2: Add `IsCustomSelected` and notify**

```csharp
public bool IsCustomSelected => SelectedBlurType == BlurType.Custom;
```

Extend `partial void OnSelectedBlurTypeChanged(BlurType value)` to include `OnPropertyChanged(nameof(IsCustomSelected))`.

- [ ] **Step 3: Add observable + commands**

```csharp
[ObservableProperty] private SuggestionRecord? _customPsfAcceptedRecord;

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

[RelayCommand(CanExecute = nameof(CanAcceptBlindKernel))]
private void AcceptBlindKernel()
{
    if (EstimatedKernel is null) return;
    var runtimeCopy = CloneKernel(EstimatedKernel);
    var auditCopy   = CloneKernel(EstimatedKernel);

    _customPsfKernel.SetPsf(runtimeCopy);
    _customPsfSequence++;

    var record = new SuggestionRecord(
        BlindDeconvolutionDeconvolver.MetadataId,
        BlindDeconvolutionDeconvolver.MetadataVersion,
        (float[,]?)auditCopy,
        confidence: (float?)null,
        suggestedAtUtc: DateTime.UtcNow)
        with { AcceptedAtUtc = DateTime.UtcNow };
    SuggestionHistory.Add(record);
    CustomPsfAcceptedRecord = record;

    SelectedBlurType = BlurType.Custom;
    SelectedAlgorithm = AlgorithmType.Wiener;
    InvalidateFullResCache();
}

[RelayCommand]
private void ClearCustomPsf()
{
    // Stored PSF stays in _customPsfKernel — prevents null race with an in-flight
    // WorkerLoop preview. Only switch the type back. Next Accept replaces the
    // stored PSF; a switch back to Custom without a re-Accept would apply the
    // OLD stored PSF, which is why the type combobox never surfaces Custom
    // directly (it's only reachable via Accept, and the Custom panel is only
    // visible when IsCustomSelected).
    SelectedBlurType = BlurType.Motion;
    InvalidateFullResCache();
}
```

CanExecute plumbing:

```csharp
partial void OnEstimatedKernelChanged(float[,]? value)
    => AcceptBlindKernelCommand.NotifyCanExecuteChanged();
```

- [ ] **Step 4: `BuildCurrentParams` uses KernelId for Custom**

```csharp
private KernelParams BuildCurrentParams()
    => new KernelParams(
        SelectedBlurType, Angle, Length, Smoothness, Radius, Sigma, SelectedAlgorithm,
        NoiseVariance: _acceptedNoiseVariance,
        KernelId: SelectedBlurType == BlurType.Custom ? _customPsfSequence : null);
```

- [ ] **Step 5: Verify + commit**

Run: `dotnet build Deblur.sln` → 0 errors. `dotnet test Deblur.sln` → all pass (no new tests in this task; UI tests come next).

```bash
git add Deblur/ViewModels/MainViewModel.cs
git commit -m "MainViewModel: AcceptBlindKernel + ClearCustomPsf + kernel-id sequence"
```

---

### Task 5: XAML — Accept button + Custom sidebar panel

**Files:**
- Modify: `Deblur/MainWindow.xaml`

**Interfaces:**
- Adds:
  - Accept button below `<controls:PsfDisplay>` when blind is selected AND `EstimatedKernel` is non-null (CanExecute handles the enable state).
  - New Custom sidebar panel — small `PsfDisplay` (Kernel bound to a new VM `AcceptedCustomKernelDisplay` computed getter over `_customPsfKernel._psf` OR simply bind to a copy the VM exposes); "Accepted from…" text bound to `CustomPsfAcceptedRecord`; Clear button.

- [ ] **Step 1: Expose an observable proxy for the accepted PSF display**

The `PsfDisplay` control takes a `float[,]?`. Add a VM property:

```csharp
[ObservableProperty] private float[,]? _acceptedCustomKernelDisplay;
```

Set it inside `AcceptBlindKernel` to `auditCopy` (or `runtimeCopy`; either works since Custom panel is display-only). Also set on `ClearCustomPsf` — leave as-is (still shows the last accepted kernel; the type is Motion now so the panel is hidden anyway).

- [ ] **Step 2: Accept button in the blind PSF display block**

Below the existing `<controls:PsfDisplay Kernel="{Binding EstimatedKernel}" ... />` in the shared footer:

```xml
<Button Content="Accept kernel" Command="{Binding AcceptBlindKernelCommand}"
        Margin="0,4,0,0" HorizontalAlignment="Left"
        Visibility="{Binding SelectedAlgorithm, Converter={StaticResource BlindAlgoToVis}}"
        ToolTip="Save the estimated kernel as a Custom PSF and switch to Wiener for evidentiary output"/>
```

The button is auto-disabled when `EstimatedKernel is null` via the CanExecute predicate (Task 4).

- [ ] **Step 3: Custom sidebar panel**

Add a per-blur-type panel in the same layout row as Motion/OutOfFocus/Gaussian:

```xml
<Grid Visibility="{Binding IsCustomSelected, Converter={StaticResource BoolToVis}}">
    <StackPanel>
        <TextBlock Text="Custom PSF (accepted from blind)"
                   FontWeight="Bold" Margin="0,0,0,4"/>
        <controls:PsfDisplay Kernel="{Binding AcceptedCustomKernelDisplay}"
                             HorizontalAlignment="Left"/>
        <TextBlock TextWrapping="Wrap" Margin="0,4,0,0" FontSize="11" Foreground="#555">
            <Run Text="Accepted from: " Mode="OneWay"/>
            <Run Text="{Binding CustomPsfAcceptedRecord.EstimatorId, Mode=OneWay}"/>
            <Run Text=" v" Mode="OneWay"/>
            <Run Text="{Binding CustomPsfAcceptedRecord.EstimatorVersion, Mode=OneWay}"/>
            <Run Text=" at " Mode="OneWay"/>
            <Run Text="{Binding CustomPsfAcceptedRecord.AcceptedAtUtc, StringFormat={}{0:yyyy-MM-dd HH:mm:ss} UTC, Mode=OneWay}"/>
        </TextBlock>
        <Button Content="Clear (switch back to Motion)"
                Command="{Binding ClearCustomPsfCommand}"
                Margin="0,8,0,0" HorizontalAlignment="Left"/>
    </StackPanel>
</Grid>
```

- [ ] **Step 4: Verify + commit**

Run: `dotnet build Deblur.sln` → 0 errors. `dotnet test Deblur.sln` → all pass.

```bash
git add Deblur/MainWindow.xaml Deblur/ViewModels/MainViewModel.cs
git commit -m "MainWindow: Accept-kernel button + Custom sidebar panel with acceptance record"
```

---

### Task 6: Restored determinism test

**Files:**
- Modify: `Deblur.Tests/BlindDeconvolutionDeconvolverTests.cs` — append the determinism test.

**Interfaces:**
- Produces:
  - `Deterministic_TwoConsecutiveRuns_ProduceByteIdenticalKernelAndOutput` — RESTORED from Phase 1.e spec's Testing section. Runs blind twice on the same input; asserts both `LastEstimatedKernel` values are byte-identical AND both output `ImageBuffer` values (R/G/B float arrays) are byte-identical.

- [ ] **Step 1: Add the test**

```csharp
[Fact]
public void Deterministic_TwoConsecutiveRuns_ProduceByteIdenticalKernelAndOutput()
{
    var gt = SyntheticImages.TexturedNoise(128, 128, seed: 42);
    var psf = new MotionBlurKernel().Build(
        new KernelParams(BlurType.Motion, 30f, 8f, 0f, 0f, 0f, AlgorithmType.BlindDeconvolution));
    var blurred = SyntheticBlur.Apply(gt, psf, gaussianNoiseSigma: 0f, seed: 42);

    var blind1 = new BlindDeconvolutionDeconvolver();
    var out1 = blind1.Apply(blurred, new float[1, 1] { { 1f } },
        new DeconvolutionParams(K: 1e-3f), PipelineOptions.Default);
    var k1 = blind1.LastEstimatedKernel!;

    var blind2 = new BlindDeconvolutionDeconvolver();
    var out2 = blind2.Apply(blurred, new float[1, 1] { { 1f } },
        new DeconvolutionParams(K: 1e-3f), PipelineOptions.Default);
    var k2 = blind2.LastEstimatedKernel!;

    Assert.Equal(k1.GetLength(0), k2.GetLength(0));
    Assert.Equal(k1.GetLength(1), k2.GetLength(1));
    for (int y = 0; y < k1.GetLength(0); y++)
        for (int x = 0; x < k1.GetLength(1); x++)
            Assert.Equal(k1[y, x], k2[y, x]);

    Assert.Equal(out1.Width, out2.Width);
    Assert.Equal(out1.Height, out2.Height);
    for (int i = 0; i < out1.PixelCount; i++)
    {
        Assert.Equal(out1.R[i], out2.R[i]);
        Assert.Equal(out1.G[i], out2.G[i]);
        Assert.Equal(out1.B[i], out2.B[i]);
    }
}
```

- [ ] **Step 2: Verify + commit**

Run: `dotnet test Deblur.sln --filter FullyQualifiedName~Deterministic_TwoConsecutiveRuns` → 1 pass.

```bash
git add Deblur.Tests/BlindDeconvolutionDeconvolverTests.cs
git commit -m "Restore blind determinism test (rolled up from Phase 1.e)"
```

---

### Task 7: Manual smoke test + tag

- [ ] **Step 1: Build in Debug and launch**

```bash
dotnet build Deblur.sln
dotnet run --project Deblur/Deblur.csproj --no-build
```

- [ ] **Step 2: Manual smoke**

- Load a motion-blurred image. Pick BlindDeconvolution → Render → PSF display shows kernel. Accept button enabled.
- Click "Accept kernel" → sidebar switches to the Custom panel showing the same kernel + "Accepted from: blind-cho-lee v1.0 at ..." timestamp. Algorithm dropdown = Wiener.
- Live preview updates as you tune the Smoothness slider (Wiener is fast, preview responsive).
- Switch algorithm to Tikhonov → live preview updates; K slider still tunes.
- Switch to RL / Landweber → skip in preview but Render produces output using the accepted kernel.
- Click "Clear" on the Custom panel → sidebar reverts to Motion. Custom PSF still stored under the hood but unused.
- Load a new image → PSF display and Custom PSF cleared (per `LoadImageFromBytes` behavior).
- Blind again on the new image → Accept → new Custom PSF replaces the old.
- 16-bit input still exports 16-bit PNG.
- ROI + Custom: enable ROI, draw a rectangle, render with Custom + Wiener → the accepted kernel applies to the ROI extract.
- Undo/redo, save-as, arrow drag (Motion only) all still work.
- **Preview-vs-render agreement smoke**: switch to Custom + Wiener with K=0.005; hit Render (full-res); note the visual result; drag Smoothness slider by a tiny amount (triggers preview re-render); the preview should look like a downscaled version of the full-res output, not visibly different.

Report smoke results in the ledger.

- [ ] **Step 3: Tag + update ledger**

```bash
git tag phase1f1
```

- [ ] **Step 4: Invoke `superpowers:finishing-a-development-branch`**

Present the standard four options.
