# Deblur — Phase 1.a Design (Linear-Light Correctness Foundation)

**Date:** 2026-07-09
**Status:** Approved
**Scope:** Correctness foundation for the forensic-grade upgrade — linear light, boundary handling, high bit depth via WIC, luminance-only mode, edge taper, area-average proxy downsample, minimal validation harness stub.

## Context

The current Wiener / Tikhonov / TotalVariation pipeline in `Deblur.Engine` treats sRGB-encoded pixel values as if they were linear intensity, silently violating the physical convolution model the deconvolvers assume. Every downstream forensic phase — new algorithms (1.c), blind deconvolution (1.e), audit log (2), and the court-facing report (2/5) — depends on this pipeline producing physically-meaningful, measurable results. Phase 1.a lands the correctness foundation before any new algorithm is added so downstream work inherits a correct baseline.

## Goal

An examiner opens a piece of source imagery. The engine decodes it into linear intensity (sRGB transfer function), honors the file's original bit depth up to 16 bpc where the format allows, and runs deconvolution with proper boundary treatment and edge taper. Preview and full-resolution renders match sharpness because the proxy is now an area-average — not nearest-neighbor — downsample. Every pipeline change is exposed as a field on a single `PipelineOptions` record (defaults = physically-correct behavior); the legacy sRGB-space code path stays reachable by flipping the flag for A/B comparison. A minimal validation harness generates known synthetic blurs, computes PSNR/SSIM against ground truth, and writes a CSV report so the linear-light quality gain is measured, not eyeballed.

## Non-goals

- New deconvolution algorithms (Phase 1.c).
- ROI processing (Phase 1.b).
- Blind deconvolution / auto-parameter estimation (Phase 1.d, 1.e).
- PSF tooling (Phase 1.f).
- ICC-aware color conversion — assume sRGB source; deferred.
- Full "Validation Mode" UI (§8 in the roadmap). Only the headless harness stub lands here.
- Case/exhibit model, audit log, hashing (Phase 2).
- Engine → Core project rename — deferred until the CLI project actually exists (§9).

## Approach

### 1. sRGB ↔ linear color pipeline

Add `Deblur.Engine/Color/SrgbLinear.cs` with the piecewise IEC 61966-2-1 transfer function. Decode implemented as a 256-entry `byte → float` LUT and a 65536-entry `ushort → float` LUT (for the 16-bit path); encode as analytic `float → byte` / `float → ushort`. `DeblurJobRunner` converts the working `ImageBuffer` to linear before calling the deconvolver, and back to sRGB before emitting the output BGRA (preview path) or handing off to the codec (render/save path). Gated by `PipelineOptions.LinearLight` (default: `true`).

### 2. High-bit-depth I/O via WIC

GDI+ 16-bit paths are unreliable, so 16 bpc I/O uses WPF's Windows Imaging Component:

- **`Deblur.Engine/IImageCodec.cs`** (new) — abstraction: `Decode(byte[]) → (ImageBuffer, BitDepth)`, `EncodePng(ImageBuffer, BitDepth)`, `EncodeJpeg(ImageBuffer, int quality)`.
- **`Deblur.Engine/ImageCodec.cs`** (existing, kept) — 8-bit GDI+ fast path; implements `IImageCodec` returning `BitDepth.Eight`.
- **`Deblur/Services/WicImageCodec.cs`** (new, WPF layer) — WIC-backed decoder/encoder using `System.Windows.Media.Imaging.BitmapDecoder` / `BitmapEncoder` with `PixelFormats.Rgb48` / `Rgba64` / `Bgra32` / `Bgr24`. Returns `BitDepth.Sixteen` when the source is >8 bpc; preserves depth on export where the target format supports it (PNG 16-bit, TIFF 16-bit). Falls back to `BitDepth.Eight` on 8-bit sources.

`MainViewModel.LoadImageFromBytes` picks the codec: WIC first (it handles both 8- and 16-bit); the GDI+ codec is retained as a fallback for formats WIC doesn't recognize. Internal precision stays `float32`. `ImageBuffer` gains a nullable `BitDepth SourceBitDepth` property (informational; drives the export choice).

