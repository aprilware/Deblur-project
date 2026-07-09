# Deblur Phase 4 Implementation Plan (Tikhonov Deconvolution)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Tikhonov deconvolution (Laplacian regularization) alongside Wiener, selectable from a new Algorithm dropdown in the sidebar.

**Architecture:** New `AlgorithmType` enum + `Algorithm` field on `KernelParams`. `TikhonovDeconvolver` implements `IDeconvolver` and reuses the reflect-pad + FFT scaffolding from `WienerDeconvolver`, differing only in the spectral divide's denominator (replaces `K` with `λ·|C|²` where `C` is the analytical DFT of the discrete 5-point Laplacian). `DeblurJobRunner`'s constructor grows a second dictionary `IReadOnlyDictionary<AlgorithmType, IDeconvolver>` and routes by `p.Algorithm`. `MainViewModel` injects both dictionaries; `MainWindow.xaml` gains an Algorithm ComboBox and a value converter that swaps the shared Smoothness slider's label between "Smoothness (K)" and "Regularization (λ)". Total Variation is deferred.

**Tech Stack:** .NET 8 (`net8.0-windows` WPF, `net8.0` Engine + Tests), WPF, CommunityToolkit.Mvvm 8.4.2, FftSharp 2.2.0, System.Drawing.Common, xUnit.

## Global Constraints

