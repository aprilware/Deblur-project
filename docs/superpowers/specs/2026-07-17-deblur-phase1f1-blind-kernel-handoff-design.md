# Deblur — Phase 1.f-1 Design (Blind Kernel Accept + Handoff)

**Date:** 2026-07-17
**Status:** Approved
**Scope:** Close the roadmap §3.2 handoff gap surfaced in Phase 1.e smoke: examiner accepts a blind-estimated kernel and applies it via a **non-blind** deconvolver (their choice) for the evidentiary output. Adds `BlurType.Custom` + `CustomPsfKernel` so any existing deconvolver can consume an arbitrary kernel. First slice of Phase 1.f — the PSF editor + PSF-from-image extraction + kernel library remain future phases.

## Context

Phase 1.e shipped blind deconvolution end-to-end (blind estimates AND deblurs on the same run). Smoke exposed the real UX gap: **the recovered kernel has an unrecoverable spatial-shift ambiguity** (Levin 2011), so the end-to-end deblur reads as "same-looking as input" even when the kernel SHAPE is correct. The PSF display shows the recovered kernel, but the examiner had no way to use it — the algorithm dropdown couldn't switch without re-running the blind loop.

The roadmap §3.2 always intended blind as a suggestion mechanism: **blind estimates → examiner accepts/edits/rejects → accepted kernel runs through a non-blind algorithm for the evidentiary output.** Phase 1.f-1 lands the accept + handoff. Edit (interactive PSF editor) and reject-alternatives (PSF-from-image extraction + kernel library) defer to later 1.f slices.

## Goal

An examiner loads a blurred image, picks BlindDeconvolution, renders, and sees the estimated kernel in the sidebar. They click **Accept kernel**. The algorithm dropdown switches to a non-blind default (Wiener); the deconvolver now uses the accepted kernel instead of one built from the Motion/OutOfFocus/Gaussian sliders. The examiner can pick any non-blind algorithm (Wiener, Tikhonov, TV, RL, CLS, Landweber) and tune its parameters — the same accepted kernel is applied each time. Live preview works because non-blind algorithms are fast. Save-As stamps the accepted kernel into the audit-log-precursor `SuggestionHistory` so Phase 2's report will surface it as evidentiary provenance.

## Non-goals

- **Interactive PSF editor** (drawable grid to hand-refine the kernel). Phase 1.f-2.
- **PSF-from-image extraction** (box a specular streak / point source, use it as the kernel). Phase 1.f-2 or 1.f-3.
- **Kernel library** (save/load named kernels per case). Phase 1.f-3.
- **Spatially variant blur**. Deferred beyond phase 1.
- **Manual kernel entry via numeric grid**. If the examiner wants a specific kernel they'll wait for the PSF editor.
- **Re-running blind starting from an accepted kernel as seed** (kernel refinement). Deferred.
- Fixing rolled-up items from Phase 1.e review: determinism test, extreme-parameters test, BT.601 luma weights on linear, adaptive finest-scale window, defocus shape-aware prior, live-preview blind via cached kernel.

## Approach

### 1. New `BlurType.Custom` + `CustomPsfKernel`

New enum value:

```csharp
public enum BlurType { Motion, OutOfFocus, Gaussian, Custom }
```

New `Deblur.Engine/CustomPsfKernel.cs` implementing `IBlurKernel`:

```csharp
public sealed class CustomPsfKernel : IBlurKernel
{
    private float[,]? _psf;

    /// <summary>
    /// Set the current custom PSF. Not thread-safe; assumes single-threaded runner
    /// invocation (matches existing DeblurJobRunner discipline).
    /// </summary>
    public void SetPsf(float[,] psf) => _psf = psf;

    public float[,] Build(KernelParams p)
    {
        if (_psf is null)
            throw new InvalidOperationException("CustomPsfKernel: no PSF set. Call SetPsf first.");
        return _psf;
    }
}
```

The kernel is single-instance, stateful. `MainViewModel` holds a reference and calls `SetPsf` when the examiner accepts a kernel (from blind or, in future phases, from the editor / library). All existing deconvolvers work against it unchanged — they receive a `float[,]` psf, no interface changes.

