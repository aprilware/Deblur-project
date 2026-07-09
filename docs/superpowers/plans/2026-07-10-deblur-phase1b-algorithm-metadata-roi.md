# Deblur Phase 1.b Implementation Plan — Algorithm Metadata + ROI Processing

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Attach versioned metadata (Id, Version, DisplayName, DescriptionMarkdown, LiteratureCitation) to every deconvolution algorithm, and land region-of-interest processing at render-time so an examiner can deblur just a plate/face/tattoo with feathered blending back into the untouched full plate.

**Architecture:** `IDeconvolver` grows a `Metadata` property. `RegionOfInterest` (record) + `RoiProcessor` (pure static helper) live in `Deblur.Engine`. `DeblurJobRunner` gains a nullable `Roi` property; `RenderFullAsync` routes through `RoiProcessor` when set. UI: `PreviewCanvas` gets a rubber-band ROI mode; `MainWindow` gets a sidebar toggle + feather slider; `MainViewModel` plumbs the selection through.

**Tech Stack:** .NET 8; `FftSharp`; `CommunityToolkit.Mvvm`; WPF (`net8.0-windows`, `UseWPF`); xUnit.

## Global Constraints

- .NET 8. `net8.0` for `Deblur.Engine` + `Deblur.Tests`. `net8.0-windows` + `UseWPF` for `Deblur` and `Deblur.Wpf.Tests`. Nullable + ImplicitUsings enabled.
- No new NuGet packages.
- `Deblur.Engine` stays UI-free.
- All 91 Phase 1.a tests remain green. Test count target after 1.b: ~110 (91 + ~19 new).
- Every deconvolver's `AlgorithmMetadata.Version` is a stable semver-shaped string. Bumping algorithm math without bumping `Version` is a forensic-integrity bug.
- `RegionOfInterest` coordinates are full-resolution pixels. The UI converts from proxy to full-res before calling `MainViewModel.CommitRoi`.
- ROI applies at render/save time only. Live preview stays whole-image.
- ROI selection is NOT part of `KernelParams` — the undo/redo history only walks algorithm parameters, not render-target selection.
- Feather ramp uses a raised cosine (Hanning shape): `alpha(d) = 0.5 * (1 - cos(π * d / FeatherRadius))` where `d` is distance from the ROI edge inward.
- `RoiProcessor`'s padded-extract uses reflect boundary handling where the pad crosses the source image edge (matches `BoundaryFill` default).
- Phase 1.b branches from tag `phase1a` onto `phase1b-algorithm-metadata-roi` (already created).

---

### Task 1: AlgorithmMetadata SPI + deconvolver implementations

**Files:**
- Create: `Deblur.Engine/AlgorithmMetadata.cs`
- Modify: `Deblur.Engine/IDeconvolver.cs`
- Modify: `Deblur.Engine/WienerDeconvolver.cs`
- Modify: `Deblur.Engine/TikhonovDeconvolver.cs`
- Modify: `Deblur.Engine/TotalVariationDeconvolver.cs`
- Modify: `Deblur.Tests/DeblurJobRunnerTests.cs` — the two stub `IDeconvolver` classes gain `Metadata` implementations.
- Test:   `Deblur.Tests/AlgorithmMetadataTests.cs`

**Interfaces:**
- Produces:
  - `sealed record AlgorithmMetadata(string Id, string Version, string DisplayName, string DescriptionMarkdown, string LiteratureCitation)`.
  - `IDeconvolver.Metadata { get; }`.

- [ ] **Step 1: Add `AlgorithmMetadata` record**

```csharp
// Deblur.Engine/AlgorithmMetadata.cs
namespace Deblur.Engine;

public sealed record AlgorithmMetadata(
    string Id,
    string Version,
    string DisplayName,
    string DescriptionMarkdown,
    string LiteratureCitation);
```

- [ ] **Step 2: Extend `IDeconvolver`**

```csharp
// Deblur.Engine/IDeconvolver.cs
namespace Deblur.Engine;

public interface IDeconvolver
{
    AlgorithmMetadata Metadata { get; }
    ImageBuffer Apply(
        ImageBuffer input,
        float[,] psf,
        DeconvolutionParams p,
        PipelineOptions? options = null);
}
```

- [ ] **Step 3: Implement `Metadata` on each deconvolver**

Add this property (with the corresponding metadata block) as the FIRST member of the class body, above `Apply`:

`WienerDeconvolver`:

```csharp
public AlgorithmMetadata Metadata { get; } = new(
    Id: "wiener",
    Version: "1.0",
    DisplayName: "Wiener filter",
    DescriptionMarkdown:
        "The Wiener filter is a linear frequency-domain deconvolver that " +
        "minimizes the expected squared error between the estimated and true image, " +
        "assuming known point spread function (PSF) and a scalar noise-to-signal " +
        "ratio parameter K. The filter response is conj(H) / (|H|^2 + K), where " +
        "H is the PSF's Fourier transform. Increasing K suppresses noise " +
        "amplification at the cost of retained blur.",
    LiteratureCitation:
        "Wiener, N. (1949). Extrapolation, Interpolation, and Smoothing of " +
        "Stationary Time Series. MIT Press / Wiley.");
```

`TikhonovDeconvolver`:

