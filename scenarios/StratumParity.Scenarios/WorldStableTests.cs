using Xunit;

namespace StratumParity.Scenarios;

/// <summary>World-free checks of the settle rule behind <see cref="WorldStable"/>, so a scenario that waits for
/// stability cannot return on a single read or never return on a constant value.</summary>
public class WorldStableTests
{
    [Fact]
    public void Feed_Should_ReportStable_When_ValueRepeatsForTheQuietReads()
    {
        var tracker = new StableTracker<int>(2);

        Assert.False(tracker.Feed(7));
        Assert.False(tracker.Feed(7));
        Assert.True(tracker.Feed(7));
        Assert.True(tracker.Feed(7));
    }

    [Fact]
    public void Feed_Should_RestartTheCount_When_ValueChanges()
    {
        var tracker = new StableTracker<string>(2);

        Assert.False(tracker.Feed("a"));
        Assert.False(tracker.Feed("a"));
        Assert.False(tracker.Feed("b"));
        Assert.False(tracker.Feed("b"));
        Assert.True(tracker.Feed("b"));
    }

    [Fact]
    public void Constructor_Should_Reject_When_NoQuietReadIsRequired()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StableTracker<int>(0));
    }
}