- Target framework: `net8.0` for `Deblur.Engine` and `Deblur.Tests`; `net8.0-windows` for the WPF `Deblur` project.
- `Nullable` and `ImplicitUsings` enabled everywhere.
- `Deblur.Engine` stays WPF-free (no `System.Windows` references).
- No new NuGet packages for phase 4.
- MVVM via `CommunityToolkit.Mvvm 8.4.2`.
- All 47 phase-3 tests remain green after every task.
- `Algorithm` is appended as the last field of `KernelParams` — every existing construction site gets a trailing `AlgorithmType.Wiener`.
- `DeblurJobRunner` takes TWO dictionaries: `(IReadOnlyDictionary<BlurType, IBlurKernel> kernels, IReadOnlyDictionary<AlgorithmType, IDeconvolver> deconvolvers)`.
- Routing: `_kernels[p.Type]` (unchanged), `_deconvolvers[p.Algorithm]` (new).
- `IsNoOp` is unchanged — the raw-passthrough decision is a property of the blur PSF, not the algorithm.
- `TikhonovDeconvolver.Apply` treats `DeconvolutionParams.K` as `λ` (Tikhonov's regularization coefficient); no new field on `DeconvolutionParams`.
- Tikhonov denominator formula: `|H|² + λ · (Cu + Cv)²` where `Cu = 2 - 2·cos(2π·u/fftSize)`, `Cv = 2 - 2·cos(2π·v/fftSize)`.
- Sidebar layout: existing structure plus a new "Algorithm" TextBlock + ComboBox pair immediately beneath "Blur type"; the shared-footer Smoothness `<TextBlock>` text is bound via a converter.
- `MainViewModel.OnSelectedAlgorithmChanged` fires `PropertyChanged` for `IsWienerSelected` and `IsTikhonovSelected`, invalidates the full-res cache, and calls `PushCurrentParams`.
- `Reset()` does NOT reset `SelectedAlgorithm`.
- Phase 4 branches from tag `phase3` onto branch `phase4-tikhonov`.

---

### Task 1: `AlgorithmType` enum + `Algorithm` field on `KernelParams`

**Files:**
- Create: `Deblur.Engine/AlgorithmType.cs`
- Modify: `Deblur.Engine/KernelParams.cs`
- Modify: `Deblur/ViewModels/MainViewModel.cs:168`
- Modify: `Deblur.Tests/DeblurJobRunnerTests.cs` (9 sites at lines 48, 75, 91, 110, 138, 167, 197, 226, 251)
- Modify: `Deblur.Tests/MotionBlurKernelTests.cs` (5 sites at lines 26, 34, 49, 51, 62)
- Modify: `Deblur.Tests/OutOfFocusBlurKernelTests.cs` (5 sites at lines 22, 29, 39, 47, 63)
- Modify: `Deblur.Tests/GaussianBlurKernelTests.cs` (5 sites at lines 22, 29, 39, 47, 62)
- Modify: `Deblur.Tests/WienerDeconvolverTests.cs` (7 sites at lines 17, 32, 34, 50, 73, 92, 113)

**Interfaces:**
- Consumes: nothing new.
- Produces: `AlgorithmType { Wiener, Tikhonov }`. `KernelParams` becomes `(BlurType Type, float Angle, float Length, float Smoothness, float Radius, float Sigma, AlgorithmType Algorithm)`. Every existing call site adds a trailing `AlgorithmType.Wiener`. No behavior change.

- [ ] **Step 1: Create the `AlgorithmType` enum**

Create `Deblur.Engine/AlgorithmType.cs`:
```csharp
namespace Deblur.Engine;

public enum AlgorithmType
{
    Wiener,
    Tikhonov,
}
```

- [ ] **Step 2: Extend `KernelParams`**

Replace `Deblur.Engine/KernelParams.cs`:
```csharp
namespace Deblur.Engine;

public readonly record struct KernelParams(
    BlurType Type,
    float Angle,
    float Length,
    float Smoothness,
    float Radius,
    float Sigma,
    AlgorithmType Algorithm);
```

- [ ] **Step 3: Update the single production call site in `MainViewModel`**

In `Deblur/ViewModels/MainViewModel.cs`, edit line 168:
```csharp
        => new KernelParams(SelectedBlurType, Angle, Length, Smoothness, Radius, Sigma, AlgorithmType.Wiener);
```
Do NOT reference a `SelectedAlgorithm` property here — Task 4 will add the observable and update this line to use it.

- [ ] **Step 4: Update the 31 test call sites — add trailing `AlgorithmType.Wiener` to each**

In `Deblur.Tests/DeblurJobRunnerTests.cs`:
```csharp
// line 48
            runner.Request(new KernelParams(BlurType.Motion, Angle: i, Length: 5f, Smoothness: 0.005f, Radius: 0f, Sigma: 0f, Algorithm: AlgorithmType.Wiener));
// line 75
            new KernelParams(BlurType.Motion, 45f, 10f, 0.005f, 0f, 0f, AlgorithmType.Wiener), proxyScale: 0.25f);
// line 91
            new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0.005f, Radius: 10f, Sigma: 0f, Algorithm: AlgorithmType.Wiener), proxyScale: 0.25f);
// line 110
            runner.Request(new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0.005f, Radius: 5f, Sigma: 0f, Algorithm: AlgorithmType.Wiener));
// line 138
            runner.Request(new KernelParams(BlurType.Motion, 0f, Length: 0f, Smoothness: 0.005f, Radius: 0f, Sigma: 0f, Algorithm: AlgorithmType.Wiener));
// line 167
            runner.Request(new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0.005f, Radius: 0f, Sigma: 0f, Algorithm: AlgorithmType.Wiener));
// line 197
            runner.Request(new KernelParams(BlurType.Gaussian, 0f, 0f, 0.005f, 0f, Sigma: 3f, Algorithm: AlgorithmType.Wiener));
// line 226
            runner.Request(new KernelParams(BlurType.Gaussian, 0f, 0f, 0.005f, 0f, Sigma: 0f, Algorithm: AlgorithmType.Wiener));
// line 251
            new KernelParams(BlurType.Gaussian, 0f, 0f, 0.005f, 0f, Sigma: 3f, Algorithm: AlgorithmType.Wiener), proxyScale: 0.25f);
```

In `Deblur.Tests/MotionBlurKernelTests.cs`:
```csharp
// line 26
            new KernelParams(BlurType.Motion, angleDeg, length, 0, 0f, 0f, AlgorithmType.Wiener));
// line 34
            new KernelParams(BlurType.Motion, 45f, 1f, 0, 0f, 0f, AlgorithmType.Wiener));
// line 49
            new KernelParams(BlurType.Motion, 30f, 15f, 0, 0f, 0f, AlgorithmType.Wiener));
// line 51
            new KernelParams(BlurType.Motion, 30f + 180f, 15f, 0, 0f, 0f, AlgorithmType.Wiener));
// line 62
            new KernelParams(BlurType.Motion, 45f, 10f, 0, 0f, 0f, AlgorithmType.Wiener));
```

In `Deblur.Tests/OutOfFocusBlurKernelTests.cs`:
```csharp
// line 22
            () => kernel.Build(new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0f, -1f, 0f, AlgorithmType.Wiener)));
// line 29
        var k = kernel.Build(new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0f, 0f, 0f, AlgorithmType.Wiener));
// line 39
        var k = kernel.Build(new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0f, 8f, 0f, AlgorithmType.Wiener));
// line 47
        var k = kernel.Build(new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0f, 6f, 0f, AlgorithmType.Wiener));
// line 63
        var k = kernel.Build(new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0f, 5f, 0f, AlgorithmType.Wiener));
```

In `Deblur.Tests/GaussianBlurKernelTests.cs`:
```csharp
// line 22
            () => kernel.Build(new KernelParams(BlurType.Gaussian, 0f, 0f, 0f, 0f, -1f, AlgorithmType.Wiener)));
// line 29
        var k = kernel.Build(new KernelParams(BlurType.Gaussian, 0f, 0f, 0f, 0f, 0f, AlgorithmType.Wiener));
// line 39
        var k = kernel.Build(new KernelParams(BlurType.Gaussian, 0f, 0f, 0f, 0f, 2f, AlgorithmType.Wiener));
// line 47
        var k = kernel.Build(new KernelParams(BlurType.Gaussian, 0f, 0f, 0f, 0f, 2f, AlgorithmType.Wiener));
// line 62
        var k = kernel.Build(new KernelParams(BlurType.Gaussian, 0f, 0f, 0f, 0f, 2f, AlgorithmType.Wiener));
```

In `Deblur.Tests/WienerDeconvolverTests.cs`:
```csharp
// line 17
            new KernelParams(BlurType.Motion, 30f, 12f, 0, 0f, 0f, AlgorithmType.Wiener));
// line 32
            new KernelParams(BlurType.Motion, 30f, 12f, 0, 0f, 0f, AlgorithmType.Wiener));
// line 34
            new KernelParams(BlurType.Motion, 90f, 12f, 0, 0f, 0f, AlgorithmType.Wiener));
// line 50
            new KernelParams(BlurType.Motion, 0f, 8f, 0, 0f, 0f, AlgorithmType.Wiener));
// line 73
            new KernelParams(BlurType.Motion, 22f, 100f, 0, 0f, 0f, AlgorithmType.Wiener));
// line 92
            new KernelParams(BlurType.OutOfFocus, 0f, 0f, 0f, 4f, 0f, AlgorithmType.Wiener));
// line 113
            new KernelParams(BlurType.Gaussian, 0f, 0f, 0f, 0f, 2f, AlgorithmType.Wiener));
```

- [ ] **Step 5: Run the full test suite — confirm no regressions**

```bash
dotnet test Deblur.sln
```
Expected: `Passed: 47`.

- [ ] **Step 6: Commit**

```bash
git add Deblur.Engine/AlgorithmType.cs Deblur.Engine/KernelParams.cs Deblur/ViewModels/MainViewModel.cs Deblur.Tests/DeblurJobRunnerTests.cs Deblur.Tests/MotionBlurKernelTests.cs Deblur.Tests/OutOfFocusBlurKernelTests.cs Deblur.Tests/GaussianBlurKernelTests.cs Deblur.Tests/WienerDeconvolverTests.cs
git commit -m "Add AlgorithmType enum and Algorithm field to KernelParams (mechanical)"
```

---

### Task 2: `TikhonovDeconvolver` + tests (TDD)

**Files:**
- Create: `Deblur.Engine/TikhonovDeconvolver.cs`
- Create: `Deblur.Tests/TikhonovDeconvolverTests.cs`

**Interfaces:**
- Consumes: `IDeconvolver`, `DeconvolutionParams`, `KernelParams`, `MotionBlurKernel`, `OutOfFocusBlurKernel`, `GaussianBlurKernel`, `FftAdapter`, `SyntheticImages` from `Deblur.Tests.TestHelpers`.
- Produces:
```csharp
public sealed class TikhonovDeconvolver : IDeconvolver
{
    public ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p);
}
```
- The `DeconvolutionParams.K` is reinterpreted as `λ` inside Tikhonov's spectral divide; no new field.

- [ ] **Step 1: Write the failing unit tests**

Create `Deblur.Tests/TikhonovDeconvolverTests.cs`:
```csharp
using Deblur.Engine;
using Deblur.Tests.TestHelpers;
using Xunit;

namespace Deblur.Tests;

public class TikhonovDeconvolverTests
{
    [Fact]
    public void RoundTrip_RecoversCheckerboard_AbovePsnrThreshold()
    {
        var original = SyntheticImages.Checkerboard(128, 128, 32);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 12f, 0, 0f, 0f, AlgorithmType.Tikhonov));
        var blurred = SyntheticImages.Convolve(original, psf);
        var noisy = SyntheticImages.AddGaussianNoise(blurred, 0.005f, seed: 42);

        var deconv = new TikhonovDeconvolver().Apply(
            noisy, psf, new DeconvolutionParams(K: 0.005f));

        Assert.True(SyntheticImages.Psnr(original, deconv) > 20f);
    }

    [Fact]
    public void Gaussian_RoundTrip_RecoversAbovePsnrThreshold()
    {
        var original = SyntheticImages.Checkerboard(128, 128, 32);
        var psf = new GaussianBlurKernel().Build(
            new KernelParams(BlurType.Gaussian, 0f, 0f, 0f, 0f, 2f, AlgorithmType.Tikhonov));
        var blurred = SyntheticImages.Convolve(original, psf);
        var noisy = SyntheticImages.AddGaussianNoise(blurred, 0.005f, seed: 42);

        var deconv = new TikhonovDeconvolver().Apply(
            noisy, psf, new DeconvolutionParams(K: 0.005f));

        float blurredPsnr = SyntheticImages.Psnr(original, blurred);
        float deconvPsnr = SyntheticImages.Psnr(original, deconv);
        Assert.True(deconvPsnr > 15f, $"deconv PSNR {deconvPsnr} below 15 dB floor");
        Assert.True(deconvPsnr > blurredPsnr + 2.5f,
            $"deconv PSNR {deconvPsnr} not > blurred {blurredPsnr} + 2.5 dB");
    }

    [Fact]
    public void WrongPsf_WorsePsnrThanBlurred()
    {
        var original = SyntheticImages.Checkerboard(128, 128, 8);
        var truePsf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 12f, 0, 0f, 0f, AlgorithmType.Tikhonov));
        var wrongPsf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 90f, 12f, 0, 0f, 0f, AlgorithmType.Tikhonov));
        var blurred = SyntheticImages.Convolve(original, truePsf);

        var deconv = new TikhonovDeconvolver().Apply(
            blurred, wrongPsf, new DeconvolutionParams(K: 0.005f));

        float blurredPsnr = SyntheticImages.Psnr(original, blurred);
        float wrongPsnr = SyntheticImages.Psnr(original, deconv);
        Assert.True(wrongPsnr < blurredPsnr);
    }

    [Fact]
    public void BorderPixels_BoundedVariance()
    {
        var original = SyntheticImages.Checkerboard(128, 128, 8);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 0f, 8f, 0, 0f, 0f, AlgorithmType.Tikhonov));
        var blurred = SyntheticImages.Convolve(original, psf);
        var deconv = new TikhonovDeconvolver().Apply(
            blurred, psf, new DeconvolutionParams(K: 0.005f));

        double mean = 0, mean2 = 0; int n = 0;
        for (int y = 0; y < 5; y++)
            for (int x = 0; x < deconv.Width; x++)
            {
                float v = deconv.R[y * deconv.Width + x];
                mean += v; mean2 += v * v; n++;
            }
        mean /= n; mean2 /= n;
        double variance = mean2 - mean * mean;
        Assert.True(variance < 0.2, $"variance {variance} too high — border ringing?");
    }

    [Fact]
    public void ExtremeParams_NoNaNOrInfInOutput()
    {
        var original = SyntheticImages.Checkerboard(64, 64, 4);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 22f, 100f, 0, 0f, 0f, AlgorithmType.Tikhonov));
        var deconv = new TikhonovDeconvolver().Apply(
            original, psf, new DeconvolutionParams(K: 1e-6f));

        for (int i = 0; i < deconv.PixelCount; i++)
        {
            Assert.False(float.IsNaN(deconv.R[i]) || float.IsInfinity(deconv.R[i]));
            Assert.False(float.IsNaN(deconv.G[i]) || float.IsInfinity(deconv.G[i]));
            Assert.False(float.IsNaN(deconv.B[i]) || float.IsInfinity(deconv.B[i]));
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail (compile errors)**

```bash
dotnet test Deblur.sln --filter "FullyQualifiedName~TikhonovDeconvolverTests"
```
Expected: compile errors — `TikhonovDeconvolver` not defined.

- [ ] **Step 3: Implement `TikhonovDeconvolver`**

Create `Deblur.Engine/TikhonovDeconvolver.cs`:
```csharp
using System.Numerics;

namespace Deblur.Engine;

public sealed class TikhonovDeconvolver : IDeconvolver
{
    public ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p)
    {
        int psfH = psf.GetLength(0);
        int psfW = psf.GetLength(1);
        int pad = Math.Max(psfW, psfH) / 2 + 1;

        int paddedW = input.Width + 2 * pad;
        int paddedH = input.Height + 2 * pad;
        int fftSize = FftAdapter.NextPow2(Math.Max(paddedW, paddedH));

        // Build centered PSF in an fftSize x fftSize buffer with DC at (0,0).
        var psfBuf = new float[fftSize, fftSize];
        int cy = psfH / 2, cx = psfW / 2;
        for (int y = 0; y < psfH; y++)
            for (int x = 0; x < psfW; x++)
            {
                int dy = (y - cy + fftSize) % fftSize;
                int dx = (x - cx + fftSize) % fftSize;
                psfBuf[dy, dx] = psf[y, x];
            }
        var H = FftAdapter.Forward2D(psfBuf);

        // Precompute Tikhonov numerator: conj(H) / (|H|^2 + lambda*|C|^2),
        // where |C(u,v)|^2 = (Cu + Cv)^2 for the discrete 5-point Laplacian.
        var tikhonovNumer = new Complex[fftSize, fftSize];
        for (int y = 0; y < fftSize; y++)
        {
            double Cv = 2.0 - 2.0 * Math.Cos(2.0 * Math.PI * y / fftSize);
            for (int x = 0; x < fftSize; x++)
            {
                double Cu = 2.0 - 2.0 * Math.Cos(2.0 * Math.PI * x / fftSize);
                double cSq = (Cu + Cv) * (Cu + Cv);
                var h = H[y, x];
                double mag2 = h.Real * h.Real + h.Imaginary * h.Imaginary;
                tikhonovNumer[y, x] = Complex.Conjugate(h) / (mag2 + p.K * cSq);
            }
        }

        float[] outR = ProcessChannel(input.R, input.Width, input.Height, pad, fftSize, tikhonovNumer);
        float[] outG = ProcessChannel(input.G, input.Width, input.Height, pad, fftSize, tikhonovNumer);
        float[] outB = ProcessChannel(input.B, input.Width, input.Height, pad, fftSize, tikhonovNumer);
        return new ImageBuffer(input.Width, input.Height, outR, outG, outB);
    }

    private static float[] ProcessChannel(
        float[] channel, int w, int h, int pad, int fftSize, Complex[,] tikhonovNumer)
    {
        var padded = new float[fftSize, fftSize];
        for (int y = 0; y < fftSize; y++)
        {
            int sy = ReflectIndex(y - pad, h);
            for (int x = 0; x < fftSize; x++)
            {
                int sx = ReflectIndex(x - pad, w);
                padded[y, x] = channel[sy * w + sx];
            }
        }

        var G = FftAdapter.Forward2D(padded);
        var Fhat = new Complex[fftSize, fftSize];
        for (int y = 0; y < fftSize; y++)
            for (int x = 0; x < fftSize; x++)
                Fhat[y, x] = tikhonovNumer[y, x] * G[y, x];

        var real = FftAdapter.Inverse2DReal(Fhat);

        var result = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float v = real[y + pad, x + pad];
                if (!float.IsFinite(v)) v = 0f;
                result[y * w + x] = Math.Clamp(v, 0f, 1f);
            }
        }
        return result;
    }

    private static int ReflectIndex(int i, int len)
    {
        if (len <= 1) return 0;
        int period = 2 * (len - 1);
        int m = ((i % period) + period) % period;
        return m < len ? m : period - m;
    }
}
```

Notes:
- At `(u=0, v=0)`, `|C|² = 0`, so the denominator becomes just `|H|²`; if `H` also happens to be zero there the point produces NaN, but the reflect-pad DC coefficient of `H` is nonzero for any nontrivial PSF, and the `float.IsFinite` guard at crop time handles any residual singularity.
- Reflect-pad and FFT plumbing are copy-pasted from `WienerDeconvolver` deliberately — the two implementations share this scaffolding as a phase-4 non-goal (three-way abstraction lands in phase 5).

- [ ] **Step 4: Run the filtered tests — verify green**

```bash
dotnet test Deblur.sln --filter "FullyQualifiedName~TikhonovDeconvolverTests"
```
Expected: 5 passing.

- [ ] **Step 5: Run the full suite to confirm no regression**

```bash
dotnet test Deblur.sln
```
Expected: `Passed: 52` (47 phase-3 + 5 new).

- [ ] **Step 6: Commit**

```bash
git add Deblur.Engine/TikhonovDeconvolver.cs Deblur.Tests/TikhonovDeconvolverTests.cs
git commit -m "Add TikhonovDeconvolver with Laplacian regularization"
```

---

### Task 3: `DeblurJobRunner` — deconvolver dictionary + routing test

**Files:**
- Modify: `Deblur.Engine/DeblurJobRunner.cs`
- Modify: `Deblur.Tests/DeblurJobRunnerTests.cs`

**Interfaces:**
- Consumes: `TikhonovDeconvolver` (Task 2), `AlgorithmType` (Task 1).
- Produces:
```csharp
public sealed class DeblurJobRunner : IDisposable
{
    public DeblurJobRunner(
        IReadOnlyDictionary<BlurType, IBlurKernel> kernels,
        IReadOnlyDictionary<AlgorithmType, IDeconvolver> deconvolvers);
    // remainder unchanged (SetProxy, Request, RenderFullAsync, ProxyReady, Idle, HasPending, Dispose)
}
```
- `WorkerLoop` and `RenderFullAsync` both look up `_deconvolvers[p.Algorithm]` at the point where they currently reference `_deconvolver`.

- [ ] **Step 1: Write the failing routing test**

Modify `Deblur.Tests/DeblurJobRunnerTests.cs`. First, add a new `RecordingStubDeconvolver` class inside the existing `DeblurJobRunnerTests` class (alongside the existing `SlowStubDeconvolver` and `RecordingStubKernel`):

```csharp
    private sealed class RecordingStubDeconvolver : IDeconvolver
    {
        public readonly System.Collections.Concurrent.ConcurrentBag<KernelParams> Applied = new();
        public int SleepMs { get; init; } = 0;

        public ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p)
        {
            // We only need the algorithm for routing; the caller's KernelParams isn't
            // reachable here, so we record something distinguishable via the PSF hash.
            // In practice, the routing test uses a stub kernel that echoes p.Type into
            // the psf, but simplest is to just record we were called.
            Applied.Add(new KernelParams(BlurType.Motion, 0f, 0f, p.K, 0f, 0f, AlgorithmType.Wiener));
            if (SleepMs > 0) Thread.Sleep(SleepMs);
            return input.Clone();
        }
    }
