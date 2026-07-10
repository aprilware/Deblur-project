# Deblur Phase 1.c Implementation Plan — Classical Iterative Algorithms

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract the shared FFT scaffolding out of Wiener/Tikhonov, add Richardson–Lucy, Constrained Least Squares, and Landweber deconvolvers on top of it, and wire them into the algorithm dropdown — all under the existing `IDeconvolver` interface with full `AlgorithmMetadata`.

**Architecture:** `FftDeconvolverBase` (abstract) owns padding, PSF centering, per-channel FFT + filter apply + iFFT + crop. Wiener, Tikhonov, and CLS become thin subclasses that only implement `BuildFilterResponse`. Iterative deconvolvers (RL, Landweber) use a new `FftConvolve` primitive for per-iteration convolutions with the PSF. All new deconvolvers ship `AlgorithmMetadata` for the audit-log/report track.

**Tech Stack:** .NET 8; `FftSharp`; `CommunityToolkit.Mvvm`; WPF (`net8.0-windows`, `UseWPF`); xUnit.

## Global Constraints

- .NET 8. `net8.0` for `Deblur.Engine` + `Deblur.Tests`. `net8.0-windows` + `UseWPF` for `Deblur` and `Deblur.Wpf.Tests`. Nullable + ImplicitUsings enabled.
- No new NuGet packages.
- `Deblur.Engine` stays UI-free.
- All 104 Phase 1.b tests remain green. Test count target after 1.c: ~125.
- The FFT-scaffold refactor must produce **near-exact** results vs. the pre-refactor Wiener/Tikhonov: max absolute per-channel difference ≤ `1e-5` (equivalently PSNR ≥ 100 dB). This is enforced by pinning the pre-refactor implementation as a reference in the regression test file.
- Every new algorithm's `Metadata`: `DescriptionMarkdown > 100 chars`, `LiteratureCitation > 20 chars`. Descriptions must accurately reflect the code — no aspirational or misleading claims. RL's metadata explicitly names "fractional-power under-relaxation" (not White 1994 damped RL).
- Improvement criterion for algorithm correctness tests: `deblurred PSNR-vs-GT ≥ blurred PSNR-vs-GT + 3 dB`. Absolute PSNR floors are NOT used (an identity transform would pass those — a subtle test-methodology bug from earlier phases).
- Fixed hyperparameters: RL `Iterations=30, Alpha=0.5, Accelerate=true`; Landweber `Iterations=100, Step=0.9`. No UI slider for these in this phase.
- `p.Smoothness` slider is ignored by RL and Landweber. Label converter shows "Iterations (fixed)".
- Phase 1.c branches from tag `phase1b` onto `phase1c-classical-iterative-algorithms` (already created).

---

### Task 1: Extract FftDeconvolverBase; refactor Wiener + Tikhonov

**Files:**
- Create: `Deblur.Engine/FftDeconvolverBase.cs`
- Modify: `Deblur.Engine/WienerDeconvolver.cs` (extends base; keeps public constructor)
- Modify: `Deblur.Engine/TikhonovDeconvolver.cs` (extends base; keeps public constructor)
- Test:   `Deblur.Tests/FftDeconvolverRefactorRegressionTests.cs` (new)

**Interfaces:**
- Consumes: `PipelineOptions`, `BoundaryFill`, `EdgeTaper`, `FftAdapter`.
- Produces:
  - `abstract class FftDeconvolverBase : IDeconvolver` with `abstract AlgorithmMetadata Metadata`, `abstract Complex[,] BuildFilterResponse(Complex[,] H, DeconvolutionParams p, int fftSize)`, and concrete `Apply(...)`.
  - `WienerDeconvolver` and `TikhonovDeconvolver` public shape unchanged (same constructors, same `Apply` signature via base).

### Regression contract

The regression test file pins the pre-refactor implementation as `LegacyWienerReference` / `LegacyTikhonovReference` private static classes and asserts the refactored deconvolvers produce identical output. Max absolute per-channel diff ≤ `1e-5`.

- [ ] **Step 1: Write the failing regression tests (BEFORE the refactor)**

Create the file with the current-code copies pinned inline. The `[Fact]` methods will pass initially (since we compare current-code to current-code) but they lock in the reference for the refactor:

```csharp
// Deblur.Tests/FftDeconvolverRefactorRegressionTests.cs
using System.Numerics;
using Deblur.Engine;
using Deblur.Engine.Color;
using Deblur.Tests.TestHelpers;
using Xunit;

namespace Deblur.Tests;

public class FftDeconvolverRefactorRegressionTests
{
    [Fact]
    public void WienerRefactor_ProducesNearExactSameOutput()
    {
        var input = SyntheticImages.Checkerboard(64, 64, 8);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 8f, 0f, 0f, 0f, AlgorithmType.Wiener));
        var opts = PipelineOptions.Default with { LinearLight = false, EdgeTaper = false };
        var p = new DeconvolutionParams(K: 0.005f);

        var expected = LegacyWienerReference.Apply(input, psf, p, opts);
        var actual = new WienerDeconvolver().Apply(input, psf, p, opts);
        AssertNearExact(expected, actual, tol: 1e-5f);
    }

    [Fact]
    public void TikhonovRefactor_ProducesNearExactSameOutput()
    {
        var input = SyntheticImages.Checkerboard(64, 64, 8);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 8f, 0f, 0f, 0f, AlgorithmType.Tikhonov));
        var opts = PipelineOptions.Default with { LinearLight = false, EdgeTaper = false };
        var p = new DeconvolutionParams(K: 0.005f);

        var expected = LegacyTikhonovReference.Apply(input, psf, p, opts);
        var actual = new TikhonovDeconvolver().Apply(input, psf, p, opts);
        AssertNearExact(expected, actual, tol: 1e-5f);
    }

    private static void AssertNearExact(ImageBuffer expected, ImageBuffer actual, float tol)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        float maxDiff = 0f;
        for (int i = 0; i < expected.PixelCount; i++)
        {
            maxDiff = Math.Max(maxDiff, Math.Abs(expected.R[i] - actual.R[i]));
            maxDiff = Math.Max(maxDiff, Math.Abs(expected.G[i] - actual.G[i]));
            maxDiff = Math.Max(maxDiff, Math.Abs(expected.B[i] - actual.B[i]));
        }
        Assert.True(maxDiff <= tol, $"max abs diff {maxDiff:E} > tolerance {tol:E}");
    }

    // Pinned copy of the pre-refactor WienerDeconvolver — the reference the
    // refactored implementation must match. Do NOT edit this to fix a failure;
    // if you're changing this, you're changing algorithm behavior and the
    // phase-1.a linear-light-gain test would tell you the same thing.
    private static class LegacyWienerReference
    {
        public static ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p, PipelineOptions opt)
        {
            int psfH = psf.GetLength(0);
            int psfW = psf.GetLength(1);
            int pad = Math.Max(psfW, psfH) / 2 + 1;

            int paddedW = input.Width + 2 * pad;
            int paddedH = input.Height + 2 * pad;
            int fftSize = FftAdapter.NextPow2(Math.Max(paddedW, paddedH));

            var psfBuf = new float[fftSize, fftSize];
            int cy = psfH / 2, cx = psfW / 2;
            for (int y = 0; y < psfH; y++)
                for (int x = 0; x < psfW; x++)
                {
                    int dy = (y - cy + fftSize) % fftSize;
                    int dx = (x - cx + fftSize) % fftSize;
                    psfBuf[dy, dx] = psf[y, x];
                }
            var H = FftAdapter.Forward2D(psfBuf);

            var wienerNumer = new Complex[fftSize, fftSize];
            for (int y = 0; y < fftSize; y++)
                for (int x = 0; x < fftSize; x++)
                {
                    var h = H[y, x];
                    double mag2 = h.Real * h.Real + h.Imaginary * h.Imaginary;
                    wienerNumer[y, x] = Complex.Conjugate(h) / (mag2 + p.K);
                }

            float[] outR = ProcessChannel(input.R, input.Width, input.Height, pad, fftSize, wienerNumer, opt);
            float[] outG = ProcessChannel(input.G, input.Width, input.Height, pad, fftSize, wienerNumer, opt);
            float[] outB = ProcessChannel(input.B, input.Width, input.Height, pad, fftSize, wienerNumer, opt);
            return new ImageBuffer(input.Width, input.Height, outR, outG, outB);
        }

        private static float[] ProcessChannel(float[] channel, int w, int h, int pad, int fftSize, Complex[,] wienerNumer, PipelineOptions opt)
        {
            var padded = BoundaryFill.Pad(channel, w, h, pad, fftSize, opt.BoundaryMode);
            if (opt.EdgeTaper) EdgeTaper.ApplyInPlace(padded, pad);

            var G = FftAdapter.Forward2D(padded);
            var Fhat = new Complex[fftSize, fftSize];
            for (int y = 0; y < fftSize; y++)
                for (int x = 0; x < fftSize; x++)
                    Fhat[y, x] = wienerNumer[y, x] * G[y, x];

            var real = FftAdapter.Inverse2DReal(Fhat);

            var result = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float v = real[y + pad, x + pad];
                    if (!float.IsFinite(v)) v = 0f;
                    result[y * w + x] = Math.Clamp(v, 0f, 1f);
                }
            return result;
        }
    }

    // Pinned copy of the pre-refactor TikhonovDeconvolver.
    private static class LegacyTikhonovReference
    {
        public static ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p, PipelineOptions opt)
        {
            int psfH = psf.GetLength(0);
            int psfW = psf.GetLength(1);
            int pad = Math.Max(psfW, psfH) / 2 + 1;

            int paddedW = input.Width + 2 * pad;
            int paddedH = input.Height + 2 * pad;
            int fftSize = FftAdapter.NextPow2(Math.Max(paddedW, paddedH));

            var psfBuf = new float[fftSize, fftSize];
            int cy = psfH / 2, cx = psfW / 2;
            for (int y = 0; y < psfH; y++)
                for (int x = 0; x < psfW; x++)
                {
                    int dy = (y - cy + fftSize) % fftSize;
                    int dx = (x - cx + fftSize) % fftSize;
                    psfBuf[dy, dx] = psf[y, x];
                }
            var H = FftAdapter.Forward2D(psfBuf);

            var tikhonovNumer = new Complex[fftSize, fftSize];
            for (int y = 0; y < fftSize; y++)
            {
                double Cv = 2.0 - 2.0 * Math.Cos(2.0 * Math.PI * y / fftSize);
                for (int x = 0; x < fftSize; x++)
                {
                    double Cu = 2.0 - 2.0 * Math.Cos(2.0 * Math.PI * x / fftSize);
                    double cSq = (Cu + Cv) * (Cu + Cv);
                    var h = H[y, x];
                    double mag2 = h.Real * h.Real + h.Imaginary * h.Imaginary;
                    tikhonovNumer[y, x] = Complex.Conjugate(h) / (mag2 + p.K * cSq);
                }
            }

            float[] outR = ProcessChannel(input.R, input.Width, input.Height, pad, fftSize, tikhonovNumer, opt);
            float[] outG = ProcessChannel(input.G, input.Width, input.Height, pad, fftSize, tikhonovNumer, opt);
            float[] outB = ProcessChannel(input.B, input.Width, input.Height, pad, fftSize, tikhonovNumer, opt);
            return new ImageBuffer(input.Width, input.Height, outR, outG, outB);
        }

        private static float[] ProcessChannel(float[] channel, int w, int h, int pad, int fftSize, Complex[,] tikhonovNumer, PipelineOptions opt)
        {
            var padded = BoundaryFill.Pad(channel, w, h, pad, fftSize, opt.BoundaryMode);
            if (opt.EdgeTaper) EdgeTaper.ApplyInPlace(padded, pad);

            var G = FftAdapter.Forward2D(padded);
            var Fhat = new Complex[fftSize, fftSize];
            for (int y = 0; y < fftSize; y++)
                for (int x = 0; x < fftSize; x++)
                    Fhat[y, x] = tikhonovNumer[y, x] * G[y, x];

            var real = FftAdapter.Inverse2DReal(Fhat);

            var result = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float v = real[y + pad, x + pad];
                    if (!float.IsFinite(v)) v = 0f;
                    result[y * w + x] = Math.Clamp(v, 0f, 1f);
                }
            return result;
        }
    }
}
```

- [ ] **Step 2: Verify the tests pass against the CURRENT implementations**

Run: `dotnet test Deblur.sln --filter FullyQualifiedName~FftDeconvolverRefactorRegression`
Expected: both pass (comparing current-code to current-code). This locks in the reference behavior.

- [ ] **Step 3: Implement `FftDeconvolverBase`**

```csharp
// Deblur.Engine/FftDeconvolverBase.cs
using System.Numerics;

namespace Deblur.Engine;

public abstract class FftDeconvolverBase : IDeconvolver
{
    public abstract AlgorithmMetadata Metadata { get; }

    /// <summary>
    /// Compute the per-frequency multiplier applied to the input's Fourier transform.
    /// Called once per Apply() with the PSF's Fourier transform H and the fftSize.
    /// </summary>
    protected abstract Complex[,] BuildFilterResponse(Complex[,] H, DeconvolutionParams p, int fftSize);

    public ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p, PipelineOptions? options = null)
    {
        var opt = options ?? PipelineOptions.Default;
        int psfH = psf.GetLength(0);
        int psfW = psf.GetLength(1);
        int pad = Math.Max(psfW, psfH) / 2 + 1;

        int paddedW = input.Width + 2 * pad;
        int paddedH = input.Height + 2 * pad;
        int fftSize = FftAdapter.NextPow2(Math.Max(paddedW, paddedH));

        var psfBuf = new float[fftSize, fftSize];
        int cy = psfH / 2, cx = psfW / 2;
        for (int y = 0; y < psfH; y++)
            for (int x = 0; x < psfW; x++)
            {
                int dy = (y - cy + fftSize) % fftSize;
                int dx = (x - cx + fftSize) % fftSize;
                psfBuf[dy, dx] = psf[y, x];
            }
        var H = FftAdapter.Forward2D(psfBuf);
        var filter = BuildFilterResponse(H, p, fftSize);

        float[] outR = ProcessChannel(input.R, input.Width, input.Height, pad, fftSize, filter, opt);
        float[] outG = ProcessChannel(input.G, input.Width, input.Height, pad, fftSize, filter, opt);
        float[] outB = ProcessChannel(input.B, input.Width, input.Height, pad, fftSize, filter, opt);
        return new ImageBuffer(input.Width, input.Height, outR, outG, outB);
    }

    private static float[] ProcessChannel(
        float[] channel, int w, int h, int pad, int fftSize, Complex[,] filter, PipelineOptions opt)
    {
        var padded = BoundaryFill.Pad(channel, w, h, pad, fftSize, opt.BoundaryMode);
        if (opt.EdgeTaper) EdgeTaper.ApplyInPlace(padded, pad);

        var G = FftAdapter.Forward2D(padded);
        var Fhat = new Complex[fftSize, fftSize];
        for (int y = 0; y < fftSize; y++)
            for (int x = 0; x < fftSize; x++)
                Fhat[y, x] = filter[y, x] * G[y, x];

        var real = FftAdapter.Inverse2DReal(Fhat);

        var result = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float v = real[y + pad, x + pad];
                if (!float.IsFinite(v)) v = 0f;
                result[y * w + x] = Math.Clamp(v, 0f, 1f);
            }
        return result;
    }
}
```

