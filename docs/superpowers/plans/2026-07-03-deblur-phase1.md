# Deblur Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a WPF desktop app that removes motion blur from photos: user opens an image, drags an arrow on the preview opposite the blur direction, watches a live Wiener deconvolution update, then renders full resolution and saves. Blur-type UI is scaffolded (Motion / Out-of-Focus / Gaussian dropdown) but only Motion is functional in phase 1.

**Architecture:** Three-project solution. `Deblur.Engine` (pure C# library) owns image buffers, kernels, and deconvolution — no WPF. `Deblur.Tests` (xUnit) tests the engine end-to-end. `Deblur` (existing WPF app) does presentation only. A `DeblurJobRunner` (in the engine) coalesces requests and runs the algorithm on a background worker so slider drags stay honest to latest input.

**Tech Stack:** .NET 8, WPF, C# 12. `FftSharp` for FFT. `CommunityToolkit.Mvvm` for observable properties. `xUnit` for tests. All MIT-licensed.

## Global Constraints

- **Target frameworks:** `Deblur` = `net8.0-windows` with `<UseWPF>true</UseWPF>`; `Deblur.Engine` and `Deblur.Tests` = `net8.0`.
- **`Nullable`** and **`ImplicitUsings`** enabled in every project.
- **`Deblur.Engine` and `Deblur.Tests` must not reference any WPF or `System.Windows.*` types.** If a test needs to construct a `BitmapSource` or dispatch onto a UI thread, the design is wrong — factor it out into pure C#.
- **Numeric type:** `float` throughout the math path; `Complex32` for FFT. No `double`.
- **Pixel encoding:** internal buffers are three parallel `float[]` channels (R, G, B), values normalized to `[0, 1]`.
- **Proxy target:** downsample loaded images so the proxy is ≤ 1.5 megapixels while preserving aspect ratio.
- **Slider ranges:** Angle 0°–360°, Length 1–100 (proxy pixels), Smoothness log-scale 1e-4 to 1e-1.
- **Save default:** JPEG quality 92; PNG lossless.
- **Every dependency is MIT-or-equivalent permissive-licensed.** No FFTW, no GPL.

---

## File Structure

**`Deblur.Engine/`** (new, `net8.0`):
- `ImageBuffer.cs` — three-channel float image type.
- `BlurType.cs` — enum: `Motion`, `OutOfFocus`, `Gaussian`.
- `KernelParams.cs` — record struct (`BlurType`, `Angle`, `Length`, `Smoothness`).
- `IBlurKernel.cs` — interface: `float[,] Build(KernelParams p)`.
- `MotionBlurKernel.cs` — anti-aliased line-segment PSF.
- `DeconvolutionParams.cs` — record struct (`K` noise-to-signal ratio).
- `IDeconvolver.cs` — interface: `ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p)`.
- `WienerDeconvolver.cs` — Wiener deconvolution.
- `FftAdapter.cs` — thin wrapper around FftSharp.
- `ImageCodec.cs` — encode/decode via `System.Drawing` (WIC-backed on Windows).
- `InvalidImageFormatException.cs`.
- `DeblurJobRunner.cs` — coalescing background worker; public API per spec.

**`Deblur.Tests/`** (new, `net8.0`, xUnit):
- `ImageBufferTests.cs`
- `MotionBlurKernelTests.cs`
- `FftAdapterTests.cs`
- `WienerDeconvolverTests.cs`
- `ImageCodecTests.cs`
- `DeblurJobRunnerTests.cs`
- `TestHelpers/SyntheticImages.cs` — checkerboard + gradient generators, blur-by-convolution helper, PSNR.

**`Deblur/`** (existing WPF):
- `App.xaml`, `App.xaml.cs` — unchanged.
- `MainWindow.xaml`, `MainWindow.xaml.cs` — heavily modified (layout + wiring).
- `ViewModels/MainViewModel.cs` — new.
- `Controls/PreviewCanvas.xaml` + `.cs` — custom control hosting the `WriteableBitmap` and arrow overlay.
- `Services/ImageBufferInterop.cs` — convert `ImageBuffer` ↔ `WriteableBitmap`-shaped `byte[]` BGRA.

---

## Task List Overview

1. Bootstrap solution structure and dependencies.
2. `ImageBuffer` core type.
3. `BlurType`, `KernelParams`, `IBlurKernel` scaffolding + `MotionBlurKernel`.
4. `FftAdapter` wrapping FftSharp.
5. `DeconvolutionParams`, `IDeconvolver`, and `WienerDeconvolver`.
6. `ImageCodec` and `InvalidImageFormatException`.
7. `DeblurJobRunner` with coalescing and full-res render.
8. WPF: `ImageBufferInterop`, `MainViewModel` skeleton, blur-type dropdown, sliders.
9. WPF: `PreviewCanvas` with `WriteableBitmap` host and arrow overlay drag.
10. WPF: `MainWindow` layout wiring open / preview / sliders / reset.
11. WPF: Full-res render (modal progress) + Save flow.
12. WPF: Drag-and-drop + error modals.
13. Manual smoke test pass.

---

## Task 1: Bootstrap solution structure

**Files:**
- Create: `Deblur.Engine/Deblur.Engine.csproj`
- Create: `Deblur.Tests/Deblur.Tests.csproj`
- Modify: `Deblur.sln` (add both projects)
- Modify: `Deblur/Deblur.csproj` (add `CommunityToolkit.Mvvm` + reference `Deblur.Engine`)

**Interfaces:**
- Consumes: nothing.
- Produces: an empty `Deblur.Engine` library referenced by `Deblur` and `Deblur.Tests`; a working `dotnet test` command; a working `dotnet build Deblur.sln`.

- [ ] **Step 1: Create the engine class library**

Run:
```bash
cd C:/Users/priya/source/repos/Deblur
dotnet new classlib -n Deblur.Engine -f net8.0
```

Then edit `Deblur.Engine/Deblur.Engine.csproj` so it reads exactly:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

Delete the auto-generated `Deblur.Engine/Class1.cs`.

- [ ] **Step 2: Create the test project**

Run:
```bash
dotnet new xunit -n Deblur.Tests -f net8.0
```

Edit `Deblur.Tests/Deblur.Tests.csproj` so its `<PropertyGroup>` includes `<Nullable>enable</Nullable>` and `<ImplicitUsings>enable</ImplicitUsings>`.

Delete the auto-generated `Deblur.Tests/UnitTest1.cs`.

- [ ] **Step 3: Add NuGet packages**

Run:
```bash
dotnet add Deblur.Engine package FftSharp
dotnet add Deblur package CommunityToolkit.Mvvm
```

- [ ] **Step 4: Wire project references**

Run:
```bash
dotnet add Deblur reference Deblur.Engine/Deblur.Engine.csproj
dotnet add Deblur.Tests reference Deblur.Engine/Deblur.Engine.csproj
```

- [ ] **Step 5: Add projects to the solution**

Run:
```bash
dotnet sln Deblur.sln add Deblur.Engine/Deblur.Engine.csproj
dotnet sln Deblur.sln add Deblur.Tests/Deblur.Tests.csproj
```

- [ ] **Step 6: Verify build and empty test run**

Run:
```bash
dotnet build Deblur.sln
dotnet test Deblur.sln
```
Expected: build succeeds with 0 errors, 0 warnings. Test run reports "No test is available" (no test methods yet) — this is a pass.

- [ ] **Step 7: Commit**

```bash
git add Deblur.sln Deblur.Engine/ Deblur.Tests/ Deblur/Deblur.csproj
git commit -m "Bootstrap Deblur.Engine and Deblur.Tests projects"
```

---

## Task 2: `ImageBuffer` core type

**Files:**
- Create: `Deblur.Engine/ImageBuffer.cs`
- Create: `Deblur.Tests/ImageBufferTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `ImageBuffer` — immutable-shaped RGB float image used by every subsequent engine type.

Public shape:
```csharp
public sealed class ImageBuffer
{
    public int Width { get; }
    public int Height { get; }
    public float[] R { get; }    // length = Width * Height
    public float[] G { get; }
    public float[] B { get; }

    public ImageBuffer(int width, int height);
    public ImageBuffer(int width, int height, float[] r, float[] g, float[] b);
    public int PixelCount => Width * Height;
    public ImageBuffer Clone();
}
```

Row-major layout: pixel at `(x, y)` is index `y * Width + x`.

- [ ] **Step 1: Write the failing tests**

Create `Deblur.Tests/ImageBufferTests.cs`:
```csharp
using Deblur.Engine;
using Xunit;

namespace Deblur.Tests;

public class ImageBufferTests
{
    [Fact]
    public void Ctor_Dimensions_AllocatesChannels()
    {
        var buf = new ImageBuffer(4, 3);
        Assert.Equal(4, buf.Width);
        Assert.Equal(3, buf.Height);
        Assert.Equal(12, buf.R.Length);
        Assert.Equal(12, buf.G.Length);
        Assert.Equal(12, buf.B.Length);
        Assert.Equal(12, buf.PixelCount);
    }

    [Fact]
    public void Ctor_RejectsNonPositiveDimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageBuffer(0, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageBuffer(4, -1));
    }

    [Fact]
    public void Ctor_WithChannels_ValidatesLengths()
    {
        var r = new float[12]; var g = new float[12]; var b = new float[11];
        Assert.Throws<ArgumentException>(() => new ImageBuffer(4, 3, r, g, b));
    }

    [Fact]
    public void Clone_ProducesIndependentCopy()
    {
        var buf = new ImageBuffer(2, 2);
        buf.R[0] = 0.5f;
        var copy = buf.Clone();
        copy.R[0] = 0.9f;
        Assert.Equal(0.5f, buf.R[0]);
        Assert.Equal(0.9f, copy.R[0]);
    }
}
```

- [ ] **Step 2: Run tests and verify they fail**

Run:
```bash
dotnet test Deblur.Tests/Deblur.Tests.csproj
```
Expected: compile error — `ImageBuffer` not defined.

- [ ] **Step 3: Write the implementation**

Create `Deblur.Engine/ImageBuffer.cs`:
```csharp
namespace Deblur.Engine;

