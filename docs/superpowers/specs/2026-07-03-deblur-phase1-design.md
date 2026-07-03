# Deblur — Phase 1 Design (Motion Blur, End-to-End)

**Date:** 2026-07-03
**Status:** Approved
**Scope:** Phase 1 of a phased build inspired by [SmartDeblur](https://github.com/Y-Vladimir/SmartDeblur). This spec covers phase 1 only. Phases 2–5 will get their own specs.

## Context and phasing

The full vision is a WPF desktop app that removes out-of-focus, motion, and Gaussian blur from photos using Wiener, Tikhonov, and Total Variation deconvolution — with a real-time preview and a novel interactive helper: the user click-and-drags on the preview to specify motion-blur direction, and the deconvolution updates live.

The phased roadmap:

- **Phase 1 (this spec)** — Motion blur end-to-end. Load → downsampled proxy preview → click-and-drag arrow overlay → live Wiener deconvolution → full-resolution render → save. Blur-type UI is scaffolded from day one (dropdown with Motion / Out-of-Focus / Gaussian), but only Motion is functional.
- **Phase 2** — Out-of-focus blur (disk kernel, radius slider).
- **Phase 3** — Gaussian blur (sigma slider).
- **Phase 4** — Tikhonov and Total Variation deconvolution algorithms.
- **Phase 5** — Polish: zoom/pan, keyboard shortcuts, batch, undo, cancellation.

## Approach

Pure-managed .NET stack, split into three projects. FFT via [FftSharp](https://github.com/swharden/FftSharp) (MIT). Custom Wiener deconvolution. MVVM in the UI with `CommunityToolkit.Mvvm`. Chosen over an OpenCvSharp-based stack (heavy native binaries for little algorithmic savings) and over an FFTW.NET stack (fastest CPU FFT, but GPL forces the whole app to be GPL). Managed FFT is fast enough at proxy resolution; extensibility interfaces defined now let later phases slot in without churn.

## Solution layout

```
Deblur.sln
├── Deblur/            ← WPF app; MVVM + views + user interaction
├── Deblur.Engine/     ← pure C# library (net8.0); no WPF references
└── Deblur.Tests/      ← xUnit tests over Deblur.Engine
```

### `Deblur.Engine` (no WPF, no `System.Windows.*`)

- **`ImageBuffer`** — three `float[]` channels (R, G, B), width, height. Pixel values normalized to `[0, 1]`. This is the currency for all math.
- **`IBlurKernel`** — interface returning a discrete PSF as `float[,]` given kernel-specific parameters. Phase 1 implementation: `MotionBlurKernel(angle, length)`. Phases 2–3 add `OutOfFocusKernel` and `GaussianKernel`.
- **`IDeconvolver`** — interface: `ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p)`. Phase 1 implementation: `WienerDeconvolver`. Phase 4 adds Tikhonov and Total Variation.
- **`FftAdapter`** — thin wrapper around FftSharp so the algorithm doesn't leak the dependency. If we ever swap to a native FFT for perf, only this file changes.
- **`ImageCodec`** — decodes/encodes to/from `byte[]` payloads (PNG, JPEG, BMP, TIFF in; PNG, JPEG out). Not `BitmapSource`-based, so tests don't need a Dispatcher.
- **`InvalidImageFormatException`** — thrown by `ImageCodec.Decode` on unreadable input; caught at the UI boundary and converted to a user-facing modal.

### `Deblur` (WPF UI)

- **`MainWindow.xaml`** + **`MainViewModel`** — data-bound observable properties for kernel params (angle, length, smoothness), current file, busy state, error text.
- **`BlurType`** enum (`Motion`, `OutOfFocus`, `Gaussian`) + a `BlurType` dropdown bound to the view model. Selecting `OutOfFocus` or `Gaussian` shows a "Coming soon" panel; selecting `Motion` shows the motion-blur sidebar.
- **`PreviewCanvas`** — custom control hosting the `WriteableBitmap` and drawing the interactive arrow overlay during a drag.
- **`DeblurJobRunner`** — debounced background worker owning engine calls. Public API: `void Request(KernelParams p)`, event `ProxyReady(byte[] bgra, int width, int height)`, method `Task<byte[]> RenderFullAsync(KernelParams p, IProgress<double> progress)`.
- File open/save dialogs, drag-and-drop handling, modal progress overlay for full-resolution render.

## Data flow

```
UI gesture (drag / slider)
      │
      ▼
MainViewModel.KernelParams  ← observable (angle, length, smoothness)
      │
      ▼
DeblurJobRunner.Request(params)
      │  coalesce: drop pending; keep only latest params
      ▼
[background thread]  WienerDeconvolver.Apply(proxyImage, MotionBlurKernel(params), p)
      │
      ▼
byte[] BGRA for WriteableBitmap
      │  marshal via Dispatcher
      ▼
WriteableBitmap.WritePixels → visible preview updates
```

### Key rules

- **Proxy computed once per load.** On file open we decode the image, downscale it to fit the preview viewport (target ≤ 1.5 MP), and cache that as `_proxyBuffer`. Every preview recompute reuses this buffer; we never re-decode. We also keep the original full-resolution `ImageBuffer` in memory for the full-res render step.
- **Coalesce, don't queue.** `DeblurJobRunner` holds *one* slot for the latest requested params. If a job is in flight, new requests overwrite the slot. When the current job finishes, it picks up whatever's in the slot (if anything) and runs again. During a drag we may fire 60 requests/sec but only run 5–10 deconvolutions. The UI is honest to *latest* input, not *every* input.
- **No cancellation in phase 1.** Wiener on a 1.5 MP proxy is ~50–150 ms. If a run finishes and its params are stale, the result is discarded and the next run starts immediately. Cancellation lands in phase 5.
- **Commit-on-release for the drag arrow.** During the drag we fire preview requests as normal. `MouseUp` doesn't invoke a special engine path — the `(angle, length)` are already whatever the last drag frame set them to. "Commit" means: end the gesture, freeze the arrow overlay, leave the sliders showing those values. The user can still adjust via sliders afterward.
- **Full-res render is a separate button.** Click "Render full resolution" → same engine call but on the original (non-downscaled) `ImageBuffer` with the kernel length scaled by `1 / proxy_scale`. Runs on the same background worker but blocks the UI with a modal progress overlay because it can take several seconds on a 24 MP image. Result cached as `_fullResBuffer`; that's what "Save" writes.

### Arrow overlay coordinate handling

- Overlay lives on the `PreviewCanvas`, drawn in screen coordinates.
- On `MouseDown`: record both screen and proxy-image coordinates of the click.
- On `MouseMove`: the current cursor gives us the screen vector; the proxy-image vector is `screen_vec / display_scale`. Kernel `length` = image-space vector magnitude in proxy-pixels; kernel `angle` = `atan2(dy, dx)`.
- On `MouseUp`: freeze the arrow at its final position. Sliders reflect the committed values.
- The proxy-to-full-res length scaling happens at render time, not at drag time: whatever length the user committed on the proxy is preserved as a proxy-space value in the view model, and multiplied by `1 / proxy_scale` when the full-res render runs. This makes the physical blur they intended survive the resolution change.

## Deconvolution math

### Motion PSF construction (`MotionBlurKernel`)

- **Input:** `angle` (radians), `length` (pixels, image-space).
- **Output:** `float[,]` on a `(2⌈length⌉+1)²` bounding box.
- Anti-aliased line segment through the center: for each pixel in the box, weight = `max(0, 1 - perpendicular_distance_to_line_segment)`. Weights normalized so the kernel sums to 1. Anti-aliasing matters — a jaggy line for non-axis-aligned angles causes visible ringing in the output.

### Wiener deconvolution (`WienerDeconvolver`)

1. **Reflect-pad** the input image by `⌈length/2⌉` on all sides. Kills border ringing.
2. **Zero-pad** both the padded image `g` and the PSF `h` to a common `nextPow2` size for FFT efficiency; center the PSF so DC lands at `(0, 0)`.
3. `G = FFT(g)`, `H = FFT(h)`.
4. **Wiener filter**, pointwise: `F̂ = (conj(H) / (|H|² + K)) · G`.
5. `f̂ = Re(IFFT(F̂))`; crop back to original image dimensions.
6. Repeat independently for R, G, B channels.

### Parameters exposed to the ViewModel

- **`Angle`** — 0° to 360° (identical result under 180° flip; the UI ranges 0–360° for arrow convenience).
- **`Length`** — 1 to 100 pixels in image-space (proxy coords).
- **`Smoothness`** (K) — noise-to-signal ratio. Log-scale slider from ~`1e-4` to ~`1e-1`. Higher = less ringing, softer result.

### Numeric type

Single-precision `float` throughout (`Complex32` for FFT). Adequate for 8-bit-per-channel input, ~2× throughput of `double`.

### Non-goals for phase 1

Deferred to later phases: luma-only processing, iterative refinement, PSF estimation from image content, tiled processing for out-of-memory cases.

## File I/O

### Supported formats

- **Input:** JPEG, PNG, BMP, TIFF (via WIC `BitmapDecoder`).
- **Output:** PNG (lossless) and JPEG (quality 92 default). No 16-bit-per-channel output; internal math is 8-bit-per-channel upscaled to float and back.

### Load flow

- File → Open, or drag-and-drop onto the window.
- Decode → normalize to `[0, 1]` float `ImageBuffer` → downscale to proxy → initial "unmodified" preview shown.
- Drag-drop with multiple files: use the first, ignore the rest (batch is phase 5).

### Save flow

- File → Save As → `SaveFileDialog` with PNG / JPEG filter.
- If no `_fullResBuffer` exists (user never clicked "Render full resolution"), Save runs one implicitly first with the same modal progress overlay, then writes the result. Prevents "user saves and gets the tiny proxy" as a footgun.

## Error handling

| Failure | Detection | UX |
|---|---|---|
| Unsupported / corrupt file | `ImageCodec.Decode` throws `InvalidImageFormatException` | Modal: "Couldn't read `<name>`. Format not supported or file is corrupt." State unchanged. |
| Image very large (> 100 MP) | Pixel-count check post-decode | Modal: "Image is very large (X MP); may be slow. Continue?" Yes/No. |
| Out of memory during decode / FFT | `OutOfMemoryException` | Modal: "Ran out of memory. Try a smaller image." No auto-retry, no auto-downsample. |
| Engine returns NaN / Inf pixels | Post-run sanity check on output buffer | Log + show the untouched original in preview; toast "Deconvolution produced invalid pixels — try lowering the length or raising smoothness." |
| Save fails (permissions / disk full) | `IOException` on write | Modal with the OS error message. |

### Explicitly not handled in phase 1

- **Undo / redo.** State is a single set of kernel params; if the user wants "undo," they slide back or click Reset. Full undo stack in phase 5.
- **Reset button.** Present from day one. Clears the arrow, resets sliders to defaults, shows the untouched original in the preview.
- **In-progress cancellation of full-res render.** Modal blocks the UI; user waits. Cancellation UX is phase 5.

## Testing

`Deblur.Tests` (xUnit) covers the engine end-to-end. No WPF harness in phase 1.

### `MotionBlurKernelTests`

- Sums to `1.0` within `1e-6` for `(angle, length)` sampled across a grid.
- Symmetric under 180° angle flip.
- `length = 1` produces a single-pixel identity kernel.
- Anti-aliasing sanity: kernel at 45° has non-zero off-axis weights (guards against a jaggy fallback slipping in).

### `WienerDeconvolverTests`

- **Round-trip PSNR.** Take a checkerboard / test pattern, blur it with a known motion PSF, add small Gaussian noise (σ ≈ 0.005), deconvolve with the correct PSF and a matching K. Assert output PSNR vs. original > 25 dB. This is the "math works" test.
- **Wrong-angle test.** Deconvolving with a PSF at the wrong angle produces a *worse* PSNR than the blurred input.
- **Border-ringing test.** Outer 5-pixel border of the output has bounded variance (confirms reflect-padding works).
- **Numeric stability.** No NaN/Inf for extreme params (`length = 100`, `K = 1e-6`).

### `ImageCodecTests`

- PNG round-trip: encode → decode → identical pixel values (lossless).
- JPEG round-trip at quality 92 (the app's default): PSNR vs input > 40 dB.
- Corrupt input: garbage bytes → throws `InvalidImageFormatException`, not a raw framework exception.

### `DeblurJobRunnerTests`

- **Coalescing.** Fire 100 requests rapidly at a stub engine that sleeps 10 ms; assert ≤ ~10 jobs actually ran, and the *last* one used the final params.
- **Stale-result drop.** Params changed while a job was running; the completed result is discarded, not published.

### Coverage targets

- `Deblur.Engine`: ≥ 85% line coverage.
- `DeblurJobRunner`: 100% (small file, easy to hit).
- `Deblur` UI project: no automated coverage target. Covered by the manual smoke checklist below.

### Manual smoke checklist (run before shipping phase 1)

- [ ] Open a PNG via File → Open.
- [ ] Open a JPEG via drag-and-drop.
- [ ] Click and drag on the preview — arrow overlay renders and follows cursor.
- [ ] Release the drag — arrow freezes; sliders show the committed angle and length.
- [ ] Move a slider — preview updates within a few frames, no visible queueing lag.
- [ ] Switch blur-type dropdown to "Out of Focus" — "Coming soon" panel appears.
- [ ] Switch back to "Motion" — sidebar returns, arrow and sliders intact.
- [ ] Click "Render full resolution" — modal progress appears, then closes; preview reflects the full-res result.
- [ ] File → Save As → PNG. Reopen the saved file; matches what was on screen.
- [ ] Drop a corrupt file — error modal appears; app state unchanged.
- [ ] Click Reset — arrow clears, sliders return to defaults, preview shows the untouched image.

## Dependencies

- **`FftSharp`** (NuGet) — FFT.
- **`CommunityToolkit.Mvvm`** (NuGet) — observable properties, relay commands.
- **`xunit`, `xunit.runner.visualstudio`** (NuGet, test project only).

All MIT-licensed; the app stays MIT-compatible.