- [ ] **Step 4: Refactor `WienerDeconvolver`**

Full replacement:

```csharp
// Deblur.Engine/WienerDeconvolver.cs
using System.Numerics;

namespace Deblur.Engine;

public sealed class WienerDeconvolver : FftDeconvolverBase
{
    public override AlgorithmMetadata Metadata { get; } = new(
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

    protected override Complex[,] BuildFilterResponse(Complex[,] H, DeconvolutionParams p, int fftSize)
    {
        var filter = new Complex[fftSize, fftSize];
        for (int y = 0; y < fftSize; y++)
            for (int x = 0; x < fftSize; x++)
            {
                var h = H[y, x];
                double mag2 = h.Real * h.Real + h.Imaginary * h.Imaginary;
                filter[y, x] = Complex.Conjugate(h) / (mag2 + p.K);
            }
        return filter;
    }
}
```

- [ ] **Step 5: Refactor `TikhonovDeconvolver`**

Full replacement:

```csharp
// Deblur.Engine/TikhonovDeconvolver.cs
using System.Numerics;

namespace Deblur.Engine;

public sealed class TikhonovDeconvolver : FftDeconvolverBase
{
    public override AlgorithmMetadata Metadata { get; } = new(
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

    protected override Complex[,] BuildFilterResponse(Complex[,] H, DeconvolutionParams p, int fftSize)
    {
        var filter = new Complex[fftSize, fftSize];
        for (int y = 0; y < fftSize; y++)
        {
            double Cv = 2.0 - 2.0 * Math.Cos(2.0 * Math.PI * y / fftSize);
            for (int x = 0; x < fftSize; x++)
            {
                double Cu = 2.0 - 2.0 * Math.Cos(2.0 * Math.PI * x / fftSize);
                double cSq = (Cu + Cv) * (Cu + Cv);
                var h = H[y, x];
                double mag2 = h.Real * h.Real + h.Imaginary * h.Imaginary;
                filter[y, x] = Complex.Conjugate(h) / (mag2 + p.K * cSq);
            }
        }
        return filter;
    }
}
```

- [ ] **Step 6: Verify all tests still pass**

Run: `dotnet test Deblur.sln`
Expected: 104 pre-existing + 2 new refactor-regression = 106 tests, all pass. The refactor-regression tests compare the refactored classes to the pinned reference implementations — must match within 1e-5.

If ANY pre-existing test's PSNR/SSIM threshold shifts, the refactor is NOT byte-identical and needs investigation. Do NOT relax thresholds silently.

- [ ] **Step 7: Commit**

```bash
git add Deblur.Engine/FftDeconvolverBase.cs Deblur.Engine/WienerDeconvolver.cs Deblur.Engine/TikhonovDeconvolver.cs Deblur.Tests/FftDeconvolverRefactorRegressionTests.cs
git commit -m "Extract FftDeconvolverBase; refactor Wiener + Tikhonov onto it"
```

---

### Task 2: FftConvolve primitives

**Files:**
- Create: `Deblur.Engine/Fft/FftConvolve.cs`
- Test:   `Deblur.Tests/FftConvolveTests.cs`

**Interfaces:**
- Consumes: `BoundaryFill`, `FftAdapter`.
- Produces:
  - `static class FftConvolve` with:
    - `float[] Convolve(float[] channel, int w, int h, float[,] psf, BoundaryMode mode)` — result[i,j] = Σ_{u,v} channel[i-u, j-v] · psf[u, v].
    - `float[] Correlate(float[] channel, int w, int h, float[,] psf, BoundaryMode mode)` — adjoint: Σ_{u,v} channel[i+u, j+v] · psf[u, v].

Both use FFT internally: pad channel to `NextPow2(w + 2*pad, h + 2*pad)`, FFT the padded channel and the centered PSF, multiply (Convolve) or multiply by conjugate (Correlate), inverse FFT, crop, NaN-guard (but do NOT clamp — iterative methods handle their own clamping).

- [ ] **Step 1: Write failing tests**

```csharp
// Deblur.Tests/FftConvolveTests.cs
using Deblur.Engine;
using Deblur.Engine.Fft;
using Xunit;

namespace Deblur.Tests;

public class FftConvolveTests
{
    [Fact]
    public void Convolve_IdentityKernel_ReturnsInput()
    {
        var input = MakeGradient(16, 16);
        var identity = new float[1, 1] { { 1f } };
        var result = FftConvolve.Convolve(input, 16, 16, identity, BoundaryMode.Reflect);
        for (int i = 0; i < input.Length; i++)
            Assert.InRange(Math.Abs(result[i] - input[i]), 0f, 1e-4f);
    }

    [Fact]
    public void Correlate_IdentityKernel_ReturnsInput()
    {
        var input = MakeGradient(16, 16);
        var identity = new float[1, 1] { { 1f } };
        var result = FftConvolve.Correlate(input, 16, 16, identity, BoundaryMode.Reflect);
        for (int i = 0; i < input.Length; i++)
            Assert.InRange(Math.Abs(result[i] - input[i]), 0f, 1e-4f);
    }

    [Fact]
    public void Convolve_UniformKernel_SmoothsInput()
    {
        var input = MakeGradient(32, 32);
        var box = new float[5, 5];
        for (int y = 0; y < 5; y++) for (int x = 0; x < 5; x++) box[y, x] = 1f / 25f;
        var result = FftConvolve.Convolve(input, 32, 32, box, BoundaryMode.Reflect);
        // Smoothing reduces gradient magnitude in the interior.
        double srcGrad = 0, resGrad = 0;
        for (int y = 8; y < 24; y++)
            for (int x = 8; x < 23; x++)
            {
                int i = y * 32 + x;
                srcGrad += Math.Abs(input[i + 1] - input[i]);
                resGrad += Math.Abs(result[i + 1] - result[i]);
            }
        Assert.True(resGrad < srcGrad * 0.7,
            $"box filter did not smooth: src {srcGrad:F3} → res {resGrad:F3}");
    }

    [Fact]
    public void ConvolveThenCorrelate_ApproximatesAutocorrelation()
    {
        // <Ah, h*A> = <h, A^T A h>  — convolve then correlate with the same PSF is A^T A.
        // For a shift-invariant PSF, this is an autocorrelation-shaped smoothing.
        // We just check the operation completes without NaN and is not the identity.
        var input = MakeGradient(32, 32);
        var psf = new float[5, 5];
        for (int y = 0; y < 5; y++) for (int x = 0; x < 5; x++)
            psf[y, x] = MathF.Exp(-((x - 2) * (x - 2) + (y - 2) * (y - 2)) / 4f);
        // Normalize.
        float sum = 0; for (int y = 0; y < 5; y++) for (int x = 0; x < 5; x++) sum += psf[y, x];
        for (int y = 0; y < 5; y++) for (int x = 0; x < 5; x++) psf[y, x] /= sum;

        var conv = FftConvolve.Convolve(input, 32, 32, psf, BoundaryMode.Reflect);
        var back = FftConvolve.Correlate(conv, 32, 32, psf, BoundaryMode.Reflect);
        for (int i = 0; i < back.Length; i++)
            Assert.True(float.IsFinite(back[i]), $"NaN/Inf at index {i}");
        // Not identity: some smoothing happened.
        double diff = 0;
        for (int i = 0; i < back.Length; i++) diff += Math.Abs(back[i] - input[i]);
        Assert.True(diff > 0.1, "convolve-then-correlate produced the identity");
    }

    private static float[] MakeGradient(int w, int h)
    {
        var b = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                b[y * w + x] = (float)(x + y) / (w + h - 2);
        return b;
    }
}
```