public sealed class ImageBuffer
{
    public int Width { get; }
    public int Height { get; }
    public float[] R { get; }
    public float[] G { get; }
    public float[] B { get; }

    public int PixelCount => Width * Height;

    public ImageBuffer(int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        Width = width;
        Height = height;
        R = new float[width * height];
        G = new float[width * height];
        B = new float[width * height];
    }

    public ImageBuffer(int width, int height, float[] r, float[] g, float[] b)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        int expected = width * height;
        if (r.Length != expected || g.Length != expected || b.Length != expected)
            throw new ArgumentException("Channel lengths must equal width * height.");
        Width = width;
        Height = height;
        R = r;
        G = g;
        B = b;
    }

    public ImageBuffer Clone()
    {
        return new ImageBuffer(
            Width, Height,
            (float[])R.Clone(),
            (float[])G.Clone(),
            (float[])B.Clone());
    }
}
```

- [ ] **Step 4: Run tests and verify they pass**

Run:
```bash
dotnet test Deblur.Tests/Deblur.Tests.csproj
```
Expected: 4 passing.

- [ ] **Step 5: Commit**

```bash
git add Deblur.Engine/ImageBuffer.cs Deblur.Tests/ImageBufferTests.cs
git commit -m "Add ImageBuffer with construction, validation, clone"
```

---

## Task 3: `BlurType`, `KernelParams`, `IBlurKernel`, `MotionBlurKernel`

**Files:**
- Create: `Deblur.Engine/BlurType.cs`
- Create: `Deblur.Engine/KernelParams.cs`
- Create: `Deblur.Engine/IBlurKernel.cs`
- Create: `Deblur.Engine/MotionBlurKernel.cs`
- Create: `Deblur.Tests/MotionBlurKernelTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces:
  - `enum BlurType { Motion, OutOfFocus, Gaussian }`
  - `record struct KernelParams(BlurType Type, float Angle, float Length, float Smoothness)`
  - `interface IBlurKernel { float[,] Build(KernelParams p); }`
  - `sealed class MotionBlurKernel : IBlurKernel`
  - Kernel: `float[,]` sized `(2⌈length⌉+1) × (2⌈length⌉+1)`, sums to 1.0.

- [ ] **Step 1: Write the failing kernel tests**

Create `Deblur.Tests/MotionBlurKernelTests.cs`:
```csharp
using Deblur.Engine;
using Xunit;

namespace Deblur.Tests;

public class MotionBlurKernelTests
{
    private static float Sum(float[,] k)
    {
        float s = 0;
        for (int y = 0; y < k.GetLength(0); y++)
            for (int x = 0; x < k.GetLength(1); x++)
                s += k[y, x];
        return s;
    }

    [Theory]
    [InlineData(0f, 5f)]
    [InlineData(45f, 10f)]
    [InlineData(90f, 20f)]
    [InlineData(137f, 33f)]
    [InlineData(270f, 50f)]
    public void Kernel_SumsToOne(float angleDeg, float length)
    {
        var k = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, angleDeg, length, 0));
        Assert.InRange(Sum(k), 0.999999f, 1.000001f);
    }

    [Fact]
    public void Length1_ProducesIdentityKernel()
    {
        var k = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 45f, 1f, 0));
        // 3x3 with only center non-zero and equal to 1
        Assert.Equal(3, k.GetLength(0));
        Assert.Equal(3, k.GetLength(1));
        Assert.Equal(1f, k[1, 1], 6);
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 3; x++)
                if (!(x == 1 && y == 1))
                    Assert.Equal(0f, k[y, x], 6);
    }

    [Fact]
    public void AngleFlip_180Degrees_ProducesEquivalentKernel()
    {
        var a = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 15f, 0));
        var b = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f + 180f, 15f, 0));
        Assert.Equal(a.GetLength(0), b.GetLength(0));
        for (int y = 0; y < a.GetLength(0); y++)
            for (int x = 0; x < a.GetLength(1); x++)
                Assert.Equal(a[y, x], b[y, x], 5);
    }

    [Fact]
    public void FortyFiveDegrees_HasNonZeroOffAxisWeights()
    {
        var k = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 45f, 10f, 0));
        // Somewhere in the kernel there must be a pixel that is neither on the
        // horizontal nor vertical axis through center that carries weight;
        // this fails if we fell back to axis-aligned rasterization.
        int c = k.GetLength(0) / 2;
        bool foundOffAxis = false;
        for (int y = 0; y < k.GetLength(0); y++)
            for (int x = 0; x < k.GetLength(1); x++)
                if (x != c && y != c && k[y, x] > 0.001f)
                    foundOffAxis = true;
        Assert.True(foundOffAxis);
    }
}
```

- [ ] **Step 2: Run tests and verify they fail**

```bash
dotnet test Deblur.Tests/Deblur.Tests.csproj --filter FullyQualifiedName~MotionBlurKernelTests
```
Expected: compile errors — types not defined.

- [ ] **Step 3: Add supporting types**

Create `Deblur.Engine/BlurType.cs`:
```csharp
namespace Deblur.Engine;

public enum BlurType
{
    Motion,
    OutOfFocus,
    Gaussian,
}
```

Create `Deblur.Engine/KernelParams.cs`:
```csharp
namespace Deblur.Engine;

public readonly record struct KernelParams(
    BlurType Type,
    float Angle,
    float Length,
    float Smoothness);
```

Create `Deblur.Engine/IBlurKernel.cs`:
```csharp
namespace Deblur.Engine;

public interface IBlurKernel
{
    float[,] Build(KernelParams p);
}
```

- [ ] **Step 4: Implement `MotionBlurKernel`**

Create `Deblur.Engine/MotionBlurKernel.cs`:
```csharp
namespace Deblur.Engine;

public sealed class MotionBlurKernel : IBlurKernel
{
    public float[,] Build(KernelParams p)
    {
        if (p.Length < 1f) throw new ArgumentOutOfRangeException(nameof(p.Length));

        int r = (int)Math.Ceiling(p.Length);
        int size = 2 * r + 1;
        var k = new float[size, size];

        if (p.Length <= 1f)
        {
            k[r, r] = 1f;
            return k;
        }

        // Line segment from -halfLen*dir to +halfLen*dir, through the kernel center.
        double halfLen = p.Length / 2.0;
        double rad = p.Angle * Math.PI / 180.0;
        double dx = Math.Cos(rad);
        double dy = Math.Sin(rad);
        double ax = -halfLen * dx, ay = -halfLen * dy;
        double bx = +halfLen * dx, by = +halfLen * dy;

        float total = 0f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                // Sample point in kernel-centered coords.
                double sx = x - r;
                double sy = y - r;
                double dist = PointToSegmentDistance(sx, sy, ax, ay, bx, by);
                float w = (float)Math.Max(0.0, 1.0 - dist);
                k[y, x] = w;
                total += w;
            }
        }

        // Normalize to sum = 1.
        if (total > 0f)
        {
            float inv = 1f / total;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    k[y, x] *= inv;
        }
        return k;
    }

    private static double PointToSegmentDistance(
        double px, double py, double ax, double ay, double bx, double by)
    {
        double vx = bx - ax, vy = by - ay;
        double wx = px - ax, wy = py - ay;
        double c1 = vx * wx + vy * wy;
        double c2 = vx * vx + vy * vy;
        double t = c2 > 0 ? Math.Clamp(c1 / c2, 0.0, 1.0) : 0.0;
        double cx = ax + t * vx;
        double cy = ay + t * vy;
        return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }
}
```

- [ ] **Step 5: Run tests and verify they pass**

```bash
dotnet test Deblur.Tests/Deblur.Tests.csproj --filter FullyQualifiedName~MotionBlurKernelTests
```
Expected: 8 passing (5 Theory cases + 3 Facts).

- [ ] **Step 6: Commit**

```bash
git add Deblur.Engine/BlurType.cs Deblur.Engine/KernelParams.cs Deblur.Engine/IBlurKernel.cs Deblur.Engine/MotionBlurKernel.cs Deblur.Tests/MotionBlurKernelTests.cs
git commit -m "Add MotionBlurKernel with anti-aliased line PSF"
```

---

## Task 4: `FftAdapter` wrapping FftSharp

**Files:**
- Create: `Deblur.Engine/FftAdapter.cs`
- Create: `Deblur.Tests/FftAdapterTests.cs`

**Interfaces:**
- Consumes: `FftSharp.FFT` (external NuGet).
- Produces:
```csharp
public static class FftAdapter
{
    public static int NextPow2(int n);
    public static System.Numerics.Complex[,] Forward2D(float[,] real);
    public static float[,] Inverse2DReal(System.Numerics.Complex[,] freq);
}
```
- `Forward2D` accepts arbitrary-size 2D real input; caller has already zero-padded to a power-of-two-per-side buffer.
- `Inverse2DReal` returns the real component of the IFFT, same size as input.

- [ ] **Step 1: Write the failing round-trip test**

Create `Deblur.Tests/FftAdapterTests.cs`:
```csharp
using System.Numerics;
using Deblur.Engine;
using Xunit;

namespace Deblur.Tests;

public class FftAdapterTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(129)]
    public void NextPow2_RoundsUp(int input)
    {
        int result = FftAdapter.NextPow2(input);
        Assert.True(result >= input);
        // result must be a power of two
        Assert.True((result & (result - 1)) == 0);
        // and result / 2 must be less than input (i.e. it's the *next* one)
        Assert.True(result / 2 < input);
    }

    [Fact]
    public void RoundTrip_RecoversOriginalWithinTolerance()
    {
        // 8x8 input with a couple of arbitrary non-zero values.
        var input = new float[8, 8];
        input[3, 4] = 1.0f;
        input[5, 1] = 0.5f;
        input[0, 0] = -0.25f;

        var freq = FftAdapter.Forward2D(input);
        var recovered = FftAdapter.Inverse2DReal(freq);

        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                Assert.Equal(input[y, x], recovered[y, x], 4);
    }
}
```

