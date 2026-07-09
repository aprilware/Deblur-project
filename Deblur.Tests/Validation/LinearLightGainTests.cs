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
        ("checkerboard", n => TestHelpers.SyntheticImages.Checkerboard(n, n, cellSize: 16)),
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