- [ ] **Step 2: Verify tests fail**

Run: `dotnet test Deblur.sln --filter FullyQualifiedName~FftConvolveTests`
Expected: FAIL (FftConvolve not defined).

- [ ] **Step 3: Implement `FftConvolve`**

```csharp
// Deblur.Engine/Fft/FftConvolve.cs
using System.Numerics;

namespace Deblur.Engine.Fft;

public static class FftConvolve
{
    public static float[] Convolve(float[] channel, int w, int h, float[,] psf, BoundaryMode mode)
        => Apply(channel, w, h, psf, mode, conjugate: false);

    public static float[] Correlate(float[] channel, int w, int h, float[,] psf, BoundaryMode mode)
        => Apply(channel, w, h, psf, mode, conjugate: true);

    private static float[] Apply(float[] channel, int w, int h, float[,] psf, BoundaryMode mode, bool conjugate)
    {
        int psfH = psf.GetLength(0);
        int psfW = psf.GetLength(1);
        int pad = Math.Max(psfW, psfH) / 2 + 1;
        int paddedW = w + 2 * pad;
        int paddedH = h + 2 * pad;
        int fftSize = FftAdapter.NextPow2(Math.Max(paddedW, paddedH));

        var psfBuf = new float[fftSize, fftSize];
        int cy = psfH / 2, cx = psfW / 2;
        for (int y = 0; y < psfH; y++)
            for (int x = 0; x < psfW; x++)
            {
                int dy = (y - cy + fftSize) % fftSize;
                int dx = (x - cx + fftSize) % fftSize;
                psfBuf[dy, dx] = psf[y, x];
            }
        var H = FftAdapter.Forward2D(psfBuf);

        var padded = BoundaryFill.Pad(channel, w, h, pad, fftSize, mode);
        var G = FftAdapter.Forward2D(padded);
        var Fhat = new Complex[fftSize, fftSize];
        for (int y = 0; y < fftSize; y++)
            for (int x = 0; x < fftSize; x++)
                Fhat[y, x] = (conjugate ? Complex.Conjugate(H[y, x]) : H[y, x]) * G[y, x];

        var real = FftAdapter.Inverse2DReal(Fhat);
        var result = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float v = real[y + pad, x + pad];
                if (!float.IsFinite(v)) v = 0f;
                result[y * w + x] = v;   // no clamp — iterative callers handle it
            }
        return result;
    }
}
```

- [ ] **Step 4: Verify tests pass**

Run: `dotnet test Deblur.sln --filter FullyQualifiedName~FftConvolveTests`
Expected: 4 pass. Full suite → 110.

- [ ] **Step 5: Commit**

```bash
git add Deblur.Engine/Fft/FftConvolve.cs Deblur.Tests/FftConvolveTests.cs
git commit -m "Add FftConvolve: Convolve + Correlate primitives for iterative deconvolvers"
```

---

### Task 3: Richardson–Lucy deconvolver

**Files:**
- Create: `Deblur.Engine/RichardsonLucyDeconvolver.cs`
- Test:   `Deblur.Tests/RichardsonLucyDeconvolverTests.cs`

**Interfaces:**
- Consumes: `FftConvolve`, `PipelineOptions`, `AlgorithmMetadata`, `IDeconvolver`.
- Produces:
  - `sealed class RichardsonLucyDeconvolver : IDeconvolver` implementing per-channel RL with fractional-power under-relaxation and Biggs–Andrews acceleration. Fixed hyperparameters: `Iterations = 30`, `Alpha = 0.5`, `Accelerate = true`.

### Algorithm

Per channel:

```
x_0 = clamp(y, eps, 1)                    // eps = 1e-6 to avoid div-by-zero
h_flipped = flip(psf) — same as adjoint via FftConvolve.Correlate
for k in [0, Iterations):
    Hx = FftConvolve.Convolve(x_k, w, h, psf, Reflect)
    Hx = max(Hx, eps)                     // avoid div-by-zero
    ratio = y / Hx                        // per-pixel
    correction = FftConvolve.Correlate(ratio, w, h, psf, Reflect)
    correction_relaxed = pow(max(correction, eps), Alpha)  // under-relaxation
    x_{k+1} = clamp(x_k * correction_relaxed, 0, 1)

if Accelerate:
    apply Biggs–Andrews momentum between iterations (see below)
```

Biggs–Andrews acceleration (a common momentum-style extrapolation for RL):

```
Maintain previous iterate x_{k-1}. Before computing the k-th correction:
    beta_k = <g_k, g_{k-1}> / <g_{k-1}, g_{k-1}> clamped to [0, 1]
              where g_k = x_k - x_{k-1}
    y_k = x_k + beta_k * (x_k - x_{k-1})  // extrapolate
Run one RL step from y_k instead of x_k.
```

For the first two iterations, run vanilla RL (no acceleration until k>=2). Reference: Biggs & Andrews (1997), "Acceleration of iterative image restoration algorithms."

- [ ] **Step 1: Write failing tests**

