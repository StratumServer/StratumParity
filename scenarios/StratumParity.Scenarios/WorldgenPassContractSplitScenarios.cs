using Atlas.XUnit;
using Vintagestory.API.Server;
using Xunit;
using Xunit.Abstractions;

namespace StratumParity.Scenarios;

/// <summary>
/// What a mod's worldgen handler sees while Stratum runs the Terrain pass in two sub-stages next to that
/// very handler. By default Stratum gives the split up as soon as a foreign handler sits at the Terrain
/// pass (Worldgen.AutoDisableSplitForMods, covered by the default topology class). The stratum.json of
/// <c>fixtures/stratum-worldgen-modsplit</c> turns that off, so the staged stratumparityworldgen mod (one
/// handler at each of five passes) runs inside the split of Stratum#228. Vanilla ignores the file and
/// has one Terrain stage.
///
/// The contract is the vanilla one, read from the 9 inner columns of the 5x5 rectangle of the shared
/// goldens (<see cref="WorldgenGoldens"/>): each handler runs exactly once per column, in the order
/// Terrain, TerrainFeatures, Vegetation, NeighbourSunLightFlood, PreDone; the column's own map chunk
/// reads the handler's pass while it runs (both Terrain sub-stages must read Terrain); every neighbour is
/// at that pass or later for each pass after Terrain; the column is Done afterwards. A handler after
/// Terrain must also see strata (granite only part of the rock), which proves the late sub-stage finished
/// before the next pass started.
///
/// What a handler registered at Terrain sees is the one place where the two flavors differ, on purpose.
/// Vanilla runs it after every vanilla Terrain generator, so its record equals the golden from the vanilla
/// leg (<see cref="WorldgenGoldens.TerrainPass"/>). Stratum keeps the foreign handler in the first
/// sub-stage and moves rock strata, caves and block layers to the second (StratumWorldgenCompat: "the
/// split moves rock strata, caves and block layers into a later stage"; the config says to leave
/// AutoDisableSplitForMods on unless the server's mods have been checked). So the Terrain record of
/// Stratum is the pre-strata shape: granite equals rock in every column, measured on stratum.2 and
/// stratum.2-indev.1 over 4 runs with identical counts. That is a documented probe, not a known
/// divergence: no issue is owed, and a red here means Stratum changed the order and the probe is stale.
/// </summary>
[AtlasWorld(WorldType = "standard", Seed = WorldgenGoldens.Seed, Mods = new[] { "mods/worldgenprobe" })]
[AtlasDataFiles("fixtures/stratum-worldgen-modsplit", TargetPath = "")]
public class WorldgenPassContractSplitScenarios : AtlasScenarioBase
{
    private const int Neighbours = 8;

    private readonly ITestOutputHelper output;

    public WorldgenPassContractSplitScenarios(ITestOutputHelper output) => this.output = output;

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task ProbeHandler_Should_SeeVanillaPassSequence_When_SplitKeptWithForeignHandler()
    {
        int lateHandlers = await AssertSplitKept();

        int x0 = WorldgenGoldens.RectChunkX;
        int z0 = WorldgenGoldens.RectChunkZ;
        int edge = WorldgenGoldens.PassRectEdge;
        try
        {
            await WorldgenArea.LoadDone(World, x0, z0, edge, edge);

            List<string> structures = WorldgenArea.Structures(World, x0, z0, edge, edge);
            Assert.True(structures.Count == 0,
                $"generated structures touch the pass rectangle on {ServerFlavor.Name} ({string.Join("; ", structures)}); setup is invalid");

            var contract = new List<string>();
            var content = new List<string>();
            foreach (PassGolden golden in WorldgenGoldens.TerrainPass)
            {
                List<PassRecord> records = WorldgenProbe.Read(World, golden.ChunkX, golden.ChunkZ);
                output.WriteLine($"column ({golden.ChunkX},{golden.ChunkZ}): {WorldgenProbe.Describe(records)}");
                CheckContract(golden, records, contract);
                if (records.Count > 0 && records[0].Pass == EnumWorldGenPass.Terrain)
                {
                    content.AddRange(ServerFlavor.IsStratum ? PreStrataProblems(golden, records[0]) : TerrainMismatches(golden, records[0]));
                }
            }

            Assert.True(contract.Count == 0,
                $"the probe handlers broke the vanilla pass contract on {ServerFlavor.Name} with the split kept " +
                $"({lateHandlers} generators in the late sub-stage): {string.Join("; ", contract)}");

            // What the Terrain handler sees is the documented difference of the split (see the class summary):
            // the vanilla golden on vanilla, the pre-strata shape on Stratum.
            Assert.True(content.Count == 0, ServerFlavor.IsStratum
                ? $"the Terrain handler no longer sees the pre-strata shape on {ServerFlavor.Name} with the split kept " +
                  $"({lateHandlers} generators in the late sub-stage): {string.Join("; ", content)}; " +
                  "Stratum changed where a foreign Terrain handler runs, update this probe"
                : $"the Terrain record differs from the vanilla golden on {ServerFlavor.Name}: {string.Join("; ", content)}");
        }
        finally
        {
            WorldgenArea.Unload(World, x0, z0, edge, edge);
        }
    }

