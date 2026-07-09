# Deblur — Phase 1.b Design (Algorithm Metadata + ROI Processing)

**Date:** 2026-07-10
**Status:** Approved
**Scope:** Add versioned algorithm metadata to `IDeconvolver` (Id / Version / DisplayName / DescriptionMarkdown / LiteratureCitation) for downstream audit-log and report use, plus region-of-interest processing so an examiner can deblur a plate/face/tattoo with padding and feathered blending back into the untouched full plate.

## Context

Phase 1.a landed the correctness scaffolding — linear light, boundary handling, 16-bit I/O, area-average proxy. Every deconvolver now runs on a physically-correct pipeline. What's missing before the audit log (Phase 2) and the court report (Phase 5) can be built:

- **Provenance.** Nothing today tells the audit log which algorithm ran. The `IDeconvolver` interface exposes only `Apply(...)`. Phase 2 needs a stable `Id + Version` per algorithm, and the report needs a plain-language description with a literature citation an examiner can cite in testimony.
- **ROI workflow.** Real casework is a plate on a car, a face at 20 feet, a tattoo on an arm — not the whole frame. The current pipeline processes the whole image, which wastes CPU on regions the examiner doesn't care about and — more importantly — spreads the deconvolution's boundary ringing across content that didn't need deblurring in the first place. ROI processing is the dominant forensic pattern.

Phase 1.b adds both.

## Goal

An examiner sees a "Deblur selected region" toggle in the sidebar. When on, they drag a rectangle over a plate in the preview; the runner processes just that region (padded by the PSF radius to eat boundary artifacts, then feathered back into the untouched full plate) on render and save. When off, behavior is unchanged from Phase 1.a. Every algorithm — Wiener, Tikhonov, TotalVariation — carries a metadata block the audit log and report can read verbatim: a stable identifier, a semantic version, a display name, a plain-language description, and a literature citation.

## Non-goals

- Live-preview ROI. The proxy stays whole-image; ROI applies at render/save time only. (Phase 1.b keeps the runner integration tight; live-preview ROI is a UX-only follow-up.)
- Multiple ROIs per image. One rectangle at a time.
- Non-rectangular ROIs (polygonal, freehand). Rectangle + feather only.
- Spatially-variant PSF (per-tile kernels). Deferred stretch goal from the master roadmap.
- New deconvolution algorithms (Phase 1.c).
- Fixing the deferred items rolled up from Phase 1.a's whole-branch review: BT.601 luma weights applied in linear space; EdgeTaper mean-space asymmetry. Both tracked for later phases.

## Approach

### 1. Algorithm metadata on `IDeconvolver`

Add a new record:

```csharp
public sealed record AlgorithmMetadata(
    string Id,
    string Version,
    string DisplayName,
    string DescriptionMarkdown,
    string LiteratureCitation);
```

Extend `IDeconvolver`:

```csharp
public interface IDeconvolver
{
    AlgorithmMetadata Metadata { get; }
    ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p, PipelineOptions? options = null);
}
```

Each of the three current deconvolvers implements a `public AlgorithmMetadata Metadata { get; } = new(...)` initializer:

- **Wiener**: `Id = "wiener"`, `Version = "1.0"`, `DisplayName = "Wiener filter"`, description covering the frequency-domain MMSE formulation and the K noise-to-signal parameter, `LiteratureCitation` pointing at Wiener (1949) or a modern DSP text.
- **Tikhonov**: `Id = "tikhonov-laplacian"`, `Version = "1.0"`, `DisplayName = "Tikhonov regularization (Laplacian)"`, description of the smoothness penalty via the discrete-Laplacian frequency response, citation Tikhonov (1963).
- **TotalVariation**: `Id = "tv-chambolle"`, `Version = "1.0"`, `DisplayName = "Total Variation (Chambolle post-filter)"`, description of the Wiener warm-start + Chambolle dual projection, citation Chambolle (2004).

Because the audit log (Phase 2) will fingerprint by `Id + Version`, changing the mathematical behavior of any algorithm without bumping `Version` is a forensic-integrity bug — this is why `Version` is a first-class field, not a build number.

### 2. Region of interest

A new record `RegionOfInterest` in `Deblur.Engine`:

```csharp
public sealed record RegionOfInterest(int X, int Y, int Width, int Height, int FeatherRadius)
{
    public bool Contains(int px, int py) => px >= X && px < X + Width && py >= Y && py < Y + Height;
}
```

Coordinates are in **full-resolution image pixels**, not proxy pixels. The UI is responsible for converting from proxy display coordinates to full-res coordinates when the user finishes their drag (using `_proxyScale` in `MainViewModel`, the same conversion that scales `Length`/`Radius`/`Sigma` for render-full).

