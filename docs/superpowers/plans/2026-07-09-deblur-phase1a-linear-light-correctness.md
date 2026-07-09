# Deblur Phase 1.a Implementation Plan — Linear-Light Correctness Foundation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the sRGB / linear-light silent quality bug and land the correctness scaffolding (bit depth via WIC, boundary handling with edge taper, luminance-only mode, area-average proxy) plus a minimal validation harness that measures the gain — without changing existing algorithm math.

**Architecture:** A single `PipelineOptions` record threads new toggles through `DeblurJobRunner` and each `IDeconvolver.Apply`. Color transforms and boundary padding move into helpers so the three deconvolvers share one implementation. The WPF layer owns the WIC-backed high-bit-depth codec; `Deblur.Engine` stays UI-free.

**Tech Stack:** .NET 8; `FftSharp`; `System.Windows.Media.Imaging` (WIC, WPF layer); `CommunityToolkit.Mvvm`; xUnit.

## Global Constraints

- .NET 8. `net8.0` for `Deblur.Engine` + `Deblur.Tests`. `net8.0-windows` + `UseWPF` for `Deblur`. Nullable + ImplicitUsings enabled.
- No new NuGet packages. WIC ships with WPF (`PresentationCore.dll`).
- `Deblur.Engine` stays UI-free — WIC references live in the WPF layer only.
- `PipelineOptions.Default = new(LinearLight: true, EdgeTaper: true, BoundaryMode: BoundaryMode.Reflect, LuminanceOnly: false)`.
- Existing 64 tests remain green under `PipelineOptions.Default`; where linear-light legitimately shifts a synthetic-recovery number, adjust the threshold with an inline `// linear-light baseline: <old>→<new>` comment.
- `IDeconvolver.Apply` grows a trailing `PipelineOptions? options = null` parameter (implementations do `var opt = options ?? PipelineOptions.Default;`) so callers that only pass `DeconvolutionParams` continue to compile.
- Test count target after 1.a: ~88 (64 pre-existing + ~24 new).
- Phase 1.a branches from tag `phase4b` onto `phase1a-linear-light-correctness` (already created).

---

### Task 1: PipelineOptions, BitDepth, BoundaryFill scaffold

**Files:**
- Create: `Deblur.Engine/PipelineOptions.cs`
- Create: `Deblur.Engine/BitDepth.cs`
- Create: `Deblur.Engine/BoundaryFill.cs`
- Test:   `Deblur.Tests/BoundaryFillTests.cs`

**Interfaces:**
- Produces:
  - `enum BoundaryMode { Reflect, Replicate, Periodic }`
  - `enum BitDepth { Eight, Sixteen }`
  - `sealed record PipelineOptions(bool LinearLight, bool EdgeTaper, BoundaryMode BoundaryMode, bool LuminanceOnly) { public static PipelineOptions Default => new(true, true, BoundaryMode.Reflect, false); }`
  - `static class BoundaryFill { public static float[,] Pad(float[] channel, int w, int h, int pad, int fftSize, BoundaryMode mode); }`

- [ ] **Step 1: Write failing tests**

```csharp
// Deblur.Tests/BoundaryFillTests.cs
using Deblur.Engine;
using Xunit;

namespace Deblur.Tests;

public class BoundaryFillTests
{
    [Fact]
    public void Reflect_MatchesLegacyReflectIndex()
    {
        var channel = new float[] { 1, 2, 3, 4 }; // 4x1
        var padded = BoundaryFill.Pad(channel, w: 4, h: 1, pad: 2, fftSize: 8, BoundaryMode.Reflect);
        // fftSize row 0: reflect across [1,2,3,4] with pad=2:
        // index in fftSize: 0..7, source position = i - pad => -2,-1,0,1,2,3,4,5
        // reflect over [0..3] period 6: 2,1,0,1,2,3,4? no — validate via ReflectIndex bounce math
        Assert.Equal(3f, padded[0, 0]); // reflect(-2)=2 → channel[2]=3
        Assert.Equal(2f, padded[0, 1]); // reflect(-1)=1 → channel[1]=2
        Assert.Equal(1f, padded[0, 2]); // reflect(0)=0  → channel[0]=1
        Assert.Equal(4f, padded[0, 5]); // reflect(3)=3  → channel[3]=4
        Assert.Equal(3f, padded[0, 6]); // reflect(4)=2  → channel[2]=3
    }

    [Fact]
    public void Replicate_ClampsToEdges()
    {
        var channel = new float[] { 1, 2, 3, 4 };
        var padded = BoundaryFill.Pad(channel, 4, 1, pad: 2, fftSize: 8, BoundaryMode.Replicate);
        Assert.Equal(1f, padded[0, 0]);
        Assert.Equal(1f, padded[0, 1]);
        Assert.Equal(4f, padded[0, 6]);
        Assert.Equal(4f, padded[0, 7]);
    }

    [Fact]
    public void Periodic_WrapsModulo()
    {
        var channel = new float[] { 1, 2, 3, 4 };
        var padded = BoundaryFill.Pad(channel, 4, 1, pad: 2, fftSize: 8, BoundaryMode.Periodic);
        Assert.Equal(3f, padded[0, 0]); // (-2 mod 4) = 2 → channel[2]=3
        Assert.Equal(4f, padded[0, 1]); // (-1 mod 4) = 3 → channel[3]=4
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Deblur.sln --filter FullyQualifiedName~BoundaryFillTests`
Expected: FAIL (BoundaryFill not defined).

- [ ] **Step 3: Implement scaffold types**

```csharp
// Deblur.Engine/BitDepth.cs
namespace Deblur.Engine;

public enum BitDepth { Eight, Sixteen }
```

```csharp
// Deblur.Engine/PipelineOptions.cs
namespace Deblur.Engine;

public sealed record PipelineOptions(
    bool LinearLight,
    bool EdgeTaper,
    BoundaryMode BoundaryMode,
    bool LuminanceOnly)
{
    public static PipelineOptions Default { get; } =
        new(LinearLight: true, EdgeTaper: true, BoundaryMode: BoundaryMode.Reflect, LuminanceOnly: false);
}
```

```csharp
// Deblur.Engine/BoundaryFill.cs
namespace Deblur.Engine;

public enum BoundaryMode { Reflect, Replicate, Periodic }

public static class BoundaryFill
{
    public static float[,] Pad(float[] channel, int w, int h, int pad, int fftSize, BoundaryMode mode)
    {
        var padded = new float[fftSize, fftSize];
        for (int y = 0; y < fftSize; y++)
        {
            int sy = MapIndex(y - pad, h, mode);
            for (int x = 0; x < fftSize; x++)
            {
                int sx = MapIndex(x - pad, w, mode);
                padded[y, x] = channel[sy * w + sx];
            }
        }
        return padded;
    }

    private static int MapIndex(int i, int len, BoundaryMode mode)
    {
        if (len <= 1) return 0;
        return mode switch
        {
            BoundaryMode.Reflect   => Reflect(i, len),
            BoundaryMode.Replicate => Math.Clamp(i, 0, len - 1),
            BoundaryMode.Periodic  => ((i % len) + len) % len,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    private static int Reflect(int i, int len)
    {
        int period = 2 * (len - 1);
        int m = ((i % period) + period) % period;
        return m < len ? m : period - m;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Deblur.sln --filter FullyQualifiedName~BoundaryFillTests`
Expected: 3 tests pass.

- [ ] **Step 5: Commit**

```bash
git add Deblur.Engine/PipelineOptions.cs Deblur.Engine/BitDepth.cs Deblur.Engine/BoundaryFill.cs Deblur.Tests/BoundaryFillTests.cs
git commit -m "Add PipelineOptions, BitDepth, BoundaryFill scaffold"
```

---

### Task 2: sRGB ↔ linear color transform

**Files:**
- Create: `Deblur.Engine/Color/SrgbLinear.cs`
- Test:   `Deblur.Tests/SrgbLinearTests.cs`

**Interfaces:**
- Produces:
  - `static class SrgbLinear` with:
    - `float ToLinear(byte v)`, `float ToLinear(ushort v)`, `float ToLinear(float v)` (float overload for testing)
    - `byte ToSrgb8(float linear)`, `ushort ToSrgb16(float linear)`
    - `void ToLinearInPlace(float[] srgbNormalized)` — treats the array as `[0,1]` sRGB-encoded, converts each entry to linear
    - `void ToSrgbInPlace(float[] linear)` — inverse of above

