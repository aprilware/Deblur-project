# Deblur — Phase 3 Design (Gaussian Blur)

**Date:** 2026-07-07
**Status:** Approved
**Scope:** Phase 3 of the phased Deblur roadmap. This spec covers phase 3 only.

## Context and phasing

Phase 1 shipped motion-blur end-to-end and phase 2 added out-of-focus (disk-kernel) blur. Both use Wiener deconvolution and share the same runner + kernel-dictionary + per-type sidebar architecture. Phase 3 completes the three original blur types from the roadmap by making **Gaussian** functional. After this phase the "coming soon" scaffolding disappears entirely; every dropdown option runs a real deconvolution.

Roadmap position:

- **Phase 1 (shipped, tag `phase1`)** — Motion blur end-to-end.
- **Phase 2 (shipped, tag `phase2`)** — Out-of-focus blur (disk kernel + radius slider).
- **Phase 3 (this spec)** — Gaussian blur (sigma slider).
- **Phase 4** — Tikhonov and Total Variation deconvolution algorithms.
- **Phase 5** — Polish: zoom/pan, keyboard shortcuts, batch, undo, cancellation.

## Goal

A user who picks "Gaussian" from the blur-type dropdown gets a Sigma + Smoothness slider pair in the sidebar. Dragging Sigma drives a Wiener deconvolution against a 2D Gaussian PSF, live-previewed on the proxy and rendered at full resolution on save — identical UX affordances to OutOfFocus.

Two phase-2 minor punch-list items get folded in as opportunistic housekeeping (called out because the third real blur type is the moment they cross the "worth it" threshold):

- **Sidebar footer de-duplication.** Motion and OutOfFocus panels each carry their own Smoothness slider + Reset button + Render button. A third copy would triplicate the same three widgets. Phase 3 extracts these into a single shared `<StackPanel>` sibling of the three per-type Grids.
- **`DeblurJobRunner.IsNoOp` documentation.** The current `_ => true` default arm is a load-bearing contract with the runtime kernel dictionary. Phase 3 adds a short XML doc comment stating the invariant.

## Non-goals

- Non-Gaussian shapes (Cauchy, Laplacian, etc.).
- Spatially-varying Gaussian (sigma changing across the image).
- Any phase-4 (algorithmic) or phase-5 (polish) items.
- PSF estimation from image content.
- Refactoring existing engine code beyond the specific IsNoOp doc-comment addition.

## Approach

Add `GaussianBlurKernel` as the third `IBlurKernel` implementation. `KernelParams` grows one more field (`Sigma`); every kernel ignores what it doesn't use. `DeblurJobRunner`'s `IsNoOp` switch grows a `Gaussian => Sigma < 1f` case and its `RenderFullAsync` scaling grows a `Sigma = Sigma * scaleInv` term. `MainViewModel` injects a third dictionary entry and adds `Sigma` observable + per-type reset arm. `MainWindow.xaml` restructures the sidebar so each per-type panel holds only its unique slider(s) and one shared footer (Smoothness + Reset + Render) sits below all three. The coming-soon TextBlock is deleted.

No Wiener changes. No `FftAdapter` changes. No new NuGet packages.

## Solution layout

Same three projects; no new project or dependency:

```
Deblur.sln
├── Deblur/            ← WPF app (adds Gaussian sidebar panel + Sigma observable + HasImage; deletes coming-soon)
├── Deblur.Engine/     ← adds GaussianBlurKernel; Sigma field on KernelParams; IsNoOp Gaussian case; RenderFullAsync Sigma scaling
└── Deblur.Tests/      ← adds GaussianBlurKernelTests + Gaussian Wiener round-trip + Gaussian routing + Sigma-scaling regression test
```

## Components

### Engine changes

**New: `Deblur.Engine/GaussianBlurKernel.cs`**

Implements `IBlurKernel`. Produces a 2D isotropic Gaussian PSF of standard deviation `p.Sigma`. Kernel side is `2·ceil(3σ)+1` (covers 99.7% of the mass); each pixel's weight is `exp(-(dx² + dy²) / (2σ²))` where `dx, dy` are Euclidean offsets from the kernel center. The kernel is normalized to sum = 1 after all weights are computed. `Build` throws `ArgumentOutOfRangeException` for `Sigma < 0`; returns a 1×1 identity kernel for `Sigma == 0` (parallel to `OutOfFocusBlurKernel` at Radius=0).

