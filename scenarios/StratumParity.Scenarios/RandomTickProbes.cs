using System.Diagnostics;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Xunit;
using Xunit.Abstractions;

namespace StratumParity.Scenarios;

/// <summary>
/// Statistical probes for Stratum's random tick limiting
/// (Performance.SimulationDistance.LimitRandomTicks, enabled by default, radius 96
/// blocks). The staged stratumparityprobe mod supplies a block that turns to granite on
/// every random tick it receives, so coverage becomes countable world state: a platform
/// of probe blocks near the player converts steadily on both flavors, a platform 200
/// blocks out converts on vanilla and must stay untouched on Stratum.
///
/// The effective conversion rate stacks many engine factors (random sampling, per-chunk
/// caps, Stratum's per-pass chunk cap with rotation and slice striding, and a
/// Calendar.SpeedOfTime scale), so positive assertions claim CANDIDACY, not a rate: one
/// single conversion proves the column is inside the sampled radius (floor of 1, long
/// converging ceiling). Only the Stratum far-platform assertion is exact zero, which is
/// rate-insensitive: outside the clamped radius the chunk is never a candidate at all
/// (decompiled: candidacy is a pure grid scan of server-loaded chunks around each
/// Playing client's chunk, ±range in all three axes). That chunk is the one the ENGINE
/// recorded for the player entity (Entity.InChunkIndex3d), not the one its position
/// implies: it is resynchronised only by a 1000 ms listener, so after a teleport it lags
/// the position by up to a second, on vanilla and Stratum alike. PlacePlatforms waits for
/// the two to agree, and AssertAnchorChunkPinned turns any later disagreement into an
/// invalid-setup failure instead of a bogus "converted" count.
///
/// The burst scenarios measure the other end of the same pipeline: the per-chunk BUDGET.
/// One whole chunk of randomtickcounter blocks (a block that logs every random tick sampled
/// onto it from ShouldReceiveServerGameTicks, off the main thread, and never accepts one)
/// turns every sampled position into a log entry, so each pass over the chunk shows up as a
/// burst of closely spaced timestamps whose size is the chunk's budget for that pass. Vanilla
/// samples (int)(RandomBlockTicksPerChunk * SpeedOfTime / 60) = 16 positions per chunk per
/// pass. Stratum (Performance.BlockTicks, on by default) caps that at MaxRandomTicksPerChunk
/// = 8 (a halving nothing documents) and visits each chunk in only one of every
/// RandomTickSliceCount = 8 passes, which is why burst SIZE, not rate, is asserted: the size
/// is exact, the rate stacks pass spacing, slice striding and the adaptive overload cap.
/// </summary>
[AtlasWorld(Mods = new[] { "mods/randomtickprobe" })]
public class RandomTickProbes : AtlasScenarioBase
{
    // Platforms are 16x16x4 slabs (1024 blocks in a single column) so that even heavily
    // throttled sampling produces a first conversion well within the ceiling. The floor
    // is deliberately "more than zero": rate-based floors proved flaky on slow CI
    // runners because the engine's stacked rate factors can slow sampling several-fold.
    internal const int ConversionFloor = 0;
    internal const int ConvergenceTimeoutTicks = 2400;
    // The engine resynchronises the anchor's chunk about every second (about 14 ticks
    // observed): generous, so only a stuck anchor can exhaust it.
    private const int ChunkSyncTimeoutTicks = 150;
    private const int PlatformEdge = 16;
    private const int PlatformLayers = 4;

    private readonly ITestOutputHelper output;

