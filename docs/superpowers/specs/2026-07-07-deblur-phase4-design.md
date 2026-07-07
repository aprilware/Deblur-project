# Deblur — Phase 4 Design (Tikhonov Deconvolution)

**Date:** 2026-07-07
**Status:** Approved
**Scope:** Phase 4 of the phased Deblur roadmap. This spec covers phase 4 only (Tikhonov). Total Variation is deferred.

## Context and phasing

Phases 1–3 shipped three blur types (Motion, OutOfFocus, Gaussian) all deconvolved via a single algorithm: Wiener. Phase 4 introduces the first alternative deconvolution algorithm — **Tikhonov with a Laplacian regularization operator** — and the UI surface for the user to pick between them. Total Variation was on the original roadmap for phase 4 but is deferred to phase 4b or 5 because it needs an iterative solver (20–100 iterations of proximal splitting), too slow for the interactive preview path and requiring a separate UI flow.

Roadmap position:

- **Phase 1 (shipped, tag `phase1`)** — Motion blur end-to-end.
- **Phase 2 (shipped, tag `phase2`)** — Out-of-focus blur.
- **Phase 3 (shipped, tag `phase3`)** — Gaussian blur.
- **Phase 4 (this spec)** — Tikhonov deconvolution alongside Wiener.
- **Phase 4b / 5** — Total Variation, plus polish: zoom/pan, keyboard shortcuts, batch, undo, cancellation.

## Goal

A user who selects "Tikhonov" from a new **Algorithm** dropdown gets the same three blur types (Motion / OutOfFocus / Gaussian) with the same PSF-driving sliders they already know, but the deconvolution runs against a Laplacian-regularized frequency-domain estimator. The shared-footer parameter slider re-labels itself to "Regularization (λ)" so the user knows they're driving a different parameter. Live preview + full-res render + Save all work identically to the Wiener path.

## Non-goals

- Total Variation deconvolution.
- Identity-operator Tikhonov (mathematically identical to Wiener; adds a class name without adding behavior).
- User-selectable regularization operators (identity / gradient / Laplacian) — Laplacian is the only choice.
- Per-algorithm slider ranges — both algorithms share the existing `[0.0001, 0.1]` Smoothness slider.
- PSF estimation, luma-only processing, iterative refinement, tiled processing.
- Any phase-3 punch-list items (Sigma scaling ceiling, MainViewModel unit tests, Idle-race regression test, per-BlurType descriptor abstraction).
- Zoom/pan, keyboard shortcuts, batch, undo, cancellation (phase 5).

## Approach

Add `TikhonovDeconvolver` as a second `IDeconvolver` implementation. It uses the same reflect-pad + FFT + spectral divide + inverse FFT + crop + NaN guard + clamp pipeline as `WienerDeconvolver`, differing only in the denominator: Tikhonov replaces Wiener's constant `K` with `λ · |C(u,v)|²`, where `C` is the discrete 2D Laplacian expressed analytically in the frequency domain. This gives frequency-adaptive smoothing — light regularization at low frequencies, heavy regularization at high frequencies — which is the point of Tikhonov versus Wiener.

A new `AlgorithmType` enum joins `BlurType`. `KernelParams` grows an `Algorithm` field so the runner can route each request to the right deconvolver. `DeblurJobRunner`'s constructor gains a second dictionary parameter `IReadOnlyDictionary<AlgorithmType, IDeconvolver>`, symmetric with the existing kernel dictionary; it routes by `p.Algorithm` in both `WorkerLoop` and `RenderFullAsync`. `MainViewModel` builds and injects both dictionaries in its constructor.

The UI adds one new ComboBox next to the existing blur-type ComboBox. The shared-footer Smoothness slider's label swaps between "Smoothness (K)" and "Regularization (λ)" through a value converter bound to `SelectedAlgorithm`. The slider itself keeps its existing range and binding — Tikhonov reinterprets the value as λ.

No `FftAdapter`, `WienerDeconvolver`, `ImageCodec`, or per-blur-kernel changes.