```csharp
// Deblur.Tests/RichardsonLucyDeconvolverTests.cs
using Deblur.Engine;
using Deblur.Engine.Validation;
using Deblur.Tests.TestHelpers;
using Xunit;

namespace Deblur.Tests;

public class RichardsonLucyDeconvolverTests
{
    [Fact]
    public void MotionRoundTrip_BeatsBlurredBy3dB()
    {
        var gt = SyntheticImages.Checkerboard(128, 128, 16);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 8f, 0f, 0f, 0f, AlgorithmType.RichardsonLucy));
        var blurred = SyntheticBlur.Apply(gt, psf, gaussianNoiseSigma: 0f, seed: 42);
        var deconv = new RichardsonLucyDeconvolver().Apply(
            blurred, psf, new DeconvolutionParams(K: 0.005f), PipelineOptions.Default);

        double blurredPsnr = Quality.Psnr(gt, blurred);
        double deconvPsnr = Quality.Psnr(gt, deconv);
        Assert.True(deconvPsnr >= blurredPsnr + 3.0,
            $"RL did not improve by 3 dB: blurred {blurredPsnr:F2} → deconv {deconvPsnr:F2}");
    }

    [Fact]
    public void IdentityTransform_FailsImprovementCriterion()
    {
        // Test-methodology integrity: verify the 3-dB improvement criterion
        // correctly REJECTS a no-op. If this ever passes with an identity
        // transform, the criterion is broken.
        var gt = SyntheticImages.Checkerboard(128, 128, 16);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 8f, 0f, 0f, 0f, AlgorithmType.RichardsonLucy));
        var blurred = SyntheticBlur.Apply(gt, psf, gaussianNoiseSigma: 0f, seed: 42);
        var identity = blurred.Clone();

        double blurredPsnr = Quality.Psnr(gt, blurred);
        double identityPsnr = Quality.Psnr(gt, identity);
        // Identity should give the SAME PSNR — NOT an improvement.
        Assert.False(identityPsnr >= blurredPsnr + 3.0,
            $"criterion accepted an identity transform: {identityPsnr:F2} vs {blurredPsnr:F2}");
    }

    [Fact]
    public void NoAcceleration_MonotonicConvergenceOnPsnr()
    {
        // Basic RL is provably monotonic in the log-likelihood; PSNR-vs-GT should
        // be non-decreasing when noise is absent and the model matches.
        var gt = SyntheticImages.Checkerboard(64, 64, 8);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 6f, 0f, 0f, 0f, AlgorithmType.RichardsonLucy));
        var blurred = SyntheticBlur.Apply(gt, psf, gaussianNoiseSigma: 0f, seed: 7);

        double prev = double.NegativeInfinity;
        foreach (int iters in new[] { 1, 3, 5, 10, 20 })
        {
            var deconv = new RichardsonLucyDeconvolver(iterations: iters, alpha: 1.0f, accelerate: false)
                .Apply(blurred, psf, new DeconvolutionParams(K: 0.005f), PipelineOptions.Default);
            double psnr = Quality.Psnr(gt, deconv);
            Assert.True(psnr >= prev - 1e-3, $"non-monotonic at iters={iters}: {prev:F3} → {psnr:F3}");
            prev = psnr;
        }
    }

    [Fact]
    public void Accelerated_Iter30BeatsIter5BeatsIter1()
    {
        // Accelerated RL can zigzag between adjacent iterations, so we only assert
        // the ordering iter30 > iter5 > iter1 (loose long-term progression).
        var gt = SyntheticImages.Checkerboard(64, 64, 8);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 6f, 0f, 0f, 0f, AlgorithmType.RichardsonLucy));
        var blurred = SyntheticBlur.Apply(gt, psf, gaussianNoiseSigma: 0f, seed: 7);

        double PsnrAt(int iters) => Quality.Psnr(
            gt,
            new RichardsonLucyDeconvolver(iterations: iters, alpha: 0.5f, accelerate: true)
                .Apply(blurred, psf, new DeconvolutionParams(K: 0.005f), PipelineOptions.Default));

        double p1 = PsnrAt(1), p5 = PsnrAt(5), p30 = PsnrAt(30);
        Assert.True(p30 > p5, $"iter30 ({p30:F2}) not > iter5 ({p5:F2})");
        Assert.True(p5 > p1, $"iter5 ({p5:F2}) not > iter1 ({p1:F2})");
    }

    [Fact]
    public void ExtremeParams_NoNaNOrInfInOutput()
    {
        var input = SyntheticImages.Checkerboard(64, 64, 4);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 22f, 100f, 0f, 0f, 0f, AlgorithmType.RichardsonLucy));
        var deconv = new RichardsonLucyDeconvolver().Apply(
            input, psf, new DeconvolutionParams(K: 1e-6f), PipelineOptions.Default);
        for (int i = 0; i < deconv.PixelCount; i++)
        {
            Assert.False(float.IsNaN(deconv.R[i]) || float.IsInfinity(deconv.R[i]));
            Assert.False(float.IsNaN(deconv.G[i]) || float.IsInfinity(deconv.G[i]));
            Assert.False(float.IsNaN(deconv.B[i]) || float.IsInfinity(deconv.B[i]));
        }
    }
}
```

- [ ] **Step 2: Verify tests fail**

Run: `dotnet test Deblur.sln --filter FullyQualifiedName~RichardsonLucyDeconvolverTests`
Expected: FAIL (RichardsonLucyDeconvolver not defined).

- [ ] **Step 3: Implement `RichardsonLucyDeconvolver`**

```csharp
// Deblur.Engine/RichardsonLucyDeconvolver.cs
using Deblur.Engine.Fft;

namespace Deblur.Engine;

public sealed class RichardsonLucyDeconvolver : IDeconvolver
{
    private const float Eps = 1e-6f;
    private readonly int _iterations;
    private readonly float _alpha;
    private readonly bool _accelerate;

    public AlgorithmMetadata Metadata { get; } = new(
        Id: "richardson-lucy",
        Version: "1.0",
        DisplayName: "Richardson-Lucy (accelerated, under-relaxed)",
        DescriptionMarkdown:
            "Richardson-Lucy is an iterative maximum-likelihood deconvolver under a " +
            "Poisson-noise model. Each iteration applies a multiplicative correction " +
            "x_{k+1} = x_k * H^T(y / (H*x_k))^alpha, where alpha in (0, 1] under-relaxes " +
            "the update to reduce noise amplification. This is fractional-power " +
            "under-relaxation, NOT White (1994) damped RL (which uses a residual-thresholded " +
            "damping mask). Biggs-Andrews momentum-style extrapolation accelerates convergence.",
        LiteratureCitation:
            "Richardson, W.H. (1972). Bayesian-based iterative method of image restoration. " +
            "J. Opt. Soc. Am. 62, 55-59. Lucy, L.B. (1974). Astron. J. 79, 745. " +
            "Biggs, D.S.C. & Andrews, M. (1997). Applied Optics 36, 1766.");

    public RichardsonLucyDeconvolver(int iterations = 30, float alpha = 0.5f, bool accelerate = true)
    {
        if (iterations < 1) throw new ArgumentOutOfRangeException(nameof(iterations));
        if (alpha <= 0f || alpha > 1f) throw new ArgumentOutOfRangeException(nameof(alpha));
        _iterations = iterations;
        _alpha = alpha;
        _accelerate = accelerate;
    }

    public ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p, PipelineOptions? options = null)
    {
        _ = options ?? PipelineOptions.Default; // options currently unused inside RL
        int w = input.Width, h = input.Height;
        return new ImageBuffer(w, h,
            ProcessChannel(input.R, w, h, psf),
            ProcessChannel(input.G, w, h, psf),
            ProcessChannel(input.B, w, h, psf));
    }

    private float[] ProcessChannel(float[] y, int w, int h, float[,] psf)
    {
        int n = y.Length;
        var x = (float[])y.Clone();
        var xPrev = (float[])x.Clone();
        var xPrevPrev = (float[])x.Clone();

        for (int k = 0; k < _iterations; k++)
        {
            // Biggs-Andrews extrapolation for k >= 2.
            // beta = <x_k - x_{k-1}, x_{k-1} - x_{k-2}> / <x_{k-1} - x_{k-2}, x_{k-1} - x_{k-2}>
            // Requires tracking two iterations back (xPrevPrev), not just one.
            float[] xStart;
            if (_accelerate && k >= 2)
            {
                double num = 0, den = 0;
                for (int i = 0; i < n; i++)
                {
                    float d  = x[i]     - xPrev[i];        // x_k - x_{k-1}
                    float dP = xPrev[i] - xPrevPrev[i];    // x_{k-1} - x_{k-2}
                    num += d  * dP;
                    den += dP * dP;
                }
                float beta = den > 0 ? (float)Math.Clamp(num / den, 0.0, 1.0) : 0f;
                xStart = new float[n];
                for (int i = 0; i < n; i++)
                    xStart[i] = Math.Clamp(x[i] + beta * (x[i] - xPrev[i]), 0f, 1f);
            }
            else
            {
                xStart = x;
            }

            var Hx = FftConvolve.Convolve(xStart, w, h, psf, BoundaryMode.Reflect);
            var ratio = new float[n];
            for (int i = 0; i < n; i++) ratio[i] = y[i] / MathF.Max(Hx[i], Eps);

            var correction = FftConvolve.Correlate(ratio, w, h, psf, BoundaryMode.Reflect);
            var xNext = new float[n];
            for (int i = 0; i < n; i++)
            {
                float c = MathF.Max(correction[i], Eps);
                float relaxed = _alpha == 1f ? c : MathF.Pow(c, _alpha);
                float v = xStart[i] * relaxed;
                if (!float.IsFinite(v)) v = 0f;
                xNext[i] = Math.Clamp(v, 0f, 1f);
            }

            xPrevPrev = xPrev;
            xPrev = x;
            x = xNext;
        }
        return x;
    }
}
```

The Biggs-Andrews extrapolation tracks two iterations back (`xPrevPrev` and `xPrev`) so the momentum ratio `<x_k - x_{k-1}, x_{k-1} - x_{k-2}> / ||x_{k-1} - x_{k-2}||²` uses the correct backward differences. Tracking only one iteration back would give `dP = 0` and disable the acceleration silently.