```

Then add the new `[Fact]` inside the same class:

```csharp
    [Fact]
    public void Request_WithTikhonovAlgorithm_DispatchesToTikhonovDeconvolver()
    {
        var kernel = new RecordingStubKernel();
        var wienerDeconv = new RecordingStubDeconvolver();
        var tikhonovDeconv = new RecordingStubDeconvolver();
        var kernels = new Dictionary<BlurType, IBlurKernel> { [BlurType.Motion] = kernel };
        var deconvolvers = new Dictionary<AlgorithmType, IDeconvolver>
        {
            [AlgorithmType.Wiener]   = wienerDeconv,
            [AlgorithmType.Tikhonov] = tikhonovDeconv,
        };
        using var runner = new DeblurJobRunner(kernels, deconvolvers);
        runner.SetProxy(SyntheticImages.Checkerboard(32, 32, 4));

        runner.Request(new KernelParams(BlurType.Motion, 0f, Length: 5f, Smoothness: 0.005f, Radius: 0f, Sigma: 0f, Algorithm: AlgorithmType.Tikhonov));

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
            if (tikhonovDeconv.Applied.Count > 0 && !runner.HasPending) break;
        }

        Assert.NotEmpty(tikhonovDeconv.Applied);
        Assert.Empty(wienerDeconv.Applied);
    }
