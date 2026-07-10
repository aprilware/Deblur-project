# Deblur — Phase 1.c Design (Classical Iterative Deconvolution Algorithms)

**Date:** 2026-07-10
**Status:** Approved
**Scope:** Extract shared FFT scaffolding from Wiener/Tikhonov; add Richardson–Lucy, Constrained Least Squares, and Landweber deconvolvers. All under the existing `IDeconvolver` interface with full `AlgorithmMetadata`.

## Context

Phase 1.a corrected the pipeline. Phase 1.b added algorithm metadata + ROI processing. The engine now has three deconvolvers — Wiener, Tikhonov (Laplacian), TotalVariation (Chambolle post-filter). All are usable for forensic work but the toolbox is thin: the Wiener + Tikhonov pair covers frequency-domain closed-form recovery; TV covers edge-preserving denoising. Missing from the classical canon:

- **Richardson–Lucy (RL)**: iterative multiplicative update; forensic pedigree (Hubble Space Telescope); intuitive interpretation as maximum-likelihood recovery under Poisson noise.
- **Landweber**: iterative gradient descent with non-negativity projection; simple, robust, mathematically transparent.
- **Constrained Least Squares (CLS)**: frequency-domain like Tikhonov, but with the regularization strength normalized relative to the PSF's spectral energy — more consistent K behavior across different blur amounts.

All three are in the roadmap for Phase 1.c. Hyper-Laplacian and Split-Bregman TV are deferred to Phase 1.c-2 (their ADMM-based structure and LUT preprocessing warrant separate treatment).

Wiener and Tikhonov also duplicate substantial FFT scaffolding — PSF centering, forward FFT of PSF, per-channel FFT + multiply-by-filter + inverse FFT + crop with NaN guard + `[0,1]` clamp. CLS shares that shape exactly (only the filter-response formula differs). This is the fourth frequency-domain algorithm; the phase-4b review explicitly recommended extracting the shared scaffold when a fourth arrived. Phase 1.c does that first, then drops CLS onto it — Wiener and Tikhonov refactor too.

## Goal

An examiner picks Richardson–Lucy, Constrained Least Squares, or Landweber from the algorithm dropdown; the pipeline runs through the same Phase 1.a linear-light + edge-taper + luminance-only + ROI infrastructure and produces a physically-meaningful, testimony-ready result. Each algorithm's `Metadata` block explains what it does mathematically and cites its literature. Wiener and Tikhonov continue to produce byte-identical results (before rounding tolerance) after the FFT-scaffold refactor.

## Non-goals

- Hyper-Laplacian prior deconvolution (Krishnan–Fergus 2009) — Phase 1.c-2.
- TV via Split-Bregman / ADMM — Phase 1.c-2. Existing Chambolle-based `TotalVariationDeconvolver` remains available.
- User-tunable per-algorithm parameters (iteration count, damping factor, step size). Iteration counts are fixed at conservative defaults; algorithms that historically expose these knobs (RL iterations, Landweber τ) use fixed reasonable defaults for now. The `p.Smoothness` slider is ignored by RL and Landweber (label converter shows "n/a").
- Adaptive λ via discrepancy principle for CLS — requires noise-variance estimation (Phase 1.d).
- Blind deconvolution / PSF estimation — Phase 1.d, 1.e.
- Fixing rolled-up Minor items from Phase 1.b's whole-branch review (integer-div feather edge case, ReleaseMouseCapture during dual capture).

## Approach

### 1. Extract `FftDeconvolverBase`

New abstract class `Deblur.Engine/FftDeconvolverBase.cs` that owns everything the frequency-domain deconvolvers currently duplicate:

```csharp
public abstract class FftDeconvolverBase : IDeconvolver
{
    public abstract AlgorithmMetadata Metadata { get; }

    protected abstract Complex[,] BuildFilterResponse(
        Complex[,] H, DeconvolutionParams p, int fftSize);

    public ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p, PipelineOptions? options = null)
    {
        // 1. Compute pad, fftSize.
        // 2. Center PSF in fftSize x fftSize; forward FFT → H.
        // 3. Compute filterNumerator via BuildFilterResponse(H, p, fftSize).
        // 4. For each channel R, G, B: BoundaryFill.Pad + optional EdgeTaper → forward FFT →
        //    multiply by filterNumerator → inverse FFT → crop → NaN guard → [0,1] clamp.
        // 5. Return new ImageBuffer with the three channels.
    }
}
```

