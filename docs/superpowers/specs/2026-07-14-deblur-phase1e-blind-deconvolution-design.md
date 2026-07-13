# Deblur — Phase 1.e Design (Iterative Blind Deconvolution)

**Date:** 2026-07-14
**Status:** Approved
**Scope:** Multi-scale MAP-alternating blind deconvolution as a new `IDeconvolver`, plus a sidebar PSF-heatmap display showing the estimated kernel to the examiner. First estimator in the project that produces a **kernel** rather than a scalar parameter — real casework fix for the "unknown blur" problem.

## Context

Phase 1.d shipped four point-parameter estimators (cepstral motion, Radon cross-check, defocus radius, wavelet noise) with honest UX safeguards that disable Accept on low-confidence suggestions. The safeguards work — but "manual sliders recommended" is a poor answer for a forensic tool. Real casework rarely has a clean motion-line or defocus-disc PSF; blur is usually a mix (camera shake + motion + defocus) or a curved motion path.

Iterative blind deconvolution is what the roadmap and the user's original spec both called out as "the single biggest capability gap." Instead of guessing an angle and length, the engine iteratively refines a general 2D kernel by alternating between latent-image estimation (given current kernel) and kernel estimation (given current image). Multi-scale coarse-to-fine keeps it tractable.

The recovered kernel is shown to the examiner in the sidebar as a small heatmap — testimony-critical, because seeing the kernel is what distinguishes principled recovery from black-box "AI magic."

## Goal

An examiner loads a blurred image where the blur type isn't obvious (a security-cam still with camera shake + motion + focus issues). They pick "BlindDeconvolution" from the algorithm dropdown. On Render, the engine multi-scale-alternates and displays the current estimated kernel as a heatmap. On completion, they see the deblurred image AND the recovered PSF. They can inspect the PSF to sanity-check the result — a wildly asymmetric or noisy kernel is a signal that recovery failed and manual entry is needed. The kernel is stable enough that a second render on the same input produces near-identical results (deterministic).

## Non-goals

- **User-tunable hyperparameters** (iteration counts, kernel size, sparsity strength). Fixed defaults; forensic reproducibility beats knob-twiddling.
- **PSF-from-image extraction** (box a point source, use it as the kernel). Phase 1.f.
- **Interactive PSF editor** (drawable grid to hand-refine the recovered kernel). Phase 1.f.
- **Kernel library** (save/load per case). Phase 1.f.
- **Spatially-variant blur** (per-tile kernels). Stretch goal deferred beyond phase 1.
- **Live-preview blind**. Blind is full-res-only, same routing as Richardson-Lucy and Landweber.
- **Non-blind kernel refinement** (user picks a rough Motion + click "Refine" to run blind starting from that seed). Nice UX but adds scope; deferred.
- Fixing rolled-up items from Phase 1.d review: proxy/full-res noise-variance mismatch in live preview, no unit test for the "estimators receive linear grayscale" invariant, cepstral confidence formula recalibration.

## Approach

### 1. Algorithm — Cho & Lee (2009) style multi-scale MAP

Coarse-to-fine pyramid: 4 levels at scales `[1/8, 1/4, 1/2, 1/1]` of the input. At each level:

1. Downscale the blurred input via area-average.
2. Initialize kernel as a small centered delta (or from the previous coarser scale, upscaled 2× via bilinear).
3. Run N outer iterations of:
   a. **Latent image estimation** — given current kernel, solve a fast Wiener-with-Tikhonov filter (`conj(H) / (|H|² + λ_i · |C|²)`) for the latent image. Uses the existing `FftDeconvolverBase` machinery.
   b. **Shock filter** on the latent image — sharpens edges as a surrogate for the sparse-gradient prior (Osher & Rudin 1990). One pass, small step size.
   c. **Kernel estimation** — given the shock-filtered latent, solve a constrained least-squares problem in the frequency domain for the kernel that best fits `blurred ≈ H_new * latent`. Frequency-domain formula: `H = (conj(Latent) · Blurred) / (|Latent|² + λ_k)`.
   d. **Kernel projection** — inverse-FFT the estimated `H`, project to the spatial-domain kernel: (i) clip to a fixed centered `31 × 31` window, (ii) threshold small values (< 5% of max) to 0 for sparsity, (iii) enforce non-negativity, (iv) normalize to sum = 1.
4. Upscale kernel 2× (bilinear) as initialization for the next finer scale.

At the finest scale, output: (a) the deblurred latent image, (b) the estimated kernel.

**Fixed hyperparameters** (per scale):
- Outer iterations: 5.
- Wiener regularization `λ_i` (image estimation): 1e-3.
- Wiener regularization `λ_k` (kernel estimation): 1e-3.
- Shock-filter step: 1.0 (one pass per outer iteration).
- Sparsity threshold: 5% of kernel max.
- Kernel window size: `31 × 31` at every scale (large enough for motion up to ~15 px in any direction; sufficient for full-res under typical proxy scales).