## Solution layout

Same three projects; no new project or NuGet reference:

```
Deblur.sln
├── Deblur/            ← WPF app (adds Algorithm dropdown + label converter; MainViewModel gets SelectedAlgorithm)
├── Deblur.Engine/     ← adds AlgorithmType enum, TikhonovDeconvolver; KernelParams gets Algorithm; DeblurJobRunner takes deconvolver dictionary
└── Deblur.Tests/      ← adds TikhonovDeconvolverTests + a runner routing test; existing tests get trailing Algorithm arg
```

## Components

### Engine changes

**New: `Deblur.Engine/AlgorithmType.cs`**

```csharp
namespace Deblur.Engine;

public enum AlgorithmType
{
    Wiener,
    Tikhonov,
}
```

**New: `Deblur.Engine/TikhonovDeconvolver.cs`**

Implements `IDeconvolver`. Per-channel processing mirrors `WienerDeconvolver.ProcessChannel`:

1. Reflect-pad the input into an `fftSize × fftSize` buffer.
2. Compute `H = FFT(psf)` centered on `(0,0)`.
3. Compute per-frequency the Tikhonov denominator: `|H|² + λ · |C(u,v)|²`, where `|C(u,v)|²` is the analytical DFT magnitude of the discrete 5-point Laplacian mask `[0 1 0; 1 -4 1; 0 1 0]`. For `(u, v)` bin indices in `[0, fftSize)`,
   ```
   Cu = 2 - 2·cos(2π·u / fftSize)
   Cv = 2 - 2·cos(2π·v / fftSize)
   |C|² = (Cu + Cv)²
   ```
   (The DFT of the 5-point Laplacian evaluates to `−(Cu + Cv)`; squaring drops the sign and yields the magnitude used in the denominator. At DC, `|C|² = 0`, so Tikhonov reduces to inverse filtering there; at high frequencies, `|C|²` is large and regularization dominates.)
4. `F̂ = conj(H) / (|H|² + λ · |C|²) · G`, where `G = FFT(padded input)` and `λ = p.Smoothness`.
5. `output = InverseFFT(F̂)`, cropped from the reflect-padded region, NaN/Inf-guarded, and clamped to `[0, 1]`.

The reflect-pad + FFT scaffolding is copy-pasted from `WienerDeconvolver` rather than extracted. A three-way abstraction is phase-5 material; two implementations don't justify it yet.

**Change: `Deblur.Engine/KernelParams.cs`**

Append one field. Final shape:

```csharp
public readonly record struct KernelParams(
    BlurType Type,
    float Angle,
    float Length,
    float Smoothness,
    float Radius,
    float Sigma,
    AlgorithmType Algorithm);
```

Wiener and Tikhonov both consume `Smoothness` (K for Wiener, λ for Tikhonov). Every existing `new KernelParams(...)` construction site adds a trailing `AlgorithmType.Wiener`.

**Change: `Deblur.Engine/DeblurJobRunner.cs`**

Constructor signature becomes:

```csharp
public DeblurJobRunner(
    IReadOnlyDictionary<BlurType, IBlurKernel> kernels,
    IReadOnlyDictionary<AlgorithmType, IDeconvolver> deconvolvers);
```

The runner stores both dictionaries. Inside `WorkerLoop` and `RenderFullAsync`, kernel selection is `_kernels[p.Type]` (unchanged) and deconvolver selection is `_deconvolvers[p.Algorithm]` (new). `IsNoOp` is unchanged — the raw-passthrough decision is a property of the blur PSF, not the algorithm. The `Idle` event and its "fire under the lock" contract are unchanged.

The XML doc on `IsNoOp` (phase-3) already states the "runtime kernel dictionary" invariant; no update needed for the deconvolver dictionary because the runner has no equivalent short-circuit for algorithms — every algorithm reachable through `p.Algorithm` must be in `_deconvolvers`.

### WPF changes

**New: `Deblur/Converters/AlgorithmToSmoothnessLabelConverter.cs`**

Implements `IValueConverter`:

