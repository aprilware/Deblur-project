using Deblur.Engine;
using Xunit;

namespace Deblur.Tests;

public class AlgorithmMetadataTests
{
    [Fact]
    public void EveryProductionDeconvolver_HasCompleteMetadata()
    {
        IDeconvolver[] deconvolvers =
        {
            new WienerDeconvolver(),
            new TikhonovDeconvolver(),
            new TotalVariationDeconvolver(),
            new RichardsonLucyDeconvolver(),
            new ConstrainedLeastSquaresDeconvolver(),
            new LandweberDeconvolver(),
        };
        foreach (var d in deconvolvers)
        {
            var m = d.Metadata;
            Assert.False(string.IsNullOrWhiteSpace(m.Id));
            Assert.False(string.IsNullOrWhiteSpace(m.Version));
            Assert.False(string.IsNullOrWhiteSpace(m.DisplayName));
            Assert.True(m.DescriptionMarkdown.Length > 100,
                $"{m.Id} description too short: {m.DescriptionMarkdown.Length} chars");
            Assert.True(m.LiteratureCitation.Length > 20,
                $"{m.Id} citation too short: {m.LiteratureCitation}");
        }
    }

    [Fact]
    public void KnownIds_AreStable()
    {
        // Renaming any of these breaks audit-log correlation with historical
        // renders — treat any change here as a forensic-reproducibility break
        // and bump Version in Metadata accordingly.
        Assert.Equal("wiener",             new WienerDeconvolver().Metadata.Id);
        Assert.Equal("tikhonov-laplacian", new TikhonovDeconvolver().Metadata.Id);
        Assert.Equal("tv-chambolle",       new TotalVariationDeconvolver().Metadata.Id);
        Assert.Equal("richardson-lucy",    new RichardsonLucyDeconvolver().Metadata.Id);
        Assert.Equal("cls-laplacian",      new ConstrainedLeastSquaresDeconvolver().Metadata.Id);
        Assert.Equal("landweber",          new LandweberDeconvolver().Metadata.Id);
    }

    [Fact]
    public void ProductionIds_AreUnique()
    {
        var ids = new[]
        {
            new WienerDeconvolver().Metadata.Id,
            new TikhonovDeconvolver().Metadata.Id,
            new TotalVariationDeconvolver().Metadata.Id,
            new RichardsonLucyDeconvolver().Metadata.Id,
            new ConstrainedLeastSquaresDeconvolver().Metadata.Id,
            new LandweberDeconvolver().Metadata.Id,
        };
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }
}