- [ ] **Step 4: Verify tests pass**

Run: `dotnet test Deblur.sln --filter FullyQualifiedName~RichardsonLucyDeconvolverTests`
Expected: 5 pass.

If `MotionRoundTrip_BeatsBlurredBy3dB` fails because the measured gain is < 3 dB: investigate. Do NOT relax the threshold. Real RL on a length-8 motion PSF should recover several dB against ground truth. If gain is < 3 dB, either the PSF is too aggressive for the fixed 30 iterations OR the alpha/acceleration parameters are hurting more than helping. Escalate as DONE_WITH_CONCERNS with the measured PSNR values.

- [ ] **Step 5: Commit**

```bash
git add Deblur.Engine/RichardsonLucyDeconvolver.cs Deblur.Tests/RichardsonLucyDeconvolverTests.cs
git commit -m "Add Richardson-Lucy deconvolver with under-relaxation + Biggs-Andrews acceleration"
```

---

### Task 4: Constrained Least Squares deconvolver

**Files:**
- Create: `Deblur.Engine/ConstrainedLeastSquaresDeconvolver.cs`
- Test:   `Deblur.Tests/ConstrainedLeastSquaresDeconvolverTests.cs`

**Interfaces:**
- Consumes: `FftDeconvolverBase`.
- Produces:
  - `sealed class ConstrainedLeastSquaresDeconvolver : FftDeconvolverBase` with PSF-energy-scaled γ.

### The γ formula

The distinguishing feature vs. Tikhonov is: `γ = K · (E_C / E_H)` where:

- `E_H = mean_{u,v} |H(u,v)|²` — the PSF's average spectral energy.
- `E_C = mean_{u,v} |C(u,v)|²` — the Laplacian's average spectral energy over the FFT grid.

This ratio makes γ ↑ as the PSF's spectral energy ↓ (bigger blurs → deeper spectral nulls → smaller E_H → larger γ → more regularization where it's needed).

**Direction check** (the plan-mandated acceptance criterion for the K-normalization behavior): for two motion PSFs of length L₁=5 and L₂=15 at fixed `K=0.005`, CLS's output on a fixed blurred input should show LESS PSNR range between them than Tikhonov's output at the same K. i.e., CLS produces more consistent recovery quality across PSF sizes.

If empirical validation during implementation shows CLS produces indistinguishable output from Tikhonov (γ ratio ≈ 1 across typical PSFs), the implementer surfaces this at task-report time and we adjudicate: either ship on metadata differentiation alone or defer CLS to Phase 1.d.

- [ ] **Step 1: Write failing tests**

```csharp
// Deblur.Tests/ConstrainedLeastSquaresDeconvolverTests.cs
using Deblur.Engine;
using Deblur.Engine.Validation;
using Deblur.Tests.TestHelpers;
using Xunit;

namespace Deblur.Tests;

public class ConstrainedLeastSquaresDeconvolverTests
{
    [Fact]
    public void MotionRoundTrip_BeatsBlurredBy3dB()
    {
        var gt = SyntheticImages.Checkerboard(128, 128, 16);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 8f, 0f, 0f, 0f, AlgorithmType.ConstrainedLeastSquares));
        var blurred = SyntheticBlur.Apply(gt, psf, gaussianNoiseSigma: 0f, seed: 42);
        var deconv = new ConstrainedLeastSquaresDeconvolver().Apply(
            blurred, psf, new DeconvolutionParams(K: 0.005f), PipelineOptions.Default);

        double blurredPsnr = Quality.Psnr(gt, blurred);
        double deconvPsnr = Quality.Psnr(gt, deconv);
        Assert.True(deconvPsnr >= blurredPsnr + 3.0,
            $"CLS did not improve by 3 dB: blurred {blurredPsnr:F2} → deconv {deconvPsnr:F2}");
    }

    [Fact]
    public void IdentityTransform_FailsImprovementCriterion()
    {
        var gt = SyntheticImages.Checkerboard(128, 128, 16);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 8f, 0f, 0f, 0f, AlgorithmType.ConstrainedLeastSquares));
        var blurred = SyntheticBlur.Apply(gt, psf, gaussianNoiseSigma: 0f, seed: 42);
        var identity = blurred.Clone();
        Assert.False(Quality.Psnr(gt, identity) >= Quality.Psnr(gt, blurred) + 3.0);
    }

    [Fact]
    public void KNormalization_ProducesMoreConsistentRecoveryAcrossPsfSizes()
    {
        // Fixed K on length-5 and length-15 motion PSFs.
        // CLS's PSNR-vs-GT range across the two PSFs should be TIGHTER than Tikhonov's.
        var gt = SyntheticImages.Checkerboard(128, 128, 16);
        var p = new DeconvolutionParams(K: 0.005f);
        var opts = PipelineOptions.Default with { LinearLight = false, EdgeTaper = false };

        double[] clsPsnrs = new double[2];
        double[] tikPsnrs = new double[2];
        int idx = 0;
        foreach (float length in new[] { 5f, 15f })
        {
            var psf = new MotionBlurKernel().Build(
                new KernelParams(BlurType.Motion, 30f, length, 0f, 0f, 0f, AlgorithmType.ConstrainedLeastSquares));
            var blurred = SyntheticBlur.Apply(gt, psf, gaussianNoiseSigma: 0f, seed: 42);
            clsPsnrs[idx] = Quality.Psnr(gt, new ConstrainedLeastSquaresDeconvolver().Apply(blurred, psf, p, opts));
            tikPsnrs[idx] = Quality.Psnr(gt, new TikhonovDeconvolver().Apply(blurred, psf, p, opts));
            idx++;
        }
        double clsRange = Math.Abs(clsPsnrs[0] - clsPsnrs[1]);
        double tikRange = Math.Abs(tikPsnrs[0] - tikPsnrs[1]);
        Assert.True(clsRange <= tikRange,
            $"CLS K-normalization did not tighten PSNR range: cls Δ={clsRange:F2}, tik Δ={tikRange:F2}");
    }

    [Fact]
    public void ExtremeParams_NoNaNOrInfInOutput()
    {
        var input = SyntheticImages.Checkerboard(64, 64, 4);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 22f, 100f, 0f, 0f, 0f, AlgorithmType.ConstrainedLeastSquares));
        var deconv = new ConstrainedLeastSquaresDeconvolver().Apply(
            input, psf, new DeconvolutionParams(K: 1e-6f), PipelineOptions.Default);
        for (int i = 0; i < deconv.PixelCount; i++)
        {
            Assert.False(float.IsNaN(deconv.R[i]) || float.IsInfinity(deconv.R[i]));
            Assert.False(float.IsNaN(deconv.G[i]) || float.IsInfinity(deconv.G[i]));
            Assert.False(float.IsNaN(deconv.B[i]) || float.IsInfinity(deconv.B[i]));
        }
    }
}
```

- [ ] **Step 2: Verify tests fail**

Run: `dotnet test Deblur.sln --filter FullyQualifiedName~ConstrainedLeastSquaresDeconvolverTests`
Expected: FAIL.

- [ ] **Step 3: Implement `ConstrainedLeastSquaresDeconvolver`**

