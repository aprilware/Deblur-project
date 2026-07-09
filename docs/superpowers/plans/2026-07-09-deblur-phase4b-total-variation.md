# Deblur Phase 4b Implementation Plan (Total Variation Deconvolver)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a `TotalVariation` algorithm option that runs Wiener followed by 20 iterations of Chambolle-Pock TV denoising.

**Architecture:** `TotalVariationDeconvolver : IDeconvolver` composes `WienerDeconvolver.Apply` (initial estimate) with a per-channel Chambolle-Pock projected-gradient TV denoiser (20 iterations, τ=0.125). λ_TV = `params.K * 50f`. `AlgorithmType.TotalVariation` joins the enum; `MainViewModel` adds the dictionary entry; the label converter maps TV → "Regularization (λ)".

**Tech Stack:** .NET 8, WPF, xUnit. No new NuGet packages.

## Global Constraints

- .NET 8. Nullable + ImplicitUsings enabled everywhere.
- No new NuGet packages.
- `Deblur.Engine` stays WPF-free.
- New enum value APPENDED to `AlgorithmType` (never mid-list).
- Chambolle-Pock: 20 iterations, step size τ = 0.125, λ_TV = params.K * 50f.
- Per-channel processing (R, G, B independently).
- Output clamped to `[0, 1]` per pixel; NaN/Inf guard before clamp (same pattern as Wiener).
- No `KernelParams` field additions; no changes to construction sites.
- All 61 phase-5c tests remain green; new tests bring total to 64.
- Phase 4b branches from tag `phase5c` onto branch `phase4b-total-variation`.

---

### Task 1: Add `AlgorithmType.TotalVariation` + `TotalVariationDeconvolver` + tests

**Files:**
- Modify: `Deblur.Engine/AlgorithmType.cs`
- Create: `Deblur.Engine/TotalVariationDeconvolver.cs`
- Create: `Deblur.Tests/TotalVariationDeconvolverTests.cs`

**Interfaces:**
- Consumes: `IDeconvolver`, `DeconvolutionParams`, `ImageBuffer`, `WienerDeconvolver`, `KernelParams`, `MotionBlurKernel`, `GaussianBlurKernel`, `SyntheticImages`.
- Produces:
```csharp
public enum AlgorithmType { Wiener, Tikhonov, TotalVariation }
public sealed class TotalVariationDeconvolver : IDeconvolver
{
    public ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p);
}
```

- [ ] **Step 1: Extend the enum**

Replace `Deblur.Engine/AlgorithmType.cs`:
```csharp
namespace Deblur.Engine;

public enum AlgorithmType
{
    Wiener,
    Tikhonov,
    TotalVariation,
}
```

- [ ] **Step 2: Write the failing tests**

Create `Deblur.Tests/TotalVariationDeconvolverTests.cs`:
```csharp
using Deblur.Engine;
using Deblur.Tests.TestHelpers;
using Xunit;

namespace Deblur.Tests;

public class TotalVariationDeconvolverTests
{
    [Fact]
    public void RoundTrip_RecoversCheckerboard_AbovePsnrThreshold()
    {
        var original = SyntheticImages.Checkerboard(128, 128, 32);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 12f, 0, 0f, 0f, AlgorithmType.TotalVariation));
        var blurred = SyntheticImages.Convolve(original, psf);
        var noisy = SyntheticImages.AddGaussianNoise(blurred, 0.005f, seed: 42);

        var deconv = new TotalVariationDeconvolver().Apply(
            noisy, psf, new DeconvolutionParams(K: 0.005f));

        Assert.True(SyntheticImages.Psnr(original, deconv) > 15f);
    }

    [Fact]
    public void Gaussian_RoundTrip_RecoversAbovePsnrThreshold()
    {
        var original = SyntheticImages.Checkerboard(128, 128, 32);
        var psf = new GaussianBlurKernel().Build(
            new KernelParams(BlurType.Gaussian, 0f, 0f, 0f, 0f, 2f, AlgorithmType.TotalVariation));
        var blurred = SyntheticImages.Convolve(original, psf);
        var noisy = SyntheticImages.AddGaussianNoise(blurred, 0.005f, seed: 42);

        var deconv = new TotalVariationDeconvolver().Apply(
            noisy, psf, new DeconvolutionParams(K: 0.005f));

        float blurredPsnr = SyntheticImages.Psnr(original, blurred);
        float deconvPsnr = SyntheticImages.Psnr(original, deconv);
        Assert.True(deconvPsnr > 15f, $"deconv PSNR {deconvPsnr} below 15 dB floor");
        Assert.True(deconvPsnr > blurredPsnr + 2.5f,
            $"deconv PSNR {deconvPsnr} not > blurred {blurredPsnr} + 2.5 dB");
    }

    [Fact]
    public void ExtremeParams_NoNaNOrInfInOutput()
    {
        var original = SyntheticImages.Checkerboard(64, 64, 4);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 22f, 100f, 0, 0f, 0f, AlgorithmType.TotalVariation));
        var deconv = new TotalVariationDeconvolver().Apply(
            original, psf, new DeconvolutionParams(K: 1e-6f));

        for (int i = 0; i < deconv.PixelCount; i++)
        {
            Assert.False(float.IsNaN(deconv.R[i]) || float.IsInfinity(deconv.R[i]));
            Assert.False(float.IsNaN(deconv.G[i]) || float.IsInfinity(deconv.G[i]));
            Assert.False(float.IsNaN(deconv.B[i]) || float.IsInfinity(deconv.B[i]));
        }
    }
}
```

