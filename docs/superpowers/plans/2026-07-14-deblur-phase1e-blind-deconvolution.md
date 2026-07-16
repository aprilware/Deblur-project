# Deblur Phase 1.e Implementation Plan — Iterative Blind Deconvolution

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship multi-scale MAP-alternating blind deconvolution (Cho & Lee 2009) as a new `IDeconvolver`, plus a sidebar PSF-heatmap display showing the estimated kernel. Recovers a general 2D kernel from unknown blur — the "single biggest capability gap" from the original spec.

**Architecture:** Four-level pyramid `[1/8, 1/4, 1/2, 1/1]` with kernel windows `[5, 9, 17, 31]`. Each level runs 5 outer iterations: (a) latent image via Tikhonov given the current kernel; (b) prediction = Gaussian pre-smooth + shock filter (dt=0.25 × 3 passes); (c) kernel estimation in the **gradient domain** (Cho & Lee formula over ∂x and ∂y); (d) kernel projection (clip + sparsity threshold + non-negativity + sum-to-1). Fully deterministic; `LastEstimatedKernel` public getter surfaces the recovered kernel to the VM. Full-res only — skipped in live preview.

**Tech Stack:** .NET 8; `FftSharp`; `CommunityToolkit.Mvvm`; WPF (`net8.0-windows`, `UseWPF`); xUnit.

## Global Constraints

- .NET 8. `net8.0` for `Deblur.Engine` + `Deblur.Tests`. `net8.0-windows` + `UseWPF` for `Deblur` and `Deblur.Wpf.Tests`. Nullable + ImplicitUsings enabled.
- No new NuGet packages.
- `Deblur.Engine` stays UI-free.
- All 154 Phase 1.d tests remain green. Test count target after 1.e: ~170.
- **Kernel estimation runs in the GRADIENT DOMAIN** (∂x, ∂y of both latent and blurred). Intensity-domain estimation is out per the spec amendment.
- Kernel window scales per pyramid level: `[5, 9, 17, 31]` at scales `[1/8, 1/4, 1/2, 1/1]`.
- Fixed hyperparameters (§1 of spec): outer iters 5, λ_i=1e-3, λ_k=1e-3, prediction Gaussian σ=1, shock dt=0.25 × 3 passes, sparsity threshold 5%.
- Blind is DETERMINISTIC: no random seeds, no stochastic sampling.
- `BlindDeconvolutionDeconvolver.Apply` receives but IGNORES its `psf` parameter — the whole point is that it estimates its own.
- `LastEstimatedKernel` is a public `float[,]?` getter on the deconvolver instance; the VM reads it after each `RenderFullAsync` completes. Thread-safety relies on the runner's single-threaded discipline (existing invariant).
- Metadata `Id = "blind-cho-lee"`, `Version = "1.0"`. Description names the multi-scale MAP + gradient-domain + shock filter + fixed 31×31 finest window.
- Live preview: blind is added to the `isIterativePreview` skip list in `DeblurJobRunner.WorkerLoop` (matches RL/Landweber pattern).
- Cancellation via `PipelineOptions.CancellationToken` (Phase 1.c infrastructure) at every outer-iteration boundary AND every scale transition.
- Phase 1.e branches from tag `phase1d` onto `phase1e-blind-deconvolution` (already created).

---

### Task 1: AlgorithmType + skeleton + metadata

**Files:**
- Modify: `Deblur.Engine/AlgorithmType.cs`
- Create: `Deblur.Engine/BlindDeconvolutionDeconvolver.cs` — skeleton returning `input.Clone()` unchanged, with metadata + `LastEstimatedKernel { get; private set; }`.
- Modify: `Deblur.Tests/AlgorithmMetadataTests.cs` — extend to cover blind deconvolver.

**Interfaces:**
- Produces:
  - `AlgorithmType.BlindDeconvolution` enum value.
  - `sealed class BlindDeconvolutionDeconvolver : IDeconvolver` — skeleton for later tasks to fill in.
  - `public float[,]? LastEstimatedKernel { get; private set; }`.
  - Metadata: `Id = "blind-cho-lee"`, `Version = "1.0"`, description names the method + honest limits.

- [ ] **Step 1: Add enum value**

```csharp
// Deblur.Engine/AlgorithmType.cs — append
public enum AlgorithmType
{
    Wiener,
    Tikhonov,
    TotalVariation,
    RichardsonLucy,
    ConstrainedLeastSquares,
    Landweber,
    BlindDeconvolution,
}
```

- [ ] **Step 2: Create skeleton**

```csharp
// Deblur.Engine/BlindDeconvolutionDeconvolver.cs
namespace Deblur.Engine;

public sealed class BlindDeconvolutionDeconvolver : IDeconvolver
{
    public AlgorithmMetadata Metadata { get; } = new(
        Id: "blind-cho-lee",
        Version: "1.0",
        DisplayName: "Blind deconvolution (MAP, multi-scale)",
        DescriptionMarkdown:
            "Multi-scale MAP-alternating blind deconvolution. Given a blurred image with " +
            "unknown point-spread function (PSF), estimates a general 2D kernel via a " +
            "coarse-to-fine pyramid: at each pyramid level, alternate between latent-image " +
            "recovery (Tikhonov given the current kernel) and kernel refinement in the " +
            "gradient domain (Cho & Lee 2009 formulation over dx and dy). An edge-prediction " +
            "step (Gaussian pre-smooth + Osher-Rudin shock filter) sharpens the latent image " +
            "between iterations as a surrogate for the sparse-gradient prior. Kernel projection " +
            "at each step enforces non-negativity and sum-to-1 with a 5%-of-max sparsity " +
            "threshold. Four pyramid levels at scales 1/8, 1/4, 1/2, 1/1 with kernel windows " +
            "5, 9, 17, 31 (odd, centered). Deterministic — no random initialization. Blind " +
            "recovery on natural imagery is inherently noisy; the recovered kernel should be " +
            "inspected visually for testimony validation, and the estimator is unreliable on " +
            "motion larger than ~15 px (finest kernel window is 31x31).",
        LiteratureCitation:
            "Cho, S. & Lee, S. (2009). Fast Motion Deblurring. ACM Transactions on Graphics " +
            "28(5), 145. Levin, A., Weiss, Y., Durand, F. & Freeman, W.T. (2011). " +
            "Understanding Blind Deconvolution Algorithms. IEEE PAMI 33(12), 2354-2367. " +
            "Osher, S. & Rudin, L.I. (1990). Feature-oriented image enhancement using shock " +
            "filters. SIAM Journal on Numerical Analysis 27(4), 919-940.");

    /// <summary>
    /// Kernel estimated on the last <see cref="Apply"/> call. Null before the first call.
    /// Not thread-safe; assumes single-threaded runner invocation.
    /// Live-preview WorkerLoop skips this algorithm, so only RenderFullAsync writes here.
    /// </summary>
    public float[,]? LastEstimatedKernel { get; private set; }

    public ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p, PipelineOptions? options = null)
    {
        _ = options ?? PipelineOptions.Default;
        // Task 6 will fill in the real algorithm. Skeleton returns input unchanged so the
        // enum + registration + metadata plumbing can land independently and be tested.
        LastEstimatedKernel = null;
        return input.Clone();
    }
}
```

