# Deblur — Phase 2 Design (Out-of-Focus Blur)

**Date:** 2026-07-04
**Status:** Approved
**Scope:** Phase 2 of the phased Deblur roadmap. This spec covers phase 2 only.

## Context and phasing

Phase 1 shipped an end-to-end motion-blur deconvolver (open → drag arrow → live Wiener preview → full-res render → save). The blur-type dropdown is scaffolded with Motion / Out-of-Focus / Gaussian, but only Motion is functional; picking either of the other two shows a "Coming soon" panel.

Phase 2 makes the **Out-of-Focus** option functional. The rest of the roadmap is unchanged:

- **Phase 1 (shipped, tag `phase1`)** — Motion blur end-to-end.
- **Phase 2 (this spec)** — Out-of-focus blur: disk-kernel PSF driven by a radius slider.
- **Phase 3** — Gaussian blur (sigma slider).
- **Phase 4** — Tikhonov and Total Variation deconvolution.
- **Phase 5** — Polish: zoom/pan, keyboard shortcuts, batch, undo, cancellation.

## Goal

A user who picks "OutOfFocus" from the blur-type dropdown gets a Radius + Smoothness slider pair in the sidebar. Dragging Radius drives a Wiener deconvolution against a disk PSF, live-previewed on the proxy and rendered at full resolution on save — identical UX affordances to Motion, minus the drag arrow.

## Non-goals

- Direction-aware defocus (astigmatism, anamorphic bokeh) — the disk is symmetric.
- Spatially-varying deconvolution — a single global PSF is applied to the entire frame.
- Optical realism beyond a hard-edge disk (no Airy discs, no bokeh shape files).
- Anything from phases 3–5.
- Any phase-1 minor punch-list item **except** the `BlurType.Motion` hardcoding, which phase 2 must fix.

## Approach

Extend the existing three-project structure. The `IBlurKernel` interface designed in phase 1 was built for this — add a second implementation and route by `BlurType` in the runner. No changes to `WienerDeconvolver`, `FftAdapter`, `ImageCodec`, `ImageBuffer`, or the Wiener math.

The one pipeline change: `DeblurJobRunner`'s constructor gains a kernel dictionary keyed by `BlurType`, and the worker loop / `RenderFullAsync` look up the kernel by `p.Type`. `MainViewModel` builds and injects the dictionary in its constructor and stops hardcoding `BlurType.Motion` in `PushCurrentParams` / `EnsureFullResRenderedAsync` — a phase-1 review finding that this phase resolves as a side effect.

## Solution layout

Same three projects; no new project or NuGet reference:

```
Deblur.sln
├── Deblur/            ← WPF app (adds OutOfFocus sidebar panel + Radius observable)
├── Deblur.Engine/     ← adds OutOfFocusBlurKernel; Radius field on KernelParams; dictionary-based runner
└── Deblur.Tests/      ← adds OutOfFocusBlurKernelTests + OutOfFocus Wiener round-trip + runner routing test
```

## Components

### Engine changes

**New: `Deblur.Engine/OutOfFocusBlurKernel.cs`**

Implements `IBlurKernel`. Produces a square 2D kernel of size `2r+1` (where `r = ceil(Radius)`) containing an anti-aliased hard-edge disk of radius `Radius` centered on the kernel. Each pixel's weight is `saturate(Radius + 0.5 - dist)`, where `dist` is the Euclidean distance from the kernel center — this gives full weight inside the disk, a smooth 1-pixel-wide falloff at the edge (matching the anti-aliasing style of `MotionBlurKernel`), and zero outside. The whole kernel is normalized to sum = 1. `Build` throws `ArgumentOutOfRangeException` for `Radius < 0`.

**Change: `Deblur.Engine/KernelParams.cs`**

Append one field to the existing record struct:

```csharp
public readonly record struct KernelParams(
    BlurType Type,
    float Angle,
    float Length,
    float Smoothness,
    float Radius);
```

Motion ignores `Radius`; OutOfFocus ignores `Angle` and `Length`. Adding `Radius` at the end (rather than mid-list) minimizes downstream call-site churn.

**Change: `Deblur.Engine/DeblurJobRunner.cs`**

Constructor becomes:

```csharp
public DeblurJobRunner(
    IReadOnlyDictionary<BlurType, IBlurKernel> kernels,
    IDeconvolver deconvolver);
```

The runner stores the dictionary and looks up `kernels[p.Type]` inside `WorkerLoop` and `RenderFullAsync`. The Length < 1 short-circuit in phase 1 becomes:

```csharp
bool isNoOp = p.Type switch
{
    BlurType.Motion      => p.Length < 1f,
    BlurType.OutOfFocus  => p.Radius < 1f,
    _                    => true,   // Gaussian not implemented → treat as no-op
};
```

When `isNoOp`, emit the input as-is (unchanged from phase 1's behavior for Motion at Length < 1). `RenderFullAsync` scales `Radius` by `1/proxyScale` for OutOfFocus, exactly analogous to how it scales `Length` for Motion.

### WPF changes

**Change: `Deblur/ViewModels/MainViewModel.cs`**

- New `[ObservableProperty] private float _radius;` (defaults to 0).
- New computed `public bool IsOutOfFocusSelected => SelectedBlurType == BlurType.OutOfFocus;` and `public bool IsGaussianSelected => SelectedBlurType == BlurType.Gaussian;`. `IsComingSoon` is removed (superseded by `IsGaussianSelected`). `OnSelectedBlurTypeChanged` fires `PropertyChanged` for all three computed properties.
- Constructor builds the kernel dictionary:
  ```csharp
  var kernels = new Dictionary<BlurType, IBlurKernel>
  {
      [BlurType.Motion]     = new MotionBlurKernel(),
      [BlurType.OutOfFocus] = new OutOfFocusBlurKernel(),
  };
  _runner = new DeblurJobRunner(kernels, new WienerDeconvolver());
  ```
- `PushCurrentParams` and `EnsureFullResRenderedAsync` build `KernelParams` from `SelectedBlurType` (not hardcoded `BlurType.Motion`) — this closes the phase-1 review's hardcoded-`BlurType.Motion` finding.
- `OnRadiusChanged` mirrors the existing `OnLengthChanged`: invalidates the full-res cache and calls `PushCurrentParams`.
- `OnSelectedBlurTypeChanged` resets **only the incoming type's params** to 0 (Angle=0 & Length=0 for Motion; Radius=0 for OutOfFocus). Smoothness is preserved across type switches (it's a Wiener parameter, not a blur parameter). This way, switching to OutOfFocus always shows the raw image first; the user's Motion Angle/Length are preserved when they switch back.
- `Reset()` resets whichever type is currently selected: Motion sets `Angle=0, Length=0`; OutOfFocus sets `Radius=0`. Smoothness always resets to 0.005.
- `UpdateKernel(angle, length)` (called from the drag arrow) is now a no-op when `SelectedBlurType != BlurType.Motion` — the arrow shouldn't drive OutOfFocus.

**Change: `Deblur/MainWindow.xaml`**

Add a second inner panel, structurally parallel to the existing Motion panel, bound to `IsOutOfFocusSelected` visibility:

- Radius slider: `Minimum=0`, `Maximum=50`, `Value={Binding Radius}`, `StringFormat={}{0:0.0}`. (50 px in proxy space is a large defocus; the existing 100 px cap for Motion was for straight-line motion which is inherently longer.)
- Smoothness slider: identical to Motion's binding.
- Reset button: same handler as Motion (`OnResetClick` → `Vm.Reset()`).
- Render-full button: same handler.

The existing "Coming soon" panel becomes bound to a new `IsGaussianSelected` computed on `MainViewModel` (so Gaussian still shows the panel; OutOfFocus no longer does).

**Change: `Deblur/Controls/PreviewCanvas.xaml.cs` (arrow suppression)**

The arrow overlay only makes sense for Motion. Options considered:

- Hide the overlay in `PreviewCanvas` when the ViewModel is in a non-Motion mode. Requires exposing that state.
- Suppress at the ViewModel: `UpdateKernel` becomes a no-op for non-Motion. Simpler; the arrow can still be drawn (harmless visual feedback that has no effect).

Choosing the second. The `MainWindow` code-behind's `OnPreviewDragging` / `OnPreviewDragCommitted` forward to `Vm.UpdateKernel`, which becomes a guard-and-return. Zero XAML change; PreviewCanvas stays kernel-agnostic. A minor cosmetic (arrow visible but non-functional under OutOfFocus) is accepted for phase 2.

### Test changes

**New: `Deblur.Tests/OutOfFocusBlurKernelTests.cs`** — TDD, red before green:

- `Radius0_ThrowsOnBuild` — `Radius < 0` throws `ArgumentOutOfRangeException`. `Radius == 0` returns a single-pixel identity kernel (analog of `MotionBlurKernel` at `Length ≤ 1`).
- `Kernel_SumsToOne` — for `Radius = 8`, kernel values sum to 1 within FP tolerance.
- `Kernel_IsRadiallySymmetric` — for `Radius = 6`, `k[cy+d, cx] == k[cy-d, cx] == k[cy, cx+d] == k[cy, cx-d]` for all `d`.
- `Kernel_HasAntiAliasedEdge` — for `Radius = 5`, center pixel ≈ maximum; edge ring `0 < k < max`; outside `k == 0`.

**New: `Deblur.Tests/WienerDeconvolverTests.OutOfFocus_RoundTrip`** — synthesize a checkerboard (cell size chosen to survive disk-PSF frequency nulls), convolve with `OutOfFocusBlurKernel.Build(Radius = 4)`, add small Gaussian noise, deconvolve with the same PSF, assert `PSNR > 15 dB` **and** `PSNR > PSNR(original, blurred) + 3 dB`. The second half of that AND is the property that distinguishes "deconvolution is working" from "identity function" (also a phase-1 review recommendation adopted here).

**Change: `Deblur.Tests/DeblurJobRunnerTests.cs`**

- Existing tests construct the runner with `new Dictionary<BlurType, IBlurKernel> { [BlurType.Motion] = kernel }` — one-line adjustment.
- New test `Request_WithOutOfFocusType_DispatchesToOutOfFocusKernel` — builds two `RecordingStubKernel` instances, keys the OutOfFocus one, sends a `Request(Type=OutOfFocus, Radius=5)`, asserts only the OutOfFocus stub was called.
- New test `Request_WithMotionLength0_EmitsRawProxy` and `Request_WithOutOfFocusRadius0_EmitsRawProxy` — cover the extended short-circuit (the phase-1 review's "no test for the Length<1 branch" finding is resolved here too).

## Data flow (OutOfFocus path)

1. User selects "OutOfFocus" in the dropdown → `SelectedBlurType` fires `OnSelectedBlurTypeChanged` → sidebar swaps to the OutOfFocus panel → `Radius` set to 0 → `PushCurrentParams` sends `(Type=OutOfFocus, Radius=0, Smoothness=…)` → runner short-circuits, emits raw proxy → preview shows the untouched image.
2. User drags the Radius slider → `OnRadiusChanged` → `PushCurrentParams(Type=OutOfFocus, Radius=r)` → runner picks `_kernels[OutOfFocus].Build(p)` → disk PSF → Wiener → BGRA emit → preview updates.
3. User clicks "Render full resolution" → `EnsureFullResRenderedAsync` → `RenderFullAsync(fullRes, params with Type=OutOfFocus, proxyScale)` → runner scales `Radius` by `1/proxyScale` → full-res disk PSF → Wiener → returns the full-res `ImageBuffer` → cached in `_fullResBuffer`.
4. User picks Save As → PNG/JPEG encode from `_fullResBuffer` → `File.WriteAllBytes`. `IsBusy` gates drop-during-save exactly as in phase 1.

## Error handling

- `OutOfFocusBlurKernel.Build` throws `ArgumentOutOfRangeException` for `Radius < 0`; the runner's `Radius < 1` short-circuit ensures `Build` is never called at 0.
- All I/O, decode, save-as, drag-drop, and large-image error paths are phase-1 code — unchanged.

## Testing philosophy

- Engine is TDD-first: kernel tests → runner routing tests → Wiener round-trip → implementation.
- WPF is manually smoke-tested at end (analog of phase-1 Task 13):
  - Load image, dropdown → OutOfFocus, sidebar swaps, preview goes raw.
  - Drag Radius slider, preview updates within a beat; higher radius = softer.
  - Radius = 0 or Reset → raw image back.
  - Switch to Motion → Motion panel returns, previous Motion Angle/Length preserved.
  - Full-res render + Save As on an OutOfFocus deblur → external viewer shows the deblurred image at full resolution.
  - Drag arrow while in OutOfFocus mode: arrow may draw but has no effect on the image (acceptable cosmetic).

## Compatibility

- Phase-1 tests (28 of them) must still pass unchanged after the `KernelParams` field addition and `DeblurJobRunner` constructor change. Any test that constructs a `KernelParams` gains a trailing `0f` argument (or uses named args). Any test that constructs a `DeblurJobRunner` wraps its kernel in a one-entry dictionary.
- The `phase1` tag remains anchored where it is. Phase 2 lands on a new branch `phase2-out-of-focus`.