```csharp
public AlgorithmMetadata Metadata { get; } = new(
    Id: "tikhonov-laplacian",
    Version: "1.0",
    DisplayName: "Tikhonov regularization (Laplacian)",
    DescriptionMarkdown:
        "Tikhonov regularization adds a smoothness penalty to the deconvolution " +
        "objective: minimize ||H*x - y||^2 + K * ||C*x||^2, where C is the discrete " +
        "5-point Laplacian operator. The closed-form frequency-domain solution is " +
        "conj(H) / (|H|^2 + K * |C|^2). K controls the trade-off between fit and " +
        "smoothness; larger K produces smoother, less noise-amplifying reconstructions.",
    LiteratureCitation:
        "Tikhonov, A. N. (1963). Solution of incorrectly formulated problems and " +
        "the regularization method. Dokl. Akad. Nauk SSSR, 151, 501-504.");
```

`TotalVariationDeconvolver`:

```csharp
public AlgorithmMetadata Metadata { get; } = new(
    Id: "tv-chambolle",
    Version: "1.0",
    DisplayName: "Total Variation (Chambolle post-filter)",
    DescriptionMarkdown:
        "Total Variation denoising via Chambolle's projected dual algorithm, " +
        "applied as a post-filter over a Wiener warm-start. Solves " +
        "argmin_u ||u - f||^2 / (2*lambda) + TV(u), preserving edges while " +
        "suppressing noise. Twenty iterations of the dual projection with " +
        "step size tau = 0.125; lambda is derived from the smoothness slider " +
        "(K * 50) to map the UI range into a visible TV effect.",
    LiteratureCitation:
        "Chambolle, A. (2004). An algorithm for total variation minimization " +
        "and applications. Journal of Mathematical Imaging and Vision, 20, 89-97.");
```

- [ ] **Step 4: Update stub deconvolvers in tests**

In `Deblur.Tests/DeblurJobRunnerTests.cs`, add to both `SlowStubDeconvolver` and `RecordingStubDeconvolver`:

```csharp
public AlgorithmMetadata Metadata { get; } = new(
    Id: "stub", Version: "0", DisplayName: "Stub", DescriptionMarkdown: "test-only stub",
    LiteratureCitation: "n/a");
```

Similarly for `FreshBufferStubDeconvolver` (added in Phase 1.a for the 16-bit regression test).

- [ ] **Step 5: Add `AlgorithmMetadataTests.cs`**

```csharp
// Deblur.Tests/AlgorithmMetadataTests.cs
using Deblur.Engine;
using Xunit;

namespace Deblur.Tests;

public class AlgorithmMetadataTests
{
    [Fact]
    public void EveryProductionDeconvolver_HasCompleteMetadata()
    {
        IDeconvolver[] deconvolvers =
        {
            new WienerDeconvolver(),
            new TikhonovDeconvolver(),
            new TotalVariationDeconvolver(),
        };
        foreach (var d in deconvolvers)
        {
            var m = d.Metadata;
            Assert.False(string.IsNullOrWhiteSpace(m.Id));
            Assert.False(string.IsNullOrWhiteSpace(m.Version));
            Assert.False(string.IsNullOrWhiteSpace(m.DisplayName));
            Assert.True(m.DescriptionMarkdown.Length > 100,
                $"{m.Id} description too short: {m.DescriptionMarkdown.Length} chars");
            Assert.True(m.LiteratureCitation.Length > 20,
                $"{m.Id} citation too short: {m.LiteratureCitation}");
        }
    }

    [Fact]
    public void KnownIds_AreStable()
    {
        Assert.Equal("wiener",             new WienerDeconvolver().Metadata.Id);
        Assert.Equal("tikhonov-laplacian", new TikhonovDeconvolver().Metadata.Id);
        Assert.Equal("tv-chambolle",       new TotalVariationDeconvolver().Metadata.Id);
    }

    [Fact]
    public void ProductionIds_AreUnique()
    {
        var ids = new[]
        {
            new WienerDeconvolver().Metadata.Id,
            new TikhonovDeconvolver().Metadata.Id,
            new TotalVariationDeconvolver().Metadata.Id,
        };
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }
}
```

- [ ] **Step 6: Verify + commit**

Run: `dotnet build Deblur.sln` → 0 errors. `dotnet test Deblur.sln` → 94 pass (91 + 3 new).

```bash
git add Deblur.Engine/AlgorithmMetadata.cs Deblur.Engine/IDeconvolver.cs Deblur.Engine/WienerDeconvolver.cs Deblur.Engine/TikhonovDeconvolver.cs Deblur.Engine/TotalVariationDeconvolver.cs Deblur.Tests/AlgorithmMetadataTests.cs Deblur.Tests/DeblurJobRunnerTests.cs
git commit -m "Add AlgorithmMetadata to IDeconvolver + implement on Wiener/Tikhonov/TV"
```

---

### Task 2: RegionOfInterest record

**Files:**
- Create: `Deblur.Engine/RegionOfInterest.cs`
- Test:   `Deblur.Tests/RegionOfInterestTests.cs`

**Interfaces:**
- Produces:
  - `sealed record RegionOfInterest(int X, int Y, int Width, int Height, int FeatherRadius)` with `Contains(int px, int py)` and `ClampFeatherToHalfMinDim()` helper.

- [ ] **Step 1: Write failing tests**