- [ ] **Step 3: Extend AlgorithmMetadataTests**

Add `new BlindDeconvolutionDeconvolver()` to the three tests. Pin the Id in `KnownIds_AreStable`:

```csharp
Assert.Equal("blind-cho-lee", new BlindDeconvolutionDeconvolver().Metadata.Id);
```

- [ ] **Step 4: Verify + commit**

```bash
dotnet test Deblur.sln
git add Deblur.Engine/AlgorithmType.cs Deblur.Engine/BlindDeconvolutionDeconvolver.cs Deblur.Tests/AlgorithmMetadataTests.cs
git commit -m "Add BlindDeconvolution AlgorithmType + skeleton class with metadata"
```

---

### Task 2: Gradients helper

**Files:**
- Create: `Deblur.Engine/Blind/Gradients.cs`
- Test:   `Deblur.Tests/Blind/GradientsTests.cs`

**Interfaces:**
- Produces:
  - `static class Gradients` with:
    - `float[] ComputeX(float[] image, int w, int h)` — central difference `(image[x+1] - image[x-1]) / 2` with clamp at edges.
    - `float[] ComputeY(float[] image, int w, int h)` — same along y.

- [ ] **Step 1: Failing tests**

```csharp
// Deblur.Tests/Blind/GradientsTests.cs
using Deblur.Engine.Blind;
using Xunit;

namespace Deblur.Tests.Blind;

public class GradientsTests
{
    [Fact]
    public void ComputeX_ConstantImage_ReturnsZeros()
    {
        var img = new float[16 * 16];
        Array.Fill(img, 0.5f);
        var dx = Gradients.ComputeX(img, 16, 16);
        foreach (var v in dx) Assert.InRange(Math.Abs(v), 0f, 1e-6f);
    }

    [Fact]
    public void ComputeX_LinearRamp_ReturnsSlope()
    {
        var img = new float[16 * 16];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                img[y * 16 + x] = x / 15f;
        var dx = Gradients.ComputeX(img, 16, 16);
        // Interior slope = 1/15 = 0.0667.
        for (int y = 0; y < 16; y++)
            for (int x = 1; x < 15; x++)
                Assert.InRange(dx[y * 16 + x], 0.06f, 0.075f);
    }

    [Fact]
    public void ComputeY_LinearRamp_ReturnsSlope()
    {
        var img = new float[16 * 16];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                img[y * 16 + x] = y / 15f;
        var dy = Gradients.ComputeY(img, 16, 16);
        for (int y = 1; y < 15; y++)
            for (int x = 0; x < 16; x++)
                Assert.InRange(dy[y * 16 + x], 0.06f, 0.075f);
    }

    [Fact]
    public void ComputeX_EdgeClamp_NoNaN()
    {
        var img = new float[16 * 16];
        for (int i = 0; i < img.Length; i++) img[i] = (float)i / img.Length;
        var dx = Gradients.ComputeX(img, 16, 16);
        foreach (var v in dx) Assert.True(float.IsFinite(v));
    }
}
```

- [ ] **Step 2: Implement**

```csharp
// Deblur.Engine/Blind/Gradients.cs
namespace Deblur.Engine.Blind;

public static class Gradients
{
    /// <summary>Central-difference ∂/∂x with edge clamping. Result has same dimensions.</summary>
    public static float[] ComputeX(float[] image, int w, int h)
    {
        var result = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int xm = Math.Max(0, x - 1);
                int xp = Math.Min(w - 1, x + 1);
                result[y * w + x] = 0.5f * (image[y * w + xp] - image[y * w + xm]);
            }
        }
        return result;
    }

    public static float[] ComputeY(float[] image, int w, int h)
    {
        var result = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            int ym = Math.Max(0, y - 1);
            int yp = Math.Min(h - 1, y + 1);
            for (int x = 0; x < w; x++)
                result[y * w + x] = 0.5f * (image[yp * w + x] - image[ym * w + x]);
        }
        return result;
    }
}
```

- [ ] **Step 3: Verify + commit**

```bash
dotnet test Deblur.sln --filter FullyQualifiedName~GradientsTests
git add Deblur.Engine/Blind/Gradients.cs Deblur.Tests/Blind/GradientsTests.cs
git commit -m "Add Gradients: central-difference ∂x / ∂y with edge clamp"
```

---

### Task 3: Gaussian pre-smooth + shock filter

**Files:**
- Create: `Deblur.Engine/Blind/GaussianSmooth.cs` (small separable Gaussian, single σ).
- Create: `Deblur.Engine/Blind/ShockFilter.cs` — Osher-Rudin shock filter, one pass.
- Test:   `Deblur.Tests/Blind/GaussianSmoothTests.cs`
- Test:   `Deblur.Tests/Blind/ShockFilterTests.cs`

**Interfaces:**
- Produces:
  - `static class GaussianSmooth { public static float[] Apply(float[] image, int w, int h, float sigma); }` — separable 1D Gaussian, kernel size = `2*ceil(3σ) + 1` (5 for σ=1).
  - `static class ShockFilter { public static float[] ApplyOnce(float[] image, int w, int h, float dt); }` — one pass of `u_t = -sign(Δu) · |∇u|` with time step `dt`.

- [ ] **Step 1: Failing tests**

```csharp
// Deblur.Tests/Blind/GaussianSmoothTests.cs
using Deblur.Engine.Blind;
using Xunit;

namespace Deblur.Tests.Blind;

public class GaussianSmoothTests
{
    [Fact]
    public void Constant_Unchanged()
    {
        var img = new float[16 * 16];
        Array.Fill(img, 0.4f);
        var s = GaussianSmooth.Apply(img, 16, 16, 1.0f);
        foreach (var v in s) Assert.InRange(Math.Abs(v - 0.4f), 0f, 1e-4f);
    }

    [Fact]
    public void Impulse_SpreadsToNeighborhood()
    {
        var img = new float[16 * 16];
        img[8 * 16 + 8] = 1f;
        var s = GaussianSmooth.Apply(img, 16, 16, 1.0f);
        Assert.True(s[8 * 16 + 8] > 0.1f && s[8 * 16 + 8] < 0.3f, $"center: {s[8*16+8]}");
        Assert.True(s[8 * 16 + 9] > 0.05f, "neighbor should have positive weight");
    }
}
```

