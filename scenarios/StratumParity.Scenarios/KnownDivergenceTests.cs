using Xunit;

namespace StratumParity.Scenarios;

/// <summary>World-free checks of the known-divergence policy, so a typo in a build list
/// cannot turn into a silent exemption or a silent strict run.</summary>
public class KnownDivergenceTests
{
    private static readonly KnownDivergence Sample = new("#1", StratumBuild.Stable2, StratumBuild.Indev1);

    [Fact]
    public void Applies_Should_BeTrue_When_BuildIsListed()
    {
        Assert.True(Sample.AppliesTo(StratumBuild.Stable2));
        Assert.True(Sample.AppliesTo(StratumBuild.Indev1));
    }

    [Fact]
    public void Applies_Should_BeFalse_When_BuildIsUnlistedOrUnknown()
    {
        Assert.False(Sample.AppliesTo("1.22.7-stratum.3"));
        Assert.False(Sample.AppliesTo("1.22.7-stratum.2-indev.2"));
        Assert.False(Sample.AppliesTo("1.22.7-STRATUM.2"));
        Assert.False(Sample.AppliesTo(null));
    }

    [Fact]
    public void Constructor_Should_Reject_When_IssueOrBuildsMissing()
    {
        Assert.Throws<ArgumentException>(() => new KnownDivergence(" ", StratumBuild.Stable2));
        Assert.Throws<ArgumentException>(() => new KnownDivergence("#1"));
    }
}