The `[0,1]` in-place methods are what `DeblurJobRunner` calls, because `ImageBuffer` already stores normalized floats.

- [ ] **Step 1: Write failing tests**

```csharp
// Deblur.Tests/SrgbLinearTests.cs
using Deblur.Engine.Color;
using Xunit;

namespace Deblur.Tests;

public class SrgbLinearTests
{
    [Fact]
    public void ByteRoundTrip_WithinOneLsb()
    {
        for (int v = 0; v < 256; v++)
        {
            byte b = (byte)v;
            float lin = SrgbLinear.ToLinear(b);
            byte round = SrgbLinear.ToSrgb8(lin);
            Assert.InRange(Math.Abs(round - v), 0, 1);
        }
    }

    [Fact]
    public void FloatRoundTrip_WithinTolerance()
    {
        for (int i = 0; i <= 1000; i++)
        {
            float v = i / 1000f;
            float lin = SrgbLinear.ToLinear(v);
            float back = SrgbLinear.ToSrgbFloat(lin);
            Assert.InRange(Math.Abs(back - v), 0f, 1e-4f);
        }
    }

    [Fact]
    public void KnownPoints()
    {
        // At v=0.04045 sRGB the piecewise switches: linear = 0.04045/12.92 = 0.003130804...
        float lin = SrgbLinear.ToLinear(0.04045f);
        Assert.InRange(lin, 0.003130f, 0.003132f);
        // sRGB(0.5) linear ~= 0.21404
        Assert.InRange(SrgbLinear.ToLinear(0.5f), 0.213f, 0.216f);
    }

    [Fact]
    public void InPlaceMonotonic()
    {
        var arr = new float[] { 0f, 0.25f, 0.5f, 0.75f, 1f };
        SrgbLinear.ToLinearInPlace(arr);
        for (int i = 1; i < arr.Length; i++)
            Assert.True(arr[i] > arr[i - 1]);
    }

    [Fact]
    public void UshortRoundTrip_WithinOneLsb()
    {
        int[] samples = { 0, 1, 1000, 32767, 65534, 65535 };
        foreach (int v in samples)
        {
            ushort u = (ushort)v;
            float lin = SrgbLinear.ToLinear(u);
            ushort round = SrgbLinear.ToSrgb16(lin);
            Assert.InRange(Math.Abs((int)round - v), 0, 1);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Deblur.sln --filter FullyQualifiedName~SrgbLinearTests`
Expected: FAIL (SrgbLinear not defined).

- [ ] **Step 3: Implement `SrgbLinear`**

```csharp
// Deblur.Engine/Color/SrgbLinear.cs
namespace Deblur.Engine.Color;

public static class SrgbLinear
{
    private static readonly float[] _byteToLinear = BuildByteLut();
    private static readonly float[] _ushortToLinear = BuildUshortLut();

    public static float ToLinear(byte v) => _byteToLinear[v];
    public static float ToLinear(ushort v) => _ushortToLinear[v];

    public static float ToLinear(float srgb)
    {
        // srgb in [0,1]; piecewise IEC 61966-2-1.
        if (srgb <= 0.04045f) return srgb / 12.92f;
        return MathF.Pow((srgb + 0.055f) / 1.055f, 2.4f);
    }

    public static float ToSrgbFloat(float linear)
    {
        if (linear <= 0.0031308f) return linear * 12.92f;
        return 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
    }

    public static byte ToSrgb8(float linear)
    {
        float s = ToSrgbFloat(linear);
        int i = (int)MathF.Round(s * 255f);
        return (byte)Math.Clamp(i, 0, 255);
    }

    public static ushort ToSrgb16(float linear)
    {
        float s = ToSrgbFloat(linear);
        int i = (int)MathF.Round(s * 65535f);
        return (ushort)Math.Clamp(i, 0, 65535);
    }

    public static void ToLinearInPlace(float[] srgbNormalized)
    {
        for (int i = 0; i < srgbNormalized.Length; i++)
            srgbNormalized[i] = ToLinear(srgbNormalized[i]);
    }

    public static void ToSrgbInPlace(float[] linear)
    {
        for (int i = 0; i < linear.Length; i++)
            linear[i] = ToSrgbFloat(linear[i]);
    }

    private static float[] BuildByteLut()
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++) t[i] = ToLinear(i / 255f);
        return t;
    }

    private static float[] BuildUshortLut()
    {
        var t = new float[65536];
        for (int i = 0; i < 65536; i++) t[i] = ToLinear(i / 65535f);
        return t;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Deblur.sln --filter FullyQualifiedName~SrgbLinearTests`
Expected: 5 tests pass.

- [ ] **Step 5: Commit**

```bash
git add Deblur.Engine/Color/SrgbLinear.cs Deblur.Tests/SrgbLinearTests.cs
git commit -m "Add sRGB<->linear transfer with 8/16-bit LUTs"
```

---

### Task 3: Edge taper

**Files:**
- Create: `Deblur.Engine/EdgeTaper.cs`
- Test:   `Deblur.Tests/EdgeTaperTests.cs`

**Interfaces:**
- Consumes: nothing from prior tasks.
- Produces:
  - `static class EdgeTaper { public static void ApplyInPlace(float[,] padded, int pad); }` — blends a separable Tukey ramp from the interior edge outward for `pad` pixels toward the padded ring's mean. Called after `BoundaryFill.Pad` and before FFT.

- [ ] **Step 1: Write failing tests**

```csharp
// Deblur.Tests/EdgeTaperTests.cs
using Deblur.Engine;
using Xunit;

namespace Deblur.Tests;

public class EdgeTaperTests
{
    [Fact]
    public void CenterUnchanged()
    {
        var padded = new float[16, 16];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                padded[y, x] = 0.5f;
        // Add a spike well inside the interior.
        padded[8, 8] = 1f;
        EdgeTaper.ApplyInPlace(padded, pad: 4);
        Assert.Equal(1f, padded[8, 8]);
        Assert.Equal(0.5f, padded[8, 9]);
    }

    [Fact]
    public void BorderBlendsTowardInteriorMean()
    {
        var padded = new float[16, 16];
        // interior = 0.8, ring reflected but we can pretend the ring is 0.0
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                padded[y, x] = (y < 4 || y >= 12 || x < 4 || x >= 12) ? 0.0f : 0.8f;
        EdgeTaper.ApplyInPlace(padded, pad: 4);
        // Corner should be closer to interior mean 0.8 than raw 0.0.
        Assert.True(padded[0, 0] > 0.05f, $"corner {padded[0, 0]} not blended toward mean");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Deblur.sln --filter FullyQualifiedName~EdgeTaperTests`
Expected: FAIL.

- [ ] **Step 3: Implement `EdgeTaper`**

```csharp
// Deblur.Engine/EdgeTaper.cs
namespace Deblur.Engine;

public static class EdgeTaper
{
    /// <summary>
    /// Applies a separable Tukey (cosine) taper over the outer <paramref name="pad"/> pixels
    /// of a padded FFT canvas, blending them toward the interior mean so periodic-convolution
    /// wrap does not ring at the boundary. In place.
    /// </summary>
    public static void ApplyInPlace(float[,] padded, int pad)
    {
        int h = padded.GetLength(0);
        int w = padded.GetLength(1);
        if (pad <= 0 || w <= 2 * pad || h <= 2 * pad) return;

        double sum = 0; long count = 0;
        for (int y = pad; y < h - pad; y++)
            for (int x = pad; x < w - pad; x++)
            { sum += padded[y, x]; count++; }
        float mean = count > 0 ? (float)(sum / count) : 0f;

        var wx = new float[w];
        var wy = new float[h];
        for (int i = 0; i < w; i++) wx[i] = Taper(i, w, pad);
        for (int i = 0; i < h; i++) wy[i] = Taper(i, h, pad);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float m = wx[x] * wy[y];
                padded[y, x] = m * padded[y, x] + (1f - m) * mean;
            }
        }
    }

    private static float Taper(int i, int len, int pad)
    {
        if (i < pad)
            return 0.5f * (1f - MathF.Cos(MathF.PI * i / pad));
        int right = len - 1 - i;
        if (right < pad)
            return 0.5f * (1f - MathF.Cos(MathF.PI * right / pad));
        return 1f;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Deblur.sln --filter FullyQualifiedName~EdgeTaperTests`
Expected: 2 tests pass.

- [ ] **Step 5: Commit**