```csharp
// Deblur.Tests/Blind/ShockFilterTests.cs
using Deblur.Engine.Blind;
using Xunit;

namespace Deblur.Tests.Blind;

public class ShockFilterTests
{
    [Fact]
    public void ConstantImage_Unchanged()
    {
        var img = new float[16 * 16];
        Array.Fill(img, 0.5f);
        var s = ShockFilter.ApplyOnce(img, 16, 16, dt: 0.25f);
        foreach (var v in s) Assert.InRange(Math.Abs(v - 0.5f), 0f, 1e-4f);
    }

    [Fact]
    public void SoftEdge_Sharpens()
    {
        // Sigmoid edge across x=8 in a 16-wide image.
        var img = new float[32 * 32];
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
                img[y * 32 + x] = 1f / (1f + MathF.Exp(-(x - 16) * 0.5f));

        // 3 passes at dt=0.25.
        var s = img;
        for (int i = 0; i < 3; i++) s = ShockFilter.ApplyOnce(s, 32, 32, dt: 0.25f);

        // Gradient magnitude at the edge should increase.
        int mid = 16 * 32 + 16;
        float srcGrad = Math.Abs(img[mid + 1] - img[mid - 1]) / 2f;
        float outGrad = Math.Abs(s[mid + 1] - s[mid - 1]) / 2f;
        Assert.True(outGrad > srcGrad, $"expected sharpened edge, src grad {srcGrad:F3} out {outGrad:F3}");
    }
}
```

- [ ] **Step 2: Implement**

```csharp
// Deblur.Engine/Blind/GaussianSmooth.cs
namespace Deblur.Engine.Blind;

public static class GaussianSmooth
{
    public static float[] Apply(float[] image, int w, int h, float sigma)
    {
        if (sigma <= 0f) return (float[])image.Clone();
        int radius = (int)Math.Ceiling(3.0 * sigma);
        int size = 2 * radius + 1;
        var kernel = new float[size];
        float sum = 0;
        for (int i = 0; i < size; i++)
        {
            float d = i - radius;
            kernel[i] = MathF.Exp(-d * d / (2f * sigma * sigma));
            sum += kernel[i];
        }
        for (int i = 0; i < size; i++) kernel[i] /= sum;

        // Separable: pass along x, then y.
        var tmp = new float[image.Length];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float acc = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    int xk = Math.Clamp(x + k, 0, w - 1);
                    acc += image[y * w + xk] * kernel[k + radius];
                }
                tmp[y * w + x] = acc;
            }
        }
        var result = new float[image.Length];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float acc = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    int yk = Math.Clamp(y + k, 0, h - 1);
                    acc += tmp[yk * w + x] * kernel[k + radius];
                }
                result[y * w + x] = acc;
            }
        }
        return result;
    }
}
```

```csharp
// Deblur.Engine/Blind/ShockFilter.cs
namespace Deblur.Engine.Blind;

/// <summary>
/// Osher-Rudin shock filter, one pass: u_t = -sign(Δu) · |∇u|.
/// Sharpens edges while preserving flat regions. Stable for dt ≤ 0.25.
/// </summary>
public static class ShockFilter
{
    public static float[] ApplyOnce(float[] image, int w, int h, float dt)
    {
        var result = new float[image.Length];
        for (int y = 0; y < h; y++)
        {
            int ym = Math.Max(0, y - 1);
            int yp = Math.Min(h - 1, y + 1);
            for (int x = 0; x < w; x++)
            {
                int xm = Math.Max(0, x - 1);
                int xp = Math.Min(w - 1, x + 1);
                float c  = image[y * w + x];
                float dx = 0.5f * (image[y * w + xp] - image[y * w + xm]);
                float dy = 0.5f * (image[yp * w + x] - image[ym * w + x]);
                float lap = image[y * w + xp] + image[y * w + xm]
                          + image[yp * w + x] + image[ym * w + x] - 4f * c;
                float grad = MathF.Sqrt(dx * dx + dy * dy);
                float sign = lap > 0f ? 1f : (lap < 0f ? -1f : 0f);
                float v = c - dt * sign * grad;
                if (!float.IsFinite(v)) v = c;
                result[y * w + x] = Math.Clamp(v, 0f, 1f);
            }
        }
        return result;
    }
}
```

- [ ] **Step 3: Verify + commit**

```bash
dotnet test Deblur.sln --filter FullyQualifiedName~Blind
git add Deblur.Engine/Blind Deblur.Tests/Blind
git commit -m "Add GaussianSmooth (separable) + ShockFilter (Osher-Rudin one pass)"
```

---

### Task 4: Kernel projection

**Files:**
- Create: `Deblur.Engine/Blind/KernelProjection.cs`
- Test:   `Deblur.Tests/Blind/KernelProjectionTests.cs`

**Interfaces:**
- Produces:
  - `static class KernelProjection { public static float[,] Project(float[,] rawKernel, int windowSize, float sparsityThreshold); }`
  - Steps: (1) find the argmax pixel of `rawKernel`; (2) crop a centered `windowSize × windowSize` window around that argmax; (3) threshold values below `sparsityThreshold * max` to 0; (4) clamp negatives to 0; (5) normalize to sum = 1.
  - `windowSize` is odd. If the argmax is near the edge of `rawKernel`, the crop is clamped so the output is still `windowSize × windowSize` but centered as close to the argmax as the boundary allows.

- [ ] **Step 1: Failing tests**

```csharp
// Deblur.Tests/Blind/KernelProjectionTests.cs
using Deblur.Engine.Blind;
using Xunit;

namespace Deblur.Tests.Blind;

public class KernelProjectionTests
{
    [Fact]
    public void OutputDimensionsMatchWindowSize()
    {
        var raw = new float[64, 64];
        raw[32, 32] = 1f;
        var k = KernelProjection.Project(raw, 5, 0.05f);
        Assert.Equal(5, k.GetLength(0));
        Assert.Equal(5, k.GetLength(1));
    }

    [Fact]
    public void CroppedAroundArgmax()
    {
        var raw = new float[64, 64];
        raw[10, 20] = 1f;
        raw[10, 21] = 0.5f;
        raw[11, 20] = 0.5f;
        var k = KernelProjection.Project(raw, 5, 0.05f);
        // Center pixel should be argmax value (normalized).
        Assert.True(k[2, 2] > k[0, 0]);
        Assert.True(k[2, 2] > k[4, 4]);
    }

    [Fact]
    public void SparsityThreshold_ZeroesSmallValues()
    {
        var raw = new float[5, 5];
        raw[2, 2] = 1f;
        raw[0, 0] = 0.04f; // 4% of max — below 5% threshold
        raw[4, 4] = 0.06f; // 6% of max — above
        var k = KernelProjection.Project(raw, 5, 0.05f);
        // k[0,0] would be normalized. Its RAW pre-normalization value came from
        // 0.04 which is below the 5% threshold, so it should be 0.
        Assert.Equal(0f, k[0, 0]);
        Assert.True(k[4, 4] > 0f);
    }

    [Fact]
    public void NonNegativity()
    {
        var raw = new float[5, 5];
        raw[2, 2] = 1f;
        raw[1, 1] = -0.5f;
        raw[3, 3] = 0.3f;
        var k = KernelProjection.Project(raw, 5, 0.05f);
        for (int y = 0; y < 5; y++)
            for (int x = 0; x < 5; x++)
                Assert.True(k[y, x] >= 0f);
    }

    [Fact]
    public void SumsToOne()
    {
        var raw = new float[5, 5];
        raw[2, 2] = 0.7f;
        raw[2, 3] = 0.3f;
        raw[3, 2] = 0.1f;
        var k = KernelProjection.Project(raw, 5, 0.05f);
        float sum = 0;
        for (int y = 0; y < 5; y++)
            for (int x = 0; x < 5; x++)
                sum += k[y, x];
        Assert.InRange(Math.Abs(sum - 1f), 0f, 1e-5f);
    }
}
```

