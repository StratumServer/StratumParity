using Xunit;

namespace StratumParity.Scenarios;

/// <summary>World-free checks of the shared worldgen goldens, so a mistyped row or a reordered table
/// cannot turn into a comparison against the wrong column.</summary>
public class WorldgenGoldensTests
{
    [Fact]
    public void TerrainLayers_Should_ListTheInnerColumnsInOrder_When_PinnedOnTheRectangle()
    {
        var expected = WorldgenArea.Inner(WorldgenGoldens.RectChunkX, WorldgenGoldens.RectChunkZ, WorldgenGoldens.RectEdge, WorldgenGoldens.RectEdge).ToList();

        Assert.Equal(expected, WorldgenGoldens.TerrainLayers.Select(d => (d.ChunkX, d.ChunkZ)).ToList());
        Assert.All(WorldgenGoldens.TerrainLayers, d =>
        {
            Assert.Equal(64, d.HeightMap.Length);
            Assert.Equal(64, d.Rock.Length);
            Assert.Equal(0, d.DepositRocks);
        });
    }

    [Fact]
    public void TerrainPass_Should_ListTheInnerColumnsInOrder_When_PinnedOnTheRectangle()
    {
        var expected = WorldgenArea.Inner(WorldgenGoldens.RectChunkX, WorldgenGoldens.RectChunkZ, WorldgenGoldens.PassRectEdge, WorldgenGoldens.PassRectEdge).ToList();

        Assert.Equal(expected, WorldgenGoldens.TerrainPass.Select(g => (g.ChunkX, g.ChunkZ)).ToList());
        Assert.All(WorldgenGoldens.TerrainPass, g => Assert.True(g.Rock > 0 && g.Granite > 0 && g.Granite <= g.Rock));
    }

    [Fact]
    public void TerrainMismatches_Should_BeEmpty_When_MeasurementEqualsTheGolden()
    {
        Assert.Empty(WorldgenGoldens.TerrainMismatches(WorldgenGoldens.TerrainLayers.ToList()));
    }

    [Fact]
    public void TerrainMismatches_Should_NameLayerAndColumn_When_RockDiffers()
    {
        var measured = WorldgenGoldens.TerrainLayers.ToList();
        TerrainDigest changed = measured[5];
        measured[5] = changed with { Rock = new string('0', 64), CaveAir = changed.CaveAir + 1 };

        List<string> found = WorldgenGoldens.TerrainMismatches(measured);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, m => m.Contains($"column ({changed.ChunkX},{changed.ChunkZ})") && m.Contains("layer rock"));
        Assert.Contains(found, m => m.Contains("layer cave air"));
    }
}