### 3. Boundary handling and edge taper

Boundary treatment lifts from hardcoded reflection to `PipelineOptions.BoundaryMode { Reflect, Replicate, Periodic }` (default `Reflect`). All three deconvolvers replace their inline reflect loops with a shared `Deblur.Engine/BoundaryFill.cs` helper: `Pad(float[] channel, int w, int h, int pad, int fftSize, BoundaryMode) → float[,]`.

Add `Deblur.Engine/EdgeTaper.cs`: applies a separable Tukey window along the reflected border of the padded canvas, blending the padded ring toward the ROI mean so periodic-convolution wrap doesn't ring at the image boundary. Gated by `PipelineOptions.EdgeTaper` (default: `true`).

### 4. Luminance-only mode

`Deblur.Engine/Color/YCbCr.cs`: BT.601 RGB↔YCbCr in float. When `PipelineOptions.LuminanceOnly` is `true` (default: `false`), `DeblurJobRunner` extracts Y into a single-plane `ImageBuffer` (R=G=B=Y), runs the deconvolver on it, takes the R channel of the result as the new Y, and recomposes with the original Cb/Cr. Third of the CPU cost; no color fringing on aliased edges.

### 5. Proxy downsample fix (nearest-neighbor → area average)

Move `MainViewModel.Downscale` into `Deblur.Engine/Imaging/AreaResample.cs` and replace it with a proper area-average box filter that computes exact fractional coverage per source pixel for arbitrary rational scale factors. The preview PSNR now tracks the full-resolution render, so the examiner isn't misled during parameter tuning.

### 6. `PipelineOptions` plumbing

`Deblur.Engine/PipelineOptions.cs` (new record):

```
public sealed record PipelineOptions(
    bool LinearLight,
    bool EdgeTaper,
    BoundaryMode BoundaryMode,
    bool LuminanceOnly)
{
    public static PipelineOptions Default => new(true, true, BoundaryMode.Reflect, false);
}
```

`IDeconvolver.Apply` signature gains a nullable `PipelineOptions? options = null` trailing parameter; implementations do `var opt = options ?? PipelineOptions.Default;`. Existing call sites (including the 64 tests that instantiate deconvolvers directly) continue to compile unchanged and get the new-default behavior. `DeblurJobRunner` accepts an optional `PipelineOptions` constructor parameter (defaulting to `PipelineOptions.Default`); `MainViewModel` constructs the runner with defaults for now — no UI switch in this phase.

### 7. Validation harness stub

The stub is deliberately small — the full "Validation Mode" is §8. Land only:

- `Deblur.Engine/Validation/SyntheticBlur.cs` — applies a known PSF plus additive Gaussian noise at a specified SNR to a ground-truth `ImageBuffer`.
- `Deblur.Engine/Validation/Quality.cs` — `Psnr(ref, test)` and `Ssim(ref, test)` (11×11 Gaussian window, standard constants K1=0.01, K2=0.03; per-channel then mean).
- `Deblur.Tests/Validation/LinearLightGainTests.cs` — sweeps (checkerboard + gradient + step-edge test images) × (Motion length 12 @ 30°) × (Wiener/Tikhonov/TV) × (LinearLight true vs false) at three SNRs (∞, 40 dB, 30 dB). Asserts mean-PSNR gain ≥1.0 dB for the noise-free Wiener case with linear light on. Writes the full result table to `Deblur.Tests/bin/{Config}/net8.0/validation-reports/linear-light-gain-{utc-timestamp}.csv` so the numbers are captured on every CI run.

No `ValidationRunner` scaffolding or CSV framework — a for-loop in the test emitting a simple CSV is enough at this stub stage. The scaffolding grows in §8.

## Files touched

**New in `Deblur.Engine`:**
- `Color/SrgbLinear.cs`
- `Color/YCbCr.cs`
- `BoundaryFill.cs` (with `BoundaryMode` enum)
- `EdgeTaper.cs`
- `Imaging/AreaResample.cs`
- `PipelineOptions.cs`
- `IImageCodec.cs`
- `Validation/SyntheticBlur.cs`
- `Validation/Quality.cs`
- `BitDepth.cs`