- [ ] **Step 2: Implement**

```csharp
// Deblur.Engine/Blind/KernelProjection.cs
namespace Deblur.Engine.Blind;

public static class KernelProjection
{
    public static float[,] Project(float[,] rawKernel, int windowSize, float sparsityThreshold)
    {
        if (windowSize < 1 || windowSize % 2 == 0)
            throw new ArgumentException("windowSize must be a positive odd integer", nameof(windowSize));

        int rh = rawKernel.GetLength(0);
        int rw = rawKernel.GetLength(1);

        // Find argmax.
        float rawMax = float.NegativeInfinity;
        int argY = 0, argX = 0;
        for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
                if (rawKernel[y, x] > rawMax) { rawMax = rawKernel[y, x]; argY = y; argX = x; }

        int radius = windowSize / 2;
        // Clamp crop origin so output is still windowSize × windowSize.
        int y0 = Math.Clamp(argY - radius, 0, Math.Max(0, rh - windowSize));
        int x0 = Math.Clamp(argX - radius, 0, Math.Max(0, rw - windowSize));

        var result = new float[windowSize, windowSize];
        for (int y = 0; y < windowSize; y++)
        {
            int sy = y0 + y;
            if (sy < 0 || sy >= rh) continue;
            for (int x = 0; x < windowSize; x++)
            {
                int sx = x0 + x;
                if (sx < 0 || sx >= rw) continue;
                result[y, x] = rawKernel[sy, sx];
            }
        }

        // Sparsity threshold (against post-crop max).
        float postMax = 0;
        for (int y = 0; y < windowSize; y++)
            for (int x = 0; x < windowSize; x++)
                if (result[y, x] > postMax) postMax = result[y, x];
        float minVal = postMax * sparsityThreshold;
        for (int y = 0; y < windowSize; y++)
            for (int x = 0; x < windowSize; x++)
                if (result[y, x] < minVal) result[y, x] = 0f;

        // Non-negativity.
        for (int y = 0; y < windowSize; y++)
            for (int x = 0; x < windowSize; x++)
                if (result[y, x] < 0f) result[y, x] = 0f;

        // Normalize to sum = 1.
        float sum = 0;
        for (int y = 0; y < windowSize; y++)
            for (int x = 0; x < windowSize; x++)
                sum += result[y, x];
        if (sum > 0f)
        {
            float inv = 1f / sum;
            for (int y = 0; y < windowSize; y++)
                for (int x = 0; x < windowSize; x++)
                    result[y, x] *= inv;
        }
        else
        {
            // Degenerate: return a centered delta.
            result[windowSize / 2, windowSize / 2] = 1f;
        }
        return result;
    }
}
```

- [ ] **Step 3: Verify + commit**

```bash
dotnet test Deblur.sln --filter FullyQualifiedName~KernelProjection
git add Deblur.Engine/Blind/KernelProjection.cs Deblur.Tests/Blind/KernelProjectionTests.cs
git commit -m "Add KernelProjection: argmax-centered crop + sparsity + non-negativity + sum-to-1"
```

---

### Task 5: Gradient-domain kernel estimation

**Files:**
- Create: `Deblur.Engine/Blind/KernelEstimation.cs`
- Test:   `Deblur.Tests/Blind/KernelEstimationTests.cs`

**Interfaces:**
- Produces:
  - `static class KernelEstimation` with:
    ```csharp
    public static float[,] EstimateGradientDomain(
        float[] dxLatent, float[] dyLatent,
        float[] dxBlurred, float[] dyBlurred,
        int w, int h,
        float lambda,
        int fftSize)
    ```
  - Formula: `H(u,v) = ( conj(Fdx_L) · Fdx_B + conj(Fdy_L) · Fdy_B ) / ( |Fdx_L|² + |Fdy_L|² + λ )`.
  - Returns the RAW kernel as a `float[,]` of size `fftSize × fftSize` — caller runs `KernelProjection` to clip to the desired window.

- [ ] **Step 1: Failing tests**

```csharp
// Deblur.Tests/Blind/KernelEstimationTests.cs
using Deblur.Engine;
using Deblur.Engine.Blind;
using Deblur.Tests.TestHelpers;
using Xunit;

namespace Deblur.Tests.Blind;

public class KernelEstimationTests
{
    [Fact]
    public void RecoversDeltaKernel_FromIdenticalGradients()
    {
        // If latent == blurred, the kernel should be a delta.
        var gt = SyntheticImages.TexturedNoise(64, 64, seed: 42);
        var gray = ToGrayscale(gt);
        var dxL = Gradients.ComputeX(gray, 64, 64);
        var dyL = Gradients.ComputeY(gray, 64, 64);
        int fftSize = FftAdapter.NextPow2(64 + 30);

        var raw = KernelEstimation.EstimateGradientDomain(dxL, dyL, dxL, dyL, 64, 64, lambda: 1e-3f, fftSize);
        // Center of FFT canvas should hold the peak (or near it).
        float max = float.NegativeInfinity;
        int argY = 0, argX = 0;
        for (int y = 0; y < fftSize; y++)
            for (int x = 0; x < fftSize; x++)
                if (raw[y, x] > max) { max = raw[y, x]; argY = y; argX = x; }
        // Argmax should be at the origin (0, 0) under FFT convention (a delta).
        int distFromOrigin = Math.Min(argY, fftSize - argY) + Math.Min(argX, fftSize - argX);
        Assert.True(distFromOrigin <= 2, $"argmax at ({argY},{argX}), fftSize {fftSize}");
    }

    [Fact]
    public void NoNaNOnZeroLatent()
    {
        int w = 32, h = 32, fftSize = FftAdapter.NextPow2(w + 30);
        var zero = new float[w * h];
        var blurred = new float[w * h];
        for (int i = 0; i < blurred.Length; i++) blurred[i] = 0.5f;
        var dxL = Gradients.ComputeX(zero, w, h);
        var dyL = Gradients.ComputeY(zero, w, h);
        var dxB = Gradients.ComputeX(blurred, w, h);
        var dyB = Gradients.ComputeY(blurred, w, h);
        var raw = KernelEstimation.EstimateGradientDomain(dxL, dyL, dxB, dyB, w, h, lambda: 1e-3f, fftSize);
        for (int y = 0; y < fftSize; y++)
            for (int x = 0; x < fftSize; x++)
                Assert.True(float.IsFinite(raw[y, x]));
    }

    private static float[] ToGrayscale(ImageBuffer buf)
    {
        var g = new float[buf.PixelCount];
        for (int i = 0; i < g.Length; i++)
            g[i] = 0.299f * buf.R[i] + 0.587f * buf.G[i] + 0.114f * buf.B[i];
        return g;
    }
}
```