```csharp
// Deblur.Tests/RegionOfInterestTests.cs
using Deblur.Engine;
using Xunit;

namespace Deblur.Tests;

public class RegionOfInterestTests
{
    [Fact]
    public void Contains_InteriorPoint_True()
    {
        var roi = new RegionOfInterest(10, 20, 100, 50, 12);
        Assert.True(roi.Contains(10, 20));
        Assert.True(roi.Contains(109, 69));
    }

    [Fact]
    public void Contains_BoundaryAndOutsidePoints()
    {
        var roi = new RegionOfInterest(10, 20, 100, 50, 12);
        Assert.False(roi.Contains(9, 20));    // just outside left
        Assert.False(roi.Contains(10, 19));   // just outside top
        Assert.False(roi.Contains(110, 20));  // right edge exclusive
        Assert.False(roi.Contains(10, 70));   // bottom edge exclusive
    }

    [Fact]
    public void ClampFeatherToHalfMinDim_LimitsWhenExcessive()
    {
        var small = new RegionOfInterest(0, 0, 10, 20, 100);
        var clamped = small.ClampFeatherToHalfMinDim();
        Assert.Equal(5, clamped.FeatherRadius); // min(10,20)/2 = 5
    }

    [Fact]
    public void ClampFeatherToHalfMinDim_NoOpWhenSmall()
    {
        var roi = new RegionOfInterest(0, 0, 100, 100, 12);
        var clamped = roi.ClampFeatherToHalfMinDim();
        Assert.Equal(12, clamped.FeatherRadius);
    }
}
```

- [ ] **Step 2: Implement**

```csharp
// Deblur.Engine/RegionOfInterest.cs
namespace Deblur.Engine;

public sealed record RegionOfInterest(int X, int Y, int Width, int Height, int FeatherRadius)
{
    public bool Contains(int px, int py)
        => px >= X && px < X + Width && py >= Y && py < Y + Height;

    /// <summary>
    /// Returns a copy with FeatherRadius clamped to at most half the smaller dimension.
    /// Prevents the feather band from consuming the entire ROI.
    /// </summary>
    public RegionOfInterest ClampFeatherToHalfMinDim()
    {
        int cap = Math.Min(Width, Height) / 2;
        return FeatherRadius <= cap ? this : this with { FeatherRadius = cap };
    }
}
```

- [ ] **Step 3: Verify + commit**

Run: `dotnet test Deblur.sln --filter FullyQualifiedName~RegionOfInterestTests` → 4 pass. Full suite → 98 pass.

```bash
git add Deblur.Engine/RegionOfInterest.cs Deblur.Tests/RegionOfInterestTests.cs
git commit -m "Add RegionOfInterest record with Contains and feather clamp"
```

---

### Task 3: RoiProcessor core

**Files:**
- Create: `Deblur.Engine/RoiProcessor.cs`
- Test:   `Deblur.Tests/RoiProcessorTests.cs`

**Interfaces:**
- Consumes: `RegionOfInterest`, `ImageBuffer`, `BoundaryFill` (for reflect math — reused, not duplicated).
- Produces:
  - `public static class RoiProcessor { public static ImageBuffer ApplyToRoi(ImageBuffer full, RegionOfInterest roi, int psfRadius, Func<ImageBuffer, ImageBuffer> deconvolve); }`.

### Algorithm

Given `full` (H×W), `roi = (rx, ry, rw, rh, F)`, and `psfRadius = P`:

1. Compute the padded extract rectangle:
   - `pad = max(P, F)`
   - `ex = rx - pad`, `ey = ry - pad`
   - `ew = rw + 2*pad`, `eh = rh + 2*pad`
   - Note: `ex/ey` may be negative and `ex+ew/ey+eh` may exceed `full.Width/Height` — the extract wraps around the image edge via reflect.

2. Build the padded extract as a fresh `ImageBuffer(ew, eh)`. For each dest pixel `(dx, dy)` in `[0, ew) x [0, eh)`, its source coordinate in `full` is `(ex + dx, ey + dy)`. Where those go out of range, use reflect indexing (bounce math from `BoundaryFill` — extract into a shared helper or inline; either is acceptable).

3. Call `var deconvolved = deconvolve(paddedExtract);` — an `ImageBuffer` of size `ew × eh` in the padded frame. The caller-supplied closure runs the full deconvolution pipeline (with linear-light, luminance-only, etc. per its captured options).

4. Build an alpha mask covering the ROI region only (not the extract) — a `float[]` of length `rw * rh`. For each `(mx, my)` in `[0, rw) x [0, rh)`:
   - Distance to nearest ROI edge: `d = min(mx, my, rw - 1 - mx, rh - 1 - my)`.
   - If `d >= F`: `alpha = 1f`.
   - Else if `F > 0`: `alpha = 0.5 * (1 - cos(π * d / F))`.
   - Else (`F == 0`): `alpha = 1f` (hard replace).

5. Compose the output: `result = full.Clone()` (preserves `SourceBitDepth`). For each ROI pixel `(rx + mx, ry + my)`, blend:
   - `deconvPixel = deconvolved[(pad + my) * ew + (pad + mx)]` (deconvolved is padded, ROI center is at offset `pad`).
   - `result[fullIndex] = alpha * deconvPixel + (1 - alpha) * fullPixel` for R, G, B.

