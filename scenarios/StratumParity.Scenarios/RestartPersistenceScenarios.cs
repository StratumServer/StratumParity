using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.MathTools;
using Xunit;
using Xunit.Abstractions;

namespace StratumParity.Scenarios;

/// <summary>
/// What a graceful shutdown leaves behind must be the same on both flavors. The staged
/// stratumparitypersist mod (mods/persistprobe) appends "boot1", "boot2", ... at every boot to five
/// places the engine persists through different writers: the savegame store (SaveGameLoaded), and the
/// chunk moddata, the map chunk moddata and the map region moddata of the world centre column, plus
/// one granite marker block per boot in that column (ChunkColumnLoaded of the centre column, which
/// the engine loads during the startup spawn area load, so the first boot is seeded before any
/// scenario runs). The single scenario restarts the world once ([AtlasScenario(RestartWorld =
/// true)], a graceful shutdown that persists the save and a second host booted on it) and reads all of
/// it back through <see cref="PersistProbe"/>: every list must read [boot1, boot2] and both markers
/// must be there.
///
/// This is the one place that exercises the shutdown save and the startup read of the map region and
/// the map chunk, which the unload round trips of <see cref="ChunkPersistenceScenarios"/> cannot reach:
/// a map region only unloads on a 120 s timer and the map chunk is not unloaded with its column (so on
/// vanilla an API unload leaves the map chunk moddata alive in memory, measured), so the restart is
/// the practical round trip for them. Stratum's save pipeline (incremental dirty flush, DbChunk batch
/// reuse) and its chunk read path sit between these writes and the file, which is why a silent loss
/// here would be the worst kind of regression.
///
/// One scenario per class: the first scenario of a class boots, restarts and runs on the second
/// boot, and a second restart would read three boots. No player is joined before the restart (the
/// connections of joined test players die with the host, Atlas refuses the restart).
/// </summary>
[AtlasWorld(Mods = new[] { "mods/persistprobe" })]
public class RestartPersistenceScenarios : AtlasScenarioBase
{
    private static readonly string[] TwoBoots = { "boot1", "boot2" };

    private readonly ITestOutputHelper output;

    public RestartPersistenceScenarios(ITestOutputHelper output) => this.output = output;

    [AtlasScenario(RestartWorld = true, TimeoutMs = 180_000)]
    public async Task BootRecords_Should_SurviveRestart_When_ServerShutsDownGracefully()
    {
        PersistState state = await PersistProbe.Read(World, boots: 2);
        output.WriteLine($"{ServerFlavor.Name} after one restart: {state}");

        // The mod writes the savegame record at SaveGameLoaded, so an empty store means it never
        // ran (it did not compile or is not staged), not that the restart lost something.
        Assert.True(state.SaveGame.Length > 0,
            $"the persist probe left no boot record in the savegame on {ServerFlavor.Name}: the mod did not load; setup is invalid ({state})");
        Assert.True(state.SaveGame.Length <= TwoBoots.Length,
            $"the world booted {state.SaveGame.Length} times on {ServerFlavor.Name}, expected exactly one restart; setup is invalid ({state})");

        // The marker check proves something only if the positions are not granite without the mod:
        // the position of a boot that never happened must be something else (the superflat world is
        // three blocks deep, so y 100 is air).
        BlockPos neverWritten = PersistProbe.MarkerPos(World, TwoBoots.Length + 1);
        string neverWrittenCode = World.BlockAt(neverWritten).Code.ToString();
        Assert.True(neverWrittenCode != "game:rock-granite",
            $"the position of an unwritten marker at {neverWritten} is already granite on {ServerFlavor.Name}; setup is invalid");

        // Savegame first: the mod numbers its boots from it, so a savegame that lost boot1 makes
        // the second boot call itself boot1 and every list after that is a consequence.
        AssertBoots("savegame store", state.SaveGame, state);
        AssertBoots("chunk moddata", state.Chunk, state);
        AssertBoots("map chunk moddata", state.MapChunk, state);
        AssertBoots("map region moddata", state.MapRegion, state);

        for (int boot = 1; boot <= TwoBoots.Length; boot++)
        {
            Assert.True(state.Markers[boot - 1],
                $"the marker block of boot {boot} at {PersistProbe.MarkerPos(World, boot)} is not granite after one restart on {ServerFlavor.Name} ({state})");
        }
    }

    private static void AssertBoots(string place, string[] actual, PersistState state) =>
        Assert.True(actual.SequenceEqual(TwoBoots),
            $"{place} reads [{string.Join(",", actual)}] instead of [{string.Join(",", TwoBoots)}] after a graceful restart on {ServerFlavor.Name} " +
            $"(a missing boot1 is a record lost across the restart, a missing boot2 a record the probe did not write on the second boot); {state}");
}
