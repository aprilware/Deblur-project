# Deblur — Phase 1.d Design (Automatic Parameter Estimation)

**Date:** 2026-07-10
**Status:** Approved
**Scope:** Four suggestion-only estimators (cepstral motion, Radon motion cross-check, defocus radius via Bessel zeros, wavelet-MAD noise variance) plus a CLS v2.0 that uses noise variance for adaptive γ via the discrepancy principle. Nothing auto-applied — every estimate surfaces as an examiner-inspectable suggestion.

## Context

Phase 1.a corrected the pipeline. Phase 1.b added metadata + ROI. Phase 1.c added the classical iterative algorithms. All six deconvolvers are useless if the examiner doesn't know the blur parameters — real casework rarely arrives with the motion angle, motion length, defocus radius, or noise variance pre-labeled. Automatic parameter estimation is the biggest remaining gap for forensic usability.

Phase 1.d adds four estimators and finishes the deferred CLS-adaptive-γ piece from Phase 1.c. Every estimator produces a **suggestion** — a value + confidence — that the examiner reviews and accepts via an explicit "Accept" click. Nothing is silently applied to a slider. Every acceptance is logged for the (future Phase 2) audit trail.

## Goal

The examiner loads an image blurred by motion of unknown length/angle. They click "Estimate motion" in the sidebar. The engine analyzes the image, produces `(angle ≈ 30°, length ≈ 12.3, confidence: high)` as an inline suggestion next to the sliders. The examiner reviews the suggestion, clicks "Accept," and the sliders populate. They then click "Estimate noise" to get a K suggestion for Wiener, accept it, and render. If CLS is picked, γ auto-adapts to the estimated noise variance via the discrepancy principle. The whole flow surfaces provenance — timestamp, estimator Id/Version, estimated value, confidence — for the eventual audit log.

## Non-goals

- Iterative blind deconvolution (MAP alternating kernel/image estimation) — Phase 1.e. Phase 1.d gives point-parameter suggestions; 1.e will give visible-kernel refinement.
- PSF-from-image extraction (box a point source, use it as the kernel) — Phase 1.f.
- Interactive PSF editor / kernel library — Phase 1.f.
- Persistent audit log to disk — Phase 2. Phase 1.d records suggestions in-memory only; the acceptance timestamp is captured but not yet stored.
- Fixing rolled-up Phase 1.b/1.c review Minors (BT.601-on-linear, EdgeTaper mean-space asymmetry, CLS K-slider UX, iterative-algorithm classifier).

## Approach

### 1. Cepstral motion estimator

New `Deblur.Engine/Estimation/CepstralMotionEstimator.cs`. Steps:

1. Compute the grayscale channel of the input (luminance = `0.299R + 0.587G + 0.114B`).
2. Apply a Hann window to suppress boundary spectral leakage.
3. Take `|F|²` (power spectrum).
4. Compute the log power spectrum + small floor to avoid `log(0)`.
5. Take the inverse FFT of the log spectrum → the **cepstrum**.
6. Search for a dominant negative peak in the cepstrum (a motion blur's characteristic autocorrelation-of-boxcar shape).
7. The peak's polar coordinates give the estimated `(angle, length)`.

Returns `MotionEstimate(float Angle, float Length, float Confidence)` where confidence ∈ [0, 1] measures the peak's height relative to the median cepstral energy — low confidence signals "no clear motion peak; suggestion may be unreliable."

Reference: Cannon (1976) "Blind deconvolution of spatially invariant image blurs with phase"; also textbook Gonzalez & Woods sec. 5.7.

### 2. Radon motion estimator (angle cross-check)

New `Deblur.Engine/Estimation/RadonMotionEstimator.cs`. Steps:

1. Compute the log power spectrum (same as cepstral estimator).
2. For each candidate angle θ ∈ [0°, 179°] at 1° resolution: sum the log-power values along the line at angle θ through the origin (i.e., a Radon transform on the log power spectrum).
3. Motion blur imprints periodic dark stripes on the log-power spectrum (the sinc's zeros). The Radon integration is EXTREMAL at the motion direction (either min or max, convention-dependent — verified empirically against synthetic motion PSFs during implementation). The estimator's Ground Truth Test (see Testing) locks in whichever convention gives ±5° accuracy.

Returns `float Angle` only (Radon is used purely as a cross-check for the cepstral angle; length comes from cepstral).

The UI displays: `Cepstral: 30°, Radon: 32° — agree ✓` (or "disagree ⚠" if |Δ| > 10°). Disagreement signals ambiguity — the examiner uses judgment.

Reference: Krahmer et al. (2006) "Blind image deconvolution: motion blur estimation".

### 3. Defocus radius estimator (Bessel zeros)

New `Deblur.Engine/Estimation/DefocusRadiusEstimator.cs`. Steps:

1. Compute the radial average of the power spectrum: for each radius bin `r`, average `|F(u, v)|²` over all `(u, v)` with `√(u² + v²) ≈ r`.
2. A disc PSF of radius R has Fourier transform proportional to `2·J₁(2πRρ)/(2πRρ)` where `J₁` is the first-order Bessel function. Its first zero-crossing is at `ρ ≈ 1.22/R` (Airy-disc first null).
3. Find the first minimum in the radial power spectrum where the log-power crosses below a threshold (say, mean − 2·std) → get `ρ_first_zero`.
4. Estimate `R ≈ 1.22 / ρ_first_zero`.

Returns `DefocusEstimate(float Radius, float Confidence)`.

Reference: Yitzhaky & Kopeika (1997) "Identification of blur parameters from motion blurred images"; disc-PSF specifics in Gonzalez & Woods.

### 4. Wavelet-MAD noise estimator

New `Deblur.Engine/Estimation/WaveletNoiseEstimator.cs`. Steps:

1. Apply one level of Haar wavelet decomposition to the grayscale image → four subbands (LL, LH, HL, HH). Only HH is needed.
2. Compute the **median absolute deviation** (MAD) of the HH-band coefficients: `σ̂ = median(|c_HH − median(c_HH)|) / 0.6745`.
3. `σ̂` is the estimated noise standard deviation (assumes noise is dominated by finest-scale coefficients; MAD is robust to signal outliers).

Returns `NoiseEstimate(float Sigma, float Confidence)`.

For Wiener, the suggested K is proportional to `σ²` (noise variance) — the classical Wiener NSR interpretation. Suggested slider K = `Clamp(σ² * scale, K_min, K_max)` where `scale` is empirically tuned so σ=0.01 → K≈0.005.

Reference: Donoho & Johnstone (1994) "Ideal spatial adaptation by wavelet shrinkage."

Haar wavelet chosen (not Daubechies-4) because it's a trivial one-line-per-coefficient transform and MAD is robust to the wavelet choice; upgrade to Daubechies is a future micro-optimization.

### 5. CLS v2.0: adaptive γ via discrepancy principle

Modify `Deblur.Engine/ConstrainedLeastSquaresDeconvolver.cs`. Bump `Version` from `"1.0"` to `"2.0"`. Behavior change:

- New constructor: `ConstrainedLeastSquaresDeconvolver(float? noiseVariance = null)`.
- `null` (default): behavior identical to v1.0 (fixed γ = K·(E_C/E_H)). Preserves the parameter-less usage.
- Non-null: γ selected via discrepancy principle — solve for γ such that `Σ|H·x̂ - y|² = fftSize² · noiseVariance` where `x̂ = deconvolve(y; γ)`. Uses bisection on γ over `[γ_min, γ_max]` = `[1e-8, 1e2]`.

The `MainViewModel` wires the wavelet-noise-estimate acceptance to pass `noiseVariance = σ̂²` into a freshly-constructed CLS instance for the next render. If no noise estimate is accepted, CLS falls back to v1.0 fixed-γ behavior at the current K slider.

`Metadata.Version` bumps to `"2.0"`. `Metadata.DescriptionMarkdown` gains a paragraph naming the adaptive-γ mode ("when a noise variance estimate is provided, γ is chosen via the discrepancy principle; when not, γ is the fixed-scaling from v1.0"). `Metadata.Id` stays `"cls-laplacian"` — same algorithm, upgraded implementation. Version bump is the forensic-provenance marker (any old audit log referencing `cls-laplacian@1.0` would not match output from `cls-laplacian@2.0`, forcing an intentional re-check).

Since no production audit log exists yet, this in-place upgrade is safe.

### 6. UI integration

Sidebar changes per blur type:

- **Motion panel**: add "Estimate motion" button below the angle/length sliders. When clicked, run cepstral + Radon estimators, display inline: `Suggested: 12.3 px @ 30° (Radon 32° ✓, conf. high) [Accept] [Dismiss]`. Accept populates the sliders; Dismiss clears the suggestion.
- **OutOfFocus panel**: add "Estimate radius" button. Displays `Suggested: 4.5 px (conf. medium) [Accept] [Dismiss]`.
- **Gaussian panel**: no estimator this phase (defocus estimator matches disc PSF; a Gaussian σ estimator would be a separate implementation, deferred).
- **Shared footer**: add "Estimate noise" button. Displays `Suggested: K = 0.0032 (σ ≈ 0.008) [Accept] [Dismiss]`.

New `MainViewModel` properties and commands:
- `MotionSuggestion` / `DefocusSuggestion` / `NoiseSuggestion` — nullable observable objects.
- `EstimateMotionCommand`, `EstimateDefocusCommand`, `EstimateNoiseCommand` — trigger the estimators on `_originalFullRes` (or `_proxy` if noise is being estimated on the display-scale image, which is normal).
- `AcceptMotionSuggestion` / `AcceptDefocusSuggestion` / `AcceptNoiseSuggestion` — populate the underlying sliders and clear the suggestion.
- `DismissMotionSuggestion` / etc. — clear without accepting.

New `SuggestionRecord` type (`Deblur.Engine/Estimation/SuggestionRecord.cs`) captures: `EstimatorId`, `EstimatorVersion`, `SuggestedValue`, `Confidence`, `AcceptedAtUtc?`, `DismissedAtUtc?`. The VM maintains a `SuggestionHistory` list in-memory. Phase 2's audit log will read from this list.

### 7. What stays untouched

- `PipelineOptions`, `AlgorithmMetadata`, `RegionOfInterest`, `RoiProcessor`, `DeblurJobRunner` (except for a small change to accept the noise variance for CLS v2.0).
- All existing deconvolvers except CLS.
- Live-preview loop, ROI processing, WIC codec, SourceBitDepth propagation, cancellation.
- ParamHistory undo/redo — Suggestion acceptance IS a parameter change (via the underlying slider), so accepting a suggestion pushes into the undo stack naturally.

## Files touched

**New in `Deblur.Engine`:**
- `Estimation/CepstralMotionEstimator.cs`
- `Estimation/RadonMotionEstimator.cs`
- `Estimation/DefocusRadiusEstimator.cs`
- `Estimation/WaveletNoiseEstimator.cs`
- `Estimation/MotionEstimate.cs` (record)
- `Estimation/DefocusEstimate.cs` (record)
- `Estimation/NoiseEstimate.cs` (record)
- `Estimation/SuggestionRecord.cs` (record for the audit-log-precursor)

**Modified in `Deblur.Engine`:**
- `ConstrainedLeastSquaresDeconvolver.cs` — v2.0 with optional adaptive γ.

**Modified in `Deblur`:**
- `MainWindow.xaml` — three "Estimate…" buttons + suggestion display panels.
- `MainWindow.xaml.cs` — command handlers.
- `ViewModels/MainViewModel.cs` — suggestion properties, commands, acceptance handlers, `SuggestionHistory`.

**New in `Deblur.Tests`:**
- `Estimation/CepstralMotionEstimatorTests.cs` — synthetic motion at known (angle, length) → estimator returns angle ±5°, length ±20%.
- `Estimation/RadonMotionEstimatorTests.cs` — synthetic motion angle → Radon returns angle ±5°.
- `Estimation/DefocusRadiusEstimatorTests.cs` — synthetic disc PSF radius R → estimator returns R within ±15%.
- `Estimation/WaveletNoiseEstimatorTests.cs` — Gaussian noise σ → estimator returns σ within ±10%.
- `Estimation/SuggestionRecordTests.cs` — timestamps, acceptance/dismissal state transitions.
- `ConstrainedLeastSquaresDeconvolverV2Tests.cs` — with correct noise variance input, v2.0 matches or beats v1.0's MotionRoundTrip on the same signal.

## Constraints

- .NET 8. No new NuGet packages.
- Existing 123 tests remain green. Test count target after 1.d: ~145.
- Every estimator's public entry point is a static method on its class (no state) — the estimators are pure functions.
- Every suggestion carries `EstimatorId` and `EstimatorVersion` for audit provenance (matching the AlgorithmMetadata pattern). Estimator Ids: `cepstral-motion`, `radon-motion`, `bessel-defocus`, `wavelet-mad-noise` — all "1.0".
- **Nothing is silently applied**: `MainViewModel` never populates a slider from an estimator without an examiner "Accept" click. Enforced by unit tests where possible + review checklist.
- Estimator accuracy thresholds:
  - Cepstral motion: angle within ±5°, length within ±20% on synthetic motion length ≥ 6.
  - Radon motion: angle within ±5° on synthetic motion length ≥ 6.
  - Defocus radius: R within ±15% on synthetic disc PSF radius ≥ 3.
  - Wavelet noise: σ within ±10% on synthetic Gaussian noise σ ∈ [0.005, 0.05].
- CLS v2.0 with correct noise variance ≥ CLS v1.0 MotionRoundTrip PSNR on the phase-1.c test image.
- Phase 1.d branches from tag `phase1c` onto `phase1d-automatic-parameter-estimation` (branch created).

## Testing

Unit + integration tests as listed under **Files touched**. Key properties the tests lock in:

- **Directional accuracy**: each estimator returns values within the stated tolerances on synthetic PSFs where the ground truth is known.
- **Confidence signal is meaningful**: give the estimator an image with NO clear motion → confidence should be low (<0.3). Give it strong motion → confidence high (>0.7).
- **Provenance in `SuggestionRecord`**: every record carries `EstimatorId + Version + Value + Confidence + suggestedAtUtc`. Acceptance sets `AcceptedAtUtc`; dismissal sets `DismissedAtUtc`. Only one of the two is ever set.
- **CLS v2.0 with correct noise → improved recovery**: on a known-noise-blurred image, CLS v2.0 given the true noise variance produces PSNR ≥ CLS v1.0 at any K.
- **CLS v2.0 with `noiseVariance == null` → identical to v1.0**: byte-identical output, verified by a regression test.
- **UI safety**: no auto-population happens on estimator click; a separate Accept click is required to populate the slider (verified via the ViewModel-level unit tests).

Manual smoke:
- Load a Motion-blurred image. Click "Estimate motion" → suggestion appears. Click "Accept" → sliders populate. Render.
- Load a defocus-blurred image. Click "Estimate radius" → suggestion. Accept. Render.
- Click "Estimate noise" → K suggestion. Accept. Render.
- Load an image with no obvious blur (already sharp) → click "Estimate motion" → confidence should be LOW (< 0.3) and the UI should surface the low confidence.
- Under CLS: if noise has been estimated + accepted, subsequent renders use adaptive γ. If not, CLS falls back to v1.0's fixed-γ behavior.
- Undo/redo: accepting a suggestion pushes into the undo stack (standard slider-change flow). Dismissing does NOT push.

## Branch

Phase 1.d branches from tag `phase1c` onto `phase1d-automatic-parameter-estimation`.