Pixels outside the ROI in the composed output are byte-identical to `full` (from the initial `Clone()` — no writes touch them).

- [ ] **Step 1: Write failing tests**

```csharp
// Deblur.Tests/RoiProcessorTests.cs
using Deblur.Engine;
using Deblur.Engine.Validation;
using Deblur.Tests.TestHelpers;
using Xunit;

namespace Deblur.Tests;

public class RoiProcessorTests
{
    [Fact]
    public void OutsideRoi_IsByteIdentical_ToInput()
    {
        var src = SyntheticImages.Checkerboard(128, 128, 16);
        var roi = new RegionOfInterest(30, 30, 40, 40, FeatherRadius: 0);
        var result = RoiProcessor.ApplyToRoi(src, roi, psfRadius: 5,
            deconvolve: extract => Fill(extract, 0.5f));

        for (int y = 0; y < src.Height; y++)
        {
            for (int x = 0; x < src.Width; x++)
            {
                if (roi.Contains(x, y)) continue;
                int i = y * src.Width + x;
                Assert.Equal(src.R[i], result.R[i]);
                Assert.Equal(src.G[i], result.G[i]);
                Assert.Equal(src.B[i], result.B[i]);
            }
        }
    }

    [Fact]
    public void HardReplace_FeatherZero_UsesDeconvValueInside()
    {
        var src = SyntheticImages.Checkerboard(64, 64, 8);
        var roi = new RegionOfInterest(10, 10, 20, 20, FeatherRadius: 0);
        var result = RoiProcessor.ApplyToRoi(src, roi, psfRadius: 2,
            deconvolve: extract => Fill(extract, 0.42f));

        int inside = 15 * result.Width + 15;
        Assert.InRange(Math.Abs(result.R[inside] - 0.42f), 0f, 1e-5f);
    }

    [Fact]
    public void RoiEquivalence_CoreMatchesFullImageDeconvolution()
    {
        // Wiener on a 128x128 checkerboard, ROI 40x40 in the center with feather 8.
        // The un-feathered core (24x24 interior) after ROI processing must match
        // a full-image Wiener recovery of the same input inside that same core.
        var gt = SyntheticImages.Checkerboard(128, 128, 16);
        var kernel = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 10f, 0f, 0f, 0f, AlgorithmType.Wiener));
        var blurred = SyntheticBlur.Apply(gt, kernel, gaussianNoiseSigma: 0f, seed: 42);

        var deconvolver = new WienerDeconvolver();
        var opts = PipelineOptions.Default with { LinearLight = false, EdgeTaper = false };
        var fullDeconv = deconvolver.Apply(blurred, kernel,
            new DeconvolutionParams(K: 0.005f), opts);

        var roi = new RegionOfInterest(X: 44, Y: 44, Width: 40, Height: 40, FeatherRadius: 8);
        var roiResult = RoiProcessor.ApplyToRoi(blurred, roi, psfRadius: 5,
            deconvolve: extract => deconvolver.Apply(extract, kernel,
                new DeconvolutionParams(K: 0.005f), opts));

        // Compare pixels inside the un-feathered core (feather=8 pixels inset from ROI edge).
        double sumSq = 0; int count = 0;
        for (int y = roi.Y + roi.FeatherRadius; y < roi.Y + roi.Height - roi.FeatherRadius; y++)
        {
            for (int x = roi.X + roi.FeatherRadius; x < roi.X + roi.Width - roi.FeatherRadius; x++)
            {
                int i = y * gt.Width + x;
                double dr = fullDeconv.R[i] - roiResult.R[i];
                double dg = fullDeconv.G[i] - roiResult.G[i];
                double db = fullDeconv.B[i] - roiResult.B[i];
                sumSq += (dr * dr + dg * dg + db * db) / 3.0;
                count++;
            }
        }
        double mse = sumSq / count;
        double psnr = mse <= 0 ? double.PositiveInfinity : 10.0 * Math.Log10(1.0 / mse);
        // 25 dB is a substantive-equivalence threshold. Exact match is unrealistic:
        // "Wiener on 56x56 extract" and "Wiener on 128x128 whole" use different FFT
        // sizes and see different boundary reflections at the padded FFT canvas edge.
        // Deep interiors converge, so 25 dB comfortably proves the ROI core is doing
        // the same recovery as the full-image path.
        Assert.True(psnr > 25.0, $"ROI core diverges from full-image deconv: PSNR {psnr:F2} dB");
    }

    [Fact]
    public void SourceBitDepth_Preserved()
    {
        var src = SyntheticImages.Checkerboard(64, 64, 8);
        src.SourceBitDepth = BitDepth.Sixteen;
        var roi = new RegionOfInterest(20, 20, 20, 20, FeatherRadius: 4);
        var result = RoiProcessor.ApplyToRoi(src, roi, psfRadius: 3,
            deconvolve: extract => extract.Clone());
        Assert.Equal(BitDepth.Sixteen, result.SourceBitDepth);
    }

    private static ImageBuffer Fill(ImageBuffer template, float v)
    {
        var b = new ImageBuffer(template.Width, template.Height);
        for (int i = 0; i < b.PixelCount; i++) { b.R[i] = v; b.G[i] = v; b.B[i] = v; }
        return b;
    }
}
```

