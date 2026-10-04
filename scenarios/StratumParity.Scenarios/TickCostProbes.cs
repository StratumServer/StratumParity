using System.Globalization;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Xunit;
using Xunit.Abstractions;

namespace StratumParity.Scenarios;

/// <summary>
/// Informational perf pack: measures the embedded server's per-tick work time (ms/tick) under a
/// fixed load of active straw dummies clustered next to the player, on both flavors, and emits it
/// to the TRX so the dashboard can trend it.
///
/// This is deliberately NOT a pass/fail perf gate. Vanilla and Stratum run in separate CI jobs on
/// separate shared runners (no same-machine pairing), and the engine counter has 1ms integer
/// resolution, so an absolute or tight-ratio assertion cannot be made reliable. Instead each run
/// records a number, the dashboard plots the two trend lines, and a human reads regressions. The
/// in-code assertions are purely structural (finite numbers, the server actually ticked, load did
/// not make ticks cheaper). The metric is documented on MeasureMedian, and the README perf section
/// has the full caveats.
///
/// The dummies sit inside the near band (&lt; 32 blocks): both flavors tick every dummy every tick,
/// so ms/tick measures the CORE tick loop and is comparable across flavors. It captures Stratum's
/// per-tick allocation/LINQ reductions (measured locally ~2.7x cheaper than vanilla). A far-band
/// variant was tried and dropped: beyond the tracking range vanilla already deactivates the
/// dummies, so far-band cost is ~0 on both flavors and shows no clean signal; Stratum's
/// distance-band throttle is covered by the tick-COUNT probes in EntityTickingProbes instead.
/// </summary>
public class TickCostProbes : AtlasScenarioBase
{
    // Load sizing and protocol constants. DummyCount is tuned so loaded work clears the 1ms
    // quantization floor by a wide margin while staying below the overload threshold (~45ms) that
    // would trip Stratum's adaptive path; the rest set the two-level-median sampling that rejects
    // GC spikes. Measured locally: ~1000 near-band dummies land around 3-8 ms/tick.
    private const int DummyCount = 1000;
    private const int WarmupTicks = 300;
    private const int Windows = 5;
    private const int TicksPerWindow = 90;

    private readonly ITestOutputHelper output;

    public TickCostProbes(ITestOutputHelper output) => this.output = output;

    // No FreshWorld: this class has a single scenario, so its host is already a clean, empty,
    // player-less superflat world at first run and the idle baseline is genuinely idle. Forcing a
    // recycle would only add a reboot (and thus more exposure to the Stratum#151 boot race).
    [AtlasScenario(TimeoutMs = 300_000)]
    public Task TickCost_NearBand() => Measure("near");

    private async Task Measure(string label)
    {
        // Idle baseline first, on the empty world, so the loaded number can be read against the
        // machine's own idle cost (a self-relative sanity check that cancels machine speed).
        await World.JoinPlayer("tickcost-anchor");
        await World.Ticks(5);

        await Settle();
        double idleMs = await MeasureMedian();

        long simBefore = SafeSimTicks();
        int spawned = await SpawnDummies();
        Assert.True(spawned > DummyCount / 2,
            $"only spawned {spawned}/{DummyCount} dummies on {ServerFlavor.Name}; load did not land");

        await Settle();
        double loadedMs = await MeasureMedian();
        long simAfter = SafeSimTicks();

        // Structural assertions only, never a magnitude gate.
        Assert.True(double.IsFinite(idleMs) && idleMs >= 0, $"idle ms/tick not finite on {ServerFlavor.Name}: {idleMs}");
        Assert.True(double.IsFinite(loadedMs) && loadedMs >= 0, $"loaded ms/tick not finite on {ServerFlavor.Name}: {loadedMs}");
        long simElapsed = simAfter - simBefore;
        Assert.True(simElapsed >= Windows * TicksPerWindow / 2,
            $"server barely ticked during measure on {ServerFlavor.Name}: {simElapsed} sim ticks");
        Assert.True(loadedMs + 1.0 >= idleMs,
            $"load made ticks cheaper on {ServerFlavor.Name} (idle {idleMs:F2} > loaded {loadedMs:F2}); measurement is broken");

        // Invariant culture: the metric lines are machine-parsed downstream, so the decimal
        // separator must be a dot regardless of the runner's locale.
        var inv = CultureInfo.InvariantCulture;
        output.WriteLine($"ATLAS_METRIC ms_per_tick={loadedMs.ToString("F3", inv)}");
        output.WriteLine($"ATLAS_METRIC idle_ms_per_tick={idleMs.ToString("F3", inv)}");
        output.WriteLine($"ATLAS_METRIC load_entities={spawned}");
        output.WriteLine($"[{label} band] {ServerFlavor.Name}: loaded {loadedMs.ToString("F2", inv)} ms/tick, idle {idleMs.ToString("F2", inv)} ms/tick, {spawned} dummies, {simElapsed} sim ticks");
    }

