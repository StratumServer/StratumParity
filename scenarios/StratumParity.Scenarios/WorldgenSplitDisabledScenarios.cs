using Atlas.XUnit;
using Xunit;
using Xunit.Abstractions;

namespace StratumParity.Scenarios;

/// <summary>
/// The terrain layers of the golden rectangle (<see cref="WorldgenGoldens.TerrainLayers"/>), generated
/// with Stratum's split Terrain pass switched off. The seeded stratum.json sets
/// <c>Worldgen.SplitTerrainPass</c> false before boot, which gives Stratum the stock single stage
/// topology (no generator moved into <c>OnChunkColumnGenTerrainLate</c>); vanilla ignores the file and
/// always has that topology. The same constants therefore have to come out of vanilla, of Stratum with
/// the split (the default, <c>WorldgenTerrainScenarios</c>) and of Stratum without it, which pins the
/// stock pass of the Stratum build as well as the split: a difference between the two Stratum legs is
/// a bug of the split, a difference shared by both is a bug of a generator that both run.
///
/// The fixture must also seed a stratum.json: the engine only reads the other Stratum files when
/// stratum.json already exists. Two guards prove the file was read and the topology is the one the
/// class asserts on (the setting reads false and no generator sits in the late sub-stage); without
/// them a Stratum that ignored the fixture would pass or fail on the wrong pass.
///
/// What the layers cover (height map, rock below the terrain, cave air) and what they leave out
/// (rain height map, deposits, vegetation) is documented on <see cref="WorldgenDigest"/>.
/// The measured digests are written to the test output in the form of the golden table, so a
/// Vintage Story bump can be re-pinned from the vanilla leg of this class.
///
/// Two confirmed Stratum divergences move the rock layer of the rectangle, with the split on and off
/// alike (the same six columns on every stable and prerelease run): the rewritten
/// <c>GenCaves.SetBlocks</c> carves the lava layer differently in 5 columns, and the changed random
/// bound of <c>GenRivulets.tryGenRivulet</c> puts one rivulet source into a sixth. Height map and cave air
/// stay equal in all 16 columns. On the listed builds the Stratum branch asserts exactly that shape; every
/// other build, and vanilla, is strict parity.
/// </summary>
[AtlasWorld(WorldType = "standard", Seed = WorldgenGoldens.Seed)]
[AtlasDataFiles("fixtures/stratum-worldgen-unsplit", TargetPath = "")]
public class WorldgenSplitDisabledScenarios : AtlasScenarioBase
{
    private const string SplitSetting = "Worldgen.SplitTerrainPass";

    // Lava cells keep their rock block, the floor of a step is cut 1.4 blocks lower, the liquid check
    // runs per column so a step next to a liquid is carved in part, domes are clipped and a carve through
    // the surface lowers the height maps once instead of down the whole opening (the hunk of
    // GenCaves.SetBlocks, see StratumServer/Stratum#366). In the rectangle it shows below y=8 only.
    private static readonly KnownDivergence CaveCarve =
        new("StratumServer/Stratum#366", StratumBuild.Stable2, StratumBuild.Indev1);

    // aboveSurfaceHeight is one smaller (MapSizeY - surfaceY against maxRivuletY - surfaceY + 1, StratumServer/Stratum#368), so the
    // sampled heights differ and the later draws fall out of step: the rivulet sources move (replaying the
    // engine's draws with both bounds gives no source in the rectangle for vanilla and one for Stratum).
    private static readonly KnownDivergence RivuletBound =
        new("StratumServer/Stratum#368", StratumBuild.Stable2, StratumBuild.Indev1);

    // Columns whose rock layer each divergence moves in the rectangle (measured, per block diff of
    // vanilla against Stratum; the rivulet one also replayed from the engine's draws).
    private static readonly (int Cx, int Cz)[] CaveColumns =
    {
        (16048, 16047), (16048, 16050), (16049, 16047), (16050, 16047), (16050, 16050),
    };

    private static readonly (int Cx, int Cz)[] RivuletColumns = { (16050, 16049) };

    private readonly ITestOutputHelper output;