- [ ] **Step 2: Implement `RoiProcessor`**

```csharp
// Deblur.Engine/RoiProcessor.cs
namespace Deblur.Engine;

public static class RoiProcessor
{
    public static ImageBuffer ApplyToRoi(
        ImageBuffer full,
        RegionOfInterest roi,
        int psfRadius,
        Func<ImageBuffer, ImageBuffer> deconvolve)
    {
        if (roi.Width <= 0 || roi.Height <= 0)
            throw new ArgumentException("ROI dimensions must be positive.");
        var clampedRoi = roi.ClampFeatherToHalfMinDim();
        int pad = Math.Max(psfRadius, clampedRoi.FeatherRadius);
        int ex = clampedRoi.X - pad;
        int ey = clampedRoi.Y - pad;
        int ew = clampedRoi.Width + 2 * pad;
        int eh = clampedRoi.Height + 2 * pad;

        var extract = new ImageBuffer(ew, eh);
        for (int dy = 0; dy < eh; dy++)
        {
            int sy = ReflectIndex(ey + dy, full.Height);
            for (int dx = 0; dx < ew; dx++)
            {
                int sx = ReflectIndex(ex + dx, full.Width);
                int si = sy * full.Width + sx;
                int di = dy * ew + dx;
                extract.R[di] = full.R[si];
                extract.G[di] = full.G[si];
                extract.B[di] = full.B[si];
            }
        }

        var deconvolved = deconvolve(extract);
        if (deconvolved.Width != ew || deconvolved.Height != eh)
            throw new InvalidOperationException(
                $"deconvolve returned {deconvolved.Width}x{deconvolved.Height}, expected {ew}x{eh}.");

        var result = full.Clone(); // preserves SourceBitDepth

        int F = clampedRoi.FeatherRadius;
        int rw = clampedRoi.Width, rh = clampedRoi.Height;
        int rx = clampedRoi.X, ry = clampedRoi.Y;
        for (int my = 0; my < rh; my++)
        {
            int fullY = ry + my;
            if (fullY < 0 || fullY >= full.Height) continue;
            for (int mx = 0; mx < rw; mx++)
            {
                int fullX = rx + mx;
                if (fullX < 0 || fullX >= full.Width) continue;

                float alpha;
                if (F <= 0)
                {
                    alpha = 1f;
                }
                else
                {
                    int d = Math.Min(Math.Min(mx, my), Math.Min(rw - 1 - mx, rh - 1 - my));
                    if (d >= F) alpha = 1f;
                    else alpha = 0.5f * (1f - MathF.Cos(MathF.PI * d / F));
                }

                int di = fullY * full.Width + fullX;
                int ei = (pad + my) * ew + (pad + mx);
                result.R[di] = alpha * deconvolved.R[ei] + (1f - alpha) * full.R[di];
                result.G[di] = alpha * deconvolved.G[ei] + (1f - alpha) * full.G[di];
                result.B[di] = alpha * deconvolved.B[ei] + (1f - alpha) * full.B[di];
            }
        }
        return result;
    }

    private static int ReflectIndex(int i, int len)
    {
        if (len <= 1) return 0;
        int period = 2 * (len - 1);
        int m = ((i % period) + period) % period;
        return m < len ? m : period - m;
    }
}
```

- [ ] **Step 3: Verify + commit**

Run: `dotnet test Deblur.sln --filter FullyQualifiedName~RoiProcessorTests` → 4 pass. Full suite → 102 pass.

```bash
git add Deblur.Engine/RoiProcessor.cs Deblur.Tests/RoiProcessorTests.cs
git commit -m "Add RoiProcessor: padded extract + Chambolle-shaped feather blend"
```

---

### Task 4: DeblurJobRunner ROI integration

**Files:**
- Modify: `Deblur.Engine/DeblurJobRunner.cs`
- Test:   `Deblur.Tests/RoiRunnerIntegrationTests.cs`

**Interfaces:**
- Consumes: `RegionOfInterest`, `RoiProcessor`, existing `RunDeconvolve` helper.
- Produces: `DeblurJobRunner.Roi { get; set; }` — nullable. When null, `RenderFullAsync` matches Phase 1.a behavior. When set, `RenderFullAsync` routes through `RoiProcessor`.

- [ ] **Step 1: Add `Roi` property + `EstimatePsfRadius` helper**

Add near the top of `DeblurJobRunner`, alongside `_options`:

```csharp
public RegionOfInterest? Roi { get; set; }

private static int EstimatePsfRadius(KernelParams p) => p.Type switch
{
    BlurType.Motion     => (int)Math.Ceiling(p.Length / 2.0),
    BlurType.OutOfFocus => (int)Math.Ceiling((double)p.Radius),
    BlurType.Gaussian   => (int)Math.Ceiling(3.0 * p.Sigma),
    _                   => 1,
};
```

- [ ] **Step 2: Route `RenderFullAsync` through `RoiProcessor` when `Roi` is set**

Replace the current inline dispatch inside the `Task.Run` closure:

```csharp
cancellationToken.ThrowIfCancellationRequested();
ImageBuffer result;
if (Roi is null)
{
    result = IsNoOp(scaledParams) ? fullRes.Clone() : RunDeconvolve(fullRes, scaledParams);
}
else
{
    result = RoiProcessor.ApplyToRoi(
        fullRes,
        Roi,
        psfRadius: EstimatePsfRadius(scaledParams),
        deconvolve: extract => IsNoOp(scaledParams)
            ? extract.Clone()
            : RunDeconvolve(extract, scaledParams));
}
progress?.Report(1.0);
return result;
```