    /// <summary>GC, then a long unmeasured warmup so JIT, chunk loads and the mass spawn settle
    /// before any window is kept.</summary>
    private async Task Settle()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        await World.Ticks(WarmupTicks);
    }

    /// <summary>Median, across Windows windows of TicksPerWindow passes each, of the mean per-pass
    /// busy time: <c>PassTimingStats.MeanMs</c> from <c>MeasureTicks</c>, which is the engine's own
    /// <c>tickTimeTotal / ticksTotal</c> taken over exactly that window's passes. Each pass is the
    /// engine's whole-millisecond busy time (pacing sleep excluded), so the mean is a trend value,
    /// not a sub-millisecond timing. The median across windows rejects one that caught a GC pause.</summary>
    private async Task<double> MeasureMedian()
    {
        var samples = new List<double>(Windows);
        for (int w = 0; w < Windows; w++)
        {
            TickMeasurement window = await World.MeasureTicks(TicksPerWindow);
            samples.Add(window.BusyTime.MeanMs);
        }
        samples.Sort();
        return samples[samples.Count / 2];
    }

    private long SafeSimTicks()
    {
        try
        {
            return World.EntitySimulationTicks;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Spawns DummyCount stationary straw dummies clustered around a point in the near or
    /// far band relative to the anchor player's actual chunk. Straw dummies stand still, so
    /// Stratum's SkipMovingEntities exemption never fires and the inanimate throttle applies in the
    /// far band. Returns how many actually landed.</summary>
    private async Task<int> SpawnDummies()
    {
        BlockPos anchor = World.Spawn;
        BlockPos center = anchor.AddCopy(12, 1, 0);

        // Load the target column and wait for it before spawning: LoadChunkColumnPriority is
        // asynchronous, and spawning into an unloaded chunk silently drops the entity (which is
        // exactly what happened on the far band before this wait).
        World.Api.WorldManager.LoadChunkColumnPriority(center.X / 32, center.Z / 32,
            new Vintagestory.API.Server.ChunkLoadOptions { KeepLoaded = true });
        await World.Until(
            () => World.Api.World.BlockAccessor.GetChunkAtBlockPos(center) != null,
            timeoutTicks: 600);

        for (int i = 0; i < DummyCount; i++)
        {
            // Spread across a small footprint so all positions stay in loaded chunks near the
            // cluster center; several dummies share each cell, which is fine (they stand still).
            int dx = i % 8;
            int dz = (i / 8) % 8;
            BlockPos pos = center.AddCopy(dx, 0, dz);
            World.SpawnEntity("game:strawdummy", pos);
        }
        await World.Ticks(2);

        var area = new Cuboidi(center.AddCopy(-4, -4, -4), center.AddCopy(12, 6, 12));
        return World.EntitiesIn(area).Count(e => e.Code?.Path == "strawdummy");
    }
}