- `Convert(AlgorithmType.Wiener, …)` → `"Smoothness (K)"`
- `Convert(AlgorithmType.Tikhonov, …)` → `"Regularization (λ)"`
- `ConvertBack` throws `NotSupportedException`.

**Change: `Deblur/App.xaml`**

Add to `Application.Resources`:
- `<local:AlgorithmToSmoothnessLabelConverter x:Key="AlgLabel"/>` (with the appropriate `xmlns:local="clr-namespace:Deblur.Converters"`).
- `<ObjectDataProvider x:Key="AlgorithmTypeValues" MethodName="GetValues" ObjectType="{x:Type sys:Enum}">` populated from `engine:AlgorithmType` — same shape as the existing `BlurTypeValues`.

**Change: `Deblur/ViewModels/MainViewModel.cs`**

- New `[ObservableProperty] private AlgorithmType _selectedAlgorithm = AlgorithmType.Wiener;`.
- New computed `public bool IsWienerSelected => SelectedAlgorithm == AlgorithmType.Wiener;` and `public bool IsTikhonovSelected => SelectedAlgorithm == AlgorithmType.Tikhonov;` (both for potential future UI, not required by this phase's XAML).
- Constructor builds the deconvolver dictionary:
  ```csharp
  var deconvolvers = new Dictionary<AlgorithmType, IDeconvolver>
  {
      [AlgorithmType.Wiener]   = new WienerDeconvolver(),
      [AlgorithmType.Tikhonov] = new TikhonovDeconvolver(),
  };
  _runner = new DeblurJobRunner(kernels, deconvolvers);
  ```
- `OnSelectedAlgorithmChanged` fires `PropertyChanged` for `IsWienerSelected` and `IsTikhonovSelected`, invalidates the full-res cache (algorithm change forces a re-render), and calls `PushCurrentParams`.
- `BuildCurrentParams` grows one arg: `new KernelParams(SelectedBlurType, Angle, Length, Smoothness, Radius, Sigma, SelectedAlgorithm)`.

**Change: `Deblur/MainWindow.xaml`**

Directly beneath the existing "Blur type" TextBlock + ComboBox pair (around line 42–44), insert an "Algorithm" TextBlock + ComboBox pair:

```xml
<TextBlock Text="Algorithm" FontWeight="Bold" Margin="0,12,0,4"/>
<ComboBox ItemsSource="{Binding Source={StaticResource AlgorithmTypeValues}}"
          SelectedItem="{Binding SelectedAlgorithm}"/>
```

In the shared footer, change the Smoothness label from literal text to a bound converter:

```xml
<TextBlock Text="{Binding SelectedAlgorithm, Converter={StaticResource AlgLabel}}" Margin="0,4,0,0"/>
```

Everything else in the XAML (per-type Grids, PreviewCanvas, BusyOverlay, ProgressBar, StatusMessage, drag-drop, menu handlers) is unchanged.

### Test changes

**New: `Deblur.Tests/TikhonovDeconvolverTests.cs`** — five tests parallel to `WienerDeconvolverTests`, all TDD-first:

- `RoundTrip_RecoversCheckerboard_AbovePsnrThreshold` — Motion PSF at length 12 through a Tikhonov round-trip; PSNR > 20 dB (matches Wiener's Motion threshold).
- `Gaussian_RoundTrip_RecoversAbovePsnrThreshold` — Gaussian PSF at σ=2; dual threshold `> 15 dB` AND `> blurred + 2.5 dB`, matching phase-3's Wiener Gaussian test.
- `WrongPsf_WorsePsnrThanBlurred` — deconv with the wrong PSF (angle 90° instead of 30°) must have lower PSNR than the blurred input.
- `BorderPixels_BoundedVariance` — top-5-row variance < 0.2 after deconv, catches border ringing.
- `ExtremeParams_NoNaNOrInfInOutput` — Lambda=1e-6 stress test; no NaN/Inf survives to output.

**New: `Deblur.Tests/DeblurJobRunnerTests.Request_WithTikhonovAlgorithm_DispatchesToTikhonovDeconvolver`** — routing test using a new `RecordingStubDeconvolver` (mirrors the existing `RecordingStubKernel` — tracks each `Apply` invocation's params). Constructs the runner with a Motion-only kernel dictionary and both stub deconvolvers keyed on `Wiener` and `Tikhonov`. Sends `Request(..., Algorithm=Tikhonov)`; asserts the Tikhonov stub's recorded list contains the request and the Wiener stub's is empty.

**Change: existing `DeblurJobRunnerTests`** — each test's runner construction wraps the single `SlowStubDeconvolver` in a `Dictionary<AlgorithmType, IDeconvolver> { [AlgorithmType.Wiener] = deconv }` (with a `Tikhonov` entry too, or fall back to using the same instance for both keys — implementer's choice provided both keys resolve).

**Change: ~28 existing `KernelParams` construction sites** across MainViewModel + all test files — append trailing `AlgorithmType.Wiener`. Mechanical.

## Data flow (Tikhonov path)

1. User picks "Tikhonov" in the Algorithm dropdown → `SelectedAlgorithm` fires `OnSelectedAlgorithmChanged` → cache invalidated → `PushCurrentParams` sends `KernelParams(..., Algorithm=Tikhonov)` → runner's worker locks, grabs pending → `IsNoOp` returns false (blur params unchanged) → PSF built via `_kernels[p.Type].Build(p)` → `_deconvolvers[Tikhonov].Apply(proxy, psf, DeconvolutionParams(K=Smoothness))` → BGRA emit → preview updates. In the sidebar, the shared-footer label swaps to "Regularization (λ)" via the converter binding.
2. User drags the Smoothness slider → `OnSmoothnessChanged` invalidates cache, pushes a new request → runner picks up, routes through Tikhonov → preview updates. Slider label reads "Regularization (λ)".
3. Save → `EnsureFullResRenderedAsync` → `RenderFullAsync(fullRes, params with Algorithm=Tikhonov, proxyScale, progress)` → runner scales `Length/Radius/Sigma` by `1/proxyScale`, routes to Tikhonov deconvolver → full-res buffer cached → encoded → written.

## Error handling

- `TikhonovDeconvolver`'s NaN/Inf guard runs before the clamp, same as Wiener.
- `DeblurJobRunner._deconvolvers[p.Algorithm]` throws `KeyNotFoundException` on an unknown algorithm — same trust model as `_kernels[p.Type]`. `MainViewModel`'s dictionary construction is the source of truth.
- All I/O, decode, save-as, drag-drop, and large-image error paths are phase-1 code — unchanged.

## Testing philosophy

- Engine is TDD-first: kernel/deconvolver tests → runner routing test → implementations → Wiener round-trip → dispatch verification.
- WPF is manually smoke-tested at end (analog of prior phases):
  - Algorithm dropdown shows "Wiener" (default) and "Tikhonov".
  - Switching to Tikhonov: shared-footer label swaps to "Regularization (λ)"; preview updates immediately.
  - All three blur types work under Tikhonov (Motion drag arrow, OutOfFocus radius, Gaussian sigma).
  - Switching back to Wiener: label swaps back; preview updates; blur params preserved across the switch.
  - Full-res render + Save under Tikhonov produces a saved file that matches the deblurred preview.
  - Reset button behavior unchanged: currently-selected blur type resets, Smoothness → 0.005f, Algorithm does NOT reset.
  - Progress bar and IsPreviewComputing behavior unchanged from phase 3 (Idle-under-lock + 80ms debounce still applies).

## Compatibility

- All 47 phase-3 tests must still pass after the `KernelParams` field addition (each existing construction gains a trailing `AlgorithmType.Wiener`) and the `DeblurJobRunner` constructor change (each existing runner construction wraps its `SlowStubDeconvolver` in a dictionary keyed on `Wiener` at minimum).
- `phase3` tag remains anchored. Phase 4 lands on a new branch `phase4-tikhonov`.
