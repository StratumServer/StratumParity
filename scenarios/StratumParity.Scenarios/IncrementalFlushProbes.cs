using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.MathTools;
using Xunit;
using Xunit.Abstractions;

namespace StratumParity.Scenarios;

/// <summary>
/// Probes Stratum's incremental dirty flush (Performance.AutoSave.IncrementalDirtyFlush, on by
/// default) against vanilla's save policy. Vanilla writes a modified column in exactly two
/// situations: the autosave (every 300 s, and World.SaveNow resets that timer) and the
/// engine's own unload of an aged-out column, which saves what is dirty. An unload through
/// the API (UnloadChunkColumn) never saves anything. Stratum adds a writer: every
/// IncrementalFlushIntervalSeconds (10) its chunk thread saves the dirty loaded chunks and map
/// chunks it knows about, and it refreshes that list of known keys every SaveScanRefreshSeconds
/// (30), so a change is written at most about 30 s after the column was loaded or changed.
///
/// Every scenario here is a probe, flavor specific by design (this is a Stratum feature, not a
/// divergence to report), and all three share one shape. A baseline is written and saved first
/// (a pattern of eight granite blocks plus chunk moddata, then World.SaveNow), so the stored
/// copy exists and its reload is proven by the baseline reading back. The changes follow (a
/// second pattern, other moddata, map chunk moddata), nothing saves them explicitly, and the
/// scenario waits 1500 ticks (about 50 s, more than the 30 s key refresh plus one 10 s flush)
/// before dropping the column. What survives the reload is the observation: on vanilla the
/// baseline and nothing else, on Stratum with the flush on everything.
///
/// Exact world state backs the timing. After the window the scenario reads the engine's
/// DirtyForSaving flag of the chunk and of the map chunk (reflection, a setup guard when the
/// member is missing): where no flush is expected both must still be set, which proves nothing
/// saved the column in the meantime; where the flush is expected the map chunk flag must be
/// cleared (the chunk flag is only logged, simulation can dirty a chunk again). The map chunk
/// moddata itself is never asserted after an API unload: the engine does not unload the map
/// chunk with its column, so it survives in memory on vanilla too. Only the natural unload
/// scenario reads it back, after checking that the map chunk really left memory.
///
/// The flush config is read on every pass of the chunk thread, so the disabled scenarios use
/// /stratum set with restore (no fixture folder). The three scenarios use anchors 200 blocks
/// apart; the class joins no player, so nothing keeps a column alive.
/// </summary>
public class IncrementalFlushProbes : AtlasScenarioBase
{
    private const string FlushKey = "Performance.AutoSave.IncrementalDirtyFlush";
    private const int ChunkSize = ChunkPersistence.ChunkSize;
    private const int WindowTicks = 1500;
    private const double NominalTickSeconds = 0.033;
    private const int NaturalUnloadTimeoutTicks = 1800;

    // The engine's unload hands the dirty chunks to the chunk thread, which writes them a moment later;
    // reloading in the very next tick is not a flow a player or a mod can produce.
    private const int UnloadWriteSettleTicks = 10;

    private static readonly byte[] BaselineMarker = { 0xB1, 0x01 };
    private static readonly byte[] ChangedMarker = { 0xC2, 0x02, 0x7F };
    private static readonly byte[] NaturalChunkMarker = { 0x3A, 0x11 };
    private static readonly byte[] NaturalMapMarker = { 0x3B, 0x22, 0x01 };

    private readonly ITestOutputHelper output;

    public IncrementalFlushProbes(ITestOutputHelper output) => this.output = output;

    [AtlasScenario(TimeoutMs = 240_000)]
    public async Task UnsavedChanges_Should_BeLostOnVanillaAndKeptOnStratum_When_ColumnUnloadedByApi()
    {
        if (ServerFlavor.IsStratum)
        {
            await AssertDocumentedDefaults();
        }

        Outcome outcome = await RunUnsavedChangesCycle(AnchorAt(200, 0), flushExpected: ServerFlavor.IsStratum);

        AssertBaselineSurvived(outcome);
        if (ServerFlavor.IsStratum)
        {
            Assert.True(outcome.ChangesPresent,
                $"the changed pattern is gone after the API unload and reload on stratum: the incremental dirty flush did not write it " +
                $"within {WindowTicks} ticks; {outcome.Describe()}");
            Assert.True(outcome.Moddata != null && outcome.Moddata.SequenceEqual(ChangedMarker),
                $"the chunk moddata reads back as the old value after the API unload and reload on stratum: the incremental dirty flush did " +
                $"not write it within {WindowTicks} ticks; {outcome.Describe()}");
            Assert.False(outcome.MapChunkDirty,
                $"the map chunk is still flagged dirty after {WindowTicks} ticks on stratum with the flush on: the flush never reached it; {outcome.Describe()}");
        }
        else
        {
            AssertChangesLost(outcome);
        }
    }