```bash
git add Deblur.Engine/EdgeTaper.cs Deblur.Tests/EdgeTaperTests.cs
git commit -m "Add separable Tukey edge taper for FFT padded borders"
```

---

### Task 4: YCbCr conversion

**Files:**
- Create: `Deblur.Engine/Color/YCbCr.cs`
- Test:   `Deblur.Tests/YCbCrTests.cs`

**Interfaces:**
- Produces:
  - `static class YCbCr` with:
    - `(float[] y, float[] cb, float[] cr) FromRgb(float[] r, float[] g, float[] b)`
    - `(float[] r, float[] g, float[] b) ToRgb(float[] y, float[] cb, float[] cr)`
  - Uses BT.601: `Y = 0.299 R + 0.587 G + 0.114 B`; `Cb = 0.5 + (B - Y) / 1.772`; `Cr = 0.5 + (R - Y) / 1.402`. Inputs and outputs are `[0,1]` values (Y in `[0,1]`, Cb/Cr centered at 0.5).

- [ ] **Step 1: Write failing tests**

```csharp
// Deblur.Tests/YCbCrTests.cs
using Deblur.Engine.Color;
using Xunit;

namespace Deblur.Tests;

public class YCbCrTests
{
    [Fact]
    public void RoundTrip_WithinTolerance()
    {
        var r = new float[] { 0f, 0.25f, 0.5f, 0.75f, 1f, 0.3f, 0.6f };
        var g = new float[] { 0f, 0.5f, 0.5f, 0.5f, 1f, 0.7f, 0.2f };
        var b = new float[] { 0f, 0.75f, 0.5f, 0.25f, 1f, 0.1f, 0.9f };
        var (y, cb, cr) = YCbCr.FromRgb(r, g, b);
        var (r2, g2, b2) = YCbCr.ToRgb(y, cb, cr);
        for (int i = 0; i < r.Length; i++)
        {
            Assert.InRange(Math.Abs(r2[i] - r[i]), 0f, 1e-5f);
            Assert.InRange(Math.Abs(g2[i] - g[i]), 0f, 1e-5f);
            Assert.InRange(Math.Abs(b2[i] - b[i]), 0f, 1e-5f);
        }
    }

    [Fact]
    public void Grayscale_YEqualsIntensity_CbCrHalf()
    {
        var (y, cb, cr) = YCbCr.FromRgb(new[] { 0.4f }, new[] { 0.4f }, new[] { 0.4f });
        Assert.InRange(Math.Abs(y[0] - 0.4f), 0f, 1e-5f);
        Assert.InRange(Math.Abs(cb[0] - 0.5f), 0f, 1e-5f);
        Assert.InRange(Math.Abs(cr[0] - 0.5f), 0f, 1e-5f);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Deblur.sln --filter FullyQualifiedName~YCbCrTests`
Expected: FAIL.

- [ ] **Step 3: Implement `YCbCr`**

```csharp
// Deblur.Engine/Color/YCbCr.cs
namespace Deblur.Engine.Color;

public static class YCbCr
{
    public static (float[] y, float[] cb, float[] cr) FromRgb(float[] r, float[] g, float[] b)
    {
        int n = r.Length;
        var y = new float[n]; var cb = new float[n]; var cr = new float[n];
        for (int i = 0; i < n; i++)
        {
            float yi = 0.299f * r[i] + 0.587f * g[i] + 0.114f * b[i];
            y[i]  = yi;
            cb[i] = 0.5f + (b[i] - yi) / 1.772f;
            cr[i] = 0.5f + (r[i] - yi) / 1.402f;
        }
        return (y, cb, cr);
    }

    public static (float[] r, float[] g, float[] b) ToRgb(float[] y, float[] cb, float[] cr)
    {
        int n = y.Length;
        var r = new float[n]; var g = new float[n]; var b = new float[n];
        for (int i = 0; i < n; i++)
        {
            float cbC = cb[i] - 0.5f;
            float crC = cr[i] - 0.5f;
            r[i] = y[i] + 1.402f * crC;
            b[i] = y[i] + 1.772f * cbC;
            g[i] = y[i] - (0.299f * 1.402f / 0.587f) * crC - (0.114f * 1.772f / 0.587f) * cbC;
        }
        return (r, g, b);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Deblur.sln --filter FullyQualifiedName~YCbCrTests`
Expected: 2 tests pass.

- [ ] **Step 5: Commit**

```bash
git add Deblur.Engine/Color/YCbCr.cs Deblur.Tests/YCbCrTests.cs
git commit -m "Add BT.601 RGB<->YCbCr float conversion"
```

---

### Task 5: Area-average resample

**Files:**
- Create: `Deblur.Engine/Imaging/AreaResample.cs`
- Test:   `Deblur.Tests/AreaResampleTests.cs`

**Interfaces:**
- Produces:
  - `static class AreaResample { public static ImageBuffer Box(ImageBuffer src, int newW, int newH); }` — downscale via exact fractional-coverage area average. Upscale (newW > src.Width) is out of scope; throw `ArgumentException` if requested.

- [ ] **Step 1: Write failing tests**

```csharp
// Deblur.Tests/AreaResampleTests.cs
using Deblur.Engine;
using Deblur.Engine.Imaging;
using Xunit;

namespace Deblur.Tests;

public class AreaResampleTests
{
    [Fact]
    public void Checkerboard_2To1_YieldsUniformMean()
    {
        // 4x4 checkerboard (0 or 1); every 2x2 tile averages to 0.5.
        var src = new ImageBuffer(4, 4);
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                float v = ((x + y) & 1) == 0 ? 1f : 0f;
                int i = y * 4 + x;
                src.R[i] = v; src.G[i] = v; src.B[i] = v;
            }
        var dst = AreaResample.Box(src, 2, 2);
        for (int i = 0; i < 4; i++)
        {
            Assert.InRange(dst.R[i], 0.49f, 0.51f);
            Assert.InRange(dst.G[i], 0.49f, 0.51f);
            Assert.InRange(dst.B[i], 0.49f, 0.51f);
        }
    }

    [Fact]
    public void Dimensions_Correct()
    {
        var src = new ImageBuffer(100, 60);
        var dst = AreaResample.Box(src, 33, 20);
        Assert.Equal(33, dst.Width);
        Assert.Equal(20, dst.Height);
    }

    [Fact]
    public void Upscale_Throws()
    {
        var src = new ImageBuffer(10, 10);
        Assert.Throws<ArgumentException>(() => AreaResample.Box(src, 20, 20));
    }

    [Fact]
    public void ConstantInput_ConstantOutput()
    {
        var src = new ImageBuffer(50, 30);
        for (int i = 0; i < src.PixelCount; i++)
        { src.R[i] = 0.3f; src.G[i] = 0.6f; src.B[i] = 0.9f; }
        var dst = AreaResample.Box(src, 25, 15);
        for (int i = 0; i < dst.PixelCount; i++)
        {
            Assert.InRange(Math.Abs(dst.R[i] - 0.3f), 0f, 1e-5f);
            Assert.InRange(Math.Abs(dst.G[i] - 0.6f), 0f, 1e-5f);
            Assert.InRange(Math.Abs(dst.B[i] - 0.9f), 0f, 1e-5f);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Deblur.sln --filter FullyQualifiedName~AreaResampleTests`
Expected: FAIL.

- [ ] **Step 3: Implement `AreaResample.Box`**

```csharp
// Deblur.Engine/Imaging/AreaResample.cs
namespace Deblur.Engine.Imaging;

public static class AreaResample
{
    public static ImageBuffer Box(ImageBuffer src, int newW, int newH)
    {
        if (newW <= 0 || newH <= 0) throw new ArgumentOutOfRangeException();
        if (newW > src.Width || newH > src.Height)
            throw new ArgumentException("Upscale is out of scope; downscale only.");
        var dst = new ImageBuffer(newW, newH);
        double sxScale = (double)src.Width / newW;
        double syScale = (double)src.Height / newH;

        for (int dy = 0; dy < newH; dy++)
        {
            double y0 = dy * syScale;
            double y1 = (dy + 1) * syScale;
            int iy0 = (int)Math.Floor(y0);
            int iy1 = (int)Math.Ceiling(y1);
            if (iy1 > src.Height) iy1 = src.Height;
            for (int dx = 0; dx < newW; dx++)
            {
                double x0 = dx * sxScale;
                double x1 = (dx + 1) * sxScale;
                int ix0 = (int)Math.Floor(x0);
                int ix1 = (int)Math.Ceiling(x1);
                if (ix1 > src.Width) ix1 = src.Width;

                double sumR = 0, sumG = 0, sumB = 0, sumW = 0;
                for (int sy = iy0; sy < iy1; sy++)
                {
                    double wy = Math.Min(sy + 1, y1) - Math.Max(sy, y0);
                    for (int sx = ix0; sx < ix1; sx++)
                    {
                        double wx = Math.Min(sx + 1, x1) - Math.Max(sx, x0);
                        double wt = wx * wy;
                        int si = sy * src.Width + sx;
                        sumR += src.R[si] * wt;
                        sumG += src.G[si] * wt;
                        sumB += src.B[si] * wt;
                        sumW += wt;
                    }
                }
                int di = dy * newW + dx;
                float inv = sumW > 0 ? (float)(1.0 / sumW) : 0f;
                dst.R[di] = (float)(sumR * inv);
                dst.G[di] = (float)(sumG * inv);
                dst.B[di] = (float)(sumB * inv);
            }
        }
        return dst;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Deblur.sln --filter FullyQualifiedName~AreaResampleTests`