```

Also update the existing 9 `DeblurJobRunnerTests` constructions. Each of the existing `using var runner = new DeblurJobRunner(kernels, deconv);` sites needs the second argument wrapped in a deconvolver dictionary. Replace those 9 lines with:

```csharp
        var deconvolvers = new Dictionary<AlgorithmType, IDeconvolver>
        {
            [AlgorithmType.Wiener]   = deconv,
            [AlgorithmType.Tikhonov] = deconv,
        };
        using var runner = new DeblurJobRunner(kernels, deconvolvers);
```

(Reusing the same `deconv` instance for both algorithm keys keeps the existing tests semantically identical — they all send `AlgorithmType.Wiener` requests via the default in `KernelParams`, so only the Wiener entry is exercised; the Tikhonov entry is a defensive duplicate.)

- [ ] **Step 2: Run tests to verify they fail (compile errors)**

```bash
dotnet test Deblur.sln --filter "FullyQualifiedName~DeblurJobRunnerTests"
```
Expected: compile errors on the dictionary constructor and the new test's use of `AlgorithmType.Tikhonov`.

- [ ] **Step 3: Update `DeblurJobRunner` to take a deconvolver dictionary**

Replace `Deblur.Engine/DeblurJobRunner.cs`:
```csharp
namespace Deblur.Engine;