```csharp
// Deblur.Engine/ConstrainedLeastSquaresDeconvolver.cs
using System.Numerics;

namespace Deblur.Engine;

public sealed class ConstrainedLeastSquaresDeconvolver : FftDeconvolverBase
{
    public override AlgorithmMetadata Metadata { get; } = new(
        Id: "cls-laplacian",
        Version: "1.0",
        DisplayName: "Constrained Least Squares (Laplacian, PSF-normalized)",
        DescriptionMarkdown:
            "Constrained Least Squares deconvolution with a discrete-Laplacian smoothness " +
            "constraint. The regularization strength gamma is scaled by the ratio of the " +
            "Laplacian's average spectral energy to the PSF's average spectral energy so " +
            "that the K slider produces comparable regularization across different PSF sizes. " +
            "This is a pragmatic substitute for the classical CLS formulation, which chooses " +
            "gamma adaptively via the discrepancy principle. The classical adaptive gamma " +
            "requires independent noise-variance estimation and lands in a later phase; this " +
            "version's behavior is honest: fixed gamma scaled by PSF energy, not noise-adaptive.",
        LiteratureCitation:
            "Hunt, B.R. (1973). The application of constrained least squares estimation to " +
            "image restoration by digital computer. IEEE Trans. Comput. C-22(9), 805-812. " +
            "Gonzalez, R.C. & Woods, R.E. Digital Image Processing (4th ed.), sec. 5.9.");

    protected override Complex[,] BuildFilterResponse(Complex[,] H, DeconvolutionParams p, int fftSize)
    {
        int n = fftSize * fftSize;
        double sumH2 = 0, sumC2 = 0;
        var cSq = new double[fftSize, fftSize];
        for (int y = 0; y < fftSize; y++)
        {
            double Cv = 2.0 - 2.0 * Math.Cos(2.0 * Math.PI * y / fftSize);
            for (int x = 0; x < fftSize; x++)
            {
                double Cu = 2.0 - 2.0 * Math.Cos(2.0 * Math.PI * x / fftSize);
                double cs = (Cu + Cv) * (Cu + Cv);
                cSq[y, x] = cs;
                sumC2 += cs;
                var h = H[y, x];
                sumH2 += h.Real * h.Real + h.Imaginary * h.Imaginary;
            }
        }
        double meanH2 = sumH2 / n;
        double meanC2 = sumC2 / n;
        // Bigger blur → smaller meanH2 → larger gamma → more regularization.
        double gamma = p.K * (meanC2 / Math.Max(meanH2, 1e-12));

        var filter = new Complex[fftSize, fftSize];
        for (int y = 0; y < fftSize; y++)
            for (int x = 0; x < fftSize; x++)
            {
                var h = H[y, x];
                double mag2 = h.Real * h.Real + h.Imaginary * h.Imaginary;
                filter[y, x] = Complex.Conjugate(h) / (mag2 + gamma * cSq[y, x]);
            }
        return filter;
    }
}
```

- [ ] **Step 4: Verify tests pass**

Run: `dotnet test Deblur.sln --filter FullyQualifiedName~ConstrainedLeastSquaresDeconvolverTests`
Expected: 4 pass.

If `KNormalization_ProducesMoreConsistentRecoveryAcrossPsfSizes` fails (CLS range ≥ Tikhonov range), the normalization direction is wrong or the effect is negligible. Try inverting the ratio in `gamma`. If that also fails or if the effect is < 0.5 dB either way, escalate as DONE_WITH_CONCERNS: the K-normalization heuristic doesn't produce visible differentiation from Tikhonov. Controller adjudicates (either ship on metadata alone or defer to Phase 1.d).

- [ ] **Step 5: Commit**

```bash
git add Deblur.Engine/ConstrainedLeastSquaresDeconvolver.cs Deblur.Tests/ConstrainedLeastSquaresDeconvolverTests.cs
git commit -m "Add Constrained Least Squares deconvolver with PSF-energy-scaled gamma"
```

---

### Task 5: Landweber deconvolver

**Files:**
- Create: `Deblur.Engine/LandweberDeconvolver.cs`
- Test:   `Deblur.Tests/LandweberDeconvolverTests.cs`

**Interfaces:**
- Consumes: `FftConvolve`, `PipelineOptions`, `IDeconvolver`, `AlgorithmMetadata`.
- Produces:
  - `sealed class LandweberDeconvolver : IDeconvolver` with fixed `Iterations = 100`, `Step = 0.9`, non-negativity projection.

### Algorithm

Per channel:

```
x_0 = y
for k in [0, Iterations):
    Hx = FftConvolve.Convolve(x_k, w, h, psf, Reflect)
    residual[i] = y[i] - Hx[i]
    grad = FftConvolve.Correlate(residual, w, h, psf, Reflect)
    x_{k+1}[i] = max(0, x_k[i] + Step * grad[i])
```

Non-negativity projection is applied at every iteration.

- [ ] **Step 1: Write failing tests**

```csharp
// Deblur.Tests/LandweberDeconvolverTests.cs
using Deblur.Engine;
using Deblur.Engine.Validation;
using Deblur.Tests.TestHelpers;
using Xunit;

namespace Deblur.Tests;

public class LandweberDeconvolverTests
{
    [Fact]
    public void MotionRoundTrip_BeatsBlurredBy3dB()
    {
        var gt = SyntheticImages.Checkerboard(128, 128, 16);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 8f, 0f, 0f, 0f, AlgorithmType.Landweber));
        var blurred = SyntheticBlur.Apply(gt, psf, gaussianNoiseSigma: 0f, seed: 42);
        var deconv = new LandweberDeconvolver().Apply(
            blurred, psf, new DeconvolutionParams(K: 0.005f), PipelineOptions.Default);

        double blurredPsnr = Quality.Psnr(gt, blurred);
        double deconvPsnr = Quality.Psnr(gt, deconv);
        Assert.True(deconvPsnr >= blurredPsnr + 3.0,
            $"Landweber did not improve by 3 dB: blurred {blurredPsnr:F2} → deconv {deconvPsnr:F2}");
    }

    [Fact]
    public void IdentityTransform_FailsImprovementCriterion()
    {
        var gt = SyntheticImages.Checkerboard(128, 128, 16);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 8f, 0f, 0f, 0f, AlgorithmType.Landweber));
        var blurred = SyntheticBlur.Apply(gt, psf, gaussianNoiseSigma: 0f, seed: 42);
        var identity = blurred.Clone();
        Assert.False(Quality.Psnr(gt, identity) >= Quality.Psnr(gt, blurred) + 3.0);
    }

    [Fact]
    public void NonNegativity_HoldsAfterEveryIteration()
    {
        var input = SyntheticImages.Checkerboard(64, 64, 8);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 6f, 0f, 0f, 0f, AlgorithmType.Landweber));
        foreach (int iters in new[] { 1, 10, 50, 100 })
        {
            var deconv = new LandweberDeconvolver(iterations: iters, step: 0.9f)
                .Apply(input, psf, new DeconvolutionParams(K: 0.005f), PipelineOptions.Default);
            for (int i = 0; i < deconv.PixelCount; i++)
            {
                Assert.True(deconv.R[i] >= 0f, $"R[{i}]={deconv.R[i]} at iters={iters}");
                Assert.True(deconv.G[i] >= 0f, $"G[{i}]={deconv.G[i]} at iters={iters}");
                Assert.True(deconv.B[i] >= 0f, $"B[{i}]={deconv.B[i]} at iters={iters}");
            }
        }
    }

    [Fact]
    public void ExtremeParams_NoNaNOrInfInOutput()
    {
        var input = SyntheticImages.Checkerboard(64, 64, 4);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 22f, 100f, 0f, 0f, 0f, AlgorithmType.Landweber));
        var deconv = new LandweberDeconvolver().Apply(
            input, psf, new DeconvolutionParams(K: 1e-6f), PipelineOptions.Default);
        for (int i = 0; i < deconv.PixelCount; i++)
        {
            Assert.False(float.IsNaN(deconv.R[i]) || float.IsInfinity(deconv.R[i]));
            Assert.False(float.IsNaN(deconv.G[i]) || float.IsInfinity(deconv.G[i]));
            Assert.False(float.IsNaN(deconv.B[i]) || float.IsInfinity(deconv.B[i]));
        }
    }
}
```