    public WorldgenSplitDisabledScenarios(ITestOutputHelper output) => this.output = output;

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task TerrainLayers_Should_MatchVanillaGolden_When_SplitTerrainPassDisabled()
    {
        await AssertSplitDisabled();

        await WorldgenArea.LoadDone(World, WorldgenGoldens.RectChunkX, WorldgenGoldens.RectChunkZ,
            WorldgenGoldens.RectEdge, WorldgenGoldens.RectEdge);

        // The rectangle is a golden only while no structure was placed around it (the Atlas standard
        // world places none today, so a structure here means the world generation changed).
        List<string> structures = WorldgenArea.Structures(World, WorldgenGoldens.RectChunkX, WorldgenGoldens.RectChunkZ,
            WorldgenGoldens.RectEdge, WorldgenGoldens.RectEdge);
        Assert.True(structures.Count == 0,
            $"the golden rectangle has generated structures on {ServerFlavor.Name} ({string.Join("; ", structures)}); setup is invalid");

        List<TerrainDigest> digests = WorldgenArea.Inner(WorldgenGoldens.RectChunkX, WorldgenGoldens.RectChunkZ,
                WorldgenGoldens.RectEdge, WorldgenGoldens.RectEdge)
            .Select(c => WorldgenDigest.Terrain(World, c.Cx, c.Cz))
            .ToList();
        WorldgenArea.Unload(World, WorldgenGoldens.RectChunkX, WorldgenGoldens.RectChunkZ,
            WorldgenGoldens.RectEdge, WorldgenGoldens.RectEdge);

        output.WriteLine($"terrain layers measured on {ServerFlavor.Name} ({ServerFlavor.Version ?? "vanilla"}), split disabled:");
        output.WriteLine(WorldgenGoldens.Format(digests));

        List<string> mismatches = WorldgenGoldens.TerrainMismatches(digests);
        foreach (string mismatch in mismatches)
        {
            output.WriteLine(mismatch);
        }

        Assert.DoesNotContain(mismatches, m => m.Contains("setup is invalid"));

        // Empty on vanilla and on every build that is not listed: strict parity.
        List<(int Cx, int Cz)> expectedMoved = new();
        if (CaveCarve.Applies)
        {
            expectedMoved.AddRange(CaveColumns);
        }

        if (RivuletBound.Applies)
        {
            expectedMoved.AddRange(RivuletColumns);
        }

        var moved = new List<(int Cx, int Cz)>();
        var otherLayers = new List<string>();
        for (int i = 0; i < digests.Count; i++)
        {
            TerrainDigest golden = WorldgenGoldens.TerrainLayers[i];
            TerrainDigest got = digests[i];
            if (got.Rock != golden.Rock)
            {
                moved.Add((got.ChunkX, got.ChunkZ));
            }

            // The Stratum rivulet source sits next to a cave cell and flows into it once the scheduled block
            // update runs, so the cave air of that column is one lower on some runs (216 against 217,
            // seen on 1 of 4 prerelease runs): timing, not shape.
            bool airMayFlow = RivuletBound.Applies && RivuletColumns.Contains((got.ChunkX, got.ChunkZ));
            if (got.HeightMap != golden.HeightMap || got.DepositRocks != golden.DepositRocks
                || (got.CaveAir != golden.CaveAir && !airMayFlow))
            {
                otherLayers.Add($"({got.ChunkX},{got.ChunkZ})");
            }
        }

        if (expectedMoved.Count == 0)
        {
            Assert.True(mismatches.Count == 0,
                $"{mismatches.Count} layer(s) of the golden rectangle differ from the vanilla golden on {ServerFlavor.Name} " +
                $"with {SplitSetting} disabled: {string.Join("; ", mismatches)}");
            return;
        }

        string tags = string.Join("; ", new[] { CaveCarve, RivuletBound }.Where(d => d.Applies).Select(d => d.Tag));
        Assert.True(
            otherLayers.Count == 0 && moved.OrderBy(c => c).SequenceEqual(expectedMoved.OrderBy(c => c)),
            $"{tags}: bug shape changed on {ServerFlavor.Name} with {SplitSetting} disabled, the rock layer moved in " +
            $"[{Format(moved)}] where [{Format(expectedMoved)}] is known, and height map, cave air or deposit rocks " +
            $"differ in [{string.Join(", ", otherLayers)}]; narrow or drop the exemption");
    }

    private static string Format(IEnumerable<(int Cx, int Cz)> columns) =>
        string.Join(", ", columns.OrderBy(c => c).Select(c => $"({c.Cx},{c.Cz})"));

    /// <summary>On Stratum the fixture was read and the engine booted the stock single stage; vanilla has
    /// no split and no setting to read.</summary>
    private async Task AssertSplitDisabled()
    {
        if (!ServerFlavor.IsStratum)
        {
            return;
        }

        string? setting = await StratumSetting.Get(World, SplitSetting);
        Assert.True(string.Equals(setting, "false", StringComparison.OrdinalIgnoreCase),
            $"{SplitSetting} is {setting ?? "unreadable"} on {ServerFlavor.Name}, the fixture sets it false: " +
            "the fixture was not read; setup is invalid");

        int late = WorldgenTopology.TerrainLateHandlers(World);
        Assert.True(late == 0,
            $"{late} generator(s) sit in the late Terrain sub-stage on {ServerFlavor.Name} although {SplitSetting} is false: " +
            "the split is still active; setup is invalid");
    }
}