public sealed class ProxyReadyEventArgs : EventArgs
{
    public required byte[] Bgra { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
}

public sealed class DeblurJobRunner : IDisposable
{
    private readonly IReadOnlyDictionary<BlurType, IBlurKernel> _kernels;
    private readonly IReadOnlyDictionary<AlgorithmType, IDeconvolver> _deconvolvers;
    private readonly Thread _worker;
    private readonly ManualResetEventSlim _signal = new(false);
    private readonly object _lock = new();

    private ImageBuffer? _proxy;
    private KernelParams? _pending;
    private volatile bool _running = true;

    public event EventHandler<ProxyReadyEventArgs>? ProxyReady;

    /// <summary>Fires on the worker thread each time the pending queue drains to empty.</summary>
    public event EventHandler? Idle;

    public bool HasPending
    {
        get { lock (_lock) return _pending.HasValue; }
    }

    public DeblurJobRunner(
        IReadOnlyDictionary<BlurType, IBlurKernel> kernels,
        IReadOnlyDictionary<AlgorithmType, IDeconvolver> deconvolvers)
    {
        _kernels = kernels;
        _deconvolvers = deconvolvers;
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "DeblurWorker" };
        _worker.Start();
    }

    public void SetProxy(ImageBuffer proxy)
    {
        lock (_lock) _proxy = proxy;
    }

