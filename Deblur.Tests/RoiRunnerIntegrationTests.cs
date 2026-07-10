using System.Collections.Concurrent;
using Deblur.Engine;
using Deblur.Tests.TestHelpers;
using Xunit;

namespace Deblur.Tests;

public class RoiRunnerIntegrationTests
{
    private sealed class EchoStubDeconvolver : IDeconvolver
    {
        public AlgorithmMetadata Metadata { get; } = new(
            Id: "stub", Version: "0", DisplayName: "Stub",
            DescriptionMarkdown: "test-only stub", LiteratureCitation: "n/a");
        public ImageBuffer Apply(ImageBuffer input, float[,] psf, DeconvolutionParams p, PipelineOptions? options = null)
            => new ImageBuffer(input.Width, input.Height,
                (float[])input.R.Clone(), (float[])input.G.Clone(), (float[])input.B.Clone());
    }

    private sealed class RecordingStubKernel : IBlurKernel
    {
        public readonly ConcurrentBag<KernelParams> Seen = new();
        public float[,] Build(KernelParams p) { Seen.Add(p); return new float[1, 1] { { 1f } }; }
    }

    [Fact]
    public async Task RenderFullAsync_RoiNull_MatchesPhase1aWholeImageBehavior()
    {
        var deconv = new EchoStubDeconvolver();
        var kernel = new RecordingStubKernel();
        var kernels = new Dictionary<BlurType, IBlurKernel> { [BlurType.Motion] = kernel };
        var deconvolvers = new Dictionary<AlgorithmType, IDeconvolver>
        {
            [AlgorithmType.Wiener]   = deconv,
            [AlgorithmType.Tikhonov] = deconv,
        };
        using var runner = new DeblurJobRunner(kernels, deconvolvers);
        Assert.Null(runner.Roi);

        var full = SyntheticImages.Checkerboard(64, 64, 8);
        var result = await runner.RenderFullAsync(
            full,
            new KernelParams(BlurType.Motion, 45f, 10f, 0.005f, 0f, 0f, AlgorithmType.Wiener),
            proxyScale: 1f);
        Assert.Equal(full.Width, result.Width);
        Assert.Equal(full.Height, result.Height);
    }

    [Fact]
    public async Task RenderFullAsync_WithRoi_RoutesThroughRoiProcessorAndPreservesBitDepth()
    {
        var deconv = new EchoStubDeconvolver();
        var kernel = new RecordingStubKernel();
        var kernels = new Dictionary<BlurType, IBlurKernel> { [BlurType.Motion] = kernel };
        var deconvolvers = new Dictionary<AlgorithmType, IDeconvolver>
        {
            [AlgorithmType.Wiener]   = deconv,
            [AlgorithmType.Tikhonov] = deconv,
        };
        using var runner = new DeblurJobRunner(kernels, deconvolvers);

        var full = SyntheticImages.Checkerboard(64, 64, 8);
        full.SourceBitDepth = BitDepth.Sixteen;
        runner.Roi = new RegionOfInterest(X: 16, Y: 16, Width: 24, Height: 24, FeatherRadius: 4);

        var result = await runner.RenderFullAsync(
            full,
            new KernelParams(BlurType.Motion, 45f, 10f, 0.005f, 0f, 0f, AlgorithmType.Wiener),
            proxyScale: 1f);

        Assert.Equal(BitDepth.Sixteen, result.SourceBitDepth);

        // Outside the ROI, pixels should be byte-identical to input (echo stub means
        // even inside the ROI the values match, so we just check the invariant holds).
        int outIdx = 5 * full.Width + 5;
        Assert.Equal(full.R[outIdx], result.R[outIdx]);
    }
}
