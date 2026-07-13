using Deblur.Engine;
using Deblur.Engine.Estimation;
using Xunit;

namespace Deblur.Tests.Estimation;

public class EstimateRecordTests
{
    [Fact]
    public void SuggestionRecord_DefaultAcceptedAndDismissed_AreNull()
    {
        var r = new SuggestionRecord("test", "1.0", 42, 0.5f, DateTime.UtcNow);
        Assert.Null(r.AcceptedAtUtc);
        Assert.Null(r.DismissedAtUtc);
    }

    [Fact]
    public void SuggestionRecord_WithAccepted_SetsAcceptedOnly()
    {
        var suggested = DateTime.UtcNow;
        var accepted = suggested.AddSeconds(5);
        var r = new SuggestionRecord("test", "1.0", 42, 0.5f, suggested)
            with { AcceptedAtUtc = accepted };
        Assert.Equal(accepted, r.AcceptedAtUtc);
        Assert.Null(r.DismissedAtUtc);
    }

    [Fact]
    public void DeconvolutionParams_NoiseVariance_DefaultsToNull()
    {
        var p = new DeconvolutionParams(K: 0.005f);
        Assert.Null(p.NoiseVariance);
    }

    [Fact]
    public void DeconvolutionParams_NoiseVariance_RoundTripsWhenSet()
    {
        var p = new DeconvolutionParams(K: 0.005f, NoiseVariance: 0.0001f);
        Assert.Equal(0.0001f, p.NoiseVariance);
    }
}