### 2. Deconvolver dictionary registration

`MainViewModel`'s kernel dictionary gains a `[BlurType.Custom] = _customPsfKernel` entry alongside Motion/OutOfFocus/Gaussian. The `_customPsfKernel` field holds the single instance; the accepted PSF is set on it.

### 3. Accept-kernel command in the VM

New `[RelayCommand]` `AcceptBlindKernel`:

```csharp
[RelayCommand(CanExecute = nameof(CanAcceptBlindKernel))]
private void AcceptBlindKernel()
{
    if (EstimatedKernel is null) return;

    // Clone the kernel TWICE (once for the runtime slot, once for the audit
    // record). Audit records must be immutable by construction — not by a
    // future-phase promise to clone before editing. Any Phase 1.f-2 editor
    // will operate on its OWN copy of the CustomPsfKernel payload and never
    // mutate the SuggestionRecord's snapshot.
    var runtimeCopy = CloneKernel(EstimatedKernel);
    var auditCopy   = CloneKernel(EstimatedKernel);

    _customPsfKernel.SetPsf(runtimeCopy);
    _customPsfSequence++;                                 // §5.2 kernel identity
    SelectedBlurType = BlurType.Custom;
    SelectedAlgorithm = AlgorithmType.Wiener;             // hand-off default; examiner can switch

    // The estimator's own algorithmic confidence (if we tracked it — Phase 1.e
    // doesn't surface a per-run confidence for blind, so this is null).
    // Examiner acceptance is encoded by AcceptedAtUtc, NOT by a confidence of 1.0.
    SuggestionHistory.Add(new SuggestionRecord(
        BlindDeconvolutionDeconvolver.MetadataId,
        BlindDeconvolutionDeconvolver.MetadataVersion,
        (float[,]?)auditCopy,
        confidence: (float?)null,                          // estimator-side confidence, not examiner side
        suggestedAtUtc: DateTime.UtcNow)
        with { AcceptedAtUtc = DateTime.UtcNow });
    InvalidateFullResCache();
}

private bool CanAcceptBlindKernel() => EstimatedKernel is not null;

private static float[,] CloneKernel(float[,] src)
{
    int h = src.GetLength(0), w = src.GetLength(1);
    var dst = new float[h, w];
    for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            dst[y, x] = src[y, x];
    return dst;
}
```

The Blind deconvolver's `Metadata.Id / Version` are exposed as public consts so the VM can reference them without a per-call instance. Already done for the other estimators (Phase 1.d).

`SuggestionRecord.Confidence` becomes `float?` (nullable) to distinguish "no algorithmic confidence available" from "confidence 1.0" — testimony accuracy matters here. Existing estimator records (cepstral, defocus, wavelet-noise) continue to populate a non-null confidence value; only blind's kernel-accept records carry null.

### 4. UI: Accept button + Custom panel

**PSF display sidebar** (Phase 1.e) gains an "Accept kernel" button below the heatmap, visible only when `SelectedAlgorithm == BlindDeconvolution` AND `EstimatedKernel is not null`. Clicking triggers `AcceptBlindKernelCommand`.

**New Custom blur-type panel** in the sidebar's per-blur-type layout, sibling to Motion/OutOfFocus/Gaussian:
- Small `PsfDisplay` (the same control) showing the current custom PSF.
- Read-only text "Accepted from: blind-cho-lee v1.0 at YYYY-MM-DD HH:MM:SS UTC" reading the most-recent accepted-kernel `SuggestionRecord`.
- No sliders (custom PSF has no scalar parameters). The shared-footer Smoothness (K) slider still applies to the chosen deconvolver.
- A "Clear" button that switches SelectedBlurType back to Motion. **The stored PSF stays in `_customPsfKernel` untouched** — no null race with an in-flight WorkerLoop preview. The next Accept replaces it; a Clear followed by a switch back to Custom (via re-Accept) is the intended path.