Total inner-loop cost: 4 scales × 5 iterations × (2 FFTs latent + 1 shock + 2 FFTs kernel) ≈ 100 FFTs. On a full-res 4K image with 4× downsampling per level, the coarse levels are cheap; the fine level dominates. Practical budget: ~5-15 seconds on typical inputs.

### 2. Interface & metadata

New `AlgorithmType.BlindDeconvolution` enum value.

New `BlindDeconvolutionDeconvolver : IDeconvolver`:

```csharp
public sealed class BlindDeconvolutionDeconvolver : IDeconvolver
{
    public AlgorithmMetadata Metadata { get; } = new(
        Id: "blind-cho-lee",
        Version: "1.0",
        DisplayName: "Blind deconvolution (MAP, multi-scale)",
        DescriptionMarkdown: "...",
        LiteratureCitation: "Cho, S. & Lee, S. (2009). Fast Motion Deblurring...");

    /// <summary>
    /// Kernel estimated on the last Apply call. Null before the first call.
    /// Not thread-safe — assumes single-threaded runner (matches current
    /// DeblurJobRunner invariant). Live-preview WorkerLoop skips this
    /// algorithm, so only RenderFullAsync writes here.
    /// </summary>
    public float[,]? LastEstimatedKernel { get; private set; }

    public ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p, PipelineOptions? options = null);
}
```

Crucial architectural point: `Apply` receives a `psf` parameter (from `IDeconvolver`) but **ignores it**. Blind deconvolution's whole point is that it doesn't need a known PSF. The runner still calls `_kernels[BlurType].Build(...)` and passes the result, but this deconvolver treats it as a hint only (initialization seed for the coarse-scale kernel) or ignores it entirely. Documented in `DescriptionMarkdown`.

### 3. Kernel display in the sidebar

New WPF `UserControl` `Deblur/Controls/PsfDisplay.xaml{,.cs}`:
- Takes a `float[,]?` `Kernel` dependency property.
- Renders as a grayscale heatmap: kernel[y,x] normalized to `[0,1]` → grayscale byte.
- Fixed size 128×128 pixels (upscaled nearest-neighbor from the 31×31 kernel).
- Thin border, tick marks at the center to indicate the origin (motion direction reads as an off-center brightness).
- When Kernel is null, shows placeholder text "No estimated PSF".

Placed in the sidebar's shared footer, visible when `SelectedAlgorithm == AlgorithmType.BlindDeconvolution`.

The `MainViewModel` reads `_blindDeconvolver.LastEstimatedKernel` after each full-res render and exposes it via an `[ObservableProperty]` for binding.

### 4. Runner integration + preview skip

Same pattern as Richardson-Lucy and Landweber:
- Add `BlindDeconvolution` to the `isIterativePreview` gate in `DeblurJobRunner.WorkerLoop`. Live preview shows raw proxy.
- Full-res render routes through blind as normal.
- `CancellationToken` from `PipelineOptions.CancellationToken` (Phase 1.c infrastructure) is checked at every outer-iteration boundary and at every scale transition.

### 5. Deterministic behavior

Blind deconvolution can be sensitive to initialization. For forensic reproducibility, the coarse-scale kernel MUST initialize deterministically:
- Default: centered 3×3 delta (99% at center, 1% in each 4-connected neighbor for a small non-degenerate start).
- If the user picked a `BlurType` before switching to blind, the initial kernel could be built from that (Motion → the motion kernel at the current Length/Angle). This gives users a way to seed blind with a rough hint. For 1.e we do the delta start and leave hint-seeding as a future refinement.

The rest of the algorithm is deterministic: no random noise, no stochastic sampling.

### 6. What stays untouched

- `PipelineOptions`, `RegionOfInterest`, `RoiProcessor` — unchanged.
- All existing deconvolvers — unchanged.
- Live-preview loop — the iterative-algorithm skip list extends by one entry.
- WIC codec, `SourceBitDepth` propagation, `CancellationToken` plumbing — unchanged.
- ParamHistory undo/redo — blind's output depends only on input + algorithm selection, so undo/redo works via the standard flow.

## Files touched

**New in `Deblur.Engine`:**
- `BlindDeconvolutionDeconvolver.cs`
- `Blind/PyramidHelpers.cs` — downscale + upscale for the multi-scale pyramid (uses existing `AreaResample.Box` for downscale; new bilinear upscale for the kernel between scales).
- `Blind/ShockFilter.cs` — one-pass Osher-Rudin shock filter for edge sharpening.
- `Blind/KernelProjection.cs` — clip to window, threshold, non-negativity, normalize.

**Modified in `Deblur.Engine`:**
- `AlgorithmType.cs` — add `BlindDeconvolution`.
- `DeblurJobRunner.cs` — extend the `isIterativePreview` gate.