Expected: 4 tests pass.

- [ ] **Step 5: Commit**

```bash
git add Deblur.Engine/Imaging/AreaResample.cs Deblur.Tests/AreaResampleTests.cs
git commit -m "Add area-average box downsample (replaces nearest-neighbor proxy)"
```

---

### Task 6: Validation harness stub (SyntheticBlur + Quality)

**Files:**
- Create: `Deblur.Engine/Validation/SyntheticBlur.cs`
- Create: `Deblur.Engine/Validation/Quality.cs`
- Test:   `Deblur.Tests/Validation/PsnrSsimTests.cs`

**Interfaces:**
- Produces:
  - `static class SyntheticBlur { public static ImageBuffer Apply(ImageBuffer src, float[,] psf, float gaussianNoiseSigma, int seed); }` — spatial-domain convolution with reflect boundary + additive Gaussian noise.
  - `static class Quality { public static double Psnr(ImageBuffer reference, ImageBuffer test); public static double Ssim(ImageBuffer reference, ImageBuffer test); }` — per-channel mean; SSIM uses 11×11 Gaussian window σ=1.5, K1=0.01, K2=0.03, L=1.0.

- [ ] **Step 1: Write failing tests**

```csharp
// Deblur.Tests/Validation/PsnrSsimTests.cs
using Deblur.Engine;
using Deblur.Engine.Validation;
using Xunit;

namespace Deblur.Tests.Validation;

public class PsnrSsimTests
{
    [Fact]
    public void Identical_PsnrIsInfiniteOrLarge()
    {
        var a = MakeGradient(32, 32);
        var b = a.Clone();
        double psnr = Quality.Psnr(a, b);
        Assert.True(double.IsPositiveInfinity(psnr) || psnr > 100);
    }

    [Fact]
    public void Identical_SsimEqualsOne()
    {
        var a = MakeGradient(32, 32);
        double ssim = Quality.Ssim(a, a.Clone());
        Assert.InRange(ssim, 0.999, 1.0001);
    }

    [Fact]
    public void ShiftedNoise_PsnrKnownRange()
    {
        var a = MakeGradient(32, 32);
        var b = a.Clone();
        for (int i = 0; i < b.PixelCount; i++)
        { b.R[i] += 0.01f; b.G[i] += 0.01f; b.B[i] += 0.01f; }
        double psnr = Quality.Psnr(a, b);
        // MSE = 0.0001 → PSNR = 10 log10(1/0.0001) = 40 dB
        Assert.InRange(psnr, 39.5, 40.5);
    }

    [Fact]
    public void SyntheticBlur_ReducesGradientEnergy()
    {
        var src = MakeGradient(64, 64);
        var psf = new float[5, 5];
        for (int y = 0; y < 5; y++)
            for (int x = 0; x < 5; x++)
                psf[y, x] = 1f / 25f;
        var blurred = SyntheticBlur.Apply(src, psf, gaussianNoiseSigma: 0f, seed: 1);
        double srcEnergy = GradientEnergy(src);
        double blurEnergy = GradientEnergy(blurred);
        Assert.True(blurEnergy < srcEnergy * 0.5, $"blur did not reduce gradient energy: {blurEnergy}/{srcEnergy}");
    }

    private static ImageBuffer MakeGradient(int w, int h)
    {
        var b = new ImageBuffer(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                float v = (float)x / (w - 1);
                b.R[i] = v; b.G[i] = v; b.B[i] = v;
            }
        return b;
    }

    private static double GradientEnergy(ImageBuffer b)
    {
        double e = 0;
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width - 1; x++)
            {
                int i = y * b.Width + x;
                double d = b.R[i + 1] - b.R[i];
                e += d * d;
            }
        return e;
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Deblur.sln --filter FullyQualifiedName~PsnrSsimTests`
Expected: FAIL.

- [ ] **Step 3: Implement stub**

```csharp
// Deblur.Engine/Validation/SyntheticBlur.cs
namespace Deblur.Engine.Validation;

public static class SyntheticBlur
{
    public static ImageBuffer Apply(ImageBuffer src, float[,] psf, float gaussianNoiseSigma, int seed)
    {
        int kh = psf.GetLength(0);
        int kw = psf.GetLength(1);
        int cy = kh / 2, cx = kw / 2;
        int w = src.Width, h = src.Height;
        var dst = new ImageBuffer(w, h);
        var rng = new Random(seed);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float r = 0, g = 0, b = 0;
                for (int ky = 0; ky < kh; ky++)
                {
                    int sy = ReflectIndex(y + ky - cy, h);
                    for (int kx = 0; kx < kw; kx++)
                    {
                        int sx = ReflectIndex(x + kx - cx, w);
                        float p = psf[ky, kx];
                        int si = sy * w + sx;
                        r += src.R[si] * p;
                        g += src.G[si] * p;
                        b += src.B[si] * p;
                    }
                }
                if (gaussianNoiseSigma > 0f)
                {
                    r += (float)(gaussianNoiseSigma * Gaussian(rng));
                    g += (float)(gaussianNoiseSigma * Gaussian(rng));
                    b += (float)(gaussianNoiseSigma * Gaussian(rng));
                }
                int di = y * w + x;
                dst.R[di] = Math.Clamp(r, 0f, 1f);
                dst.G[di] = Math.Clamp(g, 0f, 1f);
                dst.B[di] = Math.Clamp(b, 0f, 1f);
            }
        }
        return dst;
    }

    private static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
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

```csharp
// Deblur.Engine/Validation/Quality.cs
namespace Deblur.Engine.Validation;

public static class Quality
{
    public static double Psnr(ImageBuffer reference, ImageBuffer test)
    {
        if (reference.Width != test.Width || reference.Height != test.Height)
            throw new ArgumentException("size mismatch");
        double mse = (Mse(reference.R, test.R) + Mse(reference.G, test.G) + Mse(reference.B, test.B)) / 3.0;
        if (mse <= 0) return double.PositiveInfinity;
        return 10.0 * Math.Log10(1.0 / mse);
    }

    public static double Ssim(ImageBuffer reference, ImageBuffer test)
    {
        double sR = ChannelSsim(reference.R, test.R, reference.Width, reference.Height);
        double sG = ChannelSsim(reference.G, test.G, reference.Width, reference.Height);
        double sB = ChannelSsim(reference.B, test.B, reference.Width, reference.Height);
        return (sR + sG + sB) / 3.0;
    }

    private static double Mse(float[] a, float[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) { double d = a[i] - b[i]; s += d * d; }
        return s / a.Length;
    }