- [ ] **Step 2: Implement**

```csharp
// Deblur.Engine/Blind/KernelEstimation.cs
using System.Numerics;

namespace Deblur.Engine.Blind;

public static class KernelEstimation
{
    /// <summary>
    /// Gradient-domain kernel estimation (Cho & Lee 2009):
    /// H(u,v) = ( conj(Fdx_L) · Fdx_B + conj(Fdy_L) · Fdy_B ) / ( |Fdx_L|² + |Fdy_L|² + λ )
    /// Returns the raw (unclipped, unprojected) kernel in the FFT canvas frame.
    /// Caller runs KernelProjection to clip to the desired window.
    /// </summary>
    public static float[,] EstimateGradientDomain(
        float[] dxLatent, float[] dyLatent,
        float[] dxBlurred, float[] dyBlurred,
        int w, int h,
        float lambda,
        int fftSize)
    {
        var dxL = PadToCanvas(dxLatent, w, h, fftSize);
        var dyL = PadToCanvas(dyLatent, w, h, fftSize);
        var dxB = PadToCanvas(dxBlurred, w, h, fftSize);
        var dyB = PadToCanvas(dyBlurred, w, h, fftSize);

        var FdxL = FftAdapter.Forward2D(dxL);
        var FdyL = FftAdapter.Forward2D(dyL);
        var FdxB = FftAdapter.Forward2D(dxB);
        var FdyB = FftAdapter.Forward2D(dyB);

        var Hfreq = new Complex[fftSize, fftSize];
        for (int y = 0; y < fftSize; y++)
        {
            for (int x = 0; x < fftSize; x++)
            {
                var conjDxL = Complex.Conjugate(FdxL[y, x]);
                var conjDyL = Complex.Conjugate(FdyL[y, x]);
                double magL2 = FdxL[y, x].Real * FdxL[y, x].Real + FdxL[y, x].Imaginary * FdxL[y, x].Imaginary
                             + FdyL[y, x].Real * FdyL[y, x].Real + FdyL[y, x].Imaginary * FdyL[y, x].Imaginary;
                var num = conjDxL * FdxB[y, x] + conjDyL * FdyB[y, x];
                Hfreq[y, x] = num / (magL2 + lambda);
            }
        }

        var raw = FftAdapter.Inverse2DReal(Hfreq);
        var result = new float[fftSize, fftSize];
        for (int y = 0; y < fftSize; y++)
            for (int x = 0; x < fftSize; x++)
            {
                float v = raw[y, x];
                if (!float.IsFinite(v)) v = 0f;
                result[y, x] = v;
            }
        return result;
    }

    private static float[,] PadToCanvas(float[] arr, int w, int h, int fftSize)
    {
        var canvas = new float[fftSize, fftSize];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                canvas[y, x] = arr[y * w + x];
        return canvas;
    }
}
```

- [ ] **Step 3: Verify + commit**

```bash
dotnet test Deblur.sln --filter FullyQualifiedName~KernelEstimation
git add Deblur.Engine/Blind/KernelEstimation.cs Deblur.Tests/Blind/KernelEstimationTests.cs
git commit -m "Add KernelEstimation: gradient-domain Cho & Lee formula"
```

---

### Task 6: Multi-scale orchestration in BlindDeconvolutionDeconvolver.Apply

**Files:**
- Modify: `Deblur.Engine/BlindDeconvolutionDeconvolver.cs` — replace skeleton with full multi-scale MAP loop.
- Test:   `Deblur.Tests/BlindDeconvolutionDeconvolverTests.cs`

**Interfaces:**
- Consumes: `Gradients`, `GaussianSmooth`, `ShockFilter`, `KernelProjection`, `KernelEstimation`, `TikhonovDeconvolver` (for the latent step), `AreaResample.Box` (for downsample), `PipelineOptions.CancellationToken`.
- Produces: final `BlindDeconvolutionDeconvolver.Apply` with the full algorithm; `LastEstimatedKernel` populated.

### Algorithm outline (implementer follows verbatim)