- [ ] **Step 2: Run tests and verify they fail**

```bash
dotnet test Deblur.Tests/Deblur.Tests.csproj --filter FullyQualifiedName~FftAdapterTests
```
Expected: compile errors — `FftAdapter` not defined.

- [ ] **Step 3: Implement `FftAdapter`**

Create `Deblur.Engine/FftAdapter.cs`:
```csharp
using System.Numerics;

namespace Deblur.Engine;

public static class FftAdapter
{
    public static int NextPow2(int n)
    {
        if (n <= 1) return 1;
        int p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    public static Complex[,] Forward2D(float[,] real)
    {
        int h = real.GetLength(0);
        int w = real.GetLength(1);

        // Row-wise FFT.
        var rows = new Complex[h][];
        for (int y = 0; y < h; y++)
        {
            var row = new Complex[w];
            for (int x = 0; x < w; x++) row[x] = new Complex(real[y, x], 0);
            FftSharp.FFT.Forward(row);
            rows[y] = row;
        }

        // Column-wise FFT.
        var result = new Complex[h, w];
        var col = new Complex[h];
        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++) col[y] = rows[y][x];
            FftSharp.FFT.Forward(col);
            for (int y = 0; y < h; y++) result[y, x] = col[y];
        }
        return result;
    }

    public static float[,] Inverse2DReal(Complex[,] freq)
    {
        int h = freq.GetLength(0);
        int w = freq.GetLength(1);

        // Column-wise inverse.
        var cols = new Complex[w][];
        for (int x = 0; x < w; x++)
        {
            var col = new Complex[h];
            for (int y = 0; y < h; y++) col[y] = freq[y, x];
            FftSharp.FFT.Inverse(col);
            cols[x] = col;
        }

        // Row-wise inverse.
        var result = new float[h, w];
        var row = new Complex[w];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++) row[x] = cols[x][y];
            FftSharp.FFT.Inverse(row);
            for (int x = 0; x < w; x++) result[y, x] = (float)row[x].Real;
        }
        return result;
    }
}
```

- [ ] **Step 4: Run tests and verify they pass**

```bash
dotnet test Deblur.Tests/Deblur.Tests.csproj --filter FullyQualifiedName~FftAdapterTests
```
Expected: 6 Theory cases + 1 Fact = 7 passing.

- [ ] **Step 5: Commit**

```bash
git add Deblur.Engine/FftAdapter.cs Deblur.Tests/FftAdapterTests.cs
git commit -m "Add FftAdapter with NextPow2 and 2D forward/inverse FFT"
```

---

## Task 5: `DeconvolutionParams`, `IDeconvolver`, and `WienerDeconvolver`

**Files:**
- Create: `Deblur.Engine/DeconvolutionParams.cs`
- Create: `Deblur.Engine/IDeconvolver.cs`
- Create: `Deblur.Engine/WienerDeconvolver.cs`
- Create: `Deblur.Tests/TestHelpers/SyntheticImages.cs`
- Create: `Deblur.Tests/WienerDeconvolverTests.cs`

**Interfaces:**
- Consumes: `ImageBuffer`, `FftAdapter`, `IBlurKernel` output.
- Produces:
```csharp
public readonly record struct DeconvolutionParams(float K);
public interface IDeconvolver
{
    ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p);
}
public sealed class WienerDeconvolver : IDeconvolver { /* ... */ }
```
- Also produces test helpers:
```csharp
public static class SyntheticImages
{
    public static ImageBuffer Checkerboard(int width, int height, int cellSize);
    public static ImageBuffer AddGaussianNoise(ImageBuffer input, float sigma, int seed);
    public static ImageBuffer Convolve(ImageBuffer input, float[,] kernel);
    public static float Psnr(ImageBuffer a, ImageBuffer b);
}
```

- [ ] **Step 1: Write test helpers**

Create `Deblur.Tests/TestHelpers/SyntheticImages.cs`:
```csharp
using Deblur.Engine;

namespace Deblur.Tests.TestHelpers;

public static class SyntheticImages
{
    public static ImageBuffer Checkerboard(int width, int height, int cellSize)
    {
        var buf = new ImageBuffer(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool on = ((x / cellSize) + (y / cellSize)) % 2 == 0;
                float v = on ? 0.9f : 0.1f;
                int i = y * width + x;
                buf.R[i] = v; buf.G[i] = v; buf.B[i] = v;
            }
        }
        return buf;
    }

    public static ImageBuffer AddGaussianNoise(ImageBuffer input, float sigma, int seed)
    {
        var rng = new Random(seed);
        var copy = input.Clone();
        for (int i = 0; i < copy.PixelCount; i++)
        {
            copy.R[i] = Math.Clamp(copy.R[i] + (float)NextGaussian(rng, sigma), 0f, 1f);
            copy.G[i] = Math.Clamp(copy.G[i] + (float)NextGaussian(rng, sigma), 0f, 1f);
            copy.B[i] = Math.Clamp(copy.B[i] + (float)NextGaussian(rng, sigma), 0f, 1f);
        }
        return copy;
    }

    private static double NextGaussian(Random rng, float sigma)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return sigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    public static ImageBuffer Convolve(ImageBuffer input, float[,] kernel)
    {
        int kh = kernel.GetLength(0), kw = kernel.GetLength(1);
        int kry = kh / 2, krx = kw / 2;
        var outBuf = new ImageBuffer(input.Width, input.Height);
        for (int y = 0; y < input.Height; y++)
        {
            for (int x = 0; x < input.Width; x++)
            {
                float sr = 0, sg = 0, sb = 0;
                for (int ky = 0; ky < kh; ky++)
                {
                    int sy = Math.Clamp(y + ky - kry, 0, input.Height - 1);
                    for (int kx = 0; kx < kw; kx++)
                    {
                        int sx = Math.Clamp(x + kx - krx, 0, input.Width - 1);
                        float w = kernel[ky, kx];
                        int si = sy * input.Width + sx;
                        sr += input.R[si] * w;
                        sg += input.G[si] * w;
                        sb += input.B[si] * w;
                    }
                }
                int oi = y * input.Width + x;
                outBuf.R[oi] = sr; outBuf.G[oi] = sg; outBuf.B[oi] = sb;
            }
        }
        return outBuf;
    }

    public static float Psnr(ImageBuffer a, ImageBuffer b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
            throw new ArgumentException("size mismatch");
        double mse = 0;
        long n = a.PixelCount * 3L;
        for (int i = 0; i < a.PixelCount; i++)
        {
            double dr = a.R[i] - b.R[i];
            double dg = a.G[i] - b.G[i];
            double db = a.B[i] - b.B[i];
            mse += dr * dr + dg * dg + db * db;
        }
        mse /= n;
        if (mse <= 1e-12) return 200f;
        return (float)(10.0 * Math.Log10(1.0 / mse));
    }
}
```

- [ ] **Step 2: Write the failing Wiener tests**

Create `Deblur.Tests/WienerDeconvolverTests.cs`:
```csharp
using Deblur.Engine;
using Deblur.Tests.TestHelpers;
using Xunit;

namespace Deblur.Tests;

public class WienerDeconvolverTests
{
    [Fact]
    public void RoundTrip_RecoversCheckerboard_AbovePsnrThreshold()
    {
        var original = SyntheticImages.Checkerboard(128, 128, 8);
        var psf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 12f, 0));
        var blurred = SyntheticImages.Convolve(original, psf);
        var noisy = SyntheticImages.AddGaussianNoise(blurred, 0.005f, seed: 42);

        var deconv = new WienerDeconvolver().Apply(
            noisy, psf, new DeconvolutionParams(K: 0.005f));

        Assert.True(SyntheticImages.Psnr(original, deconv) > 25f);
    }

    [Fact]
    public void WrongAngle_WorsePsnrThanBlurredInput()
    {
        var original = SyntheticImages.Checkerboard(128, 128, 8);
        var truePsf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 12f, 0));
        var wrongPsf = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 90f, 12f, 0));
        var blurred = SyntheticImages.Convolve(original, truePsf);

        var deconv = new WienerDeconvolver().Apply(
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
            new KernelParams(BlurType.Motion, 0f, 8f, 0));
        var blurred = SyntheticImages.Convolve(original, psf);
        var deconv = new WienerDeconvolver().Apply(
            blurred, psf, new DeconvolutionParams(K: 0.005f));

        // Sample the top border strip; variance must be finite and modest.
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
            new KernelParams(BlurType.Motion, 22f, 100f, 0));
        var deconv = new WienerDeconvolver().Apply(
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

- [ ] **Step 3: Run tests and verify they fail**

```bash
dotnet test Deblur.Tests/Deblur.Tests.csproj --filter FullyQualifiedName~WienerDeconvolverTests
```
Expected: compile errors — `WienerDeconvolver`, `DeconvolutionParams` not defined.

- [ ] **Step 4: Add supporting types**

Create `Deblur.Engine/DeconvolutionParams.cs`:
```csharp
namespace Deblur.Engine;

public readonly record struct DeconvolutionParams(float K);
```

Create `Deblur.Engine/IDeconvolver.cs`:
```csharp
namespace Deblur.Engine;

public interface IDeconvolver
{
    ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p);
}
```

- [ ] **Step 5: Implement `WienerDeconvolver`**

Create `Deblur.Engine/WienerDeconvolver.cs`:
```csharp
using System.Numerics;

namespace Deblur.Engine;