    [AtlasScenario(TimeoutMs = 240_000)]
    public async Task UnsavedChanges_Should_BeLost_When_IncrementalFlushDisabled()
    {
        // Same flow as above with the toggle off: vanilla has no toggle (a no-op handle), so its
        // leg repeats the first scenario and Stratum has to converge on it.
        await using IAsyncDisposable toggle = await StratumSetting.Set(World, FlushKey, "false");
        await AssertFlushReads("false");

        Outcome outcome = await RunUnsavedChangesCycle(AnchorAt(0, 200), flushExpected: false);

        AssertBaselineSurvived(outcome);
        AssertChangesLost(outcome);
    }

    [AtlasScenario(TimeoutMs = 240_000)]
    public async Task ModifiedColumn_Should_PersistOnNaturalUnload_When_IncrementalFlushDisabled()
    {
        await using IAsyncDisposable toggle = await StratumSetting.Set(World, FlushKey, "false");
        await AssertFlushReads("false");

        BlockPos anchor = AnchorAt(200, 200);
        int chunkX = anchor.X / ChunkSize;
        int chunkZ = anchor.Z / ChunkSize;

        // Nothing is saved explicitly for this column. The world save only resets vanilla's
        // 300 s autosave timer, so the unload below is the one writer left on both flavors.
        await ChunkPersistence.SaveNow(World);
        try
        {
            await ChunkPersistence.LoadColumn(World, anchor);
            AssertNoGraniteAbove(anchor);
            List<BlockPos> pattern = await ChunkPersistence.WritePatternConfirmed(World, anchor, saltForCycle: 0);
            ChunkPersistence.SetColumnModdata(World, anchor, NaturalChunkMarker);
            ChunkPersistence.SetMapChunkModdata(World, anchor, NaturalMapMarker);

            try
            {
                await ChunkPersistence.WaitNaturalUnload(World, anchor, NaturalUnloadTimeoutTicks);
            }
            catch (ScenarioTimeoutException)
            {
                Assert.Fail($"the engine did not unload the column at {anchor} by itself within {NaturalUnloadTimeoutTicks} ticks on " +
                    $"{ServerFlavor.Name} (no player, not kept loaded); setup is invalid");
            }

            // The map chunk is removed in the same pass as the chunks of its column. Reading its
            // moddata after the reload only proves the database round trip if it really left memory.
            Assert.True(World.Api.WorldManager.GetMapChunk(chunkX, chunkZ) == null,
                $"the map chunk of {anchor} is still in memory after the natural unload on {ServerFlavor.Name}; setup is invalid");

            await World.Ticks(UnloadWriteSettleTicks);
            await ChunkPersistence.ReloadColumn(World, anchor, expectModdata: false, keepLoaded: true);

            byte[]? chunkModdata = ChunkPersistence.ReadColumnModdata(World, anchor);
            byte[]? mapModdata = ChunkPersistence.ReadMapChunkModdata(World, anchor);
            Assert.True(ChunkPersistence.PatternPresent(World, pattern),
                $"the pattern written before the natural unload is gone after the reload on {ServerFlavor.Name} with the incremental flush disabled");
            Assert.True(chunkModdata != null && chunkModdata.SequenceEqual(NaturalChunkMarker),
                $"the chunk moddata is {Hex(chunkModdata)} after the natural unload and reload on {ServerFlavor.Name} with the incremental flush disabled, " +
                $"expected {Hex(NaturalChunkMarker)}");
            Assert.True(mapModdata != null && mapModdata.SequenceEqual(NaturalMapMarker),
                $"the map chunk moddata is {Hex(mapModdata)} after the natural unload and reload on {ServerFlavor.Name} with the incremental flush disabled, " +
                $"expected {Hex(NaturalMapMarker)}");
        }
        finally
        {
            World.Api.WorldManager.UnloadChunkColumn(chunkX, chunkZ);
        }
    }