    public RandomTickProbes(ITestOutputHelper output) => this.output = output;

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task FarPlatform_Should_ConvertOnVanillaAndStayUntouchedOnStratum_When_DefaultsActive()
    {
        PlatformSet platforms = await PlacePlatforms(World, "rt-anchor");

        await WaitForConversions(World, platforms, platforms.Near, "near");

        if (ServerFlavor.IsStratum)
        {
            // Random ticking is proven live by the near clock; the far column must now
            // stay untouched over a fixed observation window.
            await World.Ticks(450);
            AssertColumnStillLoaded(World, platforms.Far[0]);
            // Before the exact-zero assertion: a centre that drifted or fell out of sync
            // makes the far column a legitimate candidate, which is a broken setup and
            // must never read as a Stratum clamp failure.
            AssertAnchorChunkPinned(World, platforms.Anchor, platforms.AnchorChunkIndex,
                "after the observation window");
            int farConverted = CountConverted(World, platforms.Far);
            Assert.True(farConverted == 0,
                $"far platform received random ticks on stratum: {farConverted}/{platforms.Far.Count} converted");
        }
        else
        {
            await WaitForConversions(World, platforms, platforms.Far, "far");
        }
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task RandomTicksPerChunk_Should_BeSixteenOnVanillaAndEightOnStratum_When_ChunkIsFull()
    {
        if (ServerFlavor.IsStratum)
        {
            // The expectation below only holds for these two values; pin them so a changed
            // default reads as what it is instead of as a failed cap.
            await AssertSetting("Performance.BlockTicks.Enabled", "true");
            await AssertSetting("Performance.BlockTicks.MaxRandomTicksPerChunk", StratumRandomTickCap.ToString());
        }

        BurstRun run = await ProbeBursts("rt-burst-def", zOffset: 400);

        if (ServerFlavor.IsStratum)
        {
            // Stratum's adaptive cap halves the budget again while the recent server tick
            // averages above Performance.BlockTicks.OverloadTickMs: a loaded runner would read
            // as a cap failure, so it is a broken setup instead.
            Assert.True(run.Load.WorstWindowMeanMs <= OverloadTickMs,
                $"server tick averaged {run.Load.WorstWindowMeanMs:F1} ms in a window (adaptive cap above {OverloadTickMs} ms) " +
                $"on stratum, which halves bursts again; setup is invalid ({run.Describe()})");
            Assert.True(run.Bursts.Max() == run.ExpectedStratum,
                $"largest burst is not the {StratumRandomTickCap}-position cap ({run.ExpectedStratum}) on stratum ({run.Describe()})");
        }
        else
        {
            Assert.True(run.Bursts.All(b => b == run.ExpectedVanilla),
                $"a burst differs from the vanilla budget of {run.ExpectedVanilla} on vanilla ({run.Describe()})");
        }
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task RandomTicksPerChunk_Should_BeSixteen_When_BlockTicksDisabled()
    {
        // Hot toggle: Performance.BlockTicks.Enabled is read on every pass (interval, cap,
        // slicing and the adaptive scale all hang off it). On vanilla this is a no-op handle,
        // so the vanilla leg proves that 16 is the default behavior the toggle restores.
        // The fixture named stratum-blockticks-off does not cover this setting: it turns off
        // SimulationDistance.LimitBlockGameTickListeners, which has nothing to do with it.
        await using IAsyncDisposable toggle = await StratumSetting.Set(World, "Performance.BlockTicks.Enabled", "false");
        if (ServerFlavor.IsStratum)
        {
            await AssertSetting("Performance.BlockTicks.Enabled", "false");
        }

        BurstRun run = await ProbeBursts("rt-burst-off", zOffset: -400);

        Assert.True(run.Bursts.All(b => b == run.ExpectedVanilla),
            $"a burst differs from the budget of {run.ExpectedVanilla} with block ticks disabled on {ServerFlavor.Name} ({run.Describe()})");
    }

    internal static async Task WaitForConversions(
        Atlas.Api.IWorldSession world, PlatformSet platforms, List<BlockPos> platform, string label)
    {
        try
        {
            await world.Until(
                () => CountConverted(world, platform) > ConversionFloor,
                timeoutTicks: ConvergenceTimeoutTicks);
        }
        catch (Exception)
        {
            AssertColumnStillLoaded(world, platform[0]);
            // A starved wait on a drifted or unsynced centre is a broken setup, not a divergence.
            AssertAnchorChunkPinned(world, platforms.Anchor, platforms.AnchorChunkIndex,
                $"while waiting for the {label} platform");
            int converted = CountConverted(world, platform);
            Assert.Fail(
                $"{label} platform never reached {ConversionFloor + 1} conversions on {ServerFlavor.Name} " +
                $"within {ConvergenceTimeoutTicks} ticks ({converted}/{platform.Count} converted)");
        }
    }

    /// <summary>The placed platforms plus the anchor and the engine chunk index they were
    /// derived from, which every later guard compares against.</summary>
    internal sealed record PlatformSet(
        Atlas.Api.ITestPlayer Anchor, long AnchorChunkIndex, List<BlockPos> Near, List<BlockPos> Far);

    internal static async Task<PlatformSet> PlacePlatforms(
        Atlas.Api.IWorldSession world, string anchorName)
    {
        AnchorPin pin = await PinAnchor(world, anchorName, world.Spawn, viewDistance: 256);
        Atlas.Api.ITestPlayer anchor = pin.Anchor;
        BlockPos anchorPos = pin.Position;
        int anchorChunkX = pin.ChunkX;
        int anchorChunkZ = pin.ChunkZ;
        long anchorChunkIndex = pin.ChunkIndex;

        // Vanilla gates random ticks itself: only chunks within BlockTickChunkRange
        // (5 chunks by default) of a Playing client are sampled at all (decompiled:
        // pure grid scan of server-loaded chunks, ±range on all three axes). Stratum's
        // LimitRandomTicks merely clamps that range down to RandomTickDistanceBlocks
        // (96 blocks = 3 chunks by default). The far platform therefore sits in the
        // column at chunk distance EXACTLY 4 from the anchor's own chunk: inside the
        // vanilla range, outside the Stratum clamp. Both platforms are padded 8 blocks
        // into their column so all 16x16 positions stay in a single chunk column.
        BlockPos nearCorner = new BlockPos(anchorChunkX * 32 + 8, anchorPos.Y + 1, anchorChunkZ * 32 + 8, 0);
        int farChunkX = anchorChunkX + 4;
        BlockPos farCorner = new BlockPos(farChunkX * 32 + 8, anchorPos.Y + 1, anchorChunkZ * 32 + 8, 0);

        // KeepLoaded: the observation windows outlive the unload timer for a column
        // with no player nearby. Safe for the measurement: the random tick limit has no
        // force-loaded exemption (unlike the block listener limit), it gates purely on
        // grid distance to Playing clients.
        world.Api.WorldManager.LoadChunkColumnPriority(farChunkX, anchorChunkZ,
            new Vintagestory.API.Server.ChunkLoadOptions { KeepLoaded = true });
        await world.Until(
            () => world.Api.World.BlockAccessor.GetChunkAtBlockPos(farCorner) != null,
            timeoutTicks: 600);

        // Last check before the platforms exist: the wait above takes ticks too.
        AssertAnchorChunkPinned(world, anchor, anchorChunkIndex, "before placing the platforms");

        return new PlatformSet(anchor, anchorChunkIndex, PlacePlatform(world, nearCorner), PlacePlatform(world, farCorner));
    }

    /// <summary>A Playing anchor teleported to a fixed position, with the chunk the engine
    /// itself recorded for it (the random tick centre).</summary>
    internal sealed record AnchorPin(
        Atlas.Api.ITestPlayer Anchor, BlockPos Position, long ChunkIndex, int ChunkX, int ChunkY, int ChunkZ);

    internal static async Task<AnchorPin> PinAnchor(
        Atlas.Api.IWorldSession world, string anchorName, BlockPos target, int? viewDistance = null)
    {
        // The anchor player centers the 96-block random tick radius on its position. Since
        // Atlas 0.9.1, JoinPlayer itself waits for the server assets packet build to
        // complete (issue #84, born from this suite's CI crash), so the mass SetBlock
        // that follows no longer races the build's off-thread item enumeration.
        Atlas.Api.ITestPlayer anchor = await world.JoinPlayer(anchorName);
        if (viewDistance != null)
        {
            anchor.Player.WorldData.DesiredViewDistance = viewDistance.Value;
        }

        // A target away from spawn sits in a column nothing has loaded yet: load it first,
        // so the teleport does not wait on (or time out in) a lazy chunk load.
        if (world.Api.World.BlockAccessor.GetChunkAtBlockPos(target) == null)
        {
            world.Api.WorldManager.LoadChunkColumnPriority(target.X / 32, target.Z / 32,
                new Vintagestory.API.Server.ChunkLoadOptions { KeepLoaded = true });
            await world.Until(
                () => world.Api.World.BlockAccessor.GetChunkAtBlockPos(target) != null,
                timeoutTicks: 1200);
        }

        // The join scatters players around world spawn (SpawnPlayerRandomlyAround, up to
        // the world's spawnRadius: 50 blocks here; the 15 passed to it is the number of
        // placement tries, not a radius). Spawn sits on a chunk corner, so the scatter
        // shifts the anchor's CHUNK (by up to two) on most joins: geometry derived from
        // World.Spawn therefore varied by whole chunks per run.
        // Pin the anchor to a fixed position, then derive everything from its ACTUAL chunk.
        await anchor.TeleportTo(target);
        await world.Ticks(5);

        BlockPos anchorPos = anchor.Position;
        long anchorChunkIndex = ChunkIndexOfPosition(world, anchor);

        // Pinning the position is not enough. Random tick candidacy is centred on the
        // chunk the ENGINE recorded for the player entity (Entity.InChunkIndex3d), and a
        // teleport only moves the position: the chunk is resynchronised by a 1000 ms
        // listener, so it keeps pointing at the join-scatter chunk for up to a second.
        // The "distance 4" far column of PlacePlatforms is then at distance 3 from that stale centre,
        // a legitimate Stratum candidate for the few ticks after placement, and one
        // block occasionally converts (flake: "far platform received random ticks on
        // stratum: 1/1024 converted"). Vanilla has the same lag, it just does not care.
        // Place nothing until the engine's chunk matches the position's.
        try
        {
            await world.Until(
                () => anchor.Entity.InChunkIndex3d == anchorChunkIndex,
                timeoutTicks: ChunkSyncTimeoutTicks);
        }
        catch (Atlas.Api.ScenarioTimeoutException)
        {
            Assert.Fail(
                $"anchor's engine chunk never matched its position within {ChunkSyncTimeoutTicks} ticks of the " +
                $"teleport on {ServerFlavor.Name} ({DescribeAnchorChunk(world, anchor, anchorChunkIndex)}); setup is invalid");
        }

        return new AnchorPin(anchor, anchorPos, anchorChunkIndex, anchorPos.X / 32, anchorPos.Y / 32, anchorPos.Z / 32);
    }

    /// <summary>Setup-failure guard, same semantics as
    /// <see cref="EntityTickingProbes.AssertAnchorStillNear"/>: the platforms' geometry is
    /// derived from the pinned chunk, so the anchor's position AND the engine's own chunk
    /// for it (the random tick centre) must both still be that chunk. Anything else means
    /// the run measured a different setup than the one it was designed for, and must not
    /// read as a Stratum divergence.</summary>
    internal static void AssertAnchorChunkPinned(
        Atlas.Api.IWorldSession world, Atlas.Api.ITestPlayer anchor, long pinnedChunkIndex, string moment)
    {
        bool pinned = ChunkIndexOfPosition(world, anchor) == pinnedChunkIndex
            && anchor.Entity.InChunkIndex3d == pinnedChunkIndex;
        Assert.True(pinned,
            $"anchor chunk not pinned {moment} on {ServerFlavor.Name} " +
            $"({DescribeAnchorChunk(world, anchor, pinnedChunkIndex)}); setup is invalid");
    }

    private static string DescribeAnchorChunk(
        Atlas.Api.IWorldSession world, Atlas.Api.ITestPlayer anchor, long pinnedChunkIndex) =>
        $"pinned chunk index {pinnedChunkIndex}, chunk index of its position {ChunkIndexOfPosition(world, anchor)}, " +
        $"engine chunk index (random tick centre) {anchor.Entity.InChunkIndex3d}, position {anchor.Position}";

    private static long ChunkIndexOfPosition(Atlas.Api.IWorldSession world, Atlas.Api.ITestPlayer anchor)
    {
        BlockPos pos = anchor.Position;
        return world.Api.WorldManager.ChunkIndex3D(pos.X / 32, pos.Y / 32, pos.Z / 32);
    }

    private static List<BlockPos> PlacePlatform(Atlas.Api.IWorldSession world, BlockPos corner)
    {
        var positions = new List<BlockPos>(PlatformEdge * PlatformEdge * PlatformLayers);
        for (int x = 0; x < PlatformEdge; x++)
        {
            for (int z = 0; z < PlatformEdge; z++)
            {
                for (int y = 0; y < PlatformLayers; y++)
                {
                    BlockPos pos = corner.AddCopy(x, y, z);
                    world.SetBlock("stratumparityprobe:probe", pos);
                    positions.Add(pos);
                }
            }
        }
        return positions;
    }

    internal static int CountConverted(Atlas.Api.IWorldSession world, List<BlockPos> platform)
    {
        int converted = 0;
        foreach (BlockPos pos in platform)
        {
            if (world.BlockAt(pos).Code.ToString() == "game:rock-granite")
            {
                converted++;
            }
        }
        return converted;
    }

    internal static void AssertColumnStillLoaded(Atlas.Api.IWorldSession world, BlockPos pos)
    {
        Assert.True(world.Api.World.BlockAccessor.GetChunkAtBlockPos(pos) != null,
            "far column unloaded mid-measurement; the probe window is too long for the unload timer");
    }

    // --- burst probes ---

    private const string CounterBlockCode = "stratumparityprobe:randomtickcounter";
    // Performance.BlockTicks.MaxRandomTicksPerChunk and OverloadTickMs defaults, pinned by the suite.
    private const int StratumRandomTickCap = 8;
    private const double OverloadTickMs = 45;
    // Two passes over the same chunk are never closer than this on Stratum (the slice pass
    // interval is 100 / 8 = 12 ms and the engine fires strictly after it, so 13 ms), while a
    // burst itself spans microseconds: any gap between 1 ms and 13 ms separates them. 10 ms
    // tolerates a preempted burst thread without ever fusing two passes.
    private const double BurstGapMs = 10;
    private const int MinBursts = 20;
    private const int SettleTicks = 60;
    private const int WindowTicks = 30;
    private const int RingRadius = 3;
    private static readonly TimeSpan CollectLimit = TimeSpan.FromSeconds(60);

    private async Task AssertSetting(string path, string expected)
    {
        string? actual = await StratumSetting.Get(World, path);
        Assert.True(string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            $"{path} is {actual ?? "unreadable"} on {ServerFlavor.Name}, the scenario expects {expected}; setup is invalid");
    }

    /// <summary>What a burst run measured: burst sizes in time order (first and last dropped,
    /// they may be cut by the window edges), the budget vanilla must show, and the tick load.</summary>
    private sealed record BurstRun(List<int> Bursts, int ExpectedVanilla, int ExpectedStratum, TickLoad Load)
    {
        public string Describe() =>
            $"{Bursts.Count} bursts, sizes {string.Join(", ", Bursts.GroupBy(b => b).OrderBy(g => g.Key).Select(g => $"{g.Key}x{g.Count()}"))}; " +
            $"tick mean {Load.MeanMs:F1} ms, worst window {Load.WorstWindowMeanMs:F1} ms over {Load.Passes} passes; " +
            $"expected {ExpectedVanilla} on vanilla, {ExpectedStratum} on stratum with the cap";
    }

    /// <summary>Busy time of the game thread over the windows of one run, the figure Stratum's
    /// adaptive cap reads (the engine's own tickTimeTotal / ticksTotal).</summary>
    private sealed class TickLoad
    {
        public int Passes { get; private set; }
        public long BusyMs { get; private set; }
        public double WorstWindowMeanMs { get; private set; }
        public double MeanMs => Passes == 0 ? 0 : (double)BusyMs / Passes;

        public async Task Run(IWorldSession world, int ticks)
        {
            TickMeasurement window = await world.MeasureTicks(ticks);
            Passes += window.Passes;
            BusyMs += window.BusyTime.TotalMs;
            WorstWindowMeanMs = Math.Max(WorstWindowMeanMs, window.BusyTime.MeanMs);
        }
    }

    /// <summary>One chunk filled with counter blocks, two chunks from the anchor's own.</summary>
    private sealed record CounterChunk(int X, int Y, int Z, IWorldChunk Chunk, int BlockId)
    {
        public bool Contains(RandomTickCall call) =>
            call.Pos.X >> 5 == X && call.Pos.Y >> 5 == Y && call.Pos.Z >> 5 == Z;
    }

    private async Task<BurstRun> ProbeBursts(string anchorName, int zOffset)
    {
        // Anchors of the scenarios sit 400 blocks either side of spawn: each one's random tick
        // radius, filled chunk and kept-loaded ring stay clear of the others and of the
        // platforms of FarPlatform_Should_ConvertOnVanillaAndStayUntouchedOnStratum_When_DefaultsActive,
        // which sit at spawn (the world is shared by every scenario of the class).
        AnchorPin pin = await PinAnchor(World, anchorName, World.Spawn.AddCopy(0, 0, zOffset));
        await LoadRing(World, pin);
        CounterChunk counters = await FillCounterChunk(World, pin);
        try
        {
            AssertFullOfCounters(counters);

            // Read in the test, the way the engine computes it: both flavors truncate
            // RandomBlockTicksPerChunk * SpeedOfTime / 60 (16 at the defaults), and Stratum
            // applies its cap to the first factor.
            float speed = World.Calendar.SpeedOfTime;
            int perChunk = World.Api.Server.Config.RandomBlockTicksPerChunk;
            int expectedVanilla = (int)((float)perChunk * speed / 60f);
            int expectedStratum = (int)((float)Math.Min(perChunk, StratumRandomTickCap) * speed / 60f);
            Assert.True(expectedStratum > 0 && expectedStratum < expectedVanilla,
                $"RandomBlockTicksPerChunk {perChunk} x SpeedOfTime {speed} / 60 gives {expectedVanilla} (cap {StratumRandomTickCap}: " +
                $"{expectedStratum}) on {ServerFlavor.Name}, so the Stratum cap cannot show; setup is invalid");

            // Let the fill, the toggle and the chunk loads settle, so the first bursts are
            // not measured while the server is still catching up.
            var load = new TickLoad();
            await load.Run(World, SettleTicks);

            List<int> bursts = await CollectBursts(World, counters, load);

            AssertAnchorChunkPinned(World, pin.Anchor, pin.ChunkIndex, "after the burst window");
            Assert.True(World.Calendar.SpeedOfTime == speed && World.Api.Server.Config.RandomBlockTicksPerChunk == perChunk,
                $"the random tick budget inputs changed during the window on {ServerFlavor.Name}; setup is invalid");

            var run = new BurstRun(bursts, expectedVanilla, expectedStratum, load);
            output.WriteLine($"[{anchorName}] {ServerFlavor.Name}: {run.Describe()}");
            return run;
        }
        finally
        {
            ClearCounterChunk(counters);
        }
    }

    /// <summary>Keeps the random tick range of the anchor loaded (Stratum clamps it to 3 chunks,
    /// vanilla scans 5, and Stratum slices by the position of a chunk among the loaded ones),
    /// so no chunk appears or disappears from the scan while bursts are counted.</summary>
    private static async Task LoadRing(IWorldSession world, AnchorPin pin)
    {
        world.Api.WorldManager.LoadChunkColumnPriority(
            pin.ChunkX - RingRadius, pin.ChunkZ - RingRadius, pin.ChunkX + RingRadius, pin.ChunkZ + RingRadius,
            new ChunkLoadOptions { KeepLoaded = true });
        try
        {
            await world.Until(() => RingLoaded(world, pin), timeoutTicks: 1200);
        }
        catch (ScenarioTimeoutException)
        {
            Assert.Fail($"the {2 * RingRadius + 1}x{2 * RingRadius + 1} chunk ring around the anchor never loaded " +
                $"on {ServerFlavor.Name}; setup is invalid");
        }
    }

    private static bool RingLoaded(IWorldSession world, AnchorPin pin)
    {
        for (int dx = -RingRadius; dx <= RingRadius; dx++)
        {
            for (int dz = -RingRadius; dz <= RingRadius; dz++)
            {
                if (world.Api.World.BlockAccessor.GetChunk(pin.ChunkX + dx, pin.ChunkY, pin.ChunkZ + dz) == null)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static async Task<CounterChunk> FillCounterChunk(IWorldSession world, AnchorPin pin)
    {
        Block? counter = world.Api.World.GetBlock(new AssetLocation(CounterBlockCode));
        Assert.True(counter != null && counter.BlockId != 0,
            $"block {CounterBlockCode} is not registered on {ServerFlavor.Name}: the class must stage mods/randomtickprobe; setup is invalid");

        // Two chunks east of the anchor's: inside the Stratum clamp (3) and the vanilla range (5).
        // The ring is loaded already, the wait only covers a chunk the engine dropped meanwhile.
        int x = pin.ChunkX + 2, y = pin.ChunkY, z = pin.ChunkZ;
        await world.Until(() => world.Api.World.BlockAccessor.GetChunk(x, y, z) != null, timeoutTicks: 600);
        IWorldChunk chunk = world.Api.World.BlockAccessor.GetChunk(x, y, z);

        // Straight into the chunk's block data: a block-by-block SetBlock of 32768 positions
        // would stall the game thread for a pass, and a slow pass right before the window is
        // exactly what Stratum's adaptive cap reads as overload. Each write takes the layer's
        // lock, the same way the tick thread's reads do.
        chunk.Unpack();
        IChunkBlocks data = chunk.Data;
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = counter!.BlockId;
        }

        chunk.MarkModified();
        return new CounterChunk(x, y, z, chunk, counter!.BlockId);
    }

    /// <summary>Every sampled position must be a counter: one air cell, one fluid cell or one
    /// leftover block would turn a burst into a short count that looks like a cap.</summary>
    private static void AssertFullOfCounters(CounterChunk counters)
    {
        counters.Chunk.Unpack();
        IChunkBlocks data = counters.Chunk.Data;
        int blocks = 0, fluids = 0;
        for (int i = 0; i < data.Length; i++)
        {
            if (data.GetBlockId(i, 1) == counters.BlockId)
            {
                blocks++;
            }

            if (data.GetFluid(i) != 0)
            {
                fluids++;
            }
        }

        Assert.True(blocks == data.Length && fluids == 0,
            $"chunk ({counters.X}, {counters.Y}, {counters.Z}) holds {blocks}/{data.Length} counter blocks and " +
            $"{fluids} fluid cells on {ServerFlavor.Name}; setup is invalid");
    }

    private static void ClearCounterChunk(CounterChunk counters)
    {
        if (counters.Chunk.Disposed)
        {
            return;
        }

        counters.Chunk.Unpack();
        for (int i = 0; i < counters.Chunk.Data.Length; i++)
        {
            counters.Chunk.Data[i] = 0;
        }

        counters.Chunk.MarkModified();
    }

    /// <summary>Counts random tick bursts of the filled chunk until MinBursts complete ones are
    /// in hand. The window loop is bounded by wall time, not by a tick count, because the number
    /// of passes per block tick interval depends on the engine's pacing.</summary>
    private static async Task<List<int>> CollectBursts(IWorldSession world, CounterChunk counters, TickLoad load)
    {
        ProbeLog.DrainRandomTickCalls(world); // discard what the settle logged
        var calls = new List<RandomTickCall>();
        var clock = Stopwatch.StartNew();

        // Two spare bursts: the first may begin before the drain and the last may be cut by
        // the final drain, so both are dropped.
        while (SplitBursts(calls).Count < MinBursts + 2 && clock.Elapsed < CollectLimit)
        {
            await load.Run(world, WindowTicks);
            calls.AddRange(ProbeLog.DrainRandomTickCalls(world).Where(counters.Contains));
        }

        List<int> all = SplitBursts(calls);
        Assert.True(all.Count >= MinBursts + 2,
            $"only {all.Count} bursts of the filled chunk in {clock.Elapsed.TotalSeconds:F0} s on {ServerFlavor.Name} " +
            $"({calls.Count} sampled positions); the chunk is not being random ticked, setup is invalid");
        return all.Skip(1).Take(all.Count - 2).ToList();
    }

    /// <summary>Sizes of the runs of timestamps separated by gaps above BurstGapMs, in time order.</summary>
    internal static List<int> SplitBursts(List<RandomTickCall> calls)
    {
        var sizes = new List<int>();
        int size = 0;
        double last = 0;
        foreach (RandomTickCall call in calls.OrderBy(c => c.Ms))
        {
            if (size > 0 && call.Ms - last > BurstGapMs)
            {
                sizes.Add(size);
                size = 0;
            }

            size++;
            last = call.Ms;
        }

        if (size > 0)
        {
            sizes.Add(size);
        }

        return sizes;
    }
}