- [ ] **Step 3: Run tests — verify compile failure**

```bash
dotnet test Deblur.sln --filter "FullyQualifiedName~TotalVariationDeconvolverTests"
```
Expected: compile error — `TotalVariationDeconvolver` not defined.

- [ ] **Step 4: Implement `TotalVariationDeconvolver`**

Create `Deblur.Engine/TotalVariationDeconvolver.cs`:
```csharp
namespace Deblur.Engine;

public sealed class TotalVariationDeconvolver : IDeconvolver
{
    private const int Iterations = 20;
    private const float Tau = 0.125f;
    private const float LambdaScale = 50f;

    public ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p)
    {
        // Warm start: Wiener gives us the initial deblurred estimate.
        var wiener = new WienerDeconvolver().Apply(input, psf, p);

        // Then apply Chambolle-Pock TV denoising per channel.
        float lambda = MathF.Max(p.K * LambdaScale, 1e-6f);
        int w = wiener.Width, h = wiener.Height;
        float[] r = ChambolleTV(wiener.R, w, h, lambda);
        float[] g = ChambolleTV(wiener.G, w, h, lambda);
        float[] b = ChambolleTV(wiener.B, w, h, lambda);
        return new ImageBuffer(w, h, r, g, b);
    }

    // Chambolle projected-gradient dual formulation of TV denoising.
    // Solves argmin_u ||u - f||^2 / (2*lambda) + TV(u).
    private static float[] ChambolleTV(float[] f, int w, int h, float lambda)
    {
        var px = new float[w * h];
        var py = new float[w * h];
        var u = new float[w * h];

        for (int iter = 0; iter < Iterations; iter++)
        {
            // u = f - lambda * div(p)
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    float dpx = px[i] - (x > 0 ? px[i - 1] : 0f);
                    float dpy = py[i] - (y > 0 ? py[i - w] : 0f);
                    u[i] = f[i] - lambda * (dpx + dpy);
                }
            }

            // p_new = p + (tau / lambda) * grad(u); then project onto unit ball.
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    float gx = (x < w - 1) ? u[i + 1] - u[i] : 0f;
                    float gy = (y < h - 1) ? u[i + w] - u[i] : 0f;
                    float pxNew = px[i] + (Tau / lambda) * gx;
                    float pyNew = py[i] + (Tau / lambda) * gy;
                    float norm = MathF.Max(1f, MathF.Sqrt(pxNew * pxNew + pyNew * pyNew));
                    px[i] = pxNew / norm;
                    py[i] = pyNew / norm;
                }
            }
        }

        // Final u = f - lambda * div(p), NaN/Inf guarded and clamped.
        var result = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                float dpx = px[i] - (x > 0 ? px[i - 1] : 0f);
                float dpy = py[i] - (y > 0 ? py[i - w] : 0f);
                float v = f[i] - lambda * (dpx + dpy);
                if (!float.IsFinite(v)) v = 0f;
                result[i] = Math.Clamp(v, 0f, 1f);
            }
        }
        return result;
    }
}
```

- [ ] **Step 5: Run the filtered tests — verify green**