    private static double ChannelSsim(float[] a, float[] b, int w, int h)
    {
        // 11x11 Gaussian window, sigma=1.5.
        const int R = 5;
        var win = new double[11, 11];
        double wsum = 0;
        for (int j = -R; j <= R; j++)
            for (int i = -R; i <= R; i++)
            {
                double v = Math.Exp(-(i * i + j * j) / (2.0 * 1.5 * 1.5));
                win[j + R, i + R] = v; wsum += v;
            }
        for (int j = 0; j < 11; j++)
            for (int i = 0; i < 11; i++)
                win[j, i] /= wsum;

        const double K1 = 0.01, K2 = 0.03, L = 1.0;
        double C1 = (K1 * L) * (K1 * L);
        double C2 = (K2 * L) * (K2 * L);

        double total = 0; long count = 0;
        for (int y = R; y < h - R; y++)
        {
            for (int x = R; x < w - R; x++)
            {
                double muA = 0, muB = 0;
                for (int j = -R; j <= R; j++)
                    for (int i = -R; i <= R; i++)
                    {
                        double wij = win[j + R, i + R];
                        muA += wij * a[(y + j) * w + (x + i)];
                        muB += wij * b[(y + j) * w + (x + i)];
                    }
                double sA = 0, sB = 0, sAB = 0;
                for (int j = -R; j <= R; j++)
                    for (int i = -R; i <= R; i++)
                    {
                        double wij = win[j + R, i + R];
                        double dA = a[(y + j) * w + (x + i)] - muA;
                        double dB = b[(y + j) * w + (x + i)] - muB;
                        sA += wij * dA * dA;
                        sB += wij * dB * dB;
                        sAB += wij * dA * dB;
                    }
                double num = (2 * muA * muB + C1) * (2 * sAB + C2);
                double den = (muA * muA + muB * muB + C1) * (sA + sB + C2);
                total += num / den; count++;
            }
        }
        return count > 0 ? total / count : 1.0;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Deblur.sln --filter FullyQualifiedName~PsnrSsimTests`
Expected: 4 tests pass.

- [ ] **Step 5: Commit**

```bash
git add Deblur.Engine/Validation/SyntheticBlur.cs Deblur.Engine/Validation/Quality.cs Deblur.Tests/Validation/PsnrSsimTests.cs
git commit -m "Add validation harness stub: SyntheticBlur + Quality (PSNR/SSIM)"
```

---

### Task 7: Deconvolver signature update — thread PipelineOptions through

**Files:**
- Modify: `Deblur.Engine/IDeconvolver.cs`
- Modify: `Deblur.Engine/WienerDeconvolver.cs`
- Modify: `Deblur.Engine/TikhonovDeconvolver.cs`
- Modify: `Deblur.Engine/TotalVariationDeconvolver.cs`

**Interfaces:**
- Consumes: `PipelineOptions`, `BoundaryFill`, `EdgeTaper`.
- Produces: `IDeconvolver.Apply(ImageBuffer, float[,], DeconvolutionParams, PipelineOptions? options = null)`. Implementations do `var opt = options ?? PipelineOptions.Default;` and route padding through `BoundaryFill.Pad(...)` then `EdgeTaper.ApplyInPlace(padded, pad)` when `opt.EdgeTaper` is true. Existing inline reflect loops removed.

Behavioral note: existing tests were written against reflect padding with **no** edge taper. Turning EdgeTaper on by default may shift some PSNR numbers by up to ~0.5 dB. Where a test relies on a specific threshold and now fails, adjust with an inline comment `// linear-light baseline: <old>→<new>` (per Global Constraints). Do not chase thresholds by widening tolerances — record the shifted baseline.

- [ ] **Step 1: Update `IDeconvolver.cs`**

```csharp
// Deblur.Engine/IDeconvolver.cs
namespace Deblur.Engine;

public interface IDeconvolver
{
    ImageBuffer Apply(
        ImageBuffer input,
        float[,] psf,
        DeconvolutionParams p,
        PipelineOptions? options = null);
}
```

- [ ] **Step 2: Rewrite `WienerDeconvolver.ProcessChannel` to use helpers**

Replace the padding block in `ProcessChannel` (the `for (int y = 0; y < fftSize; ...)` reflect fill) with a call to `BoundaryFill.Pad(channel, w, h, pad, fftSize, opt.BoundaryMode)`, then `if (opt.EdgeTaper) EdgeTaper.ApplyInPlace(padded, pad);`. Change `Apply` to accept the new options parameter and pass `opt` through to `ProcessChannel`. Remove the private `ReflectIndex` helper.

Full replacement for `WienerDeconvolver.cs`:

```csharp
using System.Numerics;

namespace Deblur.Engine;

public sealed class WienerDeconvolver : IDeconvolver
{
    public ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p, PipelineOptions? options = null)
    {
        var opt = options ?? PipelineOptions.Default;
        int psfH = psf.GetLength(0);
        int psfW = psf.GetLength(1);
        int pad = Math.Max(psfW, psfH) / 2 + 1;

        int paddedW = input.Width + 2 * pad;
        int paddedH = input.Height + 2 * pad;
        int fftSize = FftAdapter.NextPow2(Math.Max(paddedW, paddedH));

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

        var wienerNumer = new Complex[fftSize, fftSize];
        for (int y = 0; y < fftSize; y++)
            for (int x = 0; x < fftSize; x++)
            {
                var h = H[y, x];
                double mag2 = h.Real * h.Real + h.Imaginary * h.Imaginary;
                wienerNumer[y, x] = Complex.Conjugate(h) / (mag2 + p.K);
            }

        float[] outR = ProcessChannel(input.R, input.Width, input.Height, pad, fftSize, wienerNumer, opt);
        float[] outG = ProcessChannel(input.G, input.Width, input.Height, pad, fftSize, wienerNumer, opt);
        float[] outB = ProcessChannel(input.B, input.Width, input.Height, pad, fftSize, wienerNumer, opt);
        return new ImageBuffer(input.Width, input.Height, outR, outG, outB);
    }

    private static float[] ProcessChannel(
        float[] channel, int w, int h, int pad, int fftSize, Complex[,] wienerNumer, PipelineOptions opt)
    {
        var padded = BoundaryFill.Pad(channel, w, h, pad, fftSize, opt.BoundaryMode);
        if (opt.EdgeTaper) EdgeTaper.ApplyInPlace(padded, pad);

        var G = FftAdapter.Forward2D(padded);
        var Fhat = new Complex[fftSize, fftSize];
        for (int y = 0; y < fftSize; y++)
            for (int x = 0; x < fftSize; x++)
                Fhat[y, x] = wienerNumer[y, x] * G[y, x];

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
}
```

- [ ] **Step 3: Apply the same shape to `TikhonovDeconvolver.cs`**

Replace inline reflect fill with `BoundaryFill.Pad` + optional `EdgeTaper`; remove `ReflectIndex`. Signature gains `PipelineOptions? options = null`. Precomputed `tikhonovNumer` stays as-is.

- [ ] **Step 4: Apply the same shape to `TotalVariationDeconvolver.cs`**

Its inner `WienerDeconvolver` call already receives the new signature default. Add `PipelineOptions? options = null` to `Apply` and forward through: `new WienerDeconvolver().Apply(input, psf, p, options)`. The Chambolle post-filter loop is unaffected.

- [ ] **Step 5: Run all engine tests**

Run: `dotnet test Deblur.sln`
Expected: all 64 pre-existing tests + new tests (from Tasks 1–6) pass. If any pre-existing PSNR threshold fails by ≤1 dB due to EdgeTaper default, adjust threshold with the inline `// linear-light baseline: <old>→<new>` comment. Report each such adjustment in the task report.

- [ ] **Step 6: Commit**

```bash
git add Deblur.Engine/IDeconvolver.cs Deblur.Engine/WienerDeconvolver.cs Deblur.Engine/TikhonovDeconvolver.cs Deblur.Engine/TotalVariationDeconvolver.cs Deblur.Tests/**/*.cs
git commit -m "Thread PipelineOptions through IDeconvolver; use BoundaryFill + EdgeTaper"
```

---

### Task 8: DeblurJobRunner — linear-light and luminance-only routing

**Files:**
- Modify: `Deblur.Engine/DeblurJobRunner.cs`

**Interfaces:**
- Consumes: `PipelineOptions`, `SrgbLinear`, `YCbCr`.
- Produces:
  - New constructor overload: `DeblurJobRunner(IReadOnlyDictionary<BlurType, IBlurKernel> kernels, IReadOnlyDictionary<AlgorithmType, IDeconvolver> deconvolvers, PipelineOptions options)`. The existing 2-argument constructor becomes a call to the new one with `PipelineOptions.Default`.
  - `RenderFullAsync` and `WorkerLoop` both:
    1. If `LinearLight`, decode the input's R/G/B in place from sRGB to linear (on a clone — we must not mutate the caller's buffer).
    2. If `LuminanceOnly`, extract Y via `YCbCr.FromRgb`, wrap in a single-plane `ImageBuffer` (R=G=B=Y), call `Apply(..., options)`, take the R channel of the result as the new Y, recompose with the original Cb/Cr.
    3. Otherwise call `Apply(deconvInput, psf, params, options)` directly.
    4. If `LinearLight`, re-encode the result R/G/B to sRGB before emitting BGRA or returning the `ImageBuffer`.
  - `Apply` receives `options` so per-deconvolver options are honored.

Note: the existing R=G=B pseudo-plane path may share the Cb/Cr with the un-deconvolved image. In luminance-only mode we must still apply linear-light to Y before deconvolution and re-encode after. Order: `sRGB → linear → YCbCr → deconvolve Y → recompose to linear RGB → sRGB`.

- [ ] **Step 1: Update `DeblurJobRunner` constructors and fields**

Add `private readonly PipelineOptions _options;`. Add a 3-arg ctor that stores it; keep the 2-arg ctor as a thin overload passing `PipelineOptions.Default`.

- [ ] **Step 2: Extract a helper `RunDeconvolve(ImageBuffer input, KernelParams p)` in the runner**

```csharp
private ImageBuffer RunDeconvolve(ImageBuffer input, KernelParams p)
{
    var psf = _kernels[p.Type].Build(p);
    var deconvIn = input;

    if (_options.LinearLight)
    {
        deconvIn = input.Clone();
        Deblur.Engine.Color.SrgbLinear.ToLinearInPlace(deconvIn.R);
        Deblur.Engine.Color.SrgbLinear.ToLinearInPlace(deconvIn.G);
        Deblur.Engine.Color.SrgbLinear.ToLinearInPlace(deconvIn.B);
    }

    ImageBuffer result;
    if (_options.LuminanceOnly)
    {
        var (y, cb, cr) = Deblur.Engine.Color.YCbCr.FromRgb(deconvIn.R, deconvIn.G, deconvIn.B);
        var yBuf = new ImageBuffer(deconvIn.Width, deconvIn.Height, y, (float[])y.Clone(), (float[])y.Clone());
        var deconvY = _deconvolvers[p.Algorithm].Apply(yBuf, psf, new DeconvolutionParams(K: p.Smoothness), _options);
        var (r, g, b) = Deblur.Engine.Color.YCbCr.ToRgb(deconvY.R, cb, cr);
        result = new ImageBuffer(deconvIn.Width, deconvIn.Height, r, g, b);
    }
    else
    {
        result = _deconvolvers[p.Algorithm].Apply(deconvIn, psf, new DeconvolutionParams(K: p.Smoothness), _options);
    }

    if (_options.LinearLight)
    {
        // Encode result back to sRGB; result may share arrays with deconvIn — clone to be safe.
        var enc = new ImageBuffer(result.Width, result.Height,
            (float[])result.R.Clone(), (float[])result.G.Clone(), (float[])result.B.Clone());
        Deblur.Engine.Color.SrgbLinear.ToSrgbInPlace(enc.R);
        Deblur.Engine.Color.SrgbLinear.ToSrgbInPlace(enc.G);
        Deblur.Engine.Color.SrgbLinear.ToSrgbInPlace(enc.B);
        result = enc;
    }
    return result;
}
```

- [ ] **Step 3: Wire `WorkerLoop` and `RenderFullAsync` through the helper**

In `WorkerLoop`, replace the `if (IsNoOp(p)) deconv = proxy; else { var psf = ...; deconv = _deconvolvers[...].Apply(...); }` block with:

```csharp
ImageBuffer deconv = IsNoOp(p) ? proxy : RunDeconvolve(proxy, p);
```

In `RenderFullAsync`, replace the corresponding block similarly, keeping the `progress?.Report(...)` calls and `ThrowIfCancellationRequested()` interspersed.

- [ ] **Step 4: Run all tests**

Run: `dotnet test Deblur.sln`
Expected: all pass. The runner tests exercise the full pipeline through `PipelineOptions.Default` (linear-light on). If any DeblurJobRunnerTests PSNR floor shifts, adjust the threshold with the standard comment.

- [ ] **Step 5: Commit**

```bash
git add Deblur.Engine/DeblurJobRunner.cs Deblur.Tests/**/*.cs
git commit -m "DeblurJobRunner: linear-light decode/encode + luminance-only routing"
```

---

### Task 9: WIC codec + IImageCodec + ImageBuffer.SourceBitDepth

**Files:**
- Create: `Deblur.Engine/IImageCodec.cs`
- Modify: `Deblur.Engine/ImageBuffer.cs`
- Modify: `Deblur.Engine/ImageCodec.cs` — add a thin instance wrapper class `Gdi8BitImageCodec : IImageCodec` alongside the existing static (no static API break).
- Create: `Deblur/Services/WicImageCodec.cs`
- Test:   `Deblur.Tests/WicImageCodecTests.cs` — 8-bit round-trip only (WIC codec lives in WPF layer; the test project is net8.0 headless and cannot reference PresentationCore directly). See Testing note below.
- Test:   `Deblur.Tests/GdiImageCodecInterfaceTests.cs` — verifies the wrapper `Gdi8BitImageCodec` implements `IImageCodec` correctly.

**Testing note on WIC:** WIC's `BitmapDecoder` runs on `net8.0-windows` with `UseWPF`. Since `Deblur.Tests` is `net8.0` headless, the WIC integration test lives in a **new** test project `Deblur.Wpf.Tests` (`net8.0-windows`, `UseWPF=true`). Add it to the solution. Keep it minimal — one class, three tests.

**Interfaces:**
- Produces:
  - `interface IImageCodec { (ImageBuffer image, BitDepth depth) Decode(byte[] bytes); byte[] EncodePng(ImageBuffer image, BitDepth depth); byte[] EncodeJpeg(ImageBuffer image, int quality); }`
  - `ImageBuffer.SourceBitDepth { get; init; }` (default `BitDepth.Eight`).
  - `Gdi8BitImageCodec : IImageCodec` — thin wrapper over existing static `ImageCodec` (always `BitDepth.Eight`).
  - `Deblur.Services.WicImageCodec : IImageCodec` — handles 8 and 16 bpc via WIC; PNG encoding honors `depth`.

- [ ] **Step 1: Add `IImageCodec.cs` and extend `ImageBuffer.cs`**

```csharp
// Deblur.Engine/IImageCodec.cs
namespace Deblur.Engine;

public interface IImageCodec
{
    (ImageBuffer image, BitDepth depth) Decode(byte[] bytes);
    byte[] EncodePng(ImageBuffer image, BitDepth depth);
    byte[] EncodeJpeg(ImageBuffer image, int quality);
}
```

In `ImageBuffer.cs`, add:

```csharp
public BitDepth SourceBitDepth { get; set; } = BitDepth.Eight;
```

(Public setter, not `init`, because `WicImageCodec` in the `Deblur` assembly needs to mutate it after decoding — `init` only accepts object-initializer syntax, and the WIC path constructs then populates.) Make sure `Clone()` preserves it:

```csharp
public ImageBuffer Clone()
{
    return new ImageBuffer(
        Width, Height,
        (float[])R.Clone(),
        (float[])G.Clone(),
        (float[])B.Clone())
    { SourceBitDepth = this.SourceBitDepth };
}
```

- [ ] **Step 2: Add `Gdi8BitImageCodec` wrapper and unit test**

Wrapper (place in `ImageCodec.cs` alongside the existing static):

```csharp
public sealed class Gdi8BitImageCodec : IImageCodec
{
    public (ImageBuffer image, BitDepth depth) Decode(byte[] bytes)
        => (ImageCodec.DecodeFromBytes(bytes) with { SourceBitDepth = BitDepth.Eight }, BitDepth.Eight);

    public byte[] EncodePng(ImageBuffer image, BitDepth depth) => ImageCodec.EncodePng(image);

    public byte[] EncodeJpeg(ImageBuffer image, int quality) => ImageCodec.EncodeJpeg(image, quality);
}
```

With the public setter added in Step 1, this simplifies to:

```csharp
var img = ImageCodec.DecodeFromBytes(bytes);
img.SourceBitDepth = BitDepth.Eight;
return (img, BitDepth.Eight);
```

Test file:

```csharp
// Deblur.Tests/GdiImageCodecInterfaceTests.cs
using Deblur.Engine;
using System.IO;
using Xunit;

namespace Deblur.Tests;

public class GdiImageCodecInterfaceTests
{
    [Fact]
    public void RoundTrip_Png_PreservesBytes()
    {
        var codec = new Gdi8BitImageCodec();
        // Build a tiny 4x4 image via existing helpers, encode, decode, compare.
        var src = new ImageBuffer(4, 4);
        for (int i = 0; i < src.PixelCount; i++)
        { src.R[i] = (i % 4) / 3f; src.G[i] = ((i / 4) % 4) / 3f; src.B[i] = 0.5f; }
        var bytes = codec.EncodePng(src, BitDepth.Eight);
        var (rt, depth) = codec.Decode(bytes);
        Assert.Equal(BitDepth.Eight, depth);
        Assert.Equal(src.Width, rt.Width);
        Assert.Equal(src.Height, rt.Height);
        for (int i = 0; i < src.PixelCount; i++)
        {
            Assert.InRange(Math.Abs(rt.R[i] - src.R[i]), 0f, 1f / 255f);
            Assert.InRange(Math.Abs(rt.G[i] - src.G[i]), 0f, 1f / 255f);
            Assert.InRange(Math.Abs(rt.B[i] - src.B[i]), 0f, 1f / 255f);
        }
    }
}
```

- [ ] **Step 3: Create `Deblur.Wpf.Tests` project and add to solution**

```bash
dotnet new xunit -n Deblur.Wpf.Tests -o Deblur.Wpf.Tests -f net8.0-windows
dotnet sln Deblur.sln add Deblur.Wpf.Tests/Deblur.Wpf.Tests.csproj
dotnet add Deblur.Wpf.Tests/Deblur.Wpf.Tests.csproj reference Deblur.Engine/Deblur.Engine.csproj Deblur/Deblur.csproj
```

Edit `Deblur.Wpf.Tests/Deblur.Wpf.Tests.csproj` to add `<UseWPF>true</UseWPF>` under the main `<PropertyGroup>`.

- [ ] **Step 4: Implement `WicImageCodec` in `Deblur/Services/WicImageCodec.cs`**

```csharp
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Deblur.Engine;

namespace Deblur.Services;

public sealed class WicImageCodec : IImageCodec
{
    public (ImageBuffer image, BitDepth depth) Decode(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var srcFmt = frame.Format;
        bool is16 = srcFmt == PixelFormats.Rgb48 || srcFmt == PixelFormats.Rgba64 || srcFmt == PixelFormats.Gray16;

        int w = frame.PixelWidth, h = frame.PixelHeight;
        var img = new ImageBuffer(w, h);
        img.SourceBitDepth = is16 ? BitDepth.Sixteen : BitDepth.Eight;

        if (is16)
        {
            var conv = new FormatConvertedBitmap(frame, PixelFormats.Rgb48, null, 0);
            int stride = w * 6;
            var pixels = new ushort[w * h * 3];
            conv.CopyPixels(pixels, stride, 0);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int p = (y * w + x) * 3;
                    int di = y * w + x;
                    img.R[di] = pixels[p]     / 65535f;
                    img.G[di] = pixels[p + 1] / 65535f;
                    img.B[di] = pixels[p + 2] / 65535f;
                }
        }
        else
        {
            var conv = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int stride = w * 4;
            var pixels = new byte[w * h * 4];
            conv.CopyPixels(pixels, stride, 0);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int p = (y * w + x) * 4;
                    int di = y * w + x;
                    img.B[di] = pixels[p]     / 255f;
                    img.G[di] = pixels[p + 1] / 255f;
                    img.R[di] = pixels[p + 2] / 255f;
                }
        }
        return (img, img.SourceBitDepth);
    }

    public byte[] EncodePng(ImageBuffer image, BitDepth depth)
    {
        var bitmap = depth == BitDepth.Sixteen ? To48bpp(image) : To32bpp(image);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    public byte[] EncodeJpeg(ImageBuffer image, int quality)
    {
        if (quality < 1 || quality > 100) throw new ArgumentOutOfRangeException(nameof(quality));
        var bitmap = To32bpp(image);
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static BitmapSource To32bpp(ImageBuffer image)
    {
        int w = image.Width, h = image.Height;
        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                int p = i * 4;
                pixels[p]     = Clamp8(image.B[i]);
                pixels[p + 1] = Clamp8(image.G[i]);
                pixels[p + 2] = Clamp8(image.R[i]);
                pixels[p + 3] = 255;
            }
        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
    }

    private static BitmapSource To48bpp(ImageBuffer image)
    {
        int w = image.Width, h = image.Height;
        var pixels = new ushort[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                int p = i * 3;
                pixels[p]     = Clamp16(image.R[i]);
                pixels[p + 1] = Clamp16(image.G[i]);
                pixels[p + 2] = Clamp16(image.B[i]);
            }
        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Rgb48, null, pixels, w * 6);
    }

    private static byte Clamp8(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
    private static ushort Clamp16(float v) => (ushort)Math.Clamp((int)MathF.Round(v * 65535f), 0, 65535);
}
```

- [ ] **Step 5: Add WIC tests to `Deblur.Wpf.Tests`**

```csharp
// Deblur.Wpf.Tests/WicImageCodecTests.cs
using Deblur.Engine;
using Deblur.Services;
using Xunit;

namespace Deblur.Wpf.Tests;

public class WicImageCodecTests
{
    [Fact]
    public void EightBitPng_RoundTrip_WithinLsb()
    {
        var codec = new WicImageCodec();
        var src = new ImageBuffer(8, 8);
        for (int i = 0; i < src.PixelCount; i++) { src.R[i] = 0.2f; src.G[i] = 0.5f; src.B[i] = 0.8f; }
        var bytes = codec.EncodePng(src, BitDepth.Eight);
        var (rt, depth) = codec.Decode(bytes);
        Assert.Equal(BitDepth.Eight, depth);
        for (int i = 0; i < src.PixelCount; i++)
        {
            Assert.InRange(Math.Abs(rt.R[i] - src.R[i]), 0f, 1f / 255f);
            Assert.InRange(Math.Abs(rt.G[i] - src.G[i]), 0f, 1f / 255f);
            Assert.InRange(Math.Abs(rt.B[i] - src.B[i]), 0f, 1f / 255f);
        }
    }

    [Fact]
    public void SixteenBitPng_RoundTrip_WithinLsb()
    {
        var codec = new WicImageCodec();
        var src = new ImageBuffer(8, 8);
        for (int i = 0; i < src.PixelCount; i++) { src.R[i] = 0.20003f; src.G[i] = 0.50007f; src.B[i] = 0.80005f; }
        var bytes = codec.EncodePng(src, BitDepth.Sixteen);
        var (rt, depth) = codec.Decode(bytes);
        Assert.Equal(BitDepth.Sixteen, depth);
        for (int i = 0; i < src.PixelCount; i++)
        {
            Assert.InRange(Math.Abs(rt.R[i] - src.R[i]), 0f, 1f / 65535f);
            Assert.InRange(Math.Abs(rt.G[i] - src.G[i]), 0f, 1f / 65535f);
            Assert.InRange(Math.Abs(rt.B[i] - src.B[i]), 0f, 1f / 65535f);
        }
    }

    [Fact]
    public void UnknownFormat_Throws()
    {
        var codec = new WicImageCodec();
        Assert.ThrowsAny<Exception>(() => codec.Decode(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 }));
    }
}
```

- [ ] **Step 6: Run all tests**

Run: `dotnet test Deblur.sln`
Expected: all previous plus new WIC + interface tests pass.

- [ ] **Step 7: Commit**

```bash
git add Deblur.Engine/IImageCodec.cs Deblur.Engine/ImageBuffer.cs Deblur.Engine/ImageCodec.cs Deblur/Services/WicImageCodec.cs Deblur.Tests/GdiImageCodecInterfaceTests.cs Deblur.Wpf.Tests Deblur.sln
git commit -m "Add IImageCodec, WIC-backed 8/16-bit codec, and Deblur.Wpf.Tests project"
```

---

### Task 10: MainViewModel integration

**Files:**
- Modify: `Deblur/ViewModels/MainViewModel.cs`

**Interfaces:**
- Consumes: `PipelineOptions.Default`, `WicImageCodec`, `Gdi8BitImageCodec` (fallback), `AreaResample`.

- [ ] **Step 1: Update `MainViewModel`**

- Add `private readonly IImageCodec _codec = new WicImageCodec();` and a fallback `Gdi8BitImageCodec` used only if WIC throws on decode.
- Change `DeblurJobRunner` construction to `new DeblurJobRunner(kernels, deconvolvers, PipelineOptions.Default)`.
- `LoadImageFromBytes` uses `_codec.Decode(bytes)`; falls back to `Gdi8BitImageCodec` on `Exception` (WIC's failure modes are varied).
- Remove `MainViewModel.Downscale` and replace call site with `AreaResample.Box(full, pw, ph)`.
- Where the current save flow calls `ImageCodec.EncodePng/EncodeJpeg`, route through `_codec.EncodePng(buf, buf.SourceBitDepth)` / `_codec.EncodeJpeg(buf, quality)`.

- [ ] **Step 2: Build and run existing unit tests**

Run: `dotnet build Deblur.sln` then `dotnet test Deblur.sln`.
Expected: 0 errors; all tests pass. `MainViewModel` is not directly test-covered — verify indirectly via the runner tests staying green.

- [ ] **Step 3: Commit**

```bash
git add Deblur/ViewModels/MainViewModel.cs
git commit -m "MainViewModel: adopt PipelineOptions.Default, WicImageCodec, AreaResample"
```

---

### Task 11: Linear-light gain validation test

**Files:**
- Create: `Deblur.Tests/Validation/LinearLightGainTests.cs`

**Interfaces:**
- Consumes: `SyntheticBlur`, `Quality`, `MotionBlurKernel`, `WienerDeconvolver`, `TikhonovDeconvolver`, `TotalVariationDeconvolver`, `PipelineOptions`, `KernelParams`, `DeconvolutionParams`.

- [ ] **Step 1: Write the test**

```csharp
// Deblur.Tests/Validation/LinearLightGainTests.cs
using System.Globalization;
using System.IO;
using System.Text;
using Deblur.Engine;
using Deblur.Engine.Validation;
using Xunit;

namespace Deblur.Tests.Validation;

public class LinearLightGainTests
{
    private static readonly (string name, Func<int, ImageBuffer> make)[] TestImages =
    {
        ("checkerboard", n => TestHelpers.SyntheticImages.Checkerboard(n, n, tile: 16)),
        ("gradient",     n => MakeGradient(n, n)),
        ("stepedge",     n => MakeStepEdge(n, n)),
    };

    [Fact]
    public void LinearLightOn_ImprovesMeanWienerPsnr_ByAtLeast_1dB_NoiseFree()
    {
        var (mean_on, mean_off, rows) = SweepMean(algorithm: AlgorithmType.Wiener, noiseSigma: 0f);
        WriteCsv(rows, "linear-light-gain");
        Assert.True(mean_on > mean_off + 1.0,
            $"Wiener linear-light gain {mean_on - mean_off:F2} dB (< 1.0 dB threshold)");
    }

    [Fact]
    public void LinearLightOn_DoesNotRegress_UnderNoise()
    {
        foreach (var alg in new[] { AlgorithmType.Wiener, AlgorithmType.Tikhonov, AlgorithmType.TotalVariation })
        {
            var (mean_on, mean_off, _) = SweepMean(alg, noiseSigma: 0.01f); // ~40 dB SNR
            Assert.True(mean_on >= mean_off - 0.25,
                $"{alg} regressed under noise: on={mean_on:F2} off={mean_off:F2}");
        }
    }

    private static (double meanOn, double meanOff, List<string[]> rows) SweepMean(AlgorithmType algorithm, float noiseSigma)
    {
        var kernelBuilder = new MotionBlurKernel();
        IDeconvolver deconv = algorithm switch
        {
            AlgorithmType.Wiener => new WienerDeconvolver(),
            AlgorithmType.Tikhonov => new TikhonovDeconvolver(),
            AlgorithmType.TotalVariation => new TotalVariationDeconvolver(),
            _ => throw new ArgumentOutOfRangeException(),
        };

        var rows = new List<string[]>();
        rows.Add(new[] { "image", "algorithm", "noiseSigma", "linearLight", "psnr", "ssim" });

        double sumOn = 0, sumOff = 0; int nOn = 0, nOff = 0;
        foreach (var (name, make) in TestImages)
        {
            var gt = make(128);
            var psf = kernelBuilder.Build(new KernelParams(BlurType.Motion, 30f, 12f, 0f, 0f, 0f, algorithm));
            var blurred = SyntheticBlur.Apply(gt, psf, noiseSigma, seed: 42);

            foreach (bool linear in new[] { true, false })
            {
                var opts = PipelineOptions.Default with { LinearLight = linear };
                var input = blurred;
                if (linear)
                {
                    input = blurred.Clone();
                    Deblur.Engine.Color.SrgbLinear.ToLinearInPlace(input.R);
                    Deblur.Engine.Color.SrgbLinear.ToLinearInPlace(input.G);
                    Deblur.Engine.Color.SrgbLinear.ToLinearInPlace(input.B);
                }
                var recovered = deconv.Apply(input, psf, new DeconvolutionParams(K: 0.005f), opts);
                if (linear)
                {
                    var enc = recovered.Clone();
                    Deblur.Engine.Color.SrgbLinear.ToSrgbInPlace(enc.R);
                    Deblur.Engine.Color.SrgbLinear.ToSrgbInPlace(enc.G);
                    Deblur.Engine.Color.SrgbLinear.ToSrgbInPlace(enc.B);
                    recovered = enc;
                }
                double psnr = Quality.Psnr(gt, recovered);
                double ssim = Quality.Ssim(gt, recovered);
                rows.Add(new[] { name, algorithm.ToString(), noiseSigma.ToString(CultureInfo.InvariantCulture),
                    linear.ToString(), psnr.ToString("F3", CultureInfo.InvariantCulture),
                    ssim.ToString("F4", CultureInfo.InvariantCulture) });
                if (linear) { sumOn += psnr; nOn++; } else { sumOff += psnr; nOff++; }
            }
        }
        return (sumOn / nOn, sumOff / nOff, rows);
    }

    private static void WriteCsv(List<string[]> rows, string label)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "validation-reports");
        Directory.CreateDirectory(dir);
        var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        var path = Path.Combine(dir, $"{label}-{stamp}.csv");
        var sb = new StringBuilder();
        foreach (var row in rows) sb.AppendLine(string.Join(",", row));
        File.WriteAllText(path, sb.ToString());
    }

    private static ImageBuffer MakeGradient(int w, int h)
    {
        var b = new ImageBuffer(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                float v = (float)x / (w - 1);
                b.R[i] = v; b.G[i] = v; b.B[i] = v;
            }
        return b;
    }

    private static ImageBuffer MakeStepEdge(int w, int h)
    {
        var b = new ImageBuffer(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                float v = x < w / 2 ? 0.15f : 0.85f;
                b.R[i] = v; b.G[i] = v; b.B[i] = v;
            }
        return b;
    }
}
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test Deblur.sln --filter FullyQualifiedName~LinearLightGainTests`
Expected: 2 tests pass; CSV written to `Deblur.Tests/bin/{Config}/net8.0/validation-reports/`.

If the 1.0 dB threshold does not hold on the noise-free Wiener sweep, this is a signal — the linear-light path is not delivering the expected gain and needs investigation (do NOT relax the threshold silently; escalate).

- [ ] **Step 3: Commit**

```bash
git add Deblur.Tests/Validation/LinearLightGainTests.cs
git commit -m "Add linear-light gain validation test (asserts >=1 dB Wiener gain, writes CSV)"
```

---

### Task 12: Manual smoke and tag

- [ ] **Step 1: Build in Debug and launch**

Run: `dotnet build Deblur.sln` then `dotnet run --project Deblur/Deblur.csproj --no-build`

- [ ] **Step 2: Manual smoke test**

- Open a standard 8-bit JPEG — app behaves as before to the naked eye.
- Open a 16-bit PNG (a small test asset — check into `docs/assets/16bit-sample.png` if not already present).
- Apply Motion (length 12, angle 30), each of Wiener / Tikhonov / TV — nothing crashes; results look reasonable.
- Save-As PNG on a 16-bit source → resulting file is 16-bit (verify by re-opening: `IImageCodec.Decode` returns `BitDepth.Sixteen`, or via `identify -verbose file.png` if ImageMagick handy).
- Save-As JPEG — remains 8-bit (JPEG has no 16-bit option; expected).
- Zoom in on a boundary edge in the preview — no visible pronounced ringing along the image border.
- Preview and full-res render sharpness match (preview is not visibly softer than the exported file).

Report smoke results in the ledger.

- [ ] **Step 3: Tag and update the progress ledger**

```bash
git tag phase1a
echo "phase1a: complete" >> .superpowers/sdd/progress.md
```

- [ ] **Step 4: Invoke `superpowers:finishing-a-development-branch`**

Present the standard four options and wait for the user's choice.