    public void Request(KernelParams p)
    {
        lock (_lock) _pending = p;
        _signal.Set();
    }

    public Task<ImageBuffer> RenderFullAsync(
        ImageBuffer fullRes, KernelParams p, float proxyScale, IProgress<double>? progress = null)
    {
        return Task.Run(() =>
        {
            progress?.Report(0.1);
            float scaleInv = 1f / Math.Max(proxyScale, 1e-6f);
            var scaledParams = p with
            {
                Length = p.Length * scaleInv,
                Radius = p.Radius * scaleInv,
                Sigma  = p.Sigma  * scaleInv,
            };
            if (IsNoOp(scaledParams))
            {
                progress?.Report(1.0);
                return fullRes.Clone();
            }
            var psf = _kernels[scaledParams.Type].Build(scaledParams);
            progress?.Report(0.3);
            var result = _deconvolvers[scaledParams.Algorithm].Apply(fullRes, psf, new DeconvolutionParams(K: p.Smoothness));
            progress?.Report(1.0);
            return result;
        });
    }

    /// <summary>
    /// Returns true for parameter sets that produce a raw-passthrough (no deconvolution) result.
    /// Any BlurType this switch treats as a no-op need not be present in the injected kernel
    /// dictionary; any type that reaches the else branch of WorkerLoop / RenderFullAsync MUST
    /// have a corresponding entry. Keep this switch in sync with the dictionary the caller
    /// injects in MainViewModel.
    /// </summary>
    private static bool IsNoOp(KernelParams p) => p.Type switch
    {
        BlurType.Motion     => p.Length < 1f,
        BlurType.OutOfFocus => p.Radius < 1f,
        BlurType.Gaussian   => p.Sigma  < 1f,
        _                   => true,
    };

    private void WorkerLoop()
    {
        while (_running)
        {
            _signal.Wait();
            _signal.Reset();

            while (true)
            {
                KernelParams p;
                ImageBuffer? proxy;
                lock (_lock)
                {
                    if (_pending is null || _proxy is null)
                    {
                        if (_running) Idle?.Invoke(this, EventArgs.Empty);
                        break;
                    }
                    p = _pending.Value;
                    proxy = _proxy;
                    _pending = null;
                }

                ImageBuffer deconv;
                if (IsNoOp(p))
                {
                    deconv = proxy;
                }
                else
                {
                    var psf = _kernels[p.Type].Build(p);
                    deconv = _deconvolvers[p.Algorithm].Apply(
                        proxy, psf, new DeconvolutionParams(K: p.Smoothness));
                }

                int w = deconv.Width, h = deconv.Height;
                var bgra = new byte[w * h * 4];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        int o = i * 4;
                        bgra[o] = Clamp8(deconv.B[i]);
                        bgra[o + 1] = Clamp8(deconv.G[i]);
                        bgra[o + 2] = Clamp8(deconv.R[i]);
                        bgra[o + 3] = 255;
                    }
                }

                ProxyReady?.Invoke(this, new ProxyReadyEventArgs
                {
                    Bgra = bgra, Width = w, Height = h,
                });
            }
        }
    }

    private static byte Clamp8(float v)
    {
        int i = (int)MathF.Round(v * 255f);
        return (byte)Math.Clamp(i, 0, 255);
    }

    public void Dispose()
    {
        _running = false;
        _signal.Set();
        _worker.Join(1000);
        _signal.Dispose();
    }
}
```

- [ ] **Step 4: Run the runner tests — verify green**

```bash
dotnet test Deblur.Tests/Deblur.Tests.csproj --filter "FullyQualifiedName~DeblurJobRunnerTests"
```
Expected: 10 passing (9 pre-existing + 1 new).

Note: target the test project directly. `MainViewModel` still calls the old `DeblurJobRunner(kernels, deconv)` constructor and the WPF project will not build until Task 4 lands. `dotnet build Deblur.sln` at the solution level WILL fail on `Deblur/ViewModels/MainViewModel.cs`.

- [ ] **Step 5: Run the full engine test suite — verify no regression**

```bash
dotnet test Deblur.Tests/Deblur.Tests.csproj
```
Expected: `Passed: 53` (47 phase-3 + 5 from Task 2 + 1 new).

- [ ] **Step 6: Commit**

```bash
git add Deblur.Engine/DeblurJobRunner.cs Deblur.Tests/DeblurJobRunnerTests.cs
git commit -m "Route DeblurJobRunner by AlgorithmType via deconvolver dictionary"
```

---

### Task 4: `MainViewModel` — `SelectedAlgorithm` observable, deconvolver dictionary, computed props

**Files:**
- Modify: `Deblur/ViewModels/MainViewModel.cs`

**Interfaces:**
- Consumes: `AlgorithmType` (Task 1), `TikhonovDeconvolver` (Task 2), new `DeblurJobRunner` constructor (Task 3).
- Produces: `MainViewModel` gains `SelectedAlgorithm` observable + `IsWienerSelected` / `IsTikhonovSelected` computed. Constructor builds both dictionaries. `BuildCurrentParams` includes `SelectedAlgorithm`.

- [ ] **Step 1: Add the `SelectedAlgorithm` observable**

In `Deblur/ViewModels/MainViewModel.cs`, add after the existing `_selectedBlurType` observable (line 19):
```csharp
    [ObservableProperty] private AlgorithmType _selectedAlgorithm = AlgorithmType.Wiener;