- [ ] **Step 2: Verify tests fail**

Run: `dotnet test Deblur.sln --filter FullyQualifiedName~LandweberDeconvolverTests`
Expected: FAIL.

- [ ] **Step 3: Implement `LandweberDeconvolver`**

```csharp
// Deblur.Engine/LandweberDeconvolver.cs
using Deblur.Engine.Fft;

namespace Deblur.Engine;

public sealed class LandweberDeconvolver : IDeconvolver
{
    private readonly int _iterations;
    private readonly float _step;

    public AlgorithmMetadata Metadata { get; } = new(
        Id: "landweber",
        Version: "1.0",
        DisplayName: "Landweber (non-negativity-projected)",
        DescriptionMarkdown:
            "Landweber deconvolution is an iterative gradient-descent method on the least- " +
            "squares residual with a non-negativity projection. Each iteration applies " +
            "x_{k+1} = max(0, x_k + tau * H^T * (y - H*x_k)), where tau in (0, 2/lambda_max) " +
            "is the step size (lambda_max being the largest eigenvalue of H^T H, ~1 for " +
            "normalized PSFs). The non-negativity projection matches the physical assumption " +
            "that intensities are non-negative and restrains overshoot at strong edges.",
        LiteratureCitation:
            "Landweber, L. (1951). An iteration formula for Fredholm integral equations of " +
            "the first kind. American Journal of Mathematics 73(3), 615-624.");

    public LandweberDeconvolver(int iterations = 100, float step = 0.9f)
    {
        if (iterations < 1) throw new ArgumentOutOfRangeException(nameof(iterations));
        if (step <= 0f || step >= 2f) throw new ArgumentOutOfRangeException(nameof(step));
        _iterations = iterations;
        _step = step;
    }

    public ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p, PipelineOptions? options = null)
    {
        _ = options ?? PipelineOptions.Default;
        int w = input.Width, h = input.Height;
        return new ImageBuffer(w, h,
            ProcessChannel(input.R, w, h, psf),
            ProcessChannel(input.G, w, h, psf),
            ProcessChannel(input.B, w, h, psf));
    }

    private float[] ProcessChannel(float[] y, int w, int h, float[,] psf)
    {
        int n = y.Length;
        var x = (float[])y.Clone();
        for (int k = 0; k < _iterations; k++)
        {
            var Hx = FftConvolve.Convolve(x, w, h, psf, BoundaryMode.Reflect);
            var residual = new float[n];
            for (int i = 0; i < n; i++) residual[i] = y[i] - Hx[i];

            var grad = FftConvolve.Correlate(residual, w, h, psf, BoundaryMode.Reflect);
            for (int i = 0; i < n; i++)
            {
                float v = x[i] + _step * grad[i];
                if (!float.IsFinite(v)) v = 0f;
                x[i] = Math.Max(0f, v);
            }
        }
        // Clamp final output to [0,1] for consistency with other deconvolvers.
        for (int i = 0; i < n; i++) x[i] = Math.Clamp(x[i], 0f, 1f);
        return x;
    }
}
```

- [ ] **Step 4: Verify tests pass**

Run: `dotnet test Deblur.sln --filter FullyQualifiedName~LandweberDeconvolverTests`
Expected: 4 pass.

- [ ] **Step 5: Commit**

```bash
git add Deblur.Engine/LandweberDeconvolver.cs Deblur.Tests/LandweberDeconvolverTests.cs
git commit -m "Add Landweber deconvolver with non-negativity projection"
```

---

### Task 6: AlgorithmType + VM wiring

**Files:**
- Modify: `Deblur.Engine/AlgorithmType.cs`
- Modify: `Deblur/App.xaml`
- Modify: `Deblur/ViewModels/MainViewModel.cs`
- Modify: `Deblur/Converters/AlgorithmToSmoothnessLabelConverter.cs`

**Interfaces:**
- Produces: `AlgorithmType` enum values `RichardsonLucy`, `ConstrainedLeastSquares`, `Landweber`; `MainViewModel._deconvolvers` dictionary registrations; label mappings.

- [ ] **Step 1: Add enum values**

Extend `Deblur.Engine/AlgorithmType.cs` (currently `Wiener, Tikhonov, TotalVariation`) to:

```csharp
public enum AlgorithmType
{
    Wiener,
    Tikhonov,
    TotalVariation,
    RichardsonLucy,
    ConstrainedLeastSquares,
    Landweber,
}
```

- [ ] **Step 2: Register in `MainViewModel`**

In the `MainViewModel` constructor's deconvolver dictionary (after `[AlgorithmType.TotalVariation] = ...`), add:

```csharp
[AlgorithmType.RichardsonLucy]           = new RichardsonLucyDeconvolver(),
[AlgorithmType.ConstrainedLeastSquares]  = new ConstrainedLeastSquaresDeconvolver(),
[AlgorithmType.Landweber]                = new LandweberDeconvolver(),
```

- [ ] **Step 3: Update the label converter**

`Deblur/Converters/AlgorithmToSmoothnessLabelConverter.cs`: extend the switch expression:

```csharp
=> value switch
{
    AlgorithmType.Tikhonov                => "Regularization (λ)",
    AlgorithmType.TotalVariation          => "Regularization (λ)",
    AlgorithmType.ConstrainedLeastSquares => "Regularization (K)",
    AlgorithmType.RichardsonLucy          => "Iterations (fixed)",
    AlgorithmType.Landweber               => "Iterations (fixed)",
    _                                     => "Smoothness (K)",
};
```

- [ ] **Step 4: Update `App.xaml` AlgorithmTypeValues**

Add the three new enum values to the `AlgorithmTypeValues` `x:Array` resource so the ComboBox surfaces them.

- [ ] **Step 5: Verify + commit**

Run: `dotnet build Deblur.sln` → 0 errors. `dotnet test Deblur.sln` → all pass.

```bash
git add Deblur.Engine/AlgorithmType.cs Deblur/App.xaml Deblur/ViewModels/MainViewModel.cs Deblur/Converters/AlgorithmToSmoothnessLabelConverter.cs
git commit -m "Wire RL, CLS, Landweber into AlgorithmType + VM + label converter + App.xaml"
```

---

### Task 7: Manual smoke test + tag

- [ ] **Step 1: Build in Debug and launch**

```bash
dotnet build Deblur.sln
dotnet run --project Deblur/Deblur.csproj --no-build
```

- [ ] **Step 2: Manual smoke**

- Algorithm dropdown shows 6 options: Wiener, Tikhonov, TotalVariation, RichardsonLucy, ConstrainedLeastSquares, Landweber.
- Open an image. Apply Motion blur.
- Pick each of the three new algorithms — each produces a reasonable deblurred output.
- The K slider affects Wiener/Tikhonov/TV/CLS but is ignored by RL and Landweber (label reads "Iterations (fixed)" for both).
- ROI processing works with every new algorithm (toggle ROI on, draw a region, render → the region is sharpened; outside is untouched).
- 16-bit input still exports as 16-bit PNG under any new algorithm (open a 16-bit PNG, pick RL, Save-As PNG, verify depth preservation).
- Undo/redo, arrow drag (Motion), zoom/pan, cancel all still work.
- No visible regression under existing algorithm behavior.

Report smoke results in the ledger.

- [ ] **Step 3: Tag and update the progress ledger**

```bash
git tag phase1c
echo "phase1c: complete" >> .superpowers/sdd/progress.md
```

- [ ] **Step 4: Invoke `superpowers:finishing-a-development-branch`**

Present the standard four options and wait for the user's choice.
