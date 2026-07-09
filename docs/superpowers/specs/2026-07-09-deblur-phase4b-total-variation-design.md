# Deblur — Phase 4b Design (Total Variation Deconvolver)

**Date:** 2026-07-09
**Status:** Approved
**Scope:** Total Variation deconvolution as a third `IDeconvolver`, deferred from the original phase 4.

## Context

Phase 4 shipped Tikhonov alongside Wiener; Total Variation was deferred. All remaining feature phases (5a-5e) have shipped. This spec adds TV as a third algorithm the user can pick from the Algorithm dropdown.

## Goal

The user picks "TotalVariation" from the Algorithm dropdown and the deconvolution runs Wiener followed by 20 iterations of Chambolle-Pock TV denoising in the spatial domain. Edges stay sharp; noise is suppressed. The parameter slider's label swaps to "Regularization (λ)". Live proxy preview + full-res render + Save all work identically to the other two algorithms, just slower.

## Non-goals

- Explicit TV-deconvolution via ADMM or split-Bregman (would require re-plumbing the FFT path). We use Wiener as a warm start and TV as a post-filter — a common practical simplification.
- User-tunable iteration count or step size.
- Extraction of shared reflect-pad/FFT scaffolding — TV doesn't add new FFT code, so the phase-2/4-era reviewer recommendation stays deferred.
- Batch processing (phase 5d).

## Approach

Add `TotalVariationDeconvolver : IDeconvolver`. Its `Apply(input, psf, params)`:
1. Runs `new WienerDeconvolver().Apply(input, psf, params)` to get an initial deblurred estimate.
2. Applies 20 iterations of Chambolle-Pock projected-gradient TV denoising per channel on that estimate. λ_TV is derived from `params.K` (multiplied by 50 to bring K's 0.0001-0.1 slider range into the 0.005-5 TV lambda range that produces visible effects).
3. Returns the smoothed result, clamped `[0, 1]`.

Add `AlgorithmType.TotalVariation`. `MainViewModel`'s deconvolver dictionary gains a third entry. `AlgorithmToSmoothnessLabelConverter` maps TV to "Regularization (λ)" (same label as Tikhonov).

## Files touched

- `Deblur.Engine/AlgorithmType.cs` — append `TotalVariation` enum value.
- `Deblur.Engine/TotalVariationDeconvolver.cs` (new) — Wiener + Chambolle post-filter.
- `Deblur.Tests/TotalVariationDeconvolverTests.cs` (new) — 3 tests (Motion round-trip PSNR, delta-over-blurred, no NaN/Inf on extreme params).
- `Deblur/ViewModels/MainViewModel.cs` — third dictionary entry.
- `Deblur/Converters/AlgorithmToSmoothnessLabelConverter.cs` — TV → "Regularization (λ)".

## Constraints

- .NET 8. `net8.0-windows` WPF; `net8.0` Engine + Tests. Nullable + ImplicitUsings enabled.
- No new NuGet packages.
- Chambolle-Pock TV denoising, per-channel, 20 iterations, step size τ = 0.125.
- λ_TV = `params.K * 50f` internally (empirical scaling to bring K's slider range into a visible TV effect).
- No `KernelParams` change; all existing construction sites continue to compile unchanged.
- All 61 phase-5c tests remain green; new tests bring total to 64.
- Phase 4b branches from tag `phase5c` onto branch `phase4b-total-variation`.

## Testing

- `TotalVariationDeconvolverTests.RoundTrip_RecoversCheckerboard_AbovePsnrThreshold` — Motion PSF length 12, K=0.005 → `PSNR > 15 dB` (matches phase-2 OutOfFocus + phase-4 Tikhonov's Motion floor).
- `Gaussian_RoundTrip_RecoversAbovePsnrThreshold` — Gaussian σ=2, dual assertion `> 15 dB` AND `> blurred + 2.5 dB` (matches phase-3 Gaussian test shape).
- `ExtremeParams_NoNaNOrInfInOutput` — Length=100 Motion + K=1e-6 → verify no NaN/Inf survives to output.

Manual smoke:
- Algorithm dropdown gains a third option "TotalVariation".
- Picking TV re-labels the shared-footer parameter to "Regularization (λ)".
- Preview updates under TV (slower than Wiener/Tikhonov — expect 1-3 s per slider tick on a 400 KP proxy).
- Under Motion / OutOfFocus / Gaussian, TV produces a smoother result than Wiener at the same slider position.
- Full-res render + Save under TV works. Reopen saved file — TV-processed output.
- Existing behaviors (undo/redo, zoom/pan, cancel, shortcuts) unchanged.

## Branch

Phase 4b branches from tag `phase5c` onto `phase4b-total-variation`.