Note: the kernel is *not* computed separably (as row × column 1D passes) even though the math allows it — a direct 2D loop keeps the kernel structurally identical to `MotionBlurKernel` and `OutOfFocusBlurKernel` and lets the same `Build → float[,] → WienerDeconvolver` pipeline run unchanged. Separability is a phase-5 perf optimization if profiling later shows it matters.

**Change: `Deblur.Engine/KernelParams.cs`**

Append one field. Final record shape:

```csharp
public readonly record struct KernelParams(
    BlurType Type,
    float Angle,
    float Length,
    float Smoothness,
    float Radius,
    float Sigma);
```

Motion ignores `Sigma` and `Radius`; OutOfFocus ignores `Sigma`, `Angle`, `Length`; Gaussian ignores `Angle`, `Length`, `Radius`. Every existing `new KernelParams(...)` construction site (2 in `MainViewModel`, 12 in tests) gets a trailing `0f`.

**Change: `Deblur.Engine/DeblurJobRunner.cs`**

- `IsNoOp` gains a `BlurType.Gaussian => p.Sigma < 1f` case:
  ```csharp
  private static bool IsNoOp(KernelParams p) => p.Type switch
  {
      BlurType.Motion     => p.Length < 1f,
      BlurType.OutOfFocus => p.Radius < 1f,
      BlurType.Gaussian   => p.Sigma  < 1f,
      _                   => true,
  };
  ```
  A short XML doc comment above `IsNoOp` documents the runtime invariant: any `BlurType` this switch treats as a no-op need not be present in the injected kernel dictionary; any type that reaches the `else` branch of `WorkerLoop` / `RenderFullAsync` MUST have a corresponding entry. This closes the phase-2 whole-branch reviewer's "silent invariant" finding without adding runtime branching.
- `RenderFullAsync`'s `scaledParams` `with` expression adds a `Sigma = p.Sigma * scaleInv` term, so the Gaussian PSF is built at full-res scale exactly like Motion's Length and OutOfFocus's Radius.

### WPF changes

**Change: `Deblur/ViewModels/MainViewModel.cs`**

- New `[ObservableProperty] private float _sigma;` (defaults to 0).
- New `partial void OnSigmaChanged(float value)` mirrors `OnRadiusChanged`: invalidates the full-res cache and calls `PushCurrentParams`.
- New computed `public bool HasImage => _proxy is not null;`. `LoadImageFromBytes` fires `OnPropertyChanged(nameof(HasImage))` after `_proxy` is assigned so the shared footer's visibility binding tracks it.
- Constructor's kernel dictionary gains `[BlurType.Gaussian] = new GaussianBlurKernel()`.
- `OnSelectedBlurTypeChanged` reset-switch grows a `case BlurType.Gaussian: Sigma = 0f; break;` arm.
- `Reset()`'s switch grows the same arm.
- `BuildCurrentParams` includes `Sigma`.

**Change: `Deblur/MainWindow.xaml`**

Restructure the sidebar's inner StackPanel (children beneath the ComboBox) into:

- **Motion Grid** (Visibility bound to `IsMotionSelected`): Angle slider + value + Length slider + value. Only.
- **OutOfFocus Grid** (Visibility bound to `IsOutOfFocusSelected`): Radius slider + value. Only.
- **Gaussian Grid** (new, Visibility bound to `IsGaussianSelected`): Sigma slider (`Minimum=0`, `Maximum=10`, `StringFormat={}{0:0.0}`) + value. Only.
- **Shared footer StackPanel** (new, Visibility bound to `HasImage` via `BoolToVis`): Smoothness slider identical to the phase-1/2 binding + Reset button + Render full resolution button.
- **StatusMessage TextBlock**: unchanged, stays at the bottom.
- **Coming-soon TextBlock** (`Visibility={Binding IsGaussianSelected}` from phase 2): deleted, since Gaussian is now functional and there is no remaining "future phase" case for it to signal.

The three per-type Grids all appear in the same slot; only one is visible at a time. The shared footer is always visible once an image is loaded and hidden before.

### Test changes

**New: `Deblur.Tests/GaussianBlurKernelTests.cs`** — TDD, red before green:

- `NegativeSigma_Throws` — `Sigma < 0` throws `ArgumentOutOfRangeException`.
- `ZeroSigma_ReturnsSinglePixelIdentity` — `Sigma == 0` returns a 1×1 identity kernel `k[0,0] == 1`.
- `Kernel_SumsToOne` — for `Sigma = 2`, kernel values sum to 1 within FP tolerance.
- `Kernel_IsRadiallySymmetric` — for `Sigma = 2`, the four cardinal points at distance `d` from center are equal for each `d`.
- `Kernel_PeaksAtCenter_DecaysMonotonically` — for `Sigma = 2`, `k[c,c]` is the strict maximum; along one axis, `k[c, c+1] > k[c, c+2] > k[c, c+3]` (catches sign flips, formula transcription errors, and off-by-one indexing).

