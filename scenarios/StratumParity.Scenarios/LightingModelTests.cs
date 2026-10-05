using Vintagestory.API.MathTools;
using Xunit;

namespace StratumParity.Scenarios;

/// <summary>World-free checks of the closed forms behind <see cref="LightingScenarios"/>, so a wrong expectation reads
/// as a failing unit test and not as a divergence between the flavors.</summary>
public class LightingModelTests
{
    private static BlockPos Pos(int x, int y, int z) => new(x, y, z, 0);

    [Fact]
    public void Falloff_Should_LoseOnePerBlockAndStopAtZero_When_OneSource()
    {
        BlockPos[] along = Enumerable.Range(0, 20).Select(i => Pos(i, 0, 0)).ToArray();

        int[] levels = LightingModel.Falloff(along, (Pos(0, 0, 0), 16));

        Assert.Equal(new[] { 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0, 0, 0, 0 }, levels);
        Assert.Equal(new[] { 16, 14, 12, 10, 8, 6, 4, 2, 0, 0 },
            LightingModel.Falloff(Enumerable.Range(0, 10).Select(i => Pos(i, i, 0)).ToArray(), (Pos(0, 0, 0), 16)));
    }

    [Fact]
    public void Falloff_Should_TakeTheStrongestSource_When_TwoOverlap()
    {
        BlockPos[] along = Enumerable.Range(0, 7).Select(i => Pos(i, 0, 0)).ToArray();

        int[] levels = LightingModel.Falloff(along, (Pos(0, 0, 0), 14), (Pos(6, 0, 0), 16));

        // Cell 3 is 3 from the first source (11) and 3 from the second (13).
        Assert.Equal(new[] { 14, 13, 12, 13, 14, 15, 16 }, levels);
    }

    [Fact]
    public void Corridor_Should_LoseTheAbsorptionOfEveryCellItLeaves_When_GlassLeavesWaterGranite()
    {
        // Lamp (V 16), glass 0, leaves 1, water 1, glass 0, granite 99, then air.
        int[] levels = LightingModel.Corridor(16, new[] { 0, 0, 1, 1, 0, 99 }, 9);

        Assert.Equal(new[] { 16, 15, 14, 12, 10, 9, 0, 0, 0 }, levels);
    }

    [Fact]
    public void Digest_Should_RoundTrip_When_LevelsRunToThirtyOne()
    {
        int[] levels = { 0, 1, 15, 16, 31 };

        string digest = LightingModel.Digest(levels);

        Assert.Equal(levels.Length, digest.Length);
        Assert.Equal(levels, LightingModel.Levels(digest));
    }
}