public sealed class WienerDeconvolver : IDeconvolver
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

        // Precompute Wiener denominator |H|^2 + K.
        var wienerNumer = new Complex[fftSize, fftSize];
        for (int y = 0; y < fftSize; y++)
        {
            for (int x = 0; x < fftSize; x++)
            {
                var h = H[y, x];
                double mag2 = h.Real * h.Real + h.Imaginary * h.Imaginary;
                wienerNumer[y, x] = Complex.Conjugate(h) / (mag2 + p.K);
            }
        }

        float[] outR = ProcessChannel(input.R, input.Width, input.Height, pad, fftSize, wienerNumer);
        float[] outG = ProcessChannel(input.G, input.Width, input.Height, pad, fftSize, wienerNumer);
        float[] outB = ProcessChannel(input.B, input.Width, input.Height, pad, fftSize, wienerNumer);
        return new ImageBuffer(input.Width, input.Height, outR, outG, outB);
    }

    private static float[] ProcessChannel(
        float[] channel, int w, int h, int pad, int fftSize, Complex[,] wienerNumer)
    {
        // Reflect-pad into fftSize x fftSize float buffer; zero-fill outside padded region.
        var padded = new float[fftSize, fftSize];
        for (int y = 0; y < h + 2 * pad; y++)
        {
            int sy = ReflectIndex(y - pad, h);
            for (int x = 0; x < w + 2 * pad; x++)
            {
                int sx = ReflectIndex(x - pad, w);
                padded[y, x] = channel[sy * w + sx];
            }
        }

        var G = FftAdapter.Forward2D(padded);
        var Fhat = new Complex[fftSize, fftSize];
        for (int y = 0; y < fftSize; y++)
            for (int x = 0; x < fftSize; x++)
                Fhat[y, x] = wienerNumer[y, x] * G[y, x];

        var real = FftAdapter.Inverse2DReal(Fhat);

        // Crop back to original dims from the padded region.
        // Guard against NaN/Inf: Math.Clamp(NaN, 0, 1) returns NaN because
        // both comparisons involving NaN are false, so filter explicitly.
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
        // Bounce back and forth off the edges.
        int period = 2 * (len - 1);
        int m = ((i % period) + period) % period;
        return m < len ? m : period - m;
    }
}
```

- [ ] **Step 6: Run tests and verify they pass**

```bash
dotnet test Deblur.Tests/Deblur.Tests.csproj --filter FullyQualifiedName~WienerDeconvolverTests
```
Expected: 4 passing.

- [ ] **Step 7: Commit**

```bash
git add Deblur.Engine/DeconvolutionParams.cs Deblur.Engine/IDeconvolver.cs Deblur.Engine/WienerDeconvolver.cs Deblur.Tests/TestHelpers/SyntheticImages.cs Deblur.Tests/WienerDeconvolverTests.cs
git commit -m "Add WienerDeconvolver with reflect-padding and border-safe output"
```

---

## Task 6: `ImageCodec` and `InvalidImageFormatException`

**Files:**
- Create: `Deblur.Engine/InvalidImageFormatException.cs`
- Create: `Deblur.Engine/ImageCodec.cs`
- Create: `Deblur.Tests/ImageCodecTests.cs`
- Modify: `Deblur.Engine/Deblur.Engine.csproj` — add `System.Drawing.Common` package.

**Interfaces:**
- Consumes: `System.Drawing` (WIC-backed on Windows).
- Produces:
```csharp
public sealed class InvalidImageFormatException : Exception { /* ... */ }

public static class ImageCodec
{
    public static ImageBuffer DecodeFromBytes(byte[] bytes);
    public static byte[] EncodePng(ImageBuffer image);
    public static byte[] EncodeJpeg(ImageBuffer image, int quality);   // default caller = 92
}
```

Note: `System.Drawing.Common` on non-Windows targets emits warnings, but our engine targets `net8.0` and only runs alongside a `net8.0-windows` app. This is acceptable.

- [ ] **Step 1: Add the `System.Drawing.Common` dependency**

```bash
dotnet add Deblur.Engine package System.Drawing.Common
```

- [ ] **Step 2: Write the failing codec tests**

Create `Deblur.Tests/ImageCodecTests.cs`:
```csharp
using Deblur.Engine;
using Deblur.Tests.TestHelpers;
using Xunit;

namespace Deblur.Tests;

public class ImageCodecTests
{
    [Fact]
    public void PngRoundTrip_IsLossless()
    {
        var original = SyntheticImages.Checkerboard(32, 32, 4);
        byte[] png = ImageCodec.EncodePng(original);
        var decoded = ImageCodec.DecodeFromBytes(png);
        Assert.Equal(original.Width, decoded.Width);
        Assert.Equal(original.Height, decoded.Height);
        // 8-bit round-trip: allow 1/255 tolerance.
        for (int i = 0; i < original.PixelCount; i++)
        {
            Assert.InRange(decoded.R[i], original.R[i] - 1f / 255f, original.R[i] + 1f / 255f);
            Assert.InRange(decoded.G[i], original.G[i] - 1f / 255f, original.G[i] + 1f / 255f);
            Assert.InRange(decoded.B[i], original.B[i] - 1f / 255f, original.B[i] + 1f / 255f);
        }
    }

    [Fact]
    public void JpegRoundTrip_Quality92_HighFidelity()
    {
        var original = SyntheticImages.Checkerboard(64, 64, 8);
        byte[] jpeg = ImageCodec.EncodeJpeg(original, quality: 92);
        var decoded = ImageCodec.DecodeFromBytes(jpeg);
        Assert.True(SyntheticImages.Psnr(original, decoded) > 40f);
    }

    [Fact]
    public void GarbageInput_ThrowsInvalidImageFormat()
    {
        var garbage = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44 };
        Assert.Throws<InvalidImageFormatException>(() => ImageCodec.DecodeFromBytes(garbage));
    }
}
```

- [ ] **Step 3: Run tests and verify they fail**

```bash
dotnet test Deblur.Tests/Deblur.Tests.csproj --filter FullyQualifiedName~ImageCodecTests
```
Expected: compile errors.

- [ ] **Step 4: Implement `InvalidImageFormatException`**

Create `Deblur.Engine/InvalidImageFormatException.cs`:
```csharp
namespace Deblur.Engine;

public sealed class InvalidImageFormatException : Exception
{
    public InvalidImageFormatException(string message) : base(message) { }
    public InvalidImageFormatException(string message, Exception inner) : base(message, inner) { }
}
```

- [ ] **Step 5: Implement `ImageCodec`**

Create `Deblur.Engine/ImageCodec.cs`:
```csharp
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace Deblur.Engine;

[SupportedOSPlatform("windows")]
public static class ImageCodec
{
    public static ImageBuffer DecodeFromBytes(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        Bitmap bmp;
        try
        {
            bmp = new Bitmap(ms);
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException)
        {
            throw new InvalidImageFormatException("Image bytes could not be decoded.", ex);
        }

        using (bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            var buf = new ImageBuffer(w, h);
            var rect = new Rectangle(0, 0, w, h);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                var scan = new byte[stride * h];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, scan, 0, scan.Length);
                for (int y = 0; y < h; y++)
                {
                    int rowBase = y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        int p = rowBase + x * 4;
                        // BGRA order in memory.
                        buf.B[y * w + x] = scan[p] / 255f;
                        buf.G[y * w + x] = scan[p + 1] / 255f;
                        buf.R[y * w + x] = scan[p + 2] / 255f;
                    }
                }
            }
            finally { bmp.UnlockBits(data); }
            return buf;
        }
    }

    public static byte[] EncodePng(ImageBuffer image)
        => EncodeInternal(image, ImageFormat.Png, quality: null);

    public static byte[] EncodeJpeg(ImageBuffer image, int quality)
    {
        if (quality < 1 || quality > 100)
            throw new ArgumentOutOfRangeException(nameof(quality));
        return EncodeInternal(image, ImageFormat.Jpeg, quality);
    }

    private static byte[] EncodeInternal(ImageBuffer image, ImageFormat format, int? quality)
    {
        using var bmp = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, image.Width, image.Height);
        var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            var scan = new byte[stride * image.Height];
            for (int y = 0; y < image.Height; y++)
            {
                int rowBase = y * stride;
                for (int x = 0; x < image.Width; x++)
                {
                    int p = rowBase + x * 4;
                    int idx = y * image.Width + x;
                    scan[p] = Clamp8(image.B[idx]);
                    scan[p + 1] = Clamp8(image.G[idx]);
                    scan[p + 2] = Clamp8(image.R[idx]);
                    scan[p + 3] = 255;
                }
            }
            System.Runtime.InteropServices.Marshal.Copy(scan, 0, data.Scan0, scan.Length);
        }
        finally { bmp.UnlockBits(data); }

        using var ms = new MemoryStream();
        if (quality is int q && format.Guid == ImageFormat.Jpeg.Guid)
        {
            var codec = GetEncoder(ImageFormat.Jpeg);
            var eps = new EncoderParameters(1);
            eps.Param[0] = new EncoderParameter(Encoder.Quality, (long)q);
            bmp.Save(ms, codec, eps);
        }
        else
        {
            bmp.Save(ms, format);
        }
        return ms.ToArray();
    }

    private static byte Clamp8(float v)
    {
        int i = (int)MathF.Round(v * 255f);
        return (byte)Math.Clamp(i, 0, 255);
    }

    private static ImageCodecInfo GetEncoder(ImageFormat format)
    {
        foreach (var c in ImageCodecInfo.GetImageEncoders())
            if (c.FormatID == format.Guid) return c;
        throw new InvalidOperationException($"No encoder for {format}.");
    }
}
```

- [ ] **Step 6: Run tests and verify they pass**

```bash
dotnet test Deblur.Tests/Deblur.Tests.csproj --filter FullyQualifiedName~ImageCodecTests
```
Expected: 3 passing.

If `System.Drawing.Common` complains about non-Windows, add `<TargetFrameworks>net8.0-windows</TargetFrameworks>` to `Deblur.Tests.csproj` and re-run. (Keep `Deblur.Engine` at `net8.0` — the `[SupportedOSPlatform]` attribute keeps the warning localized.)

- [ ] **Step 7: Commit**

```bash
git add Deblur.Engine/InvalidImageFormatException.cs Deblur.Engine/ImageCodec.cs Deblur.Engine/Deblur.Engine.csproj Deblur.Tests/ImageCodecTests.cs Deblur.Tests/Deblur.Tests.csproj
git commit -m "Add ImageCodec (PNG/JPEG round-trip via System.Drawing)"
```

---

## Task 7: `DeblurJobRunner` with coalescing and full-res render

**Files:**
- Create: `Deblur.Engine/DeblurJobRunner.cs`
- Create: `Deblur.Tests/DeblurJobRunnerTests.cs`

**Interfaces:**
- Consumes: `ImageBuffer`, `IBlurKernel`, `IDeconvolver`, `KernelParams`.
- Produces:
```csharp
public sealed class ProxyReadyEventArgs : EventArgs
{
    public byte[] Bgra { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}

public sealed class DeblurJobRunner : IDisposable
{
    public DeblurJobRunner(IBlurKernel kernel, IDeconvolver deconvolver);
    public void SetProxy(ImageBuffer proxy);      // called on file load
    public void Request(KernelParams p);           // fire-and-forget; coalesces
    public Task<ImageBuffer> RenderFullAsync(
        ImageBuffer fullRes, KernelParams p, float proxyScale, IProgress<double>? progress = null);
    public event EventHandler<ProxyReadyEventArgs>? ProxyReady;
    public void Dispose();
}
```
- `Request` may be called from any thread; `ProxyReady` fires on the same worker thread — subscribers marshal to UI themselves.
- Full-res: caller passes `proxyScale = proxyWidth / fullResWidth`; runner scales `p.Length` by `1 / proxyScale` before invoking the kernel.
- Emits BGRA byte arrays (WPF `WriteableBitmap` friendly).

- [ ] **Step 1: Write the failing coalescing test**

Create `Deblur.Tests/DeblurJobRunnerTests.cs`:
```csharp
using System.Collections.Concurrent;
using Deblur.Engine;
using Deblur.Tests.TestHelpers;
using Xunit;

namespace Deblur.Tests;

public class DeblurJobRunnerTests
{
    private sealed class SlowStubDeconvolver : IDeconvolver
    {
        public int CallCount;
        public readonly ConcurrentBag<float> ObservedAngles = new();
        public int SleepMs { get; init; } = 10;