Preserve the `progress?.Report(0.1)` / `0.3` / `1.0` calls and `ThrowIfCancellationRequested()` guards at their existing positions.

- [ ] **Step 3: Add integration tests**

```csharp
// Deblur.Tests/RoiRunnerIntegrationTests.cs
using System.Collections.Concurrent;
using Deblur.Engine;
using Deblur.Tests.TestHelpers;
using Xunit;

namespace Deblur.Tests;

public class RoiRunnerIntegrationTests
{
    private sealed class EchoStubDeconvolver : IDeconvolver
    {
        public AlgorithmMetadata Metadata { get; } = new(
            Id: "stub", Version: "0", DisplayName: "Stub",
            DescriptionMarkdown: "test-only stub", LiteratureCitation: "n/a");
        public ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p, PipelineOptions? options = null)
            => new ImageBuffer(input.Width, input.Height,
                (float[])input.R.Clone(), (float[])input.G.Clone(), (float[])input.B.Clone());
    }

    private sealed class RecordingStubKernel : IBlurKernel
    {
        public readonly ConcurrentBag<KernelParams> Seen = new();
        public float[,] Build(KernelParams p) { Seen.Add(p); return new float[1, 1] { { 1f } }; }
    }

    [Fact]
    public async Task RenderFullAsync_RoiNull_MatchesPhase1aWholeImageBehavior()
    {
        var deconv = new EchoStubDeconvolver();
        var kernel = new RecordingStubKernel();
        var kernels = new Dictionary<BlurType, IBlurKernel> { [BlurType.Motion] = kernel };
        var deconvolvers = new Dictionary<AlgorithmType, IDeconvolver>
        {
            [AlgorithmType.Wiener]   = deconv,
            [AlgorithmType.Tikhonov] = deconv,
        };
        using var runner = new DeblurJobRunner(kernels, deconvolvers);
        Assert.Null(runner.Roi);

        var full = SyntheticImages.Checkerboard(64, 64, 8);
        var result = await runner.RenderFullAsync(
            full,
            new KernelParams(BlurType.Motion, 45f, 10f, 0.005f, 0f, 0f, AlgorithmType.Wiener),
            proxyScale: 1f);
        Assert.Equal(full.Width, result.Width);
        Assert.Equal(full.Height, result.Height);
    }

    [Fact]
    public async Task RenderFullAsync_WithRoi_RoutesThroughRoiProcessorAndPreservesBitDepth()
    {
        var deconv = new EchoStubDeconvolver();
        var kernel = new RecordingStubKernel();
        var kernels = new Dictionary<BlurType, IBlurKernel> { [BlurType.Motion] = kernel };
        var deconvolvers = new Dictionary<AlgorithmType, IDeconvolver>
        {
            [AlgorithmType.Wiener]   = deconv,
            [AlgorithmType.Tikhonov] = deconv,
        };
        using var runner = new DeblurJobRunner(kernels, deconvolvers);

        var full = SyntheticImages.Checkerboard(64, 64, 8);
        full.SourceBitDepth = BitDepth.Sixteen;
        runner.Roi = new RegionOfInterest(X: 16, Y: 16, Width: 24, Height: 24, FeatherRadius: 4);

        var result = await runner.RenderFullAsync(
            full,
            new KernelParams(BlurType.Motion, 45f, 10f, 0.005f, 0f, 0f, AlgorithmType.Wiener),
            proxyScale: 1f);

        Assert.Equal(BitDepth.Sixteen, result.SourceBitDepth);

        // Outside the ROI, pixels should be byte-identical to input (echo stub means
        // even inside the ROI the values match, so we just check the invariant holds).
        int outIdx = 5 * full.Width + 5;
        Assert.Equal(full.R[outIdx], result.R[outIdx]);
    }
}
```

- [ ] **Step 4: Verify + commit**

Run: `dotnet test Deblur.sln` → 104 pass (102 + 2 new).

```bash
git add Deblur.Engine/DeblurJobRunner.cs Deblur.Tests/RoiRunnerIntegrationTests.cs
git commit -m "DeblurJobRunner: nullable Roi property, RenderFullAsync routes via RoiProcessor"
```

---

### Task 5: PreviewCanvas rubber-band ROI mode

**Files:**
- Modify: `Deblur/Controls/PreviewCanvas.xaml`
- Modify: `Deblur/Controls/PreviewCanvas.xaml.cs`

**Interfaces:**
- Produces:
  - New DP: `bool RoiModeEnabled` — when true, left-drag enters rubber-band mode instead of arrow-drag (arrow-drag is Motion-only and gets suppressed).
  - New DP: `System.Windows.Rect? SelectedRoiRect` — nullable rect in proxy image coordinates (top-left origin, no zoom applied). The canvas draws it as a persistent overlay when non-null.
  - New event: `event EventHandler<RoiDrawnEventArgs> RoiDrawn` where `RoiDrawnEventArgs` has `int X, Y, Width, Height` in proxy image coordinates. Fired on MouseLeftButtonUp after a completed drag.

### Behavior