Feather radius defaults to 12 pixels (empirically enough to hide the seam without noticeably eroding the sharp region — a slider in the UI lets the examiner tune it 0–64 pixels).

### 3. ROI processor

A new pure helper `Deblur.Engine/RoiProcessor.cs`:

```csharp
public static class RoiProcessor
{
    public static ImageBuffer ApplyToRoi(
        ImageBuffer full,
        RegionOfInterest roi,
        int psfRadius,
        Func<ImageBuffer, ImageBuffer> deconvolve);
}
```

Algorithm:

1. Compute the padded extract rectangle: expand ROI by `max(psfRadius, roi.FeatherRadius)` on each side, clamped to the image bounds. Padding = PSF radius so the deconvolver sees enough context; feather radius adds the extra rim we need for the blend.
2. Extract the padded region as a fresh `ImageBuffer`. Where the pad crosses the source image boundary, values are filled by reflection (same bounce math as `BoundaryFill.Pad` with `BoundaryMode.Reflect`). Non-reflect modes are not exposed here — the ROI extract's boundary handling is an implementation detail of the ROI helper, not a user-visible option, and reflect is the safest default for a ROI likely to be an interior region.
3. Call `deconvolve(paddedExtract)` — the caller supplies the exact deconvolution closure so ROI processing composes cleanly with linear-light, luminance-only, and future algorithm choices.
4. Build the alpha mask: alpha = 1 inside the un-feathered ROI core, ramps to 0 linearly (cosine actually — see below) across the `FeatherRadius`-wide band at the ROI edge, alpha = 0 outside.
5. Blit the deconvolved padded region back into a clone of `full` using the alpha mask: `out[i] = alpha[i] * deconv[i] + (1 - alpha[i]) * full[i]`.

Feather ramp uses a raised cosine (Hanning-like): `alpha(d) = 0.5 * (1 - cos(π * d / FeatherRadius))` where `d` is distance from the ROI edge inward (0 at the edge, `FeatherRadius` at the deep interior). Same window shape as `EdgeTaper` for consistency.

If `FeatherRadius == 0`, the blend is a hard replace (all-or-nothing) — useful for testing but visually harsh.

### 4. Runner integration

`DeblurJobRunner` gains a nullable ROI field and a public setter:

```csharp
public RegionOfInterest? Roi { get; set; }
```

Only `RenderFullAsync` consults `Roi`. When set:

```csharp
if (_roi is null) {
    return await RunDeconvolveFull(fullRes, scaledParams);
} else {
    return RoiProcessor.ApplyToRoi(
        fullRes, _roi, psfRadius: EstimatePsfRadius(scaledParams),
        deconvolve: padded => RunDeconvolve(padded, scaledParams));
}
```

`EstimatePsfRadius` is straightforward for each blur type: Motion → `ceil(Length / 2)`, OutOfFocus → `ceil(Radius)`, Gaussian → `ceil(3 * Sigma)`.