        public ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p)
        {
            Interlocked.Increment(ref CallCount);
            Thread.Sleep(SleepMs);
            return input.Clone();
        }
    }

    private sealed class RecordingStubKernel : IBlurKernel
    {
        public readonly ConcurrentBag<KernelParams> Seen = new();
        public float[,] Build(KernelParams p) { Seen.Add(p); return new float[1, 1] { { 1f } }; }
    }

    [Fact]
    public void Rapid_Requests_Coalesce_And_LastParamsWin()
    {
        var kernel = new RecordingStubKernel();
        var deconv = new SlowStubDeconvolver { SleepMs = 15 };
        using var runner = new DeblurJobRunner(kernel, deconv);
        runner.SetProxy(SyntheticImages.Checkerboard(32, 32, 4));

        int received = 0;
        var lastEvent = new ManualResetEventSlim();
        runner.ProxyReady += (_, __) =>
        {
            Interlocked.Increment(ref received);
            lastEvent.Set();
        };

        for (int i = 0; i < 100; i++)
            runner.Request(new KernelParams(BlurType.Motion, angle: i, length: 5f, smoothness: 0.005f));

        // Wait for the last coalesced job to complete.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
            if (deconv.CallCount > 0 && !runner.HasPending) break;
        }

        Assert.True(deconv.CallCount < 100, $"expected coalescing; ran {deconv.CallCount} jobs");
        Assert.True(deconv.CallCount >= 1);
        // Latest param (angle 99) must appear in the observed kernel calls.
        Assert.Contains(kernel.Seen, p => (int)p.Angle == 99);
    }

    [Fact]
    public async Task RenderFullAsync_ScalesKernelLengthByInverseProxyScale()
    {
        var kernel = new RecordingStubKernel();
        var deconv = new SlowStubDeconvolver { SleepMs = 0 };
        using var runner = new DeblurJobRunner(kernel, deconv);

        var full = SyntheticImages.Checkerboard(200, 200, 10);
        // proxyScale = proxyW / fullW = 50 / 200 = 0.25 → length multiplier = 4x
        await runner.RenderFullAsync(full,
            new KernelParams(BlurType.Motion, 45f, 10f, 0.005f), proxyScale: 0.25f);

        Assert.Contains(kernel.Seen, p => Math.Abs(p.Length - 40f) < 0.001f);
    }
}
```

Note: `HasPending` is a property we'll expose on `DeblurJobRunner` for tests (a boolean whether there's an unprocessed request in the coalesce slot).

- [ ] **Step 2: Run tests and verify they fail**

```bash
dotnet test Deblur.Tests/Deblur.Tests.csproj --filter FullyQualifiedName~DeblurJobRunnerTests
```
Expected: compile errors — `DeblurJobRunner` not defined.

- [ ] **Step 3: Implement `DeblurJobRunner`**

Create `Deblur.Engine/DeblurJobRunner.cs`:
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
    private readonly IBlurKernel _kernel;
    private readonly IDeconvolver _deconvolver;
    private readonly Thread _worker;
    private readonly ManualResetEventSlim _signal = new(false);
    private readonly object _lock = new();

    private ImageBuffer? _proxy;
    private KernelParams? _pending;
    private volatile bool _running = true;

    public event EventHandler<ProxyReadyEventArgs>? ProxyReady;

    public bool HasPending
    {
        get { lock (_lock) return _pending.HasValue; }
    }

    public DeblurJobRunner(IBlurKernel kernel, IDeconvolver deconvolver)
    {
        _kernel = kernel;
        _deconvolver = deconvolver;
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "DeblurWorker" };
        _worker.Start();
    }

    public void SetProxy(ImageBuffer proxy)
    {
        lock (_lock) _proxy = proxy;
    }

    public void Request(KernelParams p)
    {
        lock (_lock) _pending = p;   // overwrite: only latest matters
        _signal.Set();
    }

    public Task<ImageBuffer> RenderFullAsync(
        ImageBuffer fullRes, KernelParams p, float proxyScale, IProgress<double>? progress = null)
    {
        return Task.Run(() =>
        {
            progress?.Report(0.1);
            var scaledParams = p with { Length = p.Length / Math.Max(proxyScale, 1e-6f) };
            var psf = _kernel.Build(scaledParams);
            progress?.Report(0.3);
            var result = _deconvolver.Apply(fullRes, psf, new DeconvolutionParams(K: p.Smoothness));
            progress?.Report(1.0);
            return result;
        });
    }

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
                    if (_pending is null || _proxy is null) break;
                    p = _pending.Value;
                    proxy = _proxy;
                    _pending = null;
                }

                var psf = _kernel.Build(p);
                var deconv = _deconvolver.Apply(
                    proxy, psf, new DeconvolutionParams(K: p.Smoothness));

                // Convert to BGRA.
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

- [ ] **Step 4: Run tests and verify they pass**

```bash
dotnet test Deblur.Tests/Deblur.Tests.csproj --filter FullyQualifiedName~DeblurJobRunnerTests
```
Expected: 2 passing.

- [ ] **Step 5: Run the full test suite to make sure nothing regressed**

```bash
dotnet test Deblur.sln
```
Expected: all engine tests pass (a total of ~22 tests across the six test files).

- [ ] **Step 6: Commit**

```bash
git add Deblur.Engine/DeblurJobRunner.cs Deblur.Tests/DeblurJobRunnerTests.cs
git commit -m "Add DeblurJobRunner with coalescing worker and full-res render"
```

---

## Task 8: WPF — `ImageBufferInterop`, `MainViewModel`, blur-type dropdown, sliders

**Files:**
- Create: `Deblur/Services/ImageBufferInterop.cs`
- Create: `Deblur/ViewModels/MainViewModel.cs`

**Interfaces:**
- Consumes: `ImageBuffer`, `KernelParams`, `BlurType`, `DeblurJobRunner`, `MotionBlurKernel`, `WienerDeconvolver` from `Deblur.Engine`. `CommunityToolkit.Mvvm.ComponentModel.ObservableObject`.
- Produces:
```csharp
public static class ImageBufferInterop
{
    public static void ApplyBgraToWriteableBitmap(byte[] bgra, int w, int h, WriteableBitmap target);
    public static WriteableBitmap NewCompatibleBitmap(int w, int h);
}

public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel();
    // Observables
    public partial BlurType SelectedBlurType { get; set; }   // dropdown
    public partial float Angle { get; set; }
    public partial float Length { get; set; }
    public partial float Smoothness { get; set; }
    public partial string? CurrentFilePath { get; set; }
    public partial bool IsBusy { get; set; }
    public partial string? StatusMessage { get; set; }
    // Public read-only
    public bool IsMotionSelected => SelectedBlurType == BlurType.Motion;
    public bool IsComingSoon => !IsMotionSelected;
    public WriteableBitmap? PreviewBitmap { get; }
    // Commands / API used by MainWindow code-behind:
    public void LoadImageFromBytes(byte[] bytes);
    public void UpdateKernel(float angle, float length);       // called on drag or slider change
    public void Reset();
    public Task EnsureFullResRenderedAsync(IProgress<double> progress);   // populates cache
    public Task<byte[]> RenderFullAsPngAsync(IProgress<double> progress); // ensures cache, encodes PNG
    public Task<byte[]> RenderFullAsJpegAsync(int quality, IProgress<double> progress);
}
```

- [ ] **Step 1: Implement `ImageBufferInterop`**

Create `Deblur/Services/ImageBufferInterop.cs`:
```csharp
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Deblur.Services;