**Modified in `Deblur`:**
- `App.xaml` — `AlgorithmTypeValues` already surfaces the enum reflectively (Phase 1.c fix); no XAML change needed for the dropdown.
- `Controls/PsfDisplay.xaml`, `Controls/PsfDisplay.xaml.cs` (new) — kernel heatmap.
- `MainWindow.xaml` — add `<controls:PsfDisplay>` in the sidebar, visible when blind is selected.
- `ViewModels/MainViewModel.cs` — `[ObservableProperty]` `_estimatedKernel`, reads it after RenderFullAsync completes.
- `Converters/AlgorithmToSmoothnessLabelConverter.cs` — extend switch: `BlindDeconvolution → "Iterations (fixed)"`.

**New in `Deblur.Tests`:**
- `BlindDeconvolutionDeconvolverTests.cs`:
  - Recovers a known Motion PSF from a synthetic-blurred `TexturedNoise` — cosine similarity between estimated kernel and true kernel > 0.6 after normalizing both to their centroid.
  - Deblurred output PSNR improvement over blurred > 3 dB.
  - Kernel non-negativity + sum-to-1 hold on the returned `LastEstimatedKernel`.
  - Extreme parameters (Length=100 motion) → no NaN/Inf in output or kernel.
  - Cancellation: passing a pre-cancelled token throws `OperationCanceledException`.
- `Blind/KernelProjectionTests.cs`:
  - Clip: input larger than 31×31 → output is 31×31 centered on the input's argmax.
  - Threshold: values < 5% of max → 0.
  - Non-negativity + sum-to-1 hold.
- `Blind/ShockFilterTests.cs`:
  - Edge sharpening: input soft-edge → output has larger gradient magnitude at the edge.
  - Constant image → output constant (no artifacts on flat regions).

## Constraints

- .NET 8. No new NuGet packages.
- All 154 Phase 1.d tests remain green. Test count target: 154 → ~170.
- Fixed hyperparameters (§1). No UI sliders.
- `LastEstimatedKernel` is a public getter on `BlindDeconvolutionDeconvolver`; the VM reads it after each `RenderFullAsync` completes. Not thread-safe on its own — relies on the runner's single-threaded discipline.
- Kernel size: 31×31 fixed — covers motion up to ~15 px in any direction, which handles typical CCTV motion blur and moderate camera shake. Blur larger than that produces a truncated kernel and degraded recovery; a future variant that grows kernel size adaptively from a rough length estimate is deferred.
- Multi-scale pyramid: 4 levels at scales `[1/8, 1/4, 1/2, 1/1]`.
- Cancellation: `CancellationToken` from `PipelineOptions.CancellationToken` checked at every outer-iteration boundary AND at every scale transition.
- Metadata: `Id = "blind-cho-lee"`, `Version = "1.0"`. Description honestly names the multi-scale MAP approach + shock filter + fixed 31×31 kernel + non-tunable defaults. Citation: Cho & Lee (2009); Levin et al. (2011) for the theoretical foundation.
- Deterministic: no random seeds. Same input → same kernel.
- The `psf` parameter passed to `Apply` is IGNORED. The runner still constructs one from the current blur-type sliders, but blind never uses it. This is intentional — matches the `IDeconvolver` contract minimally rather than adding a new interface.
- Phase 1.e branches from tag `phase1d` onto `phase1e-blind-deconvolution` (already created).

## Testing

Unit + integration tests as listed under **Files touched**. Key correctness properties:

- **Kernel similarity**: `cosine(recoveredKernel, trueKernel) > 0.6` after centroid alignment. Cosine similarity is shift-invariant, matching the fact that blind deconv can't recover the absolute spatial offset (it's ambiguous under `H(x) → H(x-d), I(x) → I(x+d)`).
- **Deblurred PSNR improvement > 3 dB** on synthetic motion blur applied to `TexturedNoise` — same improvement-criterion pattern established in Phase 1.c.
- **Kernel properties** on `LastEstimatedKernel`: `min ≥ 0`, `abs(sum - 1) < 1e-3`.
- **Cancellation**: pre-cancelled token → `OperationCanceledException`.
- **Determinism**: two consecutive calls on the same input produce byte-identical output + kernel.
- **Identity-transform integrity check**: an identity transform on the blurred image (return input) MUST fail the ≥3 dB improvement criterion — same pattern as Phase 1.c.

Manual smoke:
- Algorithm dropdown surfaces BlindDeconvolution.
- Load a motion-blurred image. Pick blind. Render → 5-15 second progress bar, then deblurred output.
- PSF display shows a recognizable kernel (bright line for motion, bright disc for defocus).
- 16-bit input still exports as 16-bit PNG under blind.
- Cancel during render → stops within ~1 second.
- ROI processing works with blind (though the kernel is estimated for the ROI, not the whole image — the display should reflect that).
- Undo/redo, save-as, arrow drag all still work.

## Branch

Phase 1.e branches from tag `phase1d` onto `phase1e-blind-deconvolution`.