```csharp
public ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p, PipelineOptions? options = null)
{
    var opt = options ?? PipelineOptions.Default;
    var ct = opt.CancellationToken;

    // Blind estimates kernel from luminance; deblurs each color channel with it.
    var luma = ExtractLuma(input);

    int[] windowSizes = new[] { 5, 9, 17, 31 };
    float[] scales    = new[] { 1f/8f, 1f/4f, 1f/2f, 1f };
    const int outerIters = 5;
    const float lambdaI = 1e-3f;
    const float lambdaK = 1e-3f;
    const float smoothSigma = 1.0f;
    const float shockDt = 0.25f;
    const int shockPasses = 3;
    const float sparsityThreshold = 0.05f;

    float[,] kernel = InitDeltaKernel(windowSizes[0]);

    for (int level = 0; level < scales.Length; level++)
    {
        ct.ThrowIfCancellationRequested();
        float scale = scales[level];
        int windowSize = windowSizes[level];

        int lw = Math.Max(8, (int)Math.Round(input.Width  * scale));
        int lh = Math.Max(8, (int)Math.Round(input.Height * scale));
        var lumaAtScale = DownscaleLuma(luma, input.Width, input.Height, lw, lh);

        var dxBlurred = Gradients.ComputeX(lumaAtScale, lw, lh);
        var dyBlurred = Gradients.ComputeY(lumaAtScale, lw, lh);
        int fftSize = FftAdapter.NextPow2(Math.Max(lw, lh) + windowSize * 2);

        for (int iter = 0; iter < outerIters; iter++)
        {
            ct.ThrowIfCancellationRequested();

            // (a) Latent image via Tikhonov given the current kernel.
            var singleChannel = new ImageBuffer(lw, lh,
                (float[])lumaAtScale.Clone(), (float[])lumaAtScale.Clone(), (float[])lumaAtScale.Clone());
            var latentImg = new TikhonovDeconvolver().Apply(
                singleChannel, kernel, new DeconvolutionParams(K: lambdaI),
                PipelineOptions.Default with { LinearLight = false, EdgeTaper = false });
            var latent = latentImg.R; // all three channels are equal

            // (b) Edge prediction — Gaussian pre-smooth + shock filter.
            var predicted = GaussianSmooth.Apply(latent, lw, lh, smoothSigma);
            for (int pass = 0; pass < shockPasses; pass++)
                predicted = ShockFilter.ApplyOnce(predicted, lw, lh, shockDt);

            // (c) Gradient-domain kernel estimation.
            var dxLatent = Gradients.ComputeX(predicted, lw, lh);
            var dyLatent = Gradients.ComputeY(predicted, lw, lh);
            var rawKernel = KernelEstimation.EstimateGradientDomain(
                dxLatent, dyLatent, dxBlurred, dyBlurred, lw, lh, lambdaK, fftSize);

            // (d) Projection.
            kernel = KernelProjection.Project(rawKernel, windowSize, sparsityThreshold);
        }

        // Upscale kernel for the next level.
        if (level < scales.Length - 1)
        {
            int nextSize = windowSizes[level + 1];
            var upscaled = BilinearUpscaleKernel(kernel, windowSize, nextSize);
            kernel = KernelProjection.Project(upscaled, nextSize, sparsityThreshold);
        }
    }

    LastEstimatedKernel = kernel;

    // Final deblur: apply the recovered kernel to each color channel via Tikhonov.
    return new TikhonovDeconvolver().Apply(input, kernel, new DeconvolutionParams(K: lambdaI), opt);
}
```

Helpers:
- `ExtractLuma(ImageBuffer input) → float[]`: BT.601 `0.299·R + 0.587·G + 0.114·B` over the input; output size `input.Width * input.Height`.
- `DownscaleLuma(float[] luma, int srcW, int srcH, int dstW, int dstH) → float[]`: wrap luma in a single-channel `ImageBuffer` (R=G=B=luma), call `AreaResample.Box`, extract R. Or inline area-averaging.
- `InitDeltaKernel(int size) → float[,]`: centered 3×3 pattern normalized: center 0.9, four 4-connected neighbors 0.025 each. Wrap in `size × size` matrix, then normalize.
- `BilinearUpscaleKernel(float[,] kernel, int srcSize, int dstSize) → float[,]`: bilinear resample from `srcSize × srcSize` to `dstSize × dstSize`. Small helper — no need for a full external resize library.

- [ ] **Step 1: Write failing tests**

```csharp
// Deblur.Tests/BlindDeconvolutionDeconvolverTests.cs
using Deblur.Engine;
using Deblur.Engine.Validation;
using Deblur.Tests.TestHelpers;
using Xunit;

namespace Deblur.Tests;

public class BlindDeconvolutionDeconvolverTests
{
    [Fact]
    public void MotionKernelSimilarity_AboveThreshold()
    {
        var gt = SyntheticImages.TexturedNoise(256, 256, seed: 42);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 10f, 0f, 0f, 0f, AlgorithmType.BlindDeconvolution));
        var blurred = SyntheticBlur.Apply(gt, psf, gaussianNoiseSigma: 0f, seed: 42);

        var blind = new BlindDeconvolutionDeconvolver();
        _ = blind.Apply(blurred, new float[1, 1] { { 1f } }, new DeconvolutionParams(K: 1e-3f),
                        PipelineOptions.Default);

        var estimated = blind.LastEstimatedKernel;
        Assert.NotNull(estimated);
        float sim = CosineSimilarityAlignedByCentroid(psf, estimated!);
        Assert.True(sim > 0.6f, $"kernel cosine similarity {sim:F3} below 0.6");
    }

    [Fact]
    public void DefocusKernelSimilarity_AboveThreshold()
    {
        var gt = SyntheticImages.TexturedNoise(256, 256, seed: 42);
        var psf = new OutOfFocusBlurKernel().Build(
            new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0f, 5f, 0f, AlgorithmType.BlindDeconvolution));
        var blurred = SyntheticBlur.Apply(gt, psf, gaussianNoiseSigma: 0f, seed: 42);

        var blind = new BlindDeconvolutionDeconvolver();
        _ = blind.Apply(blurred, new float[1, 1] { { 1f } }, new DeconvolutionParams(K: 1e-3f),
                        PipelineOptions.Default);

        var estimated = blind.LastEstimatedKernel;
        Assert.NotNull(estimated);
        float sim = CosineSimilarityAlignedByCentroid(psf, estimated!);
        // Disc kernels have less directional structure; relax threshold slightly.
        Assert.True(sim > 0.5f, $"defocus kernel cosine similarity {sim:F3} below 0.5");
    }

    [Fact]
    public void SharpInput_RecoversNearDeltaKernel()
    {
        var gt = SyntheticImages.TexturedNoise(256, 256, seed: 42);
        var blind = new BlindDeconvolutionDeconvolver();
        _ = blind.Apply(gt, new float[1, 1] { { 1f } }, new DeconvolutionParams(K: 1e-3f),
                        PipelineOptions.Default);
        var k = blind.LastEstimatedKernel;
        Assert.NotNull(k);
        int center = k!.GetLength(0) / 2;
        float centerVal = k[center, center];
        float off = 0;
        for (int y = 0; y < k.GetLength(0); y++)
            for (int x = 0; x < k.GetLength(1); x++)
                if (y != center || x != center) off += k[y, x];
        Assert.True(centerVal > 0.5f, $"center pixel {centerVal:F3} not dominant");
        Assert.True(off < 0.5f, $"off-center sum {off:F3} too large");
    }

    [Fact]
    public void DeblurredImprovementOverBlurred_By3dB()
    {
        var gt = SyntheticImages.TexturedNoise(256, 256, seed: 42);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 45f, 8f, 0f, 0f, 0f, AlgorithmType.BlindDeconvolution));
        var blurred = SyntheticBlur.Apply(gt, psf, gaussianNoiseSigma: 0f, seed: 42);

        var deconv = new BlindDeconvolutionDeconvolver().Apply(
            blurred, new float[1, 1] { { 1f } }, new DeconvolutionParams(K: 1e-3f),
            PipelineOptions.Default);

        double blurredPsnr = Quality.Psnr(gt, blurred);
        double deconvPsnr  = Quality.Psnr(gt, deconv);
        Assert.True(deconvPsnr >= blurredPsnr + 3.0,
            $"blind did not improve by 3 dB: blurred {blurredPsnr:F2} -> deconv {deconvPsnr:F2}");
    }

    [Fact]
    public void KernelProperties_NonNegativeSumsToOne()
    {
        var gt = SyntheticImages.TexturedNoise(128, 128, seed: 42);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 0f, 6f, 0f, 0f, 0f, AlgorithmType.BlindDeconvolution));
        var blurred = SyntheticBlur.Apply(gt, psf, gaussianNoiseSigma: 0f, seed: 42);

        var blind = new BlindDeconvolutionDeconvolver();
        _ = blind.Apply(blurred, new float[1, 1] { { 1f } }, new DeconvolutionParams(K: 1e-3f),
                        PipelineOptions.Default);
        var k = blind.LastEstimatedKernel!;
        float sum = 0;
        for (int y = 0; y < k.GetLength(0); y++)
            for (int x = 0; x < k.GetLength(1); x++)
            {
                Assert.True(k[y, x] >= 0f);
                sum += k[y, x];
            }
        Assert.InRange(Math.Abs(sum - 1f), 0f, 1e-3f);
    }

    [Fact]
    public void PrecancelledToken_Throws()
    {
        var gt = SyntheticImages.TexturedNoise(128, 128, seed: 42);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var opts = PipelineOptions.Default with { CancellationToken = cts.Token };
        var blind = new BlindDeconvolutionDeconvolver();
        Assert.Throws<OperationCanceledException>(() =>
            blind.Apply(gt, new float[1, 1] { { 1f } }, new DeconvolutionParams(K: 1e-3f), opts));
    }

    private static float CosineSimilarityAlignedByCentroid(float[,] a, float[,] b)
    {
        // Center both by centroid, then cosine similarity on overlap.
        (float cyA, float cxA) = Centroid(a);
        (float cyB, float cxB) = Centroid(b);
        int radius = Math.Min(a.GetLength(0), b.GetLength(0)) / 2 - 1;

        double dot = 0, na = 0, nb = 0;
        for (int j = -radius; j <= radius; j++)
        {
            for (int i = -radius; i <= radius; i++)
            {
                int ay = (int)Math.Round(cyA + j), ax = (int)Math.Round(cxA + i);
                int by = (int)Math.Round(cyB + j), bx = (int)Math.Round(cxB + i);
                if (ay < 0 || ay >= a.GetLength(0) || ax < 0 || ax >= a.GetLength(1)) continue;
                if (by < 0 || by >= b.GetLength(0) || bx < 0 || bx >= b.GetLength(1)) continue;
                double va = a[ay, ax], vb = b[by, bx];
                dot += va * vb; na += va * va; nb += vb * vb;
            }
        }
        return na > 0 && nb > 0 ? (float)(dot / Math.Sqrt(na * nb)) : 0f;
    }

    private static (float y, float x) Centroid(float[,] k)
    {
        double sum = 0, wy = 0, wx = 0;
        for (int y = 0; y < k.GetLength(0); y++)
            for (int x = 0; x < k.GetLength(1); x++)
            {
                sum += k[y, x];
                wy += y * k[y, x];
                wx += x * k[y, x];
            }
        return sum > 0 ? ((float)(wy / sum), (float)(wx / sum))
                       : (k.GetLength(0) / 2f, k.GetLength(1) / 2f);
    }
}
```