**New in `Deblur`:**
- `Services/WicImageCodec.cs`

**Modified in `Deblur.Engine`:**
- `IDeconvolver.cs` — trailing `PipelineOptions? options = null` parameter.
- `WienerDeconvolver.cs`, `TikhonovDeconvolver.cs`, `TotalVariationDeconvolver.cs` — honor `BoundaryMode`, `EdgeTaper` via the shared helpers; math unchanged.
- `DeblurJobRunner.cs` — accept `PipelineOptions`, wrap `WorkerLoop` and `RenderFullAsync` in linear-light decode/encode and (optionally) luminance-only routing.
- `ImageBuffer.cs` — add `BitDepth SourceBitDepth { get; init; }` (default `Eight`).
- `ImageCodec.cs` — implement `IImageCodec` on the existing static (via a thin `Gdi8BitImageCodec` wrapper class) so callers can be polymorphic. Zero API break.

**Modified in `Deblur`:**
- `ViewModels/MainViewModel.cs` — inject `PipelineOptions.Default` into the runner, prefer `WicImageCodec` on load, delegate downscale to `AreaResample.Box`.

**New in `Deblur.Tests`:**
- `SrgbLinearTests.cs`
- `YCbCrTests.cs`
- `BoundaryFillTests.cs`
- `EdgeTaperTests.cs`
- `AreaResampleTests.cs`
- `WicImageCodecTests.cs`
- `Validation/PsnrSsimTests.cs`
- `Validation/LinearLightGainTests.cs`

## Constraints

- .NET 8. All existing 64 tests continue to pass; where a synthetic-recovery threshold shifts materially because linear light is now correct, adjust with an inline `// linear-light baseline: <old>→<new>` comment.
- No new NuGet dependencies. WIC comes with WPF (`PresentationCore.dll`).
- `Deblur.Engine` stays UI-free; `WicImageCodec` lives in the WPF layer.
- `PipelineOptions.Default` = `LinearLight true, EdgeTaper true, BoundaryMode Reflect, LuminanceOnly false`.
- Test count target: 64 → ~88 (24 new).
- Phase 1.a branches from tag `phase4b` onto `phase1a-linear-light-correctness`.

## Testing

Unit tests cover:

- **`SrgbLinearTests`** — 8-bit and 16-bit round-trip within 1 LSB across full range; boundary values 0.0031308 and 0.04045; monotonicity.
- **`YCbCrTests`** — round-trip within 1e-5; grayscale → Y = luma, Cb = Cr = 0.5.
- **`BoundaryFillTests`** — each mode reproduces expected pad; matches current reflect result at all interior points.
- **`EdgeTaperTests`** — step-edge input, measure PSNR of a zero-K Wiener recovery near border with vs without taper (assert taper reduces ringing energy in a border strip by ≥3 dB).
- **`AreaResampleTests`** — 2:1 checkerboard downsample yields uniform gray (nearest-neighbor produces stripes); dimensions and clamping correct.
- **`WicImageCodecTests`** — 8-bit PNG round-trip byte-exact; 16-bit PNG round-trip float-exact to within 1/65535; unknown format throws `InvalidImageFormatException`.
- **`Validation/PsnrSsimTests`** — identical images → PSNR = ∞, SSIM = 1; shifted image → known-good PSNR against a reference computation.
- **`Validation/LinearLightGainTests`** — the sweep described in §7 above; writes CSV; asserts documented gain.

Manual smoke:

- Open a standard 8-bit JPEG — behaves as before to the naked eye; highlights slightly less crushed under Wiener at aggressive K.
- Open a 16-bit PNG (test asset ships with the tests) — engine reports source bit depth; deblur runs; Save-As PNG round-trips 16 bits, verified by re-loading and hashing the pixel data.
- Zoom in on a hard boundary edge — reduced border ringing vs. the phase-4b build.
- Preview and full-res render match sharpness (was previously off because of nearest-neighbor proxy).

## Branch

Phase 1.a branches from tag `phase4b` onto `phase1a-linear-light-correctness`.