**New: `Deblur.Tests/WienerDeconvolverTests.Gaussian_RoundTrip_RecoversAbovePsnrThreshold`** — synthesize a checkerboard (cell size chosen large enough for Gaussian-PSF frequency response), convolve with `GaussianBlurKernel.Build(σ=2)`, add small Gaussian noise, deconvolve with the same PSF, assert BOTH `PSNR > 15 dB` AND `PSNR > PSNR(original, blurred) + 3 dB`. The dual-threshold pattern matches the phase-2 OutOfFocus test.

**Change: `Deblur.Tests/DeblurJobRunnerTests.cs`** — add three tests parallel to the OutOfFocus ones from phase 2:

- `Request_WithGaussianType_DispatchesToGaussianKernel` — three-kernel routing test (Motion, OutOfFocus, Gaussian stubs). Sends `Type=Gaussian, Sigma=3`; asserts only the Gaussian stub's `Seen` is populated.
- `Request_WithGaussianSigmaBelow1_EmitsRawProxyWithoutCallingDeconvolver` — asserts `received > 0`, `deconv.CallCount == 0`, Gaussian stub's `Seen` empty.
- `RenderFullAsync_ScalesKernelSigmaByInverseProxyScale` — mirror of the Length/Radius scaling tests. Sends `Sigma=3` at `proxyScale=0.25`; asserts the observed Gaussian kernel invocation carries `Sigma=12`.

**Change: 12 existing KernelParams call sites** — each gets a trailing `0f` for the new `Sigma` field. Mechanical.

## Data flow (Gaussian path)

1. Dropdown → `SelectedBlurType = Gaussian` fires `OnSelectedBlurTypeChanged` → `Sigma = 0` → sidebar Gaussian Grid becomes visible, shared footer stays visible (image loaded) → `PushCurrentParams(Type=Gaussian, Sigma=0, …)` → runner `IsNoOp` returns true → emits raw proxy → preview shows the untouched image.
2. User drags the Sigma slider → `OnSigmaChanged` → `PushCurrentParams(Type=Gaussian, Sigma=σ)` → runner picks `_kernels[Gaussian].Build(p)` → 2D Gaussian PSF → Wiener → BGRA emit → preview updates.
3. Save → `EnsureFullResRenderedAsync` → `RenderFullAsync(fullRes, params with Type=Gaussian, proxyScale)` scales `Sigma` by `1/proxyScale` → full-res Gaussian PSF → Wiener → cached in `_fullResBuffer` → PNG/JPEG encode → `File.WriteAllBytes`.

## Error handling

- `GaussianBlurKernel.Build` throws `ArgumentOutOfRangeException` for `Sigma < 0`; the runner's `Sigma < 1` short-circuit ensures `Build` is never called at 0.
- All I/O, decode, save-as, drag-drop, and large-image error paths are phase-1 code — unchanged.

## Testing philosophy

- Engine is TDD-first: kernel tests → runner routing / short-circuit / scaling tests → Wiener round-trip → implementation.
- WPF is manually smoke-tested at end (analog of phase-1 Task 13 and phase-2 Task 6):
  - Open image → dropdown → Gaussian → sidebar shows Sigma slider + shared footer (Smoothness/Reset/Render); preview goes raw.
  - Drag Sigma up → preview softens; drag back to 0 → preview back to raw.
  - Reset button while in Gaussian: Sigma → 0, Smoothness → 0.005; preview goes raw immediately.
  - Full-res render + Save As on a Gaussian deblur → external viewer shows the correctly deblurred image at full resolution.
  - Switch to Motion → Motion Grid appears; previously-set Motion Angle/Length preserved (per-type state).
  - Switch to Gaussian a second time → Sigma resets to 0 (per-type "raw-image-on-switch" idiom).
  - Coming-soon TextBlock is gone entirely.
  - Shared footer is hidden BEFORE an image is loaded.

## Compatibility

- All 38 phase-2 tests must still pass unchanged after the `KernelParams` field addition and IsNoOp switch extension. Test-file call sites gain a trailing `0f`.
- Motion and OutOfFocus preview + render paths are behavior-identical to phase 2. The sidebar restructure moves the Smoothness slider from inside each per-type panel to a shared footer sibling; the binding is identical, so behavior is unchanged.
- `phase2` tag remains anchored where it is. Phase 3 lands on a new branch `phase3-gaussian`.