- [ ] **Step 2: Implement the multi-scale Apply**

Fill in the outline from the "Algorithm outline" section above, plus the private helpers. Do NOT amend, force-push, or rebase.

If `MotionKernelSimilarity_AboveThreshold` fails with similarity `< 0.6`, do not relax silently. Escalate as DONE_WITH_CONCERNS with measured similarity and a hypothesis (e.g., "kernel projected too tightly; increasing 1/8-level window to 7 may help"). Similarly for the defocus test at 0.5. The identity-transform integrity check (`SharpInput_RecoversNearDeltaKernel`) is the safety net — if THAT fails, the algorithm is fundamentally broken and needs a re-think.

- [ ] **Step 3: Verify + commit**

```bash
dotnet test Deblur.sln --filter FullyQualifiedName~BlindDeconvolution
git add Deblur.Engine/BlindDeconvolutionDeconvolver.cs Deblur.Tests/BlindDeconvolutionDeconvolverTests.cs
git commit -m "Blind deconv: implement multi-scale MAP-alternating with gradient-domain kernel"
```

---

### Task 7: DeblurJobRunner integration

**Files:**
- Modify: `Deblur.Engine/DeblurJobRunner.cs` — add `BlindDeconvolution` to the `isIterativePreview` skip list.

- [ ] **Step 1: Update the skip list**

```csharp
// In WorkerLoop, extend the iterative-preview skip list.
bool isIterativePreview = p.Algorithm is
    AlgorithmType.RichardsonLucy
    or AlgorithmType.Landweber
    or AlgorithmType.BlindDeconvolution;
ImageBuffer deconv = (IsNoOp(p) || isIterativePreview) ? proxy : RunDeconvolve(proxy, p);
```

- [ ] **Step 2: Verify + commit**

```bash
dotnet test Deblur.sln
git add Deblur.Engine/DeblurJobRunner.cs
git commit -m "DeblurJobRunner: skip BlindDeconvolution in live preview"
```

---

### Task 8: VM wiring + PSF display + label converter

**Files:**
- Modify: `Deblur/ViewModels/MainViewModel.cs` — register `BlindDeconvolutionDeconvolver` in the deconvolvers dictionary; `[ObservableProperty] float[,]? _estimatedKernel`; after `EnsureFullResRenderedAsync` completes, read `_blindDeconvolver.LastEstimatedKernel` and assign to `EstimatedKernel`.
- Modify: `Deblur/Converters/AlgorithmToSmoothnessLabelConverter.cs` — add `BlindDeconvolution → "Iterations (fixed)"`.
- Create: `Deblur/Controls/PsfDisplay.xaml{,.cs}` — WPF UserControl that renders a `float[,]` kernel as a grayscale heatmap, upscaled nearest-neighbor to 128×128.
- Modify: `Deblur/MainWindow.xaml` — add `<controls:PsfDisplay Kernel="{Binding EstimatedKernel}" />` visible when `SelectedAlgorithm == BlindDeconvolution`.

### PsfDisplay control design

- Dependency property: `Kernel` (`float[,]?`), fires `PropertyChanged` → rebuild image.
- Renders to an `Image` child via `WriteableBitmap`.
- Layout: 128×128 fixed size, thin `#666` border, plus a 1-px `#F00` cross at the exact center (helps the examiner spot off-center motion).
- If `Kernel` is null, shows a `TextBlock` "No estimated PSF".

### Sidebar placement

Below the shared footer's Reset button, wrap in a `Visibility` binding on `SelectedAlgorithm == BlindDeconvolution` (using an existing bool-to-visibility converter or a new comparison converter).