```

- [ ] **Step 2: Add the computed properties**

Add after the existing `HasImage` computed property (around line 33):
```csharp
    public bool IsWienerSelected   => SelectedAlgorithm == AlgorithmType.Wiener;
    public bool IsTikhonovSelected => SelectedAlgorithm == AlgorithmType.Tikhonov;
```

- [ ] **Step 3: Build the deconvolver dictionary and update the runner constructor call**

Replace the `_runner` construction in the `MainViewModel` constructor (currently around line 45):
```csharp
        var deconvolvers = new Dictionary<AlgorithmType, IDeconvolver>
        {
            [AlgorithmType.Wiener]   = new WienerDeconvolver(),
            [AlgorithmType.Tikhonov] = new TikhonovDeconvolver(),
        };
        _runner = new DeblurJobRunner(kernels, deconvolvers);
```

- [ ] **Step 4: Add the `OnSelectedAlgorithmChanged` partial method**

Add after the existing `OnSelectedBlurTypeChanged` partial method (around line 65):
```csharp
    partial void OnSelectedAlgorithmChanged(AlgorithmType value)
    {
        OnPropertyChanged(nameof(IsWienerSelected));
        OnPropertyChanged(nameof(IsTikhonovSelected));
        InvalidateFullResCache();
        PushCurrentParams();
    }
```

- [ ] **Step 5: Update `BuildCurrentParams` to include `SelectedAlgorithm`**

Replace `BuildCurrentParams()` (line 168 as-of Task 1):
```csharp
    private KernelParams BuildCurrentParams()
        => new KernelParams(SelectedBlurType, Angle, Length, Smoothness, Radius, Sigma, SelectedAlgorithm);
```

- [ ] **Step 6: Build the whole solution — confirm the WPF project now compiles**

```bash
dotnet build Deblur.sln
```
Expected: 0 errors. The XAML doesn't reference `SelectedAlgorithm` yet (Task 5 adds the ComboBox binding), but the ViewModel compiles and the runner routes correctly.

- [ ] **Step 7: Run the full test suite — no regressions**

```bash
dotnet test Deblur.sln
```
Expected: `Passed: 53`.

- [ ] **Step 8: Commit**

```bash
git add Deblur/ViewModels/MainViewModel.cs
git commit -m "Wire MainViewModel for Tikhonov: SelectedAlgorithm, deconvolver dictionary"
```

---

### Task 5: XAML — Algorithm ComboBox + label converter

**Files:**
- Create: `Deblur/Converters/AlgorithmToSmoothnessLabelConverter.cs`
- Modify: `Deblur/App.xaml`
- Modify: `Deblur/MainWindow.xaml`

**Interfaces:**
- Consumes: `MainViewModel.SelectedAlgorithm`, `AlgorithmType`.
- Produces: Sidebar gains an "Algorithm" ComboBox row directly beneath "Blur type"; the shared-footer Smoothness slider's label swaps between "Smoothness (K)" and "Regularization (λ)" via the converter.

- [ ] **Step 1: Create the label converter**

Create `Deblur/Converters/AlgorithmToSmoothnessLabelConverter.cs`:
```csharp
using System.Globalization;
using System.Windows.Data;
using Deblur.Engine;

namespace Deblur.Converters;

public sealed class AlgorithmToSmoothnessLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is AlgorithmType.Tikhonov ? "Regularization (λ)" : "Smoothness (K)";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

- [ ] **Step 2: Register the converter and the AlgorithmType ObjectDataProvider in `App.xaml`**

