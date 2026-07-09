using Deblur.Engine;
using Deblur.Engine.Validation;
using Deblur.Tests.TestHelpers;
using Xunit;

namespace Deblur.Tests;

public class RoiProcessorTests
{
    [Fact]
    public void OutsideRoi_IsByteIdentical_ToInput()
    {
        var src = SyntheticImages.Checkerboard(128, 128, 16);
        var roi = new RegionOfInterest(30, 30, 40, 40, FeatherRadius: 0);
        var result = RoiProcessor.ApplyToRoi(src, roi, psfRadius: 5,
            deconvolve: extract => Fill(extract, 0.5f));

        for (int y = 0; y < src.Height; y++)
        {
            for (int x = 0; x < src.Width; x++)
            {
                if (roi.Contains(x, y)) continue;
                int i = y * src.Width + x;
                Assert.Equal(src.R[i], result.R[i]);
                Assert.Equal(src.G[i], result.G[i]);
                Assert.Equal(src.B[i], result.B[i]);
            }
        }
    }

    [Fact]
    public void HardReplace_FeatherZero_UsesDeconvValueInside()
    {
        var src = SyntheticImages.Checkerboard(64, 64, 8);
        var roi = new RegionOfInterest(10, 10, 20, 20, FeatherRadius: 0);
        var result = RoiProcessor.ApplyToRoi(src, roi, psfRadius: 2,
            deconvolve: extract => Fill(extract, 0.42f));

        int inside = 15 * result.Width + 15;
        Assert.InRange(Math.Abs(result.R[inside] - 0.42f), 0f, 1e-5f);
    }

    [Fact]
    public void RoiEquivalence_CoreMatchesFullImageDeconvolution()
    {
        // Wiener on a 128x128 checkerboard, ROI 40x40 in the center with feather 8.
        // The un-feathered core (24x24 interior) after ROI processing must match
        // a full-image Wiener recovery of the same input inside that same core.
        var gt = SyntheticImages.Checkerboard(128, 128, 16);
        var kernel = new MotionBlurKernel().Build(
            new KernelParams(BlurType.Motion, 30f, 10f, 0f, 0f, 0f, AlgorithmType.Wiener));
        var blurred = SyntheticBlur.Apply(gt, kernel, gaussianNoiseSigma: 0f, seed: 42);

        var deconvolver = new WienerDeconvolver();
        var opts = PipelineOptions.Default with { LinearLight = false, EdgeTaper = false };
        var fullDeconv = deconvolver.Apply(blurred, kernel,
            new DeconvolutionParams(K: 0.005f), opts);

        var roi = new RegionOfInterest(X: 44, Y: 44, Width: 40, Height: 40, FeatherRadius: 8);
        var roiResult = RoiProcessor.ApplyToRoi(blurred, roi, psfRadius: 5,
            deconvolve: extract => deconvolver.Apply(extract, kernel,
                new DeconvolutionParams(K: 0.005f), opts));

        // Compare pixels inside the un-feathered core (feather=8 pixels inset from ROI edge).
        double sumSq = 0; int count = 0;
        for (int y = roi.Y + roi.FeatherRadius; y < roi.Y + roi.Height - roi.FeatherRadius; y++)
        {
            for (int x = roi.X + roi.FeatherRadius; x < roi.X + roi.Width - roi.FeatherRadius; x++)
            {
                int i = y * gt.Width + x;
                double dr = fullDeconv.R[i] - roiResult.R[i];
                double dg = fullDeconv.G[i] - roiResult.G[i];
                double db = fullDeconv.B[i] - roiResult.B[i];
                sumSq += (dr * dr + dg * dg + db * db) / 3.0;
                count++;
            }
        }
        double mse = sumSq / count;
        double psnr = mse <= 0 ? double.PositiveInfinity : 10.0 * Math.Log10(1.0 / mse);
        // 25 dB is a substantive-equivalence threshold. Exact match is unrealistic:
        // "Wiener on 56x56 extract" and "Wiener on 128x128 whole" use different FFT
        // sizes and see different boundary reflections at the padded FFT canvas edge.
        // Deep interiors converge, so 25 dB comfortably proves the ROI core is doing
        // the same recovery as the full-image path.
        Assert.True(psnr > 25.0, $"ROI core diverges from full-image deconv: PSNR {psnr:F2} dB");
    }

    [Fact]
    public void SourceBitDepth_Preserved()
    {
        var src = SyntheticImages.Checkerboard(64, 64, 8);
        src.SourceBitDepth = BitDepth.Sixteen;
        var roi = new RegionOfInterest(20, 20, 20, 20, FeatherRadius: 4);
        var result = RoiProcessor.ApplyToRoi(src, roi, psfRadius: 3,
            deconvolve: extract => extract.Clone());
        Assert.Equal(BitDepth.Sixteen, result.SourceBitDepth);
    }

    private static ImageBuffer Fill(ImageBuffer template, float v)
    {
        var b = new ImageBuffer(template.Width, template.Height);
        for (int i = 0; i < b.PixelCount; i++) { b.R[i] = v; b.G[i] = v; b.B[i] = v; }
        return b;
    }
}