- [ ] **Step 1: Register blind deconvolver in VM**

In the `MainViewModel` constructor's deconvolvers dictionary, add:

```csharp
[AlgorithmType.BlindDeconvolution] = _blindDeconvolver = new BlindDeconvolutionDeconvolver(),
```

Add a field `private readonly BlindDeconvolutionDeconvolver _blindDeconvolver;`.

- [ ] **Step 2: Add EstimatedKernel property + populate after render**

```csharp
[ObservableProperty] private float[,]? _estimatedKernel;
```

In `EnsureFullResRenderedAsync`, AFTER the `await _runner.RenderFullAsync(...)` completes:

```csharp
if (SelectedAlgorithm == AlgorithmType.BlindDeconvolution)
    EstimatedKernel = _blindDeconvolver.LastEstimatedKernel;
```

Also clear on load: in `LoadImageFromBytes` alongside the existing clears, add `EstimatedKernel = null;`.

- [ ] **Step 3: Extend label converter**

`AlgorithmToSmoothnessLabelConverter`:

```csharp
AlgorithmType.BlindDeconvolution => "Iterations (fixed)",
```

- [ ] **Step 4: Create PsfDisplay control**

```xml
<!-- Deblur/Controls/PsfDisplay.xaml -->
<UserControl x:Class="Deblur.Controls.PsfDisplay"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid Width="128" Height="128">
        <Border BorderBrush="#666" BorderThickness="1"/>
        <Image x:Name="KernelImage" Stretch="None"
               RenderOptions.BitmapScalingMode="NearestNeighbor"/>
        <Rectangle Width="8" Height="1" Fill="#F00"
                   HorizontalAlignment="Center" VerticalAlignment="Center"/>
        <Rectangle Width="1" Height="8" Fill="#F00"
                   HorizontalAlignment="Center" VerticalAlignment="Center"/>
        <TextBlock x:Name="EmptyText" Text="No estimated PSF"
                   Foreground="#888" FontSize="10"
                   HorizontalAlignment="Center" VerticalAlignment="Center"
                   Visibility="Collapsed"/>
    </Grid>
</UserControl>
```

```csharp
// Deblur/Controls/PsfDisplay.xaml.cs
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Deblur.Controls;

public partial class PsfDisplay : UserControl
{
    public static readonly DependencyProperty KernelProperty =
        DependencyProperty.Register(nameof(Kernel), typeof(float[,]), typeof(PsfDisplay),
            new PropertyMetadata(null, OnKernelChanged));

    public float[,]? Kernel
    {
        get => (float[,]?)GetValue(KernelProperty);
        set => SetValue(KernelProperty, value);
    }

    public PsfDisplay() { InitializeComponent(); }

    private static void OnKernelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((PsfDisplay)d).Rebuild();
    }

    private void Rebuild()
    {
        if (Kernel is null)
        {
            KernelImage.Source = null;
            EmptyText.Visibility = Visibility.Visible;
            return;
        }
        EmptyText.Visibility = Visibility.Collapsed;

        int kh = Kernel.GetLength(0), kw = Kernel.GetLength(1);
        // Normalize to [0,1] for display (kernels sum to 1 so pixel values are small).
        float max = 0;
        for (int y = 0; y < kh; y++)
            for (int x = 0; x < kw; x++)
                if (Kernel[y, x] > max) max = Kernel[y, x];
        float inv = max > 0 ? 1f / max : 1f;

        int display = 128;
        int cell = Math.Max(1, display / Math.Max(kw, kh));
        int outW = cell * kw;
        int outH = cell * kh;

        var pixels = new byte[outW * outH * 4];
        for (int oy = 0; oy < outH; oy++)
        {
            int ky = oy / cell;
            for (int ox = 0; ox < outW; ox++)
            {
                int kx = ox / cell;
                byte g = (byte)Math.Clamp((int)MathF.Round(Kernel[ky, kx] * inv * 255f), 0, 255);
                int p = (oy * outW + ox) * 4;
                pixels[p] = g; pixels[p + 1] = g; pixels[p + 2] = g; pixels[p + 3] = 255;
            }
        }
        var bmp = BitmapSource.Create(outW, outH, 96, 96, PixelFormats.Bgra32, null, pixels, outW * 4);
        KernelImage.Source = bmp;
    }
}
```

- [ ] **Step 5: Wire PsfDisplay into MainWindow.xaml**

In the shared footer, below the Reset button:

```xml
<controls:PsfDisplay Kernel="{Binding EstimatedKernel}"
                     Visibility="{Binding SelectedAlgorithm, Converter={StaticResource BlindAlgoToVis}}"
                     HorizontalAlignment="Left" Margin="0,12,0,4"/>
```

Add a small comparison converter `AlgorithmTypeToVisibilityConverter` that returns `Visible` when the value equals `AlgorithmType.BlindDeconvolution` — or reuse an existing pattern (there's already a `NullToVis` converter as reference). Register in App.xaml as `BlindAlgoToVis`.

- [ ] **Step 6: Verify + commit**

```bash
dotnet build Deblur.sln
dotnet test Deblur.sln
git add Deblur/ViewModels/MainViewModel.cs Deblur/Converters/AlgorithmToSmoothnessLabelConverter.cs Deblur/Controls Deblur/MainWindow.xaml Deblur/App.xaml
git commit -m "Wire BlindDeconvolution into VM + label converter + PsfDisplay sidebar control"
```

---

### Task 9: Manual smoke test + tag

- [ ] **Step 1: Build in Debug and launch**

```bash
dotnet build Deblur.sln
dotnet run --project Deblur/Deblur.csproj --no-build
```

- [ ] **Step 2: Manual smoke**

- Algorithm dropdown surfaces "BlindDeconvolution".
- Open a motion-blurred image. Pick blind. Render → progress bar for a few seconds, then output.
- PSF display in the sidebar shows a recognizable kernel: bright line for motion; bright disc for defocus.
- Load a sharp image. Pick blind. Render → PSF display shows a near-delta (bright center pixel).
- Cancel during render → stops within ~1 second.
- 16-bit input still exports 16-bit PNG under blind.
- Blind + ROI: enable ROI, draw a rectangle, render → kernel estimated on the ROI, output has the ROI sharpened.
- Undo/redo, save-as, arrow drag all still work.
- Live preview under blind = raw proxy (no deconvolution applied, IsPreviewComputing does not stick).
- Existing algorithms (Wiener, Tikhonov, TV, RL, CLS, Landweber) all still work.

Report smoke results in the ledger.

- [ ] **Step 3: Tag + update ledger**

```bash
git tag phase1e
```

- [ ] **Step 4: Invoke `superpowers:finishing-a-development-branch`**

Present the standard four options.