public static class ImageBufferInterop
{
    public static WriteableBitmap NewCompatibleBitmap(int width, int height)
        => new(width, height, 96, 96, PixelFormats.Bgra32, null);

    public static void ApplyBgraToWriteableBitmap(byte[] bgra, int w, int h, WriteableBitmap target)
    {
        if (target.PixelWidth != w || target.PixelHeight != h)
            throw new ArgumentException("target dimensions do not match source.");
        var rect = new Int32Rect(0, 0, w, h);
        target.WritePixels(rect, bgra, w * 4, 0);
    }
}
```

- [ ] **Step 2: Implement `MainViewModel`**

Create `Deblur/ViewModels/MainViewModel.cs`:
```csharp
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Deblur.Engine;
using Deblur.Services;

namespace Deblur.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DeblurJobRunner _runner;
    private ImageBuffer? _originalFullRes;
    private ImageBuffer? _proxy;
    private float _proxyScale = 1f;

    [ObservableProperty] private BlurType _selectedBlurType = BlurType.Motion;
    [ObservableProperty] private float _angle;
    [ObservableProperty] private float _length = 10f;
    [ObservableProperty] private float _smoothness = 0.005f;
    [ObservableProperty] private string? _currentFilePath;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private WriteableBitmap? _previewBitmap;

    public bool IsMotionSelected => SelectedBlurType == BlurType.Motion;
    public bool IsComingSoon => !IsMotionSelected;

    public MainViewModel()
    {
        _dispatcher = Application.Current.Dispatcher;
        _runner = new DeblurJobRunner(new MotionBlurKernel(), new WienerDeconvolver());
        _runner.ProxyReady += OnProxyReady;
    }

    partial void OnSelectedBlurTypeChanged(BlurType value)
    {
        OnPropertyChanged(nameof(IsMotionSelected));
        OnPropertyChanged(nameof(IsComingSoon));
    }

    public void LoadImageFromBytes(byte[] bytes)
    {
        var full = ImageCodec.DecodeFromBytes(bytes);
        _originalFullRes = full;
        // Compute proxy: target <= 1.5 MP, aspect-preserving.
        const int maxProxyPixels = 1_500_000;
        double scale = 1.0;
        int px = full.Width * full.Height;
        if (px > maxProxyPixels) scale = Math.Sqrt((double)maxProxyPixels / px);
        int pw = Math.Max(1, (int)Math.Round(full.Width * scale));
        int ph = Math.Max(1, (int)Math.Round(full.Height * scale));
        _proxy = Downscale(full, pw, ph);
        _proxyScale = (float)pw / full.Width;

        PreviewBitmap = ImageBufferInterop.NewCompatibleBitmap(pw, ph);
        _runner.SetProxy(_proxy);
        PushCurrentParams();
    }

    public void UpdateKernel(float angle, float length)
    {
        Angle = angle;
        Length = length;
        PushCurrentParams();
    }

    partial void OnSmoothnessChanged(float value) { InvalidateFullResCache(); PushCurrentParams(); }
    partial void OnAngleChanged(float value)      { InvalidateFullResCache(); PushCurrentParams(); }
    partial void OnLengthChanged(float value)     { InvalidateFullResCache(); PushCurrentParams(); }

    public void Reset()
    {
        Angle = 0f;
        Length = 10f;
        Smoothness = 0.005f;
        PushCurrentParams();
    }

    // Cached full-resolution render; invalidated on any param change.
    private ImageBuffer? _fullResBuffer;
    private KernelParams? _fullResParams;

    public async Task EnsureFullResRenderedAsync(IProgress<double> progress)
    {
        if (_originalFullRes is null) throw new InvalidOperationException("No image loaded.");
        var current = new KernelParams(BlurType.Motion, Angle, Length, Smoothness);
        if (_fullResBuffer is not null && _fullResParams.Equals(current))
        {
            progress.Report(1.0);
            return;
        }
        _fullResBuffer = await _runner.RenderFullAsync(_originalFullRes, current, _proxyScale, progress);
        _fullResParams = current;
    }

    public async Task<byte[]> RenderFullAsPngAsync(IProgress<double> progress)
    {
        await EnsureFullResRenderedAsync(progress);
        return ImageCodec.EncodePng(_fullResBuffer!);
    }

    public async Task<byte[]> RenderFullAsJpegAsync(int quality, IProgress<double> progress)
    {
        await EnsureFullResRenderedAsync(progress);
        return ImageCodec.EncodeJpeg(_fullResBuffer!, quality);
    }

    private void InvalidateFullResCache() => _fullResBuffer = null;

    private void PushCurrentParams()
    {
        if (_proxy is null) return;
        _runner.Request(new KernelParams(BlurType.Motion, Angle, Length, Smoothness));
    }

    private void OnProxyReady(object? sender, ProxyReadyEventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (PreviewBitmap is null || PreviewBitmap.PixelWidth != e.Width || PreviewBitmap.PixelHeight != e.Height)
                PreviewBitmap = ImageBufferInterop.NewCompatibleBitmap(e.Width, e.Height);
            ImageBufferInterop.ApplyBgraToWriteableBitmap(e.Bgra, e.Width, e.Height, PreviewBitmap);
        });
    }

    private static ImageBuffer Downscale(ImageBuffer src, int newW, int newH)
    {
        var dst = new ImageBuffer(newW, newH);
        double sx = (double)src.Width / newW;
        double sy = (double)src.Height / newH;
        for (int y = 0; y < newH; y++)
        {
            int srcY = Math.Min(src.Height - 1, (int)(y * sy));
            for (int x = 0; x < newW; x++)
            {
                int srcX = Math.Min(src.Width - 1, (int)(x * sx));
                int si = srcY * src.Width + srcX;
                int di = y * newW + x;
                dst.R[di] = src.R[si];
                dst.G[di] = src.G[si];
                dst.B[di] = src.B[si];
            }
        }
        return dst;
    }

    public void Dispose() => _runner.Dispose();
}
```

- [ ] **Step 3: Verify build**

```bash
dotnet build Deblur.sln
```
Expected: 0 errors. The `[ObservableProperty]` source generators produce the `public` partial-property counterparts.

- [ ] **Step 4: Commit**

```bash
git add Deblur/Services/ImageBufferInterop.cs Deblur/ViewModels/MainViewModel.cs
git commit -m "Add MainViewModel and ImageBufferInterop"
```

---

## Task 9: WPF — `PreviewCanvas` with `WriteableBitmap` and arrow overlay

**Files:**
- Create: `Deblur/Controls/PreviewCanvas.xaml`
- Create: `Deblur/Controls/PreviewCanvas.xaml.cs`

**Interfaces:**
- Consumes: `WriteableBitmap` (via `Source` dependency property).
- Produces: a `UserControl` with:
  - `public WriteableBitmap? Source { get; set; }` (DP, bound to `MainViewModel.PreviewBitmap`).
  - Events: `event EventHandler<ArrowDragEventArgs>? Dragging;` and `event EventHandler<ArrowDragEventArgs>? DragCommitted;`.
  - `ArrowDragEventArgs { float Angle; float Length; }` — computed in **proxy-image pixel coords**.

- [ ] **Step 1: Create the XAML**

Create `Deblur/Controls/PreviewCanvas.xaml`:
```xml
<UserControl x:Class="Deblur.Controls.PreviewCanvas"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Background="#222">
    <Grid>
        <Image x:Name="PreviewImage"
               Stretch="Uniform"
               RenderOptions.BitmapScalingMode="HighQuality"/>
        <Canvas x:Name="OverlayCanvas" IsHitTestVisible="False">
            <Line x:Name="ArrowShaft" Stroke="#FFEE33" StrokeThickness="2" Visibility="Collapsed"/>
            <Polygon x:Name="ArrowHead" Fill="#FFEE33" Visibility="Collapsed"/>
        </Canvas>
    </Grid>
</UserControl>
```

- [ ] **Step 2: Implement the code-behind**

Create `Deblur/Controls/PreviewCanvas.xaml.cs`:
```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Deblur.Controls;

public sealed class ArrowDragEventArgs : EventArgs
{
    public float Angle { get; init; }
    public float Length { get; init; }
}