Refactor `WienerDeconvolver` and `TikhonovDeconvolver` to extend `FftDeconvolverBase`. Each becomes a small class: metadata + a `BuildFilterResponse` implementation. `WienerDeconvolver`'s override returns `conj(H) / (|H|² + K)`; `TikhonovDeconvolver`'s returns `conj(H) / (|H|² + K · |C|²)` where `|C|²` is the discrete-Laplacian frequency response (unchanged formula).

**Behavior contract**: the refactor produces byte-identical results within `1e-6` numerical tolerance to the pre-refactor implementations. Regression tests carry this: PSNR-vs-reference of the refactored Wiener/Tikhonov output against the pre-refactor output should exceed 60 dB.

### 2. Extract `FftConvolve` helper

Iterative deconvolvers (RL, Landweber) apply forward and adjoint convolutions with the PSF at every iteration. That deserves a reusable primitive:

```csharp
// Deblur.Engine/Fft/FftConvolve.cs
public static class FftConvolve
{
    /// <summary>
    /// FFT-based 2D convolution: result[i,j] = sum over (u,v) of channel[i-u, j-v] * psf[u, v].
    /// Uses reflect boundary; caller supplies the padded canvas size or lets the helper compute it.
    /// </summary>
    public static float[] Convolve(float[] channel, int w, int h, float[,] psf, BoundaryMode mode);

    /// <summary>
    /// FFT-based adjoint (correlation): result[i,j] = sum over (u,v) of channel[i+u, j+v] * psf[u, v].
    /// The adjoint of Convolve — needed by iterative methods that follow gradient direction.
    /// </summary>
    public static float[] Correlate(float[] channel, int w, int h, float[,] psf, BoundaryMode mode);
}
```

Both operations pad via `BoundaryFill.Pad`, use `FftAdapter` for FFT/iFFT, crop back to original dimensions, and NaN-guard the output. Neither clamps to `[0,1]` — iterative methods clamp explicitly at their own boundaries.

### 3. Richardson–Lucy

New `Deblur.Engine/RichardsonLucyDeconvolver.cs`. Per-channel iteration with damped multiplicative update:

```
x_0 = y
for k in [0, Iterations):
    Hx = FftConvolve.Convolve(x_k, psf, reflect)
    ratio = y / max(Hx, eps)          // per-pixel
    correction = FftConvolve.Correlate(ratio, psf, reflect)
    x_{k+1} = x_k * correction         // per-pixel; damped variant applies damping factor
```

**Damped variant** (Lucy-Poisson): multiplies `correction` by `damped = correction^Damping` where `Damping = 0.5` by default. Reduces noise amplification without changing convergence behavior at edges. Fixed at 0.5.

**Biggs–Andrews acceleration**: applies momentum-style extrapolation between iterations. Optional; enabled by default.

**Fixed hyperparameters**: `Iterations = 30`, `Damping = 0.5`, `Accelerate = true`. Chose these because they produce Hubble-quality results on standard deconvolution benchmarks without user tuning; Phase 2 or later can expose them as sliders.

Metadata: `Id = "richardson-lucy"`, `Version = "1.0"`, citation to Richardson (1972) and Lucy (1974).

### 4. Constrained Least Squares

New `Deblur.Engine/ConstrainedLeastSquaresDeconvolver.cs`, extends `FftDeconvolverBase`. Filter response has the Tikhonov shape:

```
filterNumerator(u, v) = conj(H(u,v)) / (|H(u,v)|² + γ · |C(u,v)|²)
```

Where `|C|²` is the discrete-Laplacian frequency response (same as Tikhonov). The distinguishing feature vs. Tikhonov is how γ is derived from `p.K`: CLS scales `p.K` by a PSF-spectral-energy factor so that a fixed slider position produces comparable regularization strength across different PSF sizes. The specific formula (a ratio involving `mean(|H|²)`) is validated empirically during implementation — the acceptance criterion is that the CLS output on a length-5 and length-15 motion PSF, at the same K, produces gradient-energy that scales less steeply with PSF size than bare Tikhonov does.