**Algorithm ComboBox behavior**: after Accept, the ComboBox is set to Wiener (a reasonable default for a known kernel). Examiner can pick any non-blind algorithm; blind is still available in the dropdown for a fresh estimation.

### 5. Runner + live preview

When `SelectedBlurType == BlurType.Custom`:
- `MainViewModel.BuildCurrentParams` constructs `KernelParams` with `Type = BlurType.Custom`. The Length/Radius/Sigma fields are unused for Custom (0 defaults).
- `DeblurJobRunner.RunDeconvolve` dispatches `_kernels[BlurType.Custom].Build(...)` → `CustomPsfKernel.Build` → returns the stored PSF.
- Live preview runs whichever deconvolver the examiner picked (fast: Wiener/Tikhonov/CLS/TV; skipped: RL/Landweber/blind).
- `IsNoOp` returns false for `BlurType.Custom` (see §7 for the null-PSF race handling).

### 5.1 Live-preview PSF proxy scaling

The live preview runs the deconvolver against the proxy (typically ¼ resolution). A kernel accepted from a full-res blind run has pixel dimensions in FULL-resolution space; applying it as-is to the proxy would over-blur by `1/_proxyScale` and produce a preview that doesn't match the eventual full-res render — a testimony-adjacent lie.

`CustomPsfKernel.Build` receives the runner's already-scaled `KernelParams`, but it can't infer from those what scale to apply — the whole point of custom PSFs is that they're arbitrary shapes independent of the parametric sliders.

Two paths:
- **A. Full-res-only for Custom** (simplest). Live-preview WorkerLoop skips `BlurType.Custom` the same way it skips RL/Landweber/blind. Examiner tunes K with a Render click each time.
- **B. Area-resample the kernel by `_proxyScale`** (better UX). `CustomPsfKernel` carries the FULL-res kernel; `Build` (or the runner) scales it to the current effective resolution. Preview matches render. Requires a downsample helper and a proxy-vs-full agreement test.

**Chosen: B.** Ship the proxy-scaled preview path with an agreement test: on a fixed accepted kernel, the proxy-preview output must match the full-res output resampled to proxy dimensions within a small PSNR tolerance. Same shape as Phase 1.a's `AreaResample`-based proxy scaling — the kernel is downscaled via area-averaging with a post-downscale renormalize-to-sum-1.

Implementation: `CustomPsfKernel` gets a `bool ScaleForProxy { get; set; }` toggle and a `float ProxyScale` the VM sets before dispatching a preview vs. a full-res render. Or — cleaner — extend `KernelParams` with the kernel-identity field per amendment (3) below, and let the runner pass an effective-scale factor via existing plumbing. Task-time decision by the implementer; either works.

### 5.2 Kernel identity in KernelParams

To make the full-res cache, undo history, and future Phase 2 recipes distinguish between different accepted kernels, `KernelParams` gains a nullable `KernelId` field (identity token). For `Type == Custom`, `KernelId` MUST be non-null and monotonic per accept: `MainViewModel` maintains a `_customPsfSequence` counter that increments on every `AcceptBlindKernel` / (future) editor commit. Comparing two `KernelParams` for cache equality now correctly distinguishes "same custom slot but different kernel accepted mid-session."

Content-hash was considered (SHA-256 of the `float[,]` payload) — rejected as heavier than needed for cache-key purposes. Phase 2's recipe format can carry a full hash for cross-session provenance; per-session identity is a sequence integer.

### 6. What stays untouched

- Blind's end-to-end deblur still runs when picked — the accept flow is additive, doesn't remove existing paths.
- All existing deconvolvers unchanged.
- ROI processing works with Custom (blind + ROI → accept → non-blind + ROI runs the accepted kernel on the ROI extract).
- Audit-log-precursor `SuggestionHistory` extended with the accepted-kernel record — Phase 2's serializer will pick it up.
- 16-bit source depth preservation still enforced by the runner's re-stamp.

## Files touched

**New in `Deblur.Engine`:**
- `CustomPsfKernel.cs` — stateful `IBlurKernel` carrying a `float[,]`.