When `RoiModeEnabled` is true:
- `MouseLeftButtonDown` on the image starts a drag. Record the anchor point in image coordinates.
- `MouseMove` while dragging updates a rubber-band `<Rectangle>` in the visual tree — thin white stroke, semi-transparent stroke shadow, no fill.
- `MouseLeftButtonUp` finalizes: compute the normalized rect (`Math.Min` / `Math.Max` of anchor and release), suppress if width or height < 4 pixels (accidental click), then raise `RoiDrawn`.

When `RoiModeEnabled` is false:
- ROI drawing paths are inert; existing arrow-drag behavior (for Motion) runs as today.

The persistent overlay: when `SelectedRoiRect` is set, draw the same white/shadow rectangle at that position. Update automatically when the property changes.

The mode is exclusive: while ROI drag is in progress, the arrow-drag handlers must not fire; and while an arrow drag is in progress (when the ROI mode is off), the ROI handlers must not fire.

- [ ] **Step 1: Add the ROI-overlay `<Rectangle>` and the drag `<Rectangle>` to `PreviewCanvas.xaml`**

Two `<Rectangle>` elements in the content grid — one bound to `SelectedRoiRect` (via `Canvas.Left/Top` and `Width/Height`), one that's toggled visible only during an active drag. Both use `Stroke="White"`, `StrokeThickness="1"`, `SnapsToDevicePixels="True"`, and a dropped `<Rectangle.Effect><DropShadowEffect BlurRadius="2" ShadowDepth="0" Opacity="0.6" Color="Black"/></Rectangle.Effect>` for the hairline shadow so the overlay reads against any background.

- [ ] **Step 2: Wire the DPs, event, and drag state**

```csharp
public static readonly DependencyProperty RoiModeEnabledProperty =
    DependencyProperty.Register(nameof(RoiModeEnabled), typeof(bool), typeof(PreviewCanvas),
        new PropertyMetadata(false));

public bool RoiModeEnabled
{
    get => (bool)GetValue(RoiModeEnabledProperty);
    set => SetValue(RoiModeEnabledProperty, value);
}

public static readonly DependencyProperty SelectedRoiRectProperty =
    DependencyProperty.Register(nameof(SelectedRoiRect), typeof(Rect?), typeof(PreviewCanvas),
        new PropertyMetadata(null));

public Rect? SelectedRoiRect
{
    get => (Rect?)GetValue(SelectedRoiRectProperty);
    set => SetValue(SelectedRoiRectProperty, value);
}

public event EventHandler<RoiDrawnEventArgs>? RoiDrawn;

private Point? _roiAnchor;
```

`RoiDrawnEventArgs`:

```csharp
public sealed class RoiDrawnEventArgs : EventArgs
{
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}
```

- [ ] **Step 3: Handle mouse events**

In `MouseLeftButtonDown` — if `RoiModeEnabled`, capture mouse, record anchor in **image coordinates** (invert the current zoom/pan transform on the mouse position), set the active drag rectangle visible, mark `_roiAnchor`. Return early so arrow-drag logic doesn't run.

In `MouseMove` — if `_roiAnchor is not null`, update the drag rectangle's `Canvas.Left/Top/Width/Height` from the current mouse position (also in image coordinates).

In `MouseLeftButtonUp` — if `_roiAnchor is not null`, finalize: normalize the rect, drop if too small, raise `RoiDrawn`, release capture, clear `_roiAnchor`, hide the active drag rectangle.

When `RoiModeEnabled` becomes false, cancel any in-progress drag (reset `_roiAnchor`, hide the drag rectangle).

- [ ] **Step 4: Manual sanity check + commit**

Build and launch, toggle a temporary XAML checkbox bound to `RoiModeEnabled`, verify:
- With flag off: arrow-drag on a Motion image still works.
- With flag on: drag draws a rectangle; releases to fire `RoiDrawn`; the persistent overlay shows the last drawn ROI.

