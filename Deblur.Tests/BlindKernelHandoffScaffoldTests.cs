using Deblur.Engine;
using Deblur.Engine.Estimation;
using Xunit;

namespace Deblur.Tests;

public class BlindKernelHandoffScaffoldTests
{
    [Fact]
    public void BlurTypeCustom_Exists() => Assert.Equal(3, (int)BlurType.Custom);

    [Fact]
    public void KernelParams_KernelId_DefaultsToNull()
    {
        var p = new KernelParams(BlurType.Motion, 0f, 0f, 0f, 0f, 0f, AlgorithmType.Wiener);
        Assert.Null(p.KernelId);
    }

    [Fact]
    public void KernelParams_DifferentKernelIds_AreNotEqual()
    {
        var p1 = new KernelParams(BlurType.Custom, 0f, 0f, 0f, 0f, 0f, AlgorithmType.Wiener, KernelId: 1);
        var p2 = p1 with { KernelId = 2 };
        Assert.NotEqual(p1, p2);
    }

    [Fact]
    public void SuggestionRecord_Confidence_AcceptsNull()
    {
        var r = new SuggestionRecord("x", "1.0", 42, null, System.DateTime.UtcNow);
        Assert.Null(r.Confidence);
    }

    [Fact]
    public void BlindDeconvolutionDeconvolver_MetadataConsts_MatchProperty()
    {
        var d = new BlindDeconvolutionDeconvolver();
        Assert.Equal(BlindDeconvolutionDeconvolver.MetadataId, d.Metadata.Id);
        Assert.Equal(BlindDeconvolutionDeconvolver.MetadataVersion, d.Metadata.Version);
    }
}