Replace `Deblur/App.xaml`:
```xml
<Application x:Class="Deblur.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:sys="clr-namespace:System;assembly=mscorlib"
             xmlns:engine="clr-namespace:Deblur.Engine;assembly=Deblur.Engine"
             xmlns:converters="clr-namespace:Deblur.Converters"
             StartupUri="MainWindow.xaml">
    <Application.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVis"/>
        <converters:AlgorithmToSmoothnessLabelConverter x:Key="AlgLabel"/>
        <ObjectDataProvider x:Key="AlgorithmTypeValues" MethodName="GetValues" ObjectType="{x:Type sys:Enum}">
            <ObjectDataProvider.MethodParameters>
                <x:Type TypeName="engine:AlgorithmType"/>
            </ObjectDataProvider.MethodParameters>
        </ObjectDataProvider>
    </Application.Resources>
</Application>
```

Note: `App.xaml`'s existing resources may include only the `BooleanToVisibilityConverter`; the block above is the full replacement.

- [ ] **Step 3: Add the Algorithm ComboBox and update the Smoothness label binding in `MainWindow.xaml`**

In `Deblur/MainWindow.xaml`, locate the "Blur type" `<TextBlock>` + `<ComboBox>` pair (currently around lines 42–44). Directly beneath the blur-type ComboBox closing tag, insert:

```xml
                    <TextBlock Text="Algorithm" FontWeight="Bold" Margin="0,12,0,4"/>
                    <ComboBox ItemsSource="{Binding Source={StaticResource AlgorithmTypeValues}}"
                              SelectedItem="{Binding SelectedAlgorithm}"/>
```

Also in the shared footer (currently around line 74–81), replace the Smoothness label's `<TextBlock>` line — currently `<TextBlock Text="Smoothness" Margin="0,4,0,0"/>` — with:

```xml
                        <TextBlock Text="{Binding SelectedAlgorithm, Converter={StaticResource AlgLabel}}" Margin="0,4,0,0"/>
```

Leave every other element (per-type Grids, PreviewCanvas, PreviewCanvas' `IsArrowEnabled` binding, ProgressBar, StatusMessage, drag-drop, BusyOverlay, menu) unchanged.

- [ ] **Step 4: Build**

```bash
dotnet build Deblur.sln
```
Expected: 0 errors, 0 new warnings.

- [ ] **Step 5: Run the full test suite — still green**

```bash
dotnet test Deblur.sln
```
Expected: `Passed: 53`.

- [ ] **Step 6: Commit**

```bash
git add Deblur/Converters/AlgorithmToSmoothnessLabelConverter.cs Deblur/App.xaml Deblur/MainWindow.xaml
git commit -m "Add Algorithm ComboBox and dynamic Smoothness label converter"
```

---

### Task 6: Manual smoke test pass + tag `phase4`

**Files:** none.

**Interfaces:** none.

- [ ] **Step 1: Run the app**

```bash
dotnet run --project Deblur/Deblur.csproj
```

Walk through the checklist:

- [ ] Launch app without an image loaded. Sidebar shows the two ComboBoxes ("Blur type" default Motion, "Algorithm" default Wiener) — no shared footer.
- [ ] Open a PNG. Sidebar shows Motion Grid + shared footer. Shared-footer label reads "Smoothness (K)".
- [ ] Switch Algorithm dropdown to "Tikhonov". The shared-footer label swaps to "Regularization (λ)". Preview updates (the same blur params run through Tikhonov instead of Wiener).
- [ ] Slide the Smoothness/Regularization slider — preview responds under Tikhonov.
- [ ] Switch to OutOfFocus (blur type). Radius slider appears; Algorithm dropdown still shows Tikhonov; shared-footer label still "Regularization (λ)". Drag Radius → preview updates.
- [ ] Switch to Gaussian. Same flow; Tikhonov Wiener-recovers a Gaussian PSF.
- [ ] Switch Algorithm back to Wiener. Label swaps back to "Smoothness (K)". Preview updates.
- [ ] Reset button: currently-selected blur type resets its params (Angle=0/Length=0 or Radius=0 or Sigma=0), Smoothness → 0.005. Algorithm does NOT reset (stays on whichever the user picked).
- [ ] Render full resolution → Save As → PNG under Tikhonov. Reopen the saved file externally; it should look like the Tikhonov-deblurred preview at full resolution.
- [ ] Drop a corrupt file (rename a `.txt` to `.jpg`) — error modal appears; app state unchanged.
- [ ] Progress bar behavior unchanged from phase 3 (thin indeterminate bar in sidebar footer during compute).

- [ ] **Step 2: Commit any smoke-test-triggered fixes**

If the smoke test surfaces bugs, fix them and commit each fix separately with a message describing the failure and the fix. If nothing was wrong, no commit is needed for this step.

- [ ] **Step 3: Tag phase 4 complete**

```bash
git tag phase4
```

---

## Summary

Six tasks, each an independently reviewable commit. Task 1 appends `Algorithm` to `KernelParams` and adds the `AlgorithmType` enum (mechanical, 32 sites). Task 2 adds `TikhonovDeconvolver` and its five unit tests (TDD, with a Wiener-parallel test shape). Task 3 refactors `DeblurJobRunner` to take a deconvolver dictionary and adds one routing test using a new `RecordingStubDeconvolver`. Task 4 wires `MainViewModel` with the `SelectedAlgorithm` observable, both computed properties, and the deconvolver dictionary. Task 5 adds the Algorithm ComboBox and the label-swap converter. Task 6 smoke-tests end-to-end and tags `phase4`.