public partial class PreviewCanvas : UserControl
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(WriteableBitmap), typeof(PreviewCanvas),
        new PropertyMetadata(null, OnSourceChanged));

    public WriteableBitmap? Source
    {
        get => (WriteableBitmap?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public event EventHandler<ArrowDragEventArgs>? Dragging;
    public event EventHandler<ArrowDragEventArgs>? DragCommitted;

    private Point? _dragStartScreen;
    private double _displayScale = 1.0;

    public PreviewCanvas()
    {
        InitializeComponent();
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        MouseLeave += OnMouseLeave;
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (PreviewCanvas)d;
        self.PreviewImage.Source = (WriteableBitmap?)e.NewValue;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Source is null) return;
        _dragStartScreen = e.GetPosition(this);
        CaptureMouse();
        UpdateDisplayScale();
        UpdateArrow(_dragStartScreen.Value, _dragStartScreen.Value);
        ArrowShaft.Visibility = ArrowHead.Visibility = Visibility.Visible;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStartScreen is null || Source is null) return;
        var cur = e.GetPosition(this);
        UpdateArrow(_dragStartScreen.Value, cur);
        var (angle, length) = ToImageSpace(_dragStartScreen.Value, cur);
        Dragging?.Invoke(this, new ArrowDragEventArgs { Angle = angle, Length = length });
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStartScreen is null || Source is null) return;
        var end = e.GetPosition(this);
        var (angle, length) = ToImageSpace(_dragStartScreen.Value, end);
        DragCommitted?.Invoke(this, new ArrowDragEventArgs { Angle = angle, Length = length });
        _dragStartScreen = null;
        ReleaseMouseCapture();
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        // Cancel drag: clear arrow, no commit.
        if (_dragStartScreen is null) return;
        _dragStartScreen = null;
        ArrowShaft.Visibility = ArrowHead.Visibility = Visibility.Collapsed;
        ReleaseMouseCapture();
    }

    private void UpdateDisplayScale()
    {
        if (Source is null) { _displayScale = 1.0; return; }
        double sx = ActualWidth / Source.PixelWidth;
        double sy = ActualHeight / Source.PixelHeight;
        _displayScale = Math.Min(sx, sy);
        if (_displayScale <= 0) _displayScale = 1.0;
    }

    private (float angle, float length) ToImageSpace(Point start, Point cur)
    {
        double dx = (cur.X - start.X) / _displayScale;
        double dy = (cur.Y - start.Y) / _displayScale;
        double lenPx = Math.Sqrt(dx * dx + dy * dy);
        double angleDeg = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        if (angleDeg < 0) angleDeg += 360.0;
        double clampedLen = Math.Clamp(lenPx, 1.0, 100.0);
        return ((float)angleDeg, (float)clampedLen);
    }

    private void UpdateArrow(Point start, Point cur)
    {
        ArrowShaft.X1 = start.X; ArrowShaft.Y1 = start.Y;
        ArrowShaft.X2 = cur.X;   ArrowShaft.Y2 = cur.Y;

        // Simple 8-pixel head on the tip.
        double dx = cur.X - start.X;
        double dy = cur.Y - start.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 4) { ArrowHead.Points.Clear(); return; }
        double ux = dx / len, uy = dy / len;
        double bx = cur.X - 8 * ux, by = cur.Y - 8 * uy;
        double px = -uy, py = ux;

        ArrowHead.Points = new PointCollection {
            new Point(cur.X, cur.Y),
            new Point(bx + 4 * px, by + 4 * py),
            new Point(bx - 4 * px, by - 4 * py),
        };
    }
}
```

- [ ] **Step 3: Verify build**

```bash
dotnet build Deblur.sln
```
Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add Deblur/Controls/PreviewCanvas.xaml Deblur/Controls/PreviewCanvas.xaml.cs
git commit -m "Add PreviewCanvas control with arrow overlay drag"
```

---

## Task 10: WPF — `MainWindow` layout wiring load / preview / sliders / reset

**Files:**
- Modify: `Deblur/MainWindow.xaml`
- Modify: `Deblur/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `MainViewModel`, `PreviewCanvas`.
- Produces: a runnable app that opens PNG/JPEG, shows live preview, responds to slider + drag input.

- [ ] **Step 1: Replace `MainWindow.xaml`**

Replace the entire contents of `Deblur/MainWindow.xaml` with:
```xml
<Window x:Class="Deblur.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:controls="clr-namespace:Deblur.Controls"
        xmlns:vm="clr-namespace:Deblur.ViewModels"
        xmlns:engine="clr-namespace:Deblur.Engine;assembly=Deblur.Engine"
        xmlns:sys="clr-namespace:System;assembly=mscorlib"
        Title="Deblur" Height="720" Width="1200"
        AllowDrop="True">
    <Window.DataContext><vm:MainViewModel/></Window.DataContext>
    <Window.Resources>
        <ObjectDataProvider x:Key="BlurTypeValues" MethodName="GetValues" ObjectType="{x:Type sys:Enum}">
            <ObjectDataProvider.MethodParameters>
                <x:Type TypeName="engine:BlurType"/>
            </ObjectDataProvider.MethodParameters>
        </ObjectDataProvider>
    </Window.Resources>

    <DockPanel>
        <Menu DockPanel.Dock="Top">
            <MenuItem Header="_File">
                <MenuItem Header="_Open..." Click="OnOpenClick"/>
                <MenuItem Header="_Save As..." Click="OnSaveAsClick"/>
                <Separator/>
                <MenuItem Header="E_xit" Click="OnExitClick"/>
            </MenuItem>
        </Menu>

        <Grid>
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="320"/>
            </Grid.ColumnDefinitions>

            <controls:PreviewCanvas x:Name="Preview" Grid.Column="0"
                                    Source="{Binding PreviewBitmap}"
                                    Dragging="OnPreviewDragging"
                                    DragCommitted="OnPreviewDragCommitted"/>

            <StackPanel Grid.Column="1" Margin="12">
                <TextBlock Text="Blur type" FontWeight="Bold" Margin="0,0,0,4"/>
                <ComboBox ItemsSource="{Binding Source={StaticResource BlurTypeValues}}"
                          SelectedItem="{Binding SelectedBlurType}"/>

                <Grid Margin="0,12,0,0" Visibility="{Binding IsMotionSelected, Converter={StaticResource BoolToVis}}">
                    <StackPanel>
                        <TextBlock Text="Angle (°)" Margin="0,4,0,0"/>
                        <Slider Minimum="0" Maximum="360" Value="{Binding Angle}"/>
                        <TextBlock Text="{Binding Angle, StringFormat={}{0:0.0}}" HorizontalAlignment="Right"/>

                        <TextBlock Text="Length (px, proxy)" Margin="0,8,0,0"/>
                        <Slider Minimum="1" Maximum="100" Value="{Binding Length}"/>
                        <TextBlock Text="{Binding Length, StringFormat={}{0:0.0}}" HorizontalAlignment="Right"/>

                        <TextBlock Text="Smoothness" Margin="0,8,0,0"/>
                        <Slider Minimum="0.0001" Maximum="0.1" SmallChange="0.0005" Value="{Binding Smoothness}"/>
                        <TextBlock Text="{Binding Smoothness, StringFormat={}{0:0.0000}}" HorizontalAlignment="Right"/>

                        <Button Content="Reset" Margin="0,12,0,0" Click="OnResetClick"/>
                        <Button Content="Render full resolution" Margin="0,8,0,0" Click="OnRenderFullClick"/>
                    </StackPanel>
                </Grid>

                <TextBlock Margin="0,12,0,0" TextWrapping="Wrap"
                           Visibility="{Binding IsComingSoon, Converter={StaticResource BoolToVis}}"
                           Text="This blur type will be supported in a future phase. Select 'Motion' to try the current build."/>

                <TextBlock Margin="0,20,0,0" Text="{Binding StatusMessage}" TextWrapping="Wrap" Foreground="#888"/>
            </StackPanel>
        </Grid>
    </DockPanel>
</Window>
```

Also add the standard `BooleanToVisibilityConverter` to `App.xaml` resources so `BoolToVis` resolves.

Replace `Deblur/App.xaml`:
```xml
<Application x:Class="Deblur.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             StartupUri="MainWindow.xaml">
    <Application.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVis"/>
    </Application.Resources>
</Application>
```

- [ ] **Step 2: Replace `MainWindow.xaml.cs`**

Replace `Deblur/MainWindow.xaml.cs`:
```csharp
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Deblur.Controls;
using Deblur.ViewModels;

namespace Deblur;