**Modified in `Deblur.Engine`:**
- `BlurType.cs` — append `Custom`.
- `BlindDeconvolutionDeconvolver.cs` — expose `Metadata.Id / Version` as public consts (`MetadataId = "blind-cho-lee"`, `MetadataVersion = "1.0"`) for VM reference. Metadata behavior unchanged.
- `DeblurJobRunner.cs` — `IsNoOp` early-return false for Custom; runner pathways for `BlurType.Custom` with proxy-scale awareness (see §5.1).
- `KernelParams.cs` — additive `int? KernelId` field (nullable, default null). Only populated for `Type == Custom`. Cache-equality and undo-history now correctly distinguish "same custom slot, different accepted kernel" via this id.
- `Estimation/SuggestionRecord.cs` — `Confidence` field becomes `float?` (nullable). Existing estimator records continue to populate non-null; blind's kernel-accept records carry null (examiner acceptance encoded by `AcceptedAtUtc`, not by a fabricated confidence).

**Modified in `Deblur`:**
- `ViewModels/MainViewModel.cs`:
  - Add `private readonly CustomPsfKernel _customPsfKernel;` field.
  - Register `[BlurType.Custom] = _customPsfKernel` in the kernels dictionary.
  - `AcceptBlindKernelCommand` with `CanAcceptBlindKernel` predicate.
  - `ClearCustomPsfCommand`.
  - `[ObservableProperty]` `_customPsfAcceptedRecord` (nullable `SuggestionRecord`) for the "Accepted from…" display.
  - `IsCustomSelected` computed property (mirrors existing IsMotionSelected pattern).
- `MainWindow.xaml`:
  - Add Accept-kernel button below `<controls:PsfDisplay>`.
  - Add new Custom panel gated on `IsCustomSelected`.
