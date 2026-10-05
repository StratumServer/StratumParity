using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Server;
using Xunit;
using Xunit.Abstractions;

namespace StratumParity.Scenarios;

/// <summary>
/// The contract a mod's chunk column generation handler relies on: the engine calls it once per
/// column at its pass, in pass order, while the column's own map chunk is at that pass, with the
/// eight neighbouring columns already there for every pass after Terrain, and the Terrain pass it
/// runs after is complete (strata and caves placed). The staged stratumparityworldgen mod
/// (mods/worldgenprobe) registers a handler at each of the five passes of the standard world,
/// last in each list, and records what it saw in the moddata of the column's first chunk.
///
/// Stratum schedules the Terrain pass as two sub-stages (Worldgen.SplitTerrainPass) but falls
/// back to the stock single stage when a foreign handler sits at the Terrain pass
/// (Worldgen.AutoDisableSplitForMods, on by default), which is the case here because of the probe.
/// The scenario guards that topology (no generator in the late stage) before it trusts the
/// records; the split-kept case is WorldgenPassContractSplitScenarios.
///
/// The area is the pass rectangle of <see cref="WorldgenGoldens"/>: 5x5 columns at the golden corner
/// taken to Done, the inner 3x3 read. The Terrain pass numbers (rock, granite and air of the
/// whole column at the moment of the call) are compared with <see cref="WorldgenGoldens.TerrainPass"/>,
/// captured from the vanilla leg; the failure of each number is a separate assertion so a known
/// divergence can be pinned for one of them without hiding the others.
///
/// Two confirmed Stratum divergences show in those numbers on the listed builds, both in the stock topology
/// that this class runs (the probe's Terrain handler turns the split off). The rewritten
/// <c>GenCaves.SetBlocks</c> keeps the rock in lava cells, which moves the rock count of two columns and the
/// granite count of one. And <c>GenBlockLayers.PlaceTallGrass</c> draws from another generator than the one
/// seeded for the column, so the tall grass of every column differs from vanilla and from one run to the next
/// (the empty position count moves by a few cells in most columns). On the listed builds the Stratum branch
/// asserts that shape; vanilla, and every build that is not listed, is strict parity.
/// </summary>
[AtlasWorld(WorldType = "standard", Seed = WorldgenGoldens.Seed, Mods = new[] { "mods/worldgenprobe" })]
public class WorldgenPassContractScenarios : AtlasScenarioBase
{
    private const int NeighbourCount = 8;

    // The lava layer keeps its rock block (GenCaves.SetBlocks writes only the fluid where vanilla first clears
    // the cell) and a few cells around it are carved differently, see StratumServer/Stratum#366. In the 3x3 it moves the
    // rock count of two columns and the granite count of one. The deltas are Stratum minus golden and were the
    // same on every stable and prerelease run.
    private static readonly KnownDivergence CaveCarve =
        new("StratumServer/Stratum#366", StratumBuild.Stable2, StratumBuild.Indev1);

    private static readonly (int Cx, int Cz, int Delta)[] CaveRock = { (16048, 16047, 25), (16049, 16047, 1) };

    private static readonly (int Cx, int Cz, int Delta)[] CaveGranite = { (16049, 16047, 1) };

    // In the stock topology GenBlockLayers.OnChunkColumnGeneration runs on the registered instance, but its
    // PlaceTallGrass calls are forwarded to the calling thread's worker instance, whose random generator is
    // never seeded for the column. The tall grass cells (solid layer, so not empty) then depend on how many
    // columns the worker generated before, and the empty position count of a column moves by a few cells,
    // differently from one run to the next. At most one cell per surface position, hence the bound.
    private static readonly KnownDivergence TallGrassStream =
        new("StratumServer/Stratum#369", StratumBuild.Stable2, StratumBuild.Indev1);

    private const int MaxTallGrassCells = WorldgenArea.ChunkSize * WorldgenArea.ChunkSize;

    private readonly ITestOutputHelper output;