`SourceBitDepth` propagates because `RunDeconvolve` re-stamps it (Phase 1.a's critical-fix invariant).

Live preview is unaffected — `WorkerLoop` continues to process the whole proxy every request. The UI still displays the whole preview so the examiner can see context, but the eventual full-res render is ROI-only.

### 5. UI

**Sidebar toggle** in `MainWindow.xaml`: a checkbox "Deblur selected region" plus a feather-radius slider (0–64, default 12). Bound to two new `MainViewModel` observable properties.

**PreviewCanvas ROI mode**:
- When the toggle is on and the mode is "Roi", left-drag on the preview draws a rubber-band rectangle over the image.
- The arrow-drag flow (Motion) coexists — it fires only when the algorithm is Motion AND no ROI is being drawn. The mode is exclusive: only one interaction at a time.
- The current ROI stays visible as a persistent overlay (thin white rectangle with hairline shadow) until the user drags a new one or unchecks the toggle.
- Coordinates convert to full-res via `_proxyScale`.

`MainViewModel` gains:
- `[ObservableProperty] private bool _roiEnabled;`
- `[ObservableProperty] private int _roiFeatherRadius = 12;`
- `[ObservableProperty] private RegionOfInterest? _selectedRoi;`
- `CommitRoi(int x, int y, int w, int h)` — called by `PreviewCanvas` on drag completion; converts proxy → full-res, stores in `_selectedRoi`, invalidates the full-res cache.

Before each render, the VM pushes the current ROI into the runner: `_runner.Roi = _roiEnabled ? _selectedRoi : null;`.

### 6. What stays untouched

- Live preview loop (whole-image, unchanged).
- All Phase 1.a options — linear light, edge taper, boundary mode, luminance-only.
- Arrow-drag flow for Motion (still works when ROI mode is off; suppressed when a rubber-band is in progress).
- `ParamHistory` undo/redo. ROI selection is NOT part of `KernelParams` — it's a separate render-target selection, not an algorithm parameter, so it doesn't participate in the undo stack. (Discussed and deliberately excluded — the audit log records ROI at render-time; that's where forensic reproducibility for ROI lives.)

## Files touched

**New in `Deblur.Engine`:**
- `AlgorithmMetadata.cs`
- `RegionOfInterest.cs`
- `RoiProcessor.cs`

**Modified in `Deblur.Engine`:**
- `IDeconvolver.cs` — gain `Metadata` property.
- `WienerDeconvolver.cs`, `TikhonovDeconvolver.cs`, `TotalVariationDeconvolver.cs` — implement `Metadata`.
- `DeblurJobRunner.cs` — nullable `Roi` property, `RenderFullAsync` dispatches through `RoiProcessor` when set.

**Modified in `Deblur`:**
- `Controls/PreviewCanvas.xaml`, `PreviewCanvas.xaml.cs` — ROI rubber-band mode + persistent overlay + `RoiDrawn` event.
- `MainWindow.xaml` — sidebar toggle + feather slider.
- `ViewModels/MainViewModel.cs` — three new properties + `CommitRoi` + runner ROI plumbing.

**New in `Deblur.Tests`:**
- `AlgorithmMetadataTests.cs` — every deconvolver's Metadata has non-empty Id/Version/DisplayName/DescriptionMarkdown/LiteratureCitation; Ids are unique; Versions are non-empty semver-shaped.
- `RegionOfInterestTests.cs` — `Contains` bounds; feather clamping.
- `RoiProcessorTests.cs`:
  - ROI-processed image equals full-image deconvolution inside the un-feathered ROI core (PSNR > 40 dB).
  - Outside-ROI region is bit-identical to the input (no accidental writes).
  - Feather band blends smoothly (no discontinuity at ROI edge — max local gradient bounded).
  - `FeatherRadius=0` produces a hard replace with the correct pixel values.
- `RoiRunnerIntegrationTests.cs`:
  - `RenderFullAsync` with `Roi=null` matches Phase 1.a behavior (regression guard).
  - `RenderFullAsync` with `Roi` set routes through `RoiProcessor` and preserves `SourceBitDepth`.
- `Deblur.Tests/DeblurJobRunnerTests.cs` (stub deconvolver classes) — add `Metadata` implementations to keep the file compiling.

## Constraints

- .NET 8. `net8.0` for `Deblur.Engine` + `Deblur.Tests`. `net8.0-windows` + `UseWPF` for `Deblur`. Nullable + ImplicitUsings enabled.
- No new NuGet packages.
- All Phase 1.a tests remain green. Test count target: 91 → ~110 (~19 new).
- `AlgorithmMetadata.Version` uses semantic-versioning-shaped strings ("1.0", "1.1.3"). Changing algorithm math without bumping `Version` is a forensic-integrity bug.
- `RegionOfInterest` coordinates are always full-resolution pixels — the UI converts from proxy to full-res before calling `MainViewModel.CommitRoi`.
- `Deblur.Engine` stays UI-free.
- Phase 1.b branches from tag `phase1a` onto `phase1b-algorithm-metadata-roi` (branch created).

## Testing

Unit + integration tests as listed under **Files touched**. Key correctness properties the tests must lock in:

- **Metadata surface**: every deconvolver exposes non-empty metadata; the three Ids (`wiener`, `tikhonov-laplacian`, `tv-chambolle`) are stable — a `KnownIds` test asserts them literally so a rename requires an intentional test update.
- **ROI equivalence in the core**: for a large-enough ROI (say 128×128 with feather 12), the pixels in the un-feathered core after `RoiProcessor.ApplyToRoi` must match a full-image deconvolution of the same input inside that same core within PSNR > 40 dB. This is the property that makes ROI processing forensically defensible: "you get the same result you would have gotten from a whole-image run inside the region you selected."
- **Outside-ROI immutability**: pixels outside the feather band are byte-identical to the input. The examiner's expectation is "I selected this region; nothing else changed."
- **`SourceBitDepth` invariant** (from Phase 1.a): 16-bit input → 16-bit result on both the ROI and non-ROI paths.

Manual smoke:
- Toggle "Deblur selected region" on. Drag a rectangle over a plate in the preview.
- Rectangle stays visible; can be redrawn.
- Full-res render + Save produces an image with the plate sharpened and the rest of the frame untouched.
- Toggle off → whole-image behavior identical to Phase 1.a.
- Feather slider at 0 → visible seam at ROI boundary; at 32 → seamless blend.
- Undo/redo still only walks algorithm parameter history (ROI selection intentionally excluded).

## Branch

Phase 1.b branches from tag `phase1a` onto `phase1b-algorithm-metadata-roi`.