- `Controls/PreviewCanvas.xaml{,.cs}` — no changes (Custom PSF doesn't drive arrow-drag).
- `Converters/AlgorithmToSmoothnessLabelConverter.cs` — no change (Custom applies to non-blind algorithms; their labels drive).

**New in `Deblur.Tests`:**
- `CustomPsfKernelTests.cs`:
  - `Build_WithoutSetPsf_Throws` — `InvalidOperationException`.
  - `Build_ReturnsStoredPsf` — SetPsf(a) then Build() returns `a`'s content (equality by values; may not be reference equality if runtime scales for proxy).
  - `SetPsf_Replaces_PreviousPsf`.
  - `ProxyScaling_MatchesFullResWithinTolerance` — set a 31×31 kernel; render on 256×256 input at full-res vs. 64×64 proxy; verify the proxy output matches the full-res output resampled to 64×64 within a PSNR threshold (e.g., ≥ 30 dB). This locks in the §5.1 preview/render agreement contract.
- `Deblur.Tests/DeblurJobRunnerTests.cs`:
  - `RenderFullAsync_CustomBlurType_UsesCustomKernel` — set a custom PSF via the injected kernel dictionary, render, verify the deconvolver received it (via a recording stub).
  - Extend the existing stubs for `BlurType.Custom` dispatch coverage.
  - `KernelParams_DifferentKernelIds_DoNotEqual` — small record-equality regression for the new `KernelId` field.
- `Deblur.Tests/BlindDeconvolutionDeconvolverTests.cs` (extend):
  - **`Deterministic_TwoConsecutiveRuns_ProduceByteIdenticalKernelAndOutput`** — RESTORED from the Phase 1.e spec's Testing section (was rolled up as deferred at merge time). Run blind twice on the same input; assert `LastEstimatedKernel` values byte-identical and Apply's returned `ImageBuffer` byte-identical. Forensic reproducibility requirement.

**No new WPF tests needed** — Accept command's plumbing is engine-side + straightforward VM state. Manual smoke covers the UI.

## Constraints

- .NET 8. `net8.0` for `Deblur.Engine` + `Deblur.Tests`. `net8.0-windows` + `UseWPF` for `Deblur` and `Deblur.Wpf.Tests`. Nullable + ImplicitUsings enabled.
- No new NuGet packages.
- `Deblur.Engine` stays UI-free.
- All 174 Phase 1.e tests remain green. Test count target after 1.f-1: ~182.
- **Custom PSF payload is CLONED at accept time** (per amendment 2). Two clones: one for the runtime slot (`CustomPsfKernel`), one for the audit `SuggestionRecord`. Audit records are immutable by construction — no promise that future edit paths will "clone before mutating." Both runtime + audit clones are independent from `EstimatedKernel` and from each other.
- **`KernelParams.KernelId`** (nullable `int?`) added for identity of custom kernels. Monotonic per-session sequence maintained by `MainViewModel._customPsfSequence`; incremented on every Accept. Cache-equality and undo history distinguish between accepted kernels via this id.
- **`SuggestionRecord.Confidence` becomes `float?`**. Existing estimator paths continue to populate non-null. Blind's kernel-accept records store null: examiner acceptance is encoded by `AcceptedAtUtc`, not by a fabricated 1.0.
- **Clear does not null the stored PSF** (per amendment 4). Switches `SelectedBlurType` back to Motion only. Avoids a race between Clear and an in-flight WorkerLoop preview; the stored PSF stays until the next Accept replaces it. Any switch back to Custom (without re-Accept) is prevented by the UI (Custom panel only shows when a Custom PSF exists AND the user got there via Accept — the type combobox doesn't offer Custom directly).
- `BlurType.Custom` construction sites: `KernelParams` currently has `Type` as first-class positional field — additive enum value, all existing constructions still compile.
- **`BuildCurrentParams` uses `Type = BlurType.Custom`** only when `SelectedBlurType == BlurType.Custom`. The Motion/OutOfFocus/Gaussian selection paths continue to route via those types.
- **`IsNoOp` returns false for `BlurType.Custom`** (the presence of a Custom PSF means the user intentionally set a kernel). If `_customPsfKernel._psf` is null, `Build` throws — this happens only if the VM's state and the runner's state have diverged, which shouldn't be reachable via the UI.
- Accept command guarded by `CanExecute`: `EstimatedKernel is not null`. Button disabled until blind produces a kernel.
- Phase 1.f-1 branches from tag `phase1e` onto `phase1f1-blind-kernel-handoff` (already created).

## Testing

Unit tests as listed under **Files touched**. Key correctness properties:

- **`CustomPsfKernel.Build` returns the exact reference** set via `SetPsf`. No defensive clone.
- **`CustomPsfKernel.Build` throws when no PSF is set** — protects against runner/VM state divergence.
- **`DeblurJobRunner.RenderFullAsync` with `BlurType.Custom`** dispatches to `_kernels[BlurType.Custom].Build(...)`, whose returned PSF flows into the chosen deconvolver's `Apply`.
- **AlgorithmMetadataTests unchanged**: the seven production deconvolvers still pass. No new deconvolver added in this phase.

Manual smoke:
- Load a motion-blurred image. Pick BlindDeconvolution → Render → PSF display shows kernel.
- Click "Accept kernel" → algorithm dropdown switches to Wiener; blur-type effectively becomes Custom (sidebar shows the Custom panel with the accepted kernel).
- Live preview updates as user tunes the Smoothness slider — Wiener runs on the accepted kernel.
- Switch algorithm to Tikhonov → live preview updates; K slider still tunes. Same for RL/Landweber (full-res only) / CLS / TV.
- Click "Clear" on the Custom panel → sidebar reverts to Motion, custom PSF cleared, live preview updates.
- Blind still works as end-to-end (pick blind → render). Accept still available after each blind render.
- ROI + Custom: enable ROI, draw a rectangle, render with Custom + Wiener → the accepted kernel is applied to the ROI extract.
- 16-bit input still exports 16-bit PNG under Custom + any non-blind algorithm.
- Undo/redo, save-as, arrow drag (Motion only) all still work.
- SuggestionHistory retains the accepted-kernel record with the correct `AcceptedAtUtc` timestamp.

## Branch

Phase 1.f-1 branches from tag `phase1e` onto `phase1f1-blind-kernel-handoff`.