    /// <summary>The shared flow of the two API unload scenarios: baseline written and saved, changes
    /// left unsaved, the window, the guards, the unload, the reload. Returns what the reloaded
    /// column holds; the caller asserts per flavor.</summary>
    private async Task<Outcome> RunUnsavedChangesCycle(BlockPos anchor, bool flushExpected)
    {
        int chunkX = anchor.X / ChunkSize;
        int chunkZ = anchor.Z / ChunkSize;
        try
        {
            await ChunkPersistence.LoadColumn(World, anchor, keepLoaded: true);
            AssertNoGraniteAbove(anchor);

            List<BlockPos> baseline = await ChunkPersistence.WritePatternConfirmed(World, anchor, saltForCycle: 0);
            ChunkPersistence.SetColumnModdata(World, anchor, BaselineMarker);
            await ChunkPersistence.SaveNow(World);

            List<BlockPos> changes = await ChunkPersistence.WritePatternConfirmed(World, anchor, saltForCycle: 1);
            ChunkPersistence.SetColumnModdata(World, anchor, ChangedMarker);
            ChunkPersistence.SetMapChunkModdata(World, anchor, ChangedMarker);
            await World.Ticks(WindowTicks);

            // Nothing may have touched the in-memory state: a column that lost its changes while
            // still loaded would read as a lost write after the reload.
            byte[]? liveModdata = ChunkPersistence.ReadColumnModdata(World, anchor);
            byte[]? liveMapModdata = ChunkPersistence.ReadMapChunkModdata(World, anchor);
            Assert.True(
                ChunkPersistence.PatternPresent(World, baseline) && ChunkPersistence.PatternPresent(World, changes)
                    && liveModdata != null && liveModdata.SequenceEqual(ChangedMarker)
                    && liveMapModdata != null && liveMapModdata.SequenceEqual(ChangedMarker),
                $"the loaded column no longer holds the changes it was given after {WindowTicks} ticks on {ServerFlavor.Name}; setup is invalid");

            bool? chunkDirty = ReadDirty(World.Api.WorldManager.GetChunk(anchor));
            bool? mapChunkDirty = ReadDirty(World.Api.WorldManager.GetMapChunk(chunkX, chunkZ));
            Assert.True(chunkDirty.HasValue && mapChunkDirty.HasValue,
                $"the DirtyForSaving flag of the chunk or the map chunk is not readable on {ServerFlavor.Name}; setup is invalid");
            output.WriteLine($"[{ServerFlavor.Name}] after {WindowTicks} ticks: chunk dirty={chunkDirty}, map chunk dirty={mapChunkDirty}, flush expected={flushExpected}");
            if (!flushExpected)
            {
                Assert.True(chunkDirty == true && mapChunkDirty == true,
                    $"the column was saved during the window (chunk dirty={chunkDirty}, map chunk dirty={mapChunkDirty}) although nothing should write it " +
                    $"on {ServerFlavor.Name} here, so a lost change cannot be observed; setup is invalid");
            }

            await ChunkPersistence.UnloadColumn(World, anchor);
            await ChunkPersistence.ReloadColumn(World, anchor, expectModdata: false, keepLoaded: true);

            return new Outcome(
                ChunkPersistence.PatternPresent(World, baseline),
                ChunkPersistence.PatternPresent(World, changes),
                ChunkPersistence.ReadColumnModdata(World, anchor),
                chunkDirty!.Value,
                mapChunkDirty!.Value);
        }
        finally
        {
            World.Api.WorldManager.UnloadChunkColumn(chunkX, chunkZ);
        }
    }

    /// <summary>The stored copy was applied: the baseline saved before the changes reads back. Without it
    /// the reload says nothing about what was written, whichever flavor runs.</summary>
    private static void AssertBaselineSurvived(Outcome outcome) =>
        Assert.True(outcome.BaselinePresent && outcome.Moddata != null,
            $"the baseline saved with World.SaveNow did not come back after the reload on {ServerFlavor.Name}; setup is invalid ({outcome.Describe()})");

    /// <summary>The reloaded column is the saved baseline: the second pattern is absent and the
    /// moddata is the baseline value (not null: that would be a regenerated column).</summary>
    private static void AssertChangesLost(Outcome outcome)
    {
        Assert.False(outcome.ChangesPresent,
            $"the changed pattern survived the API unload and reload on {ServerFlavor.Name} although nothing should have saved it; {outcome.Describe()}");
        Assert.True(outcome.Moddata != null && outcome.Moddata.SequenceEqual(BaselineMarker),
            $"the chunk moddata is not the saved baseline after the API unload and reload on {ServerFlavor.Name}; {outcome.Describe()}");
    }