public partial class MainWindow : Window
{
    private MainViewModel Vm => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff",
        };
        if (dlg.ShowDialog(this) == true)
        {
            LoadFile(dlg.FileName);
        }
    }

    private void OnSaveAsClick(object sender, RoutedEventArgs e) { /* implemented in Task 11 */ }
    private void OnRenderFullClick(object sender, RoutedEventArgs e) { /* implemented in Task 11 */ }
    private void OnExitClick(object sender, RoutedEventArgs e) => Close();
    private void OnResetClick(object sender, RoutedEventArgs e) => Vm.Reset();

    private void OnPreviewDragging(object? sender, ArrowDragEventArgs e)
        => Vm.UpdateKernel(e.Angle, e.Length);

    private void OnPreviewDragCommitted(object? sender, ArrowDragEventArgs e)
        => Vm.UpdateKernel(e.Angle, e.Length);

    private void LoadFile(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            Vm.LoadImageFromBytes(bytes);
            Vm.CurrentFilePath = path;
            Vm.StatusMessage = System.IO.Path.GetFileName(path);
        }
        catch (Engine.InvalidImageFormatException ex)
        {
            MessageBox.Show(this, $"Couldn't read \"{System.IO.Path.GetFileName(path)}\": {ex.Message}",
                "Open failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
```

- [ ] **Step 3: Run the app**

```bash
dotnet run --project Deblur/Deblur.csproj
```
Expected: window opens; menu → Open → PNG loads and displays. Dragging on the preview draws an arrow and updates the image beneath as the drag proceeds. Sliders also drive updates.

- [ ] **Step 4: Commit**

```bash
git add Deblur/MainWindow.xaml Deblur/MainWindow.xaml.cs Deblur/App.xaml
git commit -m "Wire MainWindow: open, preview canvas, blur-type dropdown, sliders, reset"
```

---

## Task 11: WPF — Full-res render (modal progress) + Save

**Files:**
- Create: `Deblur/Controls/BusyOverlay.xaml` + `.cs`
- Modify: `Deblur/MainWindow.xaml` (add busy overlay)
- Modify: `Deblur/MainWindow.xaml.cs` (fill in `OnSaveAsClick` and `OnRenderFullClick`)

**Interfaces:**
- Consumes: `MainViewModel.RenderFullAsPngAsync`, `RenderFullAsJpegAsync`.
- Produces: a working end-to-end save (with implicit full-res render if the user hasn't rendered yet).

- [ ] **Step 1: Add the busy overlay**

Create `Deblur/Controls/BusyOverlay.xaml`:
```xml
<UserControl x:Class="Deblur.Controls.BusyOverlay"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Background="#AA000000" Visibility="Collapsed" IsHitTestVisible="True">
    <Grid>
        <Border Background="#222" CornerRadius="8" Padding="24"
                HorizontalAlignment="Center" VerticalAlignment="Center">
            <StackPanel>
                <TextBlock x:Name="MessageText" Text="Working…" Foreground="White" FontSize="14" Margin="0,0,0,12"/>
                <ProgressBar x:Name="ProgressBar" Width="240" Height="10" Minimum="0" Maximum="1"/>
            </StackPanel>
        </Border>
    </Grid>
</UserControl>
```

Create `Deblur/Controls/BusyOverlay.xaml.cs`:
```csharp
using System.Windows.Controls;

namespace Deblur.Controls;

public partial class BusyOverlay : UserControl
{
    public BusyOverlay() { InitializeComponent(); }

    public void Show(string message)
    {
        MessageText.Text = message;
        ProgressBar.Value = 0;
        Visibility = System.Windows.Visibility.Visible;
    }

    public void SetProgress(double value) => ProgressBar.Value = value;

    public void Hide() => Visibility = System.Windows.Visibility.Collapsed;
}
```

- [ ] **Step 2: Add the overlay to `MainWindow.xaml`**

In `Deblur/MainWindow.xaml`, wrap the existing `DockPanel` in a `Grid` so the overlay can float on top. Replace the outermost element with:
```xml
<Grid>
    <DockPanel>
        <!-- ... existing Menu + Grid content unchanged ... -->
    </DockPanel>
    <controls:BusyOverlay x:Name="Busy"/>
</Grid>
```
(Move the existing `<DockPanel>...</DockPanel>` inside the new `<Grid>` verbatim.)

- [ ] **Step 3: Fill in Save and Render-Full handlers**

Update `Deblur/MainWindow.xaml.cs`, replacing the stub `OnSaveAsClick` and `OnRenderFullClick`:

```csharp
private async void OnRenderFullClick(object sender, RoutedEventArgs e)
{
    if (Vm.CurrentFilePath is null)
    {
        MessageBox.Show(this, "Open an image first.", "Nothing to render", MessageBoxButton.OK, MessageBoxImage.Information);
        return;
    }
    Busy.Show("Rendering full resolution…");
    try
    {
        var progress = new Progress<double>(v => Busy.SetProgress(v));
        // Populate _fullResBuffer without touching _originalFullRes or the current preview.
        // The proxy-deblurred preview already shows the same params visually; Save will use the cache.
        await Vm.EnsureFullResRenderedAsync(progress);
        Vm.StatusMessage = "Full-resolution render ready. Use File → Save As… to write it.";
    }
    catch (Exception ex)
    {
        MessageBox.Show(this, ex.Message, "Render failed", MessageBoxButton.OK, MessageBoxImage.Error);
    }
    finally { Busy.Hide(); }
}

private async void OnSaveAsClick(object sender, RoutedEventArgs e)
{
    if (Vm.CurrentFilePath is null)
    {
        MessageBox.Show(this, "Open an image first.", "Nothing to save", MessageBoxButton.OK, MessageBoxImage.Information);
        return;
    }
    var dlg = new SaveFileDialog
    {
        Filter = "PNG image|*.png|JPEG image|*.jpg;*.jpeg",
        FileName = System.IO.Path.GetFileNameWithoutExtension(Vm.CurrentFilePath) + "_deblurred",
        DefaultExt = ".png",
    };
    if (dlg.ShowDialog(this) != true) return;

    Busy.Show("Rendering and saving…");
    try
    {
        var progress = new Progress<double>(v => Busy.SetProgress(v));
        bool jpeg = dlg.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                 || dlg.FileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
        byte[] bytes = jpeg
            ? await Vm.RenderFullAsJpegAsync(quality: 92, progress)
            : await Vm.RenderFullAsPngAsync(progress);
        File.WriteAllBytes(dlg.FileName, bytes);
        Vm.StatusMessage = $"Saved: {System.IO.Path.GetFileName(dlg.FileName)}";
    }
    catch (Exception ex)
    {
        MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
    }
    finally { Busy.Hide(); }
}
```

- [ ] **Step 4: Run and manually verify**

```bash
dotnet run --project Deblur/Deblur.csproj
```
Open a small JPEG. Drag on the preview to induce some deblur. Click "Render full resolution" — busy overlay shows for a moment; when it closes, preview shows the full-res result (downsampled for display). Then File → Save As → PNG. Reopen the saved file in an external viewer; verify it looks right.

- [ ] **Step 5: Commit**

```bash
git add Deblur/Controls/BusyOverlay.xaml Deblur/Controls/BusyOverlay.xaml.cs Deblur/MainWindow.xaml Deblur/MainWindow.xaml.cs
git commit -m "Add full-res render and save-as with modal busy overlay"
```

---

## Task 12: WPF — Drag-and-drop + error modals

**Files:**
- Modify: `Deblur/MainWindow.xaml.cs` (add `DragEnter`, `Drop`, large-image warning).

**Interfaces:**
- Consumes: WPF drag-drop routed events.
- Produces: dropping a file onto the window loads it; garbage files show the "Couldn't read" modal without crashing; very large images (> 100 MP) get a confirm prompt.

- [ ] **Step 1: Wire drop handlers**

In `Deblur/MainWindow.xaml.cs`, add `DragEnter` and `Drop` handlers wired via the constructor (or events in XAML — either works; using code for simplicity):

```csharp
public MainWindow()
{
    InitializeComponent();
    PreviewDragEnter += OnFileDragEnter;
    Drop += OnFileDrop;
}

private void OnFileDragEnter(object sender, DragEventArgs e)
{
    e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
    e.Handled = true;
}

private void OnFileDrop(object sender, DragEventArgs e)
{
    if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
    var files = (string[])e.Data.GetData(DataFormats.FileDrop);
    if (files.Length == 0) return;
    LoadFile(files[0]);
}
```

- [ ] **Step 2: Add the very-large-image guard to `LoadFile`**

Modify the existing `LoadFile` method:
```csharp
private void LoadFile(string path)
{
    try
    {
        var bytes = File.ReadAllBytes(path);

        // Pre-check pixel count via lightweight decode.
        using (var stream = new MemoryStream(bytes))
        {
            var frame = System.Windows.Media.Imaging.BitmapFrame.Create(stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation,
                System.Windows.Media.Imaging.BitmapCacheOption.None);
            long pixels = (long)frame.PixelWidth * frame.PixelHeight;
            if (pixels > 100_000_000)
            {
                double mp = pixels / 1_000_000.0;
                var choice = MessageBox.Show(this,
                    $"Image is very large ({mp:0.0} MP); may be slow. Continue?",
                    "Large image", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (choice != MessageBoxResult.Yes) return;
            }
        }

        Vm.LoadImageFromBytes(bytes);
        Vm.CurrentFilePath = path;
        Vm.StatusMessage = System.IO.Path.GetFileName(path);
    }
    catch (Engine.InvalidImageFormatException ex)
    {
        MessageBox.Show(this, $"Couldn't read \"{System.IO.Path.GetFileName(path)}\": {ex.Message}",
            "Open failed", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    catch (OutOfMemoryException)
    {
        MessageBox.Show(this, "Ran out of memory. Try a smaller image.",
            "Out of memory", MessageBoxButton.OK, MessageBoxImage.Error);
    }
    catch (Exception ex)
    {
        MessageBox.Show(this, ex.Message, "Open failed", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
```

- [ ] **Step 3: Manual verification**

```bash
dotnet run --project Deblur/Deblur.csproj
```
- Drag a PNG from Explorer onto the window → loads.
- Drag a plain text file renamed `.jpg` onto the window → "Couldn't read" modal; app state unchanged.
- (Optional) Open a very large image if you have one; confirm the size warning appears.

- [ ] **Step 4: Commit**

```bash
git add Deblur/MainWindow.xaml.cs
git commit -m "Add drag-and-drop and error modals for invalid/large images"
```

---

## Task 13: Manual smoke test pass

**Files:** none.

**Interfaces:** none.

- [ ] **Step 1: Run the full test suite one more time**

```bash
dotnet test Deblur.sln
```
Expected: all engine tests pass (~24 total).

- [ ] **Step 2: Launch the app and run the manual smoke checklist**

```bash
dotnet run --project Deblur/Deblur.csproj
```

Walk through every item from the spec's manual smoke checklist and check them off:

- [ ] Open a PNG via File → Open.
- [ ] Open a JPEG via drag-and-drop.
- [ ] Click and drag on the preview — arrow overlay renders and follows the cursor.
- [ ] Release the drag — arrow freezes; sliders show the committed angle and length.
- [ ] Move a slider — preview updates within a few frames, no visible queueing lag.
- [ ] Switch blur-type dropdown to "OutOfFocus" — "Coming soon" panel appears.
- [ ] Switch back to "Motion" — sidebar returns; arrow and sliders intact.
- [ ] Click "Render full resolution" — modal progress appears, then closes; status shows "Full-resolution render ready". The on-screen preview visually matches the deblurred result (same params, downsampled).
- [ ] File → Save As → PNG. The save uses the cached full-res render (no additional wait). Reopen the saved file externally; it should be at full resolution and match the deblurred preview.
- [ ] Drop a corrupt file (rename a `.txt` to `.jpg`) — error modal appears; app state unchanged.
- [ ] Click Reset — sliders return to defaults; preview shows the untouched image.

- [ ] **Step 3: Commit any smoke-test-triggered fixes**

If the smoke test surfaces bugs, fix them and commit each fix separately with a message describing the failure and the fix. If nothing was wrong, no commit is needed for this step.

- [ ] **Step 4: Tag phase 1 complete**

```bash
git tag phase1
```

---

## Summary

Thirteen tasks, each an independently reviewable commit. Engine (Tasks 2–7) is TDD-first with tests written before implementation. UI (Tasks 8–12) is manually verified because WPF UI tests are a known rabbit hole; the manual smoke checklist in Task 13 is the acceptance gate.