    public WorldgenPassContractScenarios(ITestOutputHelper output) => this.output = output;

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task ProbeHandler_Should_SeeVanillaPassSequence_When_RegisteredAtEveryPass()
    {
        const int cx0 = WorldgenGoldens.RectChunkX;
        const int cz0 = WorldgenGoldens.RectChunkZ;
        const int edge = WorldgenGoldens.PassRectEdge;

        AssertSetup();
        var inner = WorldgenArea.Inner(cx0, cz0, edge, edge).ToList();
        Assert.True(inner.Count == WorldgenGoldens.TerrainPass.Count,
            $"the inner area has {inner.Count} columns but the golden has {WorldgenGoldens.TerrainPass.Count} on {ServerFlavor.Name}; setup is invalid");

        await WorldgenArea.LoadDone(World, cx0, cz0, edge, edge);
        try
        {
            var records = inner.ToDictionary(c => c, c => WorldgenProbe.Read(World, c.Cx, c.Cz));
            foreach (var (cx, cz) in inner)
            {
                output.WriteLine($"{ServerFlavor.Name} column ({cx},{cz}): {WorldgenProbe.Describe(records[(cx, cz)])}");
            }

            var sequence = new List<string>();
            var current = new List<string>();
            var neighbours = new List<string>();
            var notDone = new List<string>();
            foreach (var (cx, cz) in inner)
            {
                List<PassRecord> column = records[(cx, cz)];
                string where = $"column ({cx},{cz})";
                if (!column.Select(r => r.Pass).SequenceEqual(WorldgenProbe.ExpectedPasses))
                {
                    sequence.Add($"{where} saw [{WorldgenProbe.Describe(column)}]");
                }

                foreach (PassRecord r in column.Where(r => r.Error == null && r.CurrentPass != r.Pass))
                {
                    current.Add($"{where} {r.Pass} handler ran while the column was at {r.CurrentPass}");
                }

                foreach (PassRecord r in column.Where(r => r.Error == null && r.Pass > EnumWorldGenPass.Terrain && r.NeighboursReady != NeighbourCount))
                {
                    neighbours.Add($"{where} {r.Pass} handler had {r.NeighboursReady} of {NeighbourCount} neighbours ready");
                }

                if (!WorldgenArea.IsDone(World, cx, cz))
                {
                    notDone.Add($"{where} ended at {World.Api.WorldManager.GetMapChunk(cx, cz)?.CurrentPass.ToString() ?? "no map chunk"}");
                }
            }

            Assert.True(sequence.Count == 0,
                $"handlers were not called exactly once per pass in order {string.Join(", ", WorldgenProbe.ExpectedPasses)} on {ServerFlavor.Name}: {string.Join("; ", sequence)}");
            Assert.True(current.Count == 0,
                $"a handler ran while its column was not at its pass on {ServerFlavor.Name}: {string.Join("; ", current)}");
            Assert.True(neighbours.Count == 0,
                $"a handler after the Terrain pass ran before its eight neighbours reached the pass on {ServerFlavor.Name}: {string.Join("; ", neighbours)}");
            Assert.True(notDone.Count == 0,
                $"a column did not end at Done after its five passes on {ServerFlavor.Name}: {string.Join("; ", notDone)}");

            // Rows to paste into WorldgenGoldens.TerrainPass when the golden is re-pinned from the vanilla leg.
            output.WriteLine($"{ServerFlavor.Name} Terrain pass rows:{Environment.NewLine}{WorldgenGoldens.FormatPass(World)}");

            var rockDelta = new Dictionary<(int Cx, int Cz), int>();
            var graniteDelta = new Dictionary<(int Cx, int Cz), int>();
            var airDelta = new Dictionary<(int Cx, int Cz), int>();
            foreach (PassGolden golden in WorldgenGoldens.TerrainPass)
            {
                PassRecord seen = records[(golden.ChunkX, golden.ChunkZ)].First(r => r.Pass == EnumWorldGenPass.Terrain);
                rockDelta[(golden.ChunkX, golden.ChunkZ)] = seen.Rock - golden.Rock;
                graniteDelta[(golden.ChunkX, golden.ChunkZ)] = seen.Granite - golden.Granite;
                airDelta[(golden.ChunkX, golden.ChunkZ)] = seen.Air - golden.Air;
            }

            output.WriteLine($"{ServerFlavor.Name} Terrain pass minus golden, rock: {Format(rockDelta)}; granite: {Format(graniteDelta)}; air: {Format(airDelta)}");

            AssertAsGolden("rock blocks", rockDelta, CaveCarve, CaveRock);
            AssertAsGolden("granite blocks", graniteDelta, CaveCarve, CaveGranite);
            AssertAirAsGolden(airDelta);
        }
        finally
        {
            WorldgenArea.Unload(World, cx0, cz0, edge, edge);
        }
    }