    private async Task AssertFlushReads(string expected)
    {
        if (!ServerFlavor.IsStratum)
        {
            return;
        }

        string? actual = await StratumSetting.Get(World, FlushKey);
        Assert.True(actual == expected, $"{FlushKey} reads '{actual}' after the toggle on stratum, expected '{expected}'; setup is invalid");
    }

    /// <summary>The window arithmetic comes from the documented defaults; a changed default must read as
    /// a stale probe, not as a flush that failed.</summary>
    private async Task AssertDocumentedDefaults()
    {
        string? enabled = await StratumSetting.Get(World, "Performance.AutoSave.Enabled");
        string? flush = await StratumSetting.Get(World, FlushKey);
        int interval = await ReadInt("Performance.AutoSave.IncrementalFlushIntervalSeconds");
        int refresh = await ReadInt("Performance.AutoSave.SaveScanRefreshSeconds");
        double windowSeconds = WindowTicks * NominalTickSeconds;
        Assert.True(enabled == "true" && flush == "true" && interval + refresh < windowSeconds,
            $"stratum autosave defaults are not the documented ones (AutoSave.Enabled={enabled}, IncrementalDirtyFlush={flush}, " +
            $"IncrementalFlushIntervalSeconds={interval}, SaveScanRefreshSeconds={refresh}; expected true, true and a sum below the {windowSeconds:0.#} s window): " +
            "the window of this probe is stale; setup is invalid");
    }

    private async Task<int> ReadInt(string path)
    {
        string? text = await StratumSetting.Get(World, path);
        Assert.True(int.TryParse(text, out int value), $"{path} reads '{text}' on stratum, not an integer; setup is invalid");
        return value;
    }

    /// <summary>A column origin on a chunk boundary, 200 or so blocks from the spawn, with the pattern layers
    /// (anchor Y + 2 and + 3) inside the anchor's own chunk: the moddata chunk, the baseline and the
    /// changes are then one chunk, and the eight by sixteen footprint of the pattern stays in the column.</summary>
    private BlockPos AnchorAt(int dx, int dz)
    {
        BlockPos spawn = World.Spawn;
        int y = spawn.Y + 1;
        if (y % ChunkSize > ChunkSize - 4)
        {
            y += 8;
        }

        return new BlockPos((spawn.X + dx) / ChunkSize * ChunkSize, y, (spawn.Z + dz) / ChunkSize * ChunkSize, 0);
    }

    /// <summary>The pattern cells of both salts (x 0 to 7, z 0 to 15, y + 2 and + 3 of the anchor) must not
    /// hold granite yet, or "pattern present" could not tell a written block from a native one.</summary>
    private void AssertNoGraniteAbove(BlockPos anchor)
    {
        for (int dx = 0; dx < 8; dx++)
        {
            for (int dz = 0; dz < 16; dz++)
            {
                for (int dy = 2; dy <= 3; dy++)
                {
                    BlockPos pos = anchor.AddCopy(dx, dy, dz);
                    Assert.True(World.BlockAt(pos).Code.ToString() != "game:rock-granite",
                        $"granite already stands at {pos} on {ServerFlavor.Name}, inside the pattern footprint; setup is invalid");
                }
            }
        }
    }

    /// <summary>Reads the engine's public DirtyForSaving field of a server chunk or map chunk, null when it
    /// is not there.</summary>
    private static bool? ReadDirty(object? holder) =>
        holder?.GetType()
            .GetField("DirtyForSaving", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(holder) as bool?;

    private static string Hex(byte[]? bytes) => bytes == null ? "null" : Convert.ToHexString(bytes);

    private sealed record Outcome(bool BaselinePresent, bool ChangesPresent, byte[]? Moddata, bool ChunkDirty, bool MapChunkDirty)
    {
        public string Describe() =>
            $"baseline pattern {(BaselinePresent ? "present" : "absent")}, changed pattern {(ChangesPresent ? "present" : "absent")}, " +
            $"chunk moddata {Hex(Moddata)}, chunk dirty {ChunkDirty}, map chunk dirty {MapChunkDirty} after the window";
    }
}