This is a pragmatic substitute for the classical CLS formulation, in which γ is chosen adaptively by the discrepancy principle to satisfy `||H*x - y||² = ||noise||²`. That formulation requires an independent noise-variance estimate, which is Phase 1.d work. The `DescriptionMarkdown` and this design spec both call the current formulation out honestly. When Phase 1.d ships noise estimation, CLS's `Version` bumps to `"2.0"` and switches to the classical adaptive γ.

Metadata: `Id = "cls-laplacian"`, `Version = "1.0"`, citation to Hunt (1973) and modern texts (Gonzalez & Woods).

If the empirical validation shows CLS at this phase is indistinguishable from Tikhonov in visible output, the implementation task escalates: either land it anyway (metadata differentiation is the value; adaptive γ comes in 1.d) or defer to 1.d entirely. The implementer surfaces this at task-report time.

### 5. Landweber

New `Deblur.Engine/LandweberDeconvolver.cs`. Per-channel iteration with non-negativity projection:

```
x_0 = y
step = 1.0 / max_eigenvalue(H^T H) ≈ 1.0        // safe under normalized PSFs (sum=1)
for k in [0, Iterations):
    Hx = FftConvolve.Convolve(x_k, psf, reflect)
    grad = FftConvolve.Correlate(y - Hx, psf, reflect)
    x_{k+1} = max(0, x_k + step · grad)
```

**Fixed hyperparameters**: `Iterations = 100`, `Step = 0.9`. The step-size upper bound for Landweber convergence is `2 / max_eigenvalue(H^T H)`, which for a normalized convolution PSF (sum=1) equals 2. Step = 1.0 is at the classical safe midpoint; 0.9 adds a small margin for FFT numerical noise and non-exact normalization. The non-negativity projection matches the physical assumption that intensities are non-negative — critical for restraining Landweber's characteristic overshoot at strong edges.

Metadata: `Id = "landweber"`, `Version = "1.0"`, citation to Landweber (1951).

### 6. AlgorithmType + VM wiring

Extend `Deblur.Engine/AlgorithmType.cs`:

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

`MainViewModel`'s deconvolver dictionary gains three entries. `AlgorithmToSmoothnessLabelConverter` maps the new types to appropriate labels: `ConstrainedLeastSquares` → "Regularization (K)" (same as Tikhonov); `RichardsonLucy` and `Landweber` → "Iterations" (with the slider disabled or ignored since iteration count is fixed — the slider's disabled state is a phase 1.c-2 or later UX polish; for now the slider is visible but has no effect on RL/Landweber, and the label reads "Iterations (fixed)").

The XAML shared-footer slider stays exactly as-is; only the label converter updates. `App.xaml`'s `AlgorithmTypeValues` list gains the three new entries.

### 7. What stays untouched

- `PipelineOptions` — no changes.
- `RoiProcessor`, `DeblurJobRunner` — unchanged. New algorithms use the same routing.
- Live preview loop — unchanged.
- Metadata SPI — unchanged; the new deconvolvers implement `Metadata` as required.
- `WicImageCodec`, `AreaResample`, `SourceBitDepth` — unchanged.
- Existing `WienerDeconvolver` and `TikhonovDeconvolver` PUBLIC API — unchanged (still constructible, `Apply` signature unchanged). Only their internals move to the shared base.

## Files touched

**New in `Deblur.Engine`:**
- `FftDeconvolverBase.cs`
- `Fft/FftConvolve.cs`
- `RichardsonLucyDeconvolver.cs`
- `ConstrainedLeastSquaresDeconvolver.cs`
- `LandweberDeconvolver.cs`

**Modified in `Deblur.Engine`:**
- `AlgorithmType.cs` — three new enum values.
- `WienerDeconvolver.cs` — refactored to extend `FftDeconvolverBase`. Public API unchanged.
- `TikhonovDeconvolver.cs` — refactored to extend `FftDeconvolverBase`. Public API unchanged.