```bash
dotnet test Deblur.sln --filter "FullyQualifiedName~TotalVariationDeconvolverTests"
```
Expected: 3 passing.

- [ ] **Step 6: Run the full suite**

```bash
dotnet test Deblur.sln
```
Expected: `Passed: 64` (61 + 3 new).

- [ ] **Step 7: Commit**

```bash
git add Deblur.Engine/AlgorithmType.cs Deblur.Engine/TotalVariationDeconvolver.cs Deblur.Tests/TotalVariationDeconvolverTests.cs
git commit -m "Add TotalVariationDeconvolver (Wiener + Chambolle-Pock post-filter)"
```

---

### Task 2: Wire TV into `MainViewModel` + label converter

**Files:**
- Modify: `Deblur/ViewModels/MainViewModel.cs`
- Modify: `Deblur/Converters/AlgorithmToSmoothnessLabelConverter.cs`

**Interfaces:**
- Consumes: `TotalVariationDeconvolver` (Task 1), `AlgorithmType.TotalVariation` (Task 1).
- Produces: `MainViewModel` gains a third dictionary entry so the runner can route `p.Algorithm = TotalVariation` requests. The label converter returns `"Regularization (λ)"` for TV (same as Tikhonov).

- [ ] **Step 1: Add the dictionary entry in `MainViewModel`**

In `Deblur/ViewModels/MainViewModel.cs`, locate the `deconvolvers` dictionary construction inside the constructor (currently ~line 48–52) and replace it with:
```csharp
        var deconvolvers = new Dictionary<AlgorithmType, IDeconvolver>
        {
            [AlgorithmType.Wiener]         = new WienerDeconvolver(),
            [AlgorithmType.Tikhonov]       = new TikhonovDeconvolver(),
            [AlgorithmType.TotalVariation] = new TotalVariationDeconvolver(),
        };
```

- [ ] **Step 2: Update the label converter**

Replace `Deblur/Converters/AlgorithmToSmoothnessLabelConverter.cs`:
```csharp
using System.Globalization;
using System.Windows.Data;
using Deblur.Engine;

namespace Deblur.Converters;

public sealed class AlgorithmToSmoothnessLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            AlgorithmType.Tikhonov       => "Regularization (λ)",
            AlgorithmType.TotalVariation => "Regularization (λ)",
            _                            => "Smoothness (K)",
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

- [ ] **Step 3: Build + test**

```bash
dotnet build Deblur.sln
```
Expected: 0 errors.

```bash
dotnet test Deblur.sln
```
Expected: `Passed: 64`.

- [ ] **Step 4: Commit**

```bash
git add Deblur/ViewModels/MainViewModel.cs Deblur/Converters/AlgorithmToSmoothnessLabelConverter.cs
git commit -m "Wire TotalVariation into MainViewModel and label converter"
```

---

### Task 3: Manual smoke test + tag `phase4b`

**Files:** none.

- [ ] **Step 1: Run the app**

```bash
dotnet run --project Deblur/Deblur.csproj
```

Walk the checklist:

- [ ] Load an image. Algorithm dropdown now has three options: Wiener, Tikhonov, TotalVariation.
- [ ] Switch to TotalVariation. Shared-footer label reads "Regularization (λ)".
- [ ] Move the parameter slider — preview updates, slower than Wiener/Tikhonov but usable (~1-3 s per slider tick on a small image).
- [ ] TV output looks smoother than Wiener at the same slider position (compare side by side by swapping algorithms).
- [ ] Under Motion / OutOfFocus / Gaussian — TV works for all three blur types.
- [ ] Full-res Render + Save under TV → reopen the saved file → the TV-processed result is written.
- [ ] Cancel button on the busy overlay still works with TV (Wiener step + one or two Chambolle iterations may complete before the cancel lands — acceptable).
- [ ] Undo/redo, zoom/pan, keyboard shortcuts unchanged.

- [ ] **Step 2: Commit any smoke-triggered fixes**

If needed, one commit per fix.

- [ ] **Step 3: Tag phase 4b**

```bash
git tag phase4b
```

---

## Summary

Three tasks. Task 1 adds `AlgorithmType.TotalVariation`, `TotalVariationDeconvolver` (Wiener warm-start + Chambolle-Pock post-filter), and 3 unit tests. Task 2 wires the runner dictionary entry and label converter. Task 3 smoke-tests end-to-end and tags `phase4b`.