(No new unit tests — `PreviewCanvas` is UI code without a headless test surface. Task 8's smoke covers this.)

```bash
git add Deblur/Controls/PreviewCanvas.xaml Deblur/Controls/PreviewCanvas.xaml.cs
git commit -m "PreviewCanvas: add rubber-band ROI mode + persistent overlay + RoiDrawn event"
```

---

### Task 6: MainWindow sidebar toggle + feather slider

**Files:**
- Modify: `Deblur/MainWindow.xaml`

**Interfaces:**
- Bindings:
  - Checkbox `IsChecked` ↔ `MainViewModel.RoiEnabled`.
  - Slider `Value` ↔ `MainViewModel.RoiFeatherRadius`, `Minimum=0`, `Maximum=64`, `TickFrequency=4`, `IsSnapToTickEnabled=True`.
  - Both live in the shared footer of the sidebar (below the Smoothness/Regularization slider) so they apply to every blur type.

- [ ] **Step 1: Add the controls**

In the sidebar's shared footer stack panel, add:

```xml
<CheckBox Content="Deblur selected region"
          IsChecked="{Binding RoiEnabled}"
          Margin="0,12,0,4"/>
<TextBlock Text="Feather radius"
           Margin="0,4,0,0"
           IsEnabled="{Binding RoiEnabled}"/>
<Slider Minimum="0" Maximum="64"
        Value="{Binding RoiFeatherRadius}"
        TickFrequency="4"
        IsSnapToTickEnabled="True"
        IsEnabled="{Binding RoiEnabled}"/>
<TextBlock Text="{Binding RoiFeatherRadius, StringFormat={}{0} px}"
           Margin="0,0,0,4"
           IsEnabled="{Binding RoiEnabled}"/>
```

- [ ] **Step 2: Wire the `PreviewCanvas`**

Bind `PreviewCanvas.RoiModeEnabled` to `RoiEnabled`, `PreviewCanvas.SelectedRoiRect` to `SelectedRoiOverlayRect`, hook `PreviewCanvas.RoiDrawn` to a code-behind handler that calls `MainViewModel.CommitRoi(...)`.

- [ ] **Step 3: Verify + commit**

Build → 0 errors. (No new tests; smoke test in Task 8 will exercise this.)

```bash
git add Deblur/MainWindow.xaml Deblur/MainWindow.xaml.cs
git commit -m "MainWindow: add ROI toggle + feather slider + PreviewCanvas ROI wiring"
```

---

### Task 7: MainViewModel ROI plumbing

**Files:**
- Modify: `Deblur/ViewModels/MainViewModel.cs`

**Interfaces:**
- Adds:
  - `[ObservableProperty] private bool _roiEnabled;`
  - `[ObservableProperty] private int _roiFeatherRadius = 12;`
  - `[ObservableProperty] private RegionOfInterest? _selectedRoi;`
  - `[ObservableProperty] private System.Windows.Rect? _selectedRoiOverlayRect;` — proxy-space rectangle for the PreviewCanvas overlay.
  - Public `CommitRoi(int px, int py, int pw, int ph)` — takes proxy-space coordinates from `PreviewCanvas.RoiDrawn`, converts to full-res via `_proxyScale`, stores in `_selectedRoi` AND `_selectedRoiOverlayRect`. Invalidates full-res cache.
  - `OnRoiEnabledChanged` / `OnRoiFeatherRadiusChanged` partials: invalidate full-res cache; when `RoiEnabled` becomes false, do NOT clear `_selectedRoi` (persist between toggle sessions for convenience).
- Change: `EnsureFullResRenderedAsync` — before calling `_runner.RenderFullAsync`, push the current ROI: `_runner.Roi = RoiEnabled ? (SelectedRoi is { } r ? r with { FeatherRadius = RoiFeatherRadius } : null) : null;`.

- [ ] **Step 1: Add properties + `CommitRoi`**

```csharp
[ObservableProperty] private bool _roiEnabled;
[ObservableProperty] private int _roiFeatherRadius = 12;
[ObservableProperty] private RegionOfInterest? _selectedRoi;
[ObservableProperty] private System.Windows.Rect? _selectedRoiOverlayRect;

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
    SelectedRoiOverlayRect = new System.Windows.Rect(proxyX, proxyY, proxyW, proxyH);
    InvalidateFullResCache();
}
```

- [ ] **Step 2: Push ROI into the runner before rendering**

Inside `EnsureFullResRenderedAsync`, before the `await _runner.RenderFullAsync(...)` call:

```csharp
_runner.Roi = (RoiEnabled && SelectedRoi is { } r)
    ? r with { FeatherRadius = RoiFeatherRadius }
    : null;
```

- [ ] **Step 3: Clear ROI overlay when loading a new image**

In `LoadImageFromBytes`, alongside `_history.Clear()`, add:

```csharp
SelectedRoi = null;
SelectedRoiOverlayRect = null;
```

(ROI is per-image; loading a fresh image resets it.)

- [ ] **Step 4: Verify + commit**

Run: `dotnet build Deblur.sln` → 0 errors. `dotnet test Deblur.sln` → 104 pass (same as end of Task 4; no VM tests added).

```bash
git add Deblur/ViewModels/MainViewModel.cs
git commit -m "MainViewModel: ROI properties, CommitRoi coordinate conversion, runner ROI plumbing"
```

---

### Task 8: Manual smoke test + tag

- [ ] **Step 1: Build in Debug and launch**

```bash
dotnet build Deblur.sln
dotnet run --project Deblur/Deblur.csproj --no-build
```

- [ ] **Step 2: Manual smoke**

- Open an image. Existing behavior (open, Motion arrow drag, algorithm dropdown, sliders, Save-As) works unchanged with the ROI toggle off.
- Turn ROI toggle on. Drag a rectangle over a distinctive region. Rectangle stays visible after release; can be redrawn.
- Adjust feather slider (0 → visible seam, 32 → seamless blend).
- Save-As under ROI mode: the saved image has the ROI sharpened and the rest unchanged. Open the saved file, compare pixel-by-pixel outside the ROI to the original — bit-identical.
- Toggle ROI off → whole-image render behavior returns; existing full-res behavior matches Phase 1.a.
- Load a different image → ROI overlay clears.
- Undo/redo still only walks parameter changes, not ROI selection.
- Verify all three algorithm × three blur types combinations still work end-to-end.

Report smoke results in the ledger.

- [ ] **Step 3: Tag + update ledger**

```bash
git tag phase1b
echo "phase1b: complete" >> .superpowers/sdd/progress.md
```

- [ ] **Step 4: Invoke `superpowers:finishing-a-development-branch`**

Present the standard four options.
