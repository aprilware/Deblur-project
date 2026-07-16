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
    _customPsfKernel.SetPsf(EstimatedKernel);
    SelectedBlurType = BlurType.Custom;                 // switch blur type to Custom
    SelectedAlgorithm = AlgorithmType.Wiener;           // switch off blind — user picks their preferred non-blind
    SuggestionHistory.Add(new SuggestionRecord(
        BlindDeconvolutionDeconvolver.MetadataId,       // "blind-cho-lee" — audit trail
        BlindDeconvolutionDeconvolver.MetadataVersion,
        (float[,]?)EstimatedKernel,
        confidence: 1.0f,                                // examiner-accepted; not an algorithmic confidence
        suggestedAtUtc: DateTime.UtcNow)
        with { AcceptedAtUtc = DateTime.UtcNow });
    InvalidateFullResCache();
}

private bool CanAcceptBlindKernel() => EstimatedKernel is not null;
```

The Blind deconvolver's `Metadata.Id / Version` are exposed as public consts so the VM can reference them without a per-call instance. Already done for the other estimators (Phase 1.d).

The `_customPsfKernel` payload is a REFERENCE to the same `float[,]` the blind deconvolver produced — no defensive clone at accept time. Any future edit path (Phase 1.f-2 editor) will clone before mutation.

### 4. UI: Accept button + Custom panel

**PSF display sidebar** (Phase 1.e) gains an "Accept kernel" button below the heatmap, visible only when `SelectedAlgorithm == BlindDeconvolution` AND `EstimatedKernel is not null`. Clicking triggers `AcceptBlindKernelCommand`.

**New Custom blur-type panel** in the sidebar's per-blur-type layout, sibling to Motion/OutOfFocus/Gaussian:
- Small `PsfDisplay` (the same control) showing the current custom PSF.
- Read-only text "Accepted from: blind-cho-lee v1.0 at YYYY-MM-DD HH:MM:SS UTC" reading the most-recent accepted-kernel `SuggestionRecord`.
- No sliders (custom PSF has no scalar parameters). The shared-footer Smoothness (K) slider still applies to the chosen deconvolver.
- A "Clear" button that unsets the custom PSF and switches SelectedBlurType back to Motion.

**Algorithm ComboBox behavior**: after Accept, the ComboBox is set to Wiener (a reasonable default for a known kernel). Examiner can pick any non-blind algorithm; blind is still available in the dropdown for a fresh estimation.

### 5. Runner + live preview

When `SelectedBlurType == BlurType.Custom`:
- `MainViewModel.BuildCurrentParams` constructs `KernelParams` with `Type = BlurType.Custom`. The Length/Radius/Sigma fields are unused for Custom (0 defaults).
- `DeblurJobRunner.RunDeconvolve` dispatches `_kernels[BlurType.Custom].Build(...)` → `CustomPsfKernel.Build` → returns the stored PSF.
- Live preview runs whichever deconvolver the examiner picked (fast: Wiener/Tikhonov/CLS/TV; skipped: RL/Landweber/blind).
- `IsNoOp` needs handling: Custom PSF has no scalar to check for < 1. Return false for Custom unless the stored PSF is null (in which case throw at Build time — see §3 comment).

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
- `DeblurJobRunner.cs` — `IsNoOp` early-return false for Custom.

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
  - `Build_ReturnsExactPsfReference` — SetPsf(a) then Build() returns `a` (reference equality).
  - `SetPsf_Replaces_PreviousPsf`.
- `Deblur.Tests/DeblurJobRunnerTests.cs`:
  - `RenderFullAsync_CustomBlurType_UsesCustomKernel` — set a custom PSF via the injected kernel dictionary, render, verify the deconvolver received it (via a recording stub).
  - Extend the existing stubs for `BlurType.Custom` dispatch coverage.

**No new WPF tests needed** — Accept command's plumbing is engine-side + straightforward VM state. Task 6 smoke covers the UI.

## Constraints

- .NET 8. `net8.0` for `Deblur.Engine` + `Deblur.Tests`. `net8.0-windows` + `UseWPF` for `Deblur` and `Deblur.Wpf.Tests`. Nullable + ImplicitUsings enabled.
- No new NuGet packages.
- `Deblur.Engine` stays UI-free.
- All 174 Phase 1.e tests remain green. Test count target after 1.f-1: ~180.
- **Custom PSF payload is a reference** to the accepted `float[,]` — no defensive clone at accept. Editor phases will clone before mutation.
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