    /// <summary>Proves the world booted in the topology the scenario is about and returns how many
    /// generators sit in the late sub-stage (0 on vanilla, which has one Terrain stage).</summary>
    private async Task<int> AssertSplitKept()
    {
        if (!ServerFlavor.IsStratum)
        {
            return 0;
        }

        // Both are read from the live config, so the values also prove that the fixture was seeded.
        await AssertSetting("Worldgen.SplitTerrainPass", "true");
        await AssertSetting("Worldgen.AutoDisableSplitForMods", "false");

        int late = WorldgenTopology.TerrainLateHandlers(World);
        Assert.True(late > 0,
            $"no generator was moved to the late sub-stage on {ServerFlavor.Version} although the split is on and the probe mod " +
            "registers at Terrain with AutoDisableSplitForMods off: the split is not active; setup is invalid");
        return late;
    }

    private async Task AssertSetting(string path, string expected)
    {
        string? actual = await StratumSetting.Get(World, path);
        Assert.True(string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            $"{path} is {actual ?? "unreadable"} on {ServerFlavor.Name}, the scenario expects {expected}; setup is invalid");
    }

    /// <summary>Everything that has to hold in both topologies, appended to <paramref name="problems"/>.</summary>
    private void CheckContract(PassGolden golden, List<PassRecord> records, List<string> problems)
    {
        string where = $"column ({golden.ChunkX},{golden.ChunkZ})";

        PassRecord? thrown = records.FirstOrDefault(r => r.Error != null);
        if (thrown != null)
        {
            problems.Add($"{where}: a probe handler threw {thrown.Error}");
            return;
        }

        if (!records.Select(r => r.Pass).SequenceEqual(WorldgenProbe.ExpectedPasses))
        {
            problems.Add($"{where}: handlers ran as [{string.Join(", ", records.Select(r => r.Pass))}], " +
                         $"expected [{string.Join(", ", WorldgenProbe.ExpectedPasses)}]");
            return;
        }

        foreach (PassRecord record in records)
        {
            if (record.CurrentPass != record.Pass)
            {
                problems.Add($"{where}: the map chunk read {record.CurrentPass} during the {record.Pass} handler");
            }

            if (record.Pass != EnumWorldGenPass.Terrain && record.NeighboursReady != Neighbours)
            {
                problems.Add($"{where}: {record.NeighboursReady} of {Neighbours} neighbours were at {record.Pass} or later during its handler");
            }
        }

        PassRecord terrain = records[0];
        PassRecord features = records[1];
        if (terrain.Rock <= 0)
        {
            problems.Add($"{where}: the Terrain record counted no rock; setup is invalid");
        }

        // The terrain noise places granite only and the strata swap most of it for other rock, so a
        // handler that runs after the Terrain pass must see granite as a part of the rock. Equal counts
        // mean that the late sub-stage had not run when the next pass started.
        if (features.Granite >= features.Rock)
        {
            problems.Add($"{where}: the TerrainFeatures handler saw granite {features.Granite} of rock {features.Rock}, no strata yet");
        }

        if (!WorldgenArea.IsDone(World, golden.ChunkX, golden.ChunkZ))
        {
            problems.Add($"{where}: the column is not Done after the probe handlers ran");
        }
    }

    /// <summary>One message per field of the Terrain record that differs from the vanilla golden (vanilla only).</summary>
    private static List<string> TerrainMismatches(PassGolden golden, PassRecord terrain)
    {
        var found = new List<string>();
        string where = $"column ({golden.ChunkX},{golden.ChunkZ})";
        if (terrain.Rock != golden.Rock) found.Add($"{where} rock {terrain.Rock} expected {golden.Rock}");
        if (terrain.Granite != golden.Granite) found.Add($"{where} granite {terrain.Granite} expected {golden.Granite}");
        if (terrain.Air != golden.Air) found.Add($"{where} air {terrain.Air} expected {golden.Air}");
        return found;
    }

    /// <summary>The Terrain record of a foreign handler in the first sub-stage: no stratum is placed yet, so
    /// every rock block is the granite of the noise (Stratum only).</summary>
    private static List<string> PreStrataProblems(PassGolden golden, PassRecord terrain)
    {
        var found = new List<string>();
        if (terrain.Granite != terrain.Rock)
        {
            found.Add($"column ({golden.ChunkX},{golden.ChunkZ}) granite {terrain.Granite} of rock {terrain.Rock}");
        }

        return found;
    }
}