**Modified in `Deblur`:**
- `App.xaml` — `AlgorithmTypeValues` gains three entries.
- `ViewModels/MainViewModel.cs` — three new dictionary entries.
- `Converters/AlgorithmToSmoothnessLabelConverter.cs` — mapping for three new algorithms.

**New in `Deblur.Tests`:**
- `FftConvolveTests.cs` — convolve/correlate round-trip; identity kernel is identity; correlate is the adjoint of convolve within numeric tolerance.
- `RichardsonLucyDeconvolverTests.cs` — motion round-trip PSNR > 15 dB; convergence over iterations (PSNR increases monotonically); no NaN under extreme params.
- `ConstrainedLeastSquaresDeconvolverTests.cs` — motion round-trip PSNR > 15 dB; K-normalization behavior (fixed K produces comparable smoothness across length-5 and length-15 motion PSFs); NaN safety.
- `LandweberDeconvolverTests.cs` — motion round-trip PSNR > 15 dB; non-negativity holds after every iteration; NaN safety.
- `FftDeconvolverRefactorRegressionTests.cs` — pre-refactor Wiener/Tikhonov results (via reference implementations captured inline) match post-refactor results within 60 dB PSNR.

## Constraints

- .NET 8. No new NuGet packages.
- Existing 104 tests remain green. Test count target after 1.c: ~125.
- `FftDeconvolverBase.Apply` behavior contract: refactored Wiener/Tikhonov output matches pre-refactor output within `1e-6` numerical tolerance (measured PSNR > 60 dB against a captured reference — the regression tests carry this).
- Every new algorithm's `Metadata.DescriptionMarkdown > 100 chars`, `LiteratureCitation > 20 chars` — same standard as Phase 1.b.
- Fixed hyperparameters for RL (iterations=30, damping=0.5, accelerate=true) and Landweber (iterations=100, step=1.0). No UI slider for these in this phase.
- `p.Smoothness` slider ignored by RL and Landweber. Label reads "Iterations (fixed)". Not disabled visually — the label swap is the whole UX change.
- Phase 1.c branches from tag `phase1b` onto `phase1c-classical-iterative-algorithms` (branch already created).

## Testing

Unit + regression tests as listed above. Key correctness properties the tests lock in:

- **`FftConvolve` primitives** — convolve with an identity kernel is identity; convolve then correlate with the same PSF approximates the autocorrelation of the input (within FFT precision); NaN/Inf-safe on all-zero inputs.
- **RL convergence** — under noise-free synthetic blur, RL PSNR against ground truth increases monotonically for the first ~20 iterations, plateaus after. Test asserts iteration 30 > iteration 5 > iteration 1.
- **CLS normalization** — for two motion PSFs of length 5 and 15, applying the same K yields output whose gradient-energy ratio matches the ratio Tikhonov would produce with K scaled by the PSF-energy ratio. (Not testing exact values — testing that the normalization behavior is present and directionally correct.)
- **Landweber non-negativity** — after every iteration, `min(x) >= 0`. Test uses a stub PSF and asserts the invariant for iterations 1, 10, 50, 100.
- **Refactor regression** — Wiener and Tikhonov output before and after the FFT-base refactor match within 60 dB PSNR. The `FftDeconvolverRefactorRegressionTests` captures the pre-refactor output inline as expected values, so future edits to the base can't silently regress the algorithm math.

Manual smoke:
- Algorithm dropdown gains three new options: Richardson–Lucy, Constrained Least Squares, Landweber.
- Picking each produces a deblurred result under Motion, OutOfFocus, and Gaussian on a real image.
- The K slider still affects Tikhonov + TotalVariation + Wiener + CLS behavior; RL and Landweber ignore it (label reads "Iterations (fixed)").
- ROI processing works with every new algorithm.
- 16-bit input still exports as 16-bit PNG with any new algorithm selected.
- Undo/redo, save-as, cancel, arrow drag all still work.

## Branch

Phase 1.c branches from tag `phase1b` onto `phase1c-classical-iterative-algorithms`.