    /// <summary>Strict parity with the golden, or on the builds of <paramref name="divergence"/> exactly the
    /// known shift of <paramref name="known"/> (so a partial or full fix turns the scenario red).</summary>
    private void AssertAsGolden(string what, Dictionary<(int Cx, int Cz), int> delta, KnownDivergence divergence, (int Cx, int Cz, int Delta)[] known)
    {
        var moved = delta.Where(d => d.Value != 0).OrderBy(d => d.Key).Select(d => (d.Key.Cx, d.Key.Cz, Delta: d.Value)).ToList();
        if (!divergence.Applies)
        {
            Assert.True(moved.Count == 0,
                $"{what} the Terrain handler saw differ from the vanilla golden in {moved.Count} of {delta.Count} columns on {ServerFlavor.Name}: {Format(moved)}");
            return;
        }

        Assert.True(moved.SequenceEqual(known.OrderBy(k => (k.Cx, k.Cz))),
            $"{divergence.Tag}: bug shape changed, the {what} the Terrain handler saw moved by [{Format(moved)}] against the golden " +
            $"where [{Format(known)}] is known; narrow or drop the exemption");
    }

    /// <summary>The empty position count. Exact on vanilla and on unlisted builds. On the builds of the tall grass
    /// divergence it is run dependent, so the Stratum branch asserts the shape instead: at least one column differs
    /// and no column by more than the surface positions of a column (the columns that the cave divergence moves
    /// are left out of the first part, they also lose the lava cells).</summary>
    private void AssertAirAsGolden(Dictionary<(int Cx, int Cz), int> delta)
    {
        var caveColumns = CaveCarve.Applies ? CaveRock.Select(c => (c.Cx, c.Cz)).ToHashSet() : new HashSet<(int Cx, int Cz)>();
        var checkedColumns = delta.Where(d => !caveColumns.Contains(d.Key)).ToList();
        var moved = checkedColumns.Where(d => d.Value != 0).OrderBy(d => d.Key).Select(d => (d.Key.Cx, d.Key.Cz, Delta: d.Value)).ToList();
        if (!TallGrassStream.Applies)
        {
            Assert.True(moved.Count == 0,
                $"empty positions the Terrain handler saw differ from the vanilla golden in {moved.Count} of {checkedColumns.Count} columns on {ServerFlavor.Name}: {Format(moved)}");
            return;
        }

        Assert.True(moved.Count > 0,
            $"{TallGrassStream.Tag}: bug shape gone, the empty positions equal the golden in all {checkedColumns.Count} columns that carry no cave divergence; drop the exemption");
        var outOfBound = delta.Where(d => Math.Abs(d.Value) > MaxTallGrassCells).OrderBy(d => d.Key).Select(d => (d.Key.Cx, d.Key.Cz, Delta: d.Value)).ToList();
        Assert.True(outOfBound.Count == 0,
            $"{TallGrassStream.Tag}: bug shape changed, the empty positions moved by more than the {MaxTallGrassCells} surface positions of a column in [{Format(outOfBound)}]; narrow or drop the exemption");
    }

    private static string Format(IEnumerable<(int Cx, int Cz, int Delta)> columns) =>
        columns.Any() ? string.Join(", ", columns.Select(c => $"({c.Cx},{c.Cz}) {c.Delta:+#;-#;0}")) : "none";

    private static string Format(Dictionary<(int Cx, int Cz), int> delta) =>
        string.Join(", ", delta.OrderBy(d => d.Key).Select(d => $"({d.Key.Cx},{d.Key.Cz}) {d.Value:+#;-#;0}"));

    /// <summary>The golden is only valid for the fixed seed on the standard world with the stock Terrain topology.</summary>
    private void AssertSetup()
    {
        var save = World.Api.WorldManager.SaveGame;
        Assert.True(save.Seed == WorldgenGoldens.Seed,
            $"world seed is {save.Seed}, the golden was captured on {WorldgenGoldens.Seed} on {ServerFlavor.Name}; setup is invalid");
        Assert.True(save.WorldType == "standard",
            $"world type is {save.WorldType}, the golden was captured on standard on {ServerFlavor.Name}; setup is invalid");

        int late = WorldgenTopology.TerrainLateHandlers(World);
        Assert.True(late == 0,
            $"{late} generators sit in the late Terrain stage on {ServerFlavor.Name}, so the probe's Terrain handler did not bring back the stock topology; setup is invalid");
    }
}
