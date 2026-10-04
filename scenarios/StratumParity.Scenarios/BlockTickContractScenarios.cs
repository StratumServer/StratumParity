using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Xunit;
using Xunit.Abstractions;

namespace StratumParity.Scenarios;

/// <summary>
/// Contracts of the block simulation that a mod can rely on, pinned on both flavors with
/// the probe blocks of the staged stratumparityprobe mod. Everything is exact world state
/// or an exact notification count, never a timing.
///
/// Random tick position ownership: a handler may keep the BlockPos it receives (vanilla
/// queues a private copy per sampled tick). The position probe keeps every position and
/// audits them on later ticks, so a rewrite shows up as granite in the world (the check
/// is a port of Stratum's own scenario for StratumServer/Stratum#353).
///
/// Neighbour notifications: the engine delivers OnNeighbourBlockChange from a queue that
/// the 100 ms block simulation listener drains, at most 500 positions per pass
/// (ServerSystemBlockSimulation.HandleDirtyAndUpdatedBlocks). A plain SetBlock notifies
/// nobody, so each scenario raises the notification the way a mod does, with
/// BlockAccessor.TriggerNeighbourBlockUpdate on the changed position, and reads what the
/// neighbourcounter blocks logged (ProbeLog). A bulk accessor commit with synchronize and
/// relight enqueues each modified position once and notifies the block on it with
/// pos equal to neibpos.
///
/// The notification scenarios place their blocks in kept-loaded columns 200 blocks or more
/// apart (the position probe stays at spawn), so the class's scenarios never share
/// blocks, and each one drains the log right before its change (in the same synchronous
/// step, so no tick can slip in between) and filters nothing: any extra, duplicated or
/// missing notification fails the exact comparison.
/// </summary>
[AtlasWorld(Mods = new[] { "mods/randomtickprobe" })]
public class BlockTickContractScenarios : AtlasScenarioBase
{
    private const string PositionProbeCode = "stratumparityprobe:positionprobe";
    private const string NeighbourCounterCode = "stratumparityprobe:neighbourcounter";
    private const string GraniteCode = "game:rock-granite";
    private const string AndesiteCode = "game:rock-andesite";

    // Random tick position probe: a whole chunk footprint, 12 layers, so about a third of
    // the chunk is probe blocks and a sampled tick lands on one often enough to reach
    // MinHandlerRuns in well under a minute. The floor is low on purpose: the assertion is
    // an exact zero (strict branch), so it only has to prove that handlers ran.
    private const int SlabEdge = 32;
    private const int SlabLayers = 12;
    private const int MinHandlerRuns = 20;
    private const int HandlerTimeoutTicks = 2400;
    // Extra ticks once the handlers ran, so later passes get the chance to recycle
    // whatever they can and a later handler to notice.
    private const int SettleTicks = 150;

    // Neighbour scenarios.
    private const int QuietTicks = 30;
    private const int DrainTimeoutTicks = 300;
    private const int ColumnTimeoutTicks = 600;
    // ServerSystemBlockSimulation.HandleDirtyAndUpdatedBlocks dequeues at most this many
    // positions per pass of the 100 ms listener.
    private const int PositionsPerPass = 500;
    // 10 x 20 x 10 sources, one counter next to each.
    private const int SourceCols = 10;
    private const int SourceRows = 20;
    private const int SourceLayers = 10;
    private const int BulkEdge = 10;

    // Fixed upstream in StratumServer/Stratum#353 and in neither pinned lane yet: the position pool of the
    // random tick pass hands out one object that a later pass rewrites in place.
    private static readonly KnownDivergence KeptPositionRewrite =
        new("StratumServer/Stratum#353", StratumBuild.Stable2, StratumBuild.Indev1);

    private readonly ITestOutputHelper output;

    public BlockTickContractScenarios(ITestOutputHelper output) => this.output = output;

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task KeptRandomTickPosition_Should_NeverBeRewritten_When_HandlerKeepsIt()
    {
        ITestPlayer anchor = await World.JoinPlayer("bt-posprobe");
        anchor.Player.WorldData.DesiredViewDistance = 256;
        // Pin the anchor first: the join scatters players around spawn, and the random tick
        // candidacy is centred on the chunk the engine recorded for the player.
        await anchor.TeleportTo(World.Spawn);
        await World.Ticks(5);

        BlockPos anchorPos = anchor.Position;
        long anchorChunkIndex = ChunkIndexOf(anchorPos);
        RandomTickProbes.AssertAnchorChunkPinned(World, anchor, anchorChunkIndex, "before placing the slab");

        BlockPos corner = new((anchorPos.X / 32) * 32, anchorPos.Y + 4, (anchorPos.Z / 32) * 32, 0);
        Assert.True(
            World.Api.World.BlockAccessor.GetChunkAtBlockPos(corner) != null
                && World.Api.World.BlockAccessor.GetChunkAtBlockPos(corner.AddCopy(0, SlabLayers - 1, 0)) != null,
            $"slab chunk is not loaded on {ServerFlavor.Name}; setup is invalid");

        int probeId = ResolveBlockId(PositionProbeCode);
        int andesiteId = ResolveBlockId(AndesiteCode);
        int graniteId = ResolveBlockId(GraniteCode);

        var slab = new List<BlockPos>(SlabEdge * SlabEdge * SlabLayers);
        for (int x = 0; x < SlabEdge; x++)
        {
            for (int z = 0; z < SlabEdge; z++)
            {
                for (int y = 0; y < SlabLayers; y++)
                {
                    BlockPos pos = corner.AddCopy(x, y, z);
                    World.SetBlock(PositionProbeCode, pos);
                    slab.Add(pos);
                }
            }
        }

        try
        {
            // A column still loading swallows writes silently: the slab must read back whole.
            int placed = slab.Count(pos => World.Api.World.BlockAccessor.GetBlockId(pos) == probeId);
            Assert.True(placed == slab.Count,
                $"only {placed} of {slab.Count} slab blocks read back as {PositionProbeCode} on {ServerFlavor.Name}; setup is invalid");

            try
            {
                await World.Until(
                    () =>
                    {
                        (int andesite, int granite) = Tally(slab, andesiteId, graniteId);
                        return andesite + granite >= MinHandlerRuns;
                    },
                    timeoutTicks: HandlerTimeoutTicks);
            }
            catch (ScenarioTimeoutException)
            {
                (int andesite, int granite) = Tally(slab, andesiteId, graniteId);
                Assert.Fail(
                    $"only {andesite + granite} of {MinHandlerRuns} handler runs within {HandlerTimeoutTicks} ticks " +
                    $"on {ServerFlavor.Name}: the probe never got going; setup is invalid");
            }

            await World.Ticks(SettleTicks);

            // A drifted anchor or engine chunk would starve the slab of random ticks, which
            // is a broken setup and never a divergence.
            RandomTickProbes.AssertAnchorChunkPinned(World, anchor, anchorChunkIndex, "after the settle window");

            (int ranAndesite, int rewritten) = Tally(slab, andesiteId, graniteId);
            int ran = ranAndesite + rewritten;
            output.WriteLine($"kept position probe on {ServerFlavor.Name} {ServerFlavor.Version ?? "none"}: {ran} handler runs, {rewritten} rewritten");

            if (KeptPositionRewrite.Applies)
            {
                Assert.True(rewritten >= 1,
                    $"{KeptPositionRewrite.Tag}: bug shape gone, remove the exemption (rewritten={rewritten} of {ran} handler runs)");
            }
            else
            {
                Assert.True(rewritten == 0,
                    $"the engine rewrote the position object a handler kept after returning on {ServerFlavor.Name}: " +
                    $"{rewritten} of {ran} handler runs; the queue must hold a private copy per tick ({KeptPositionRewrite.Tag})");
            }
        }
        finally
        {
            // Unconverted probe blocks would keep random ticking for the rest of the class.
            IBlockAccessor accessor = World.Api.World.BlockAccessor;
            foreach (BlockPos pos in slab)
            {
                accessor.SetBlock(0, pos);
            }
        }
    }

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task NeighbourBlock_Should_ReceiveOneUpdatePerChange_When_EachNeighbourChangesOnce()
    {
        BlockPos origin = ColumnCorner(World.Spawn.AddCopy(200, 0, 0));
        await LoadKeptColumn(origin);
        int counterId = ResolveBlockId(NeighbourCounterCode);
        int graniteId = ResolveBlockId(GraniteCode);

        // Well inside the column: all six neighbours stay in the same chunk.
        BlockPos counter = origin.AddCopy(8, 4, 8);
        World.SetBlock(NeighbourCounterCode, counter);
        AssertAll(new[] { counter }, counterId, "the counter block");
        BlockPos[] neighbours =
        {
            counter.AddCopy(1, 0, 0), counter.AddCopy(-1, 0, 0),
            counter.AddCopy(0, 1, 0), counter.AddCopy(0, -1, 0),
            counter.AddCopy(0, 0, 1), counter.AddCopy(0, 0, -1),
        };
        AssertAll(neighbours, 0, "the cells around the counter (air before the change)");

        await World.Ticks(QuietTicks);

        // Synchronous from the drain to the last trigger: no tick can deliver a stale record
        // in between, and no new record can predate the changes.
        ProbeLog.DrainNeighbourCalls(World);
        IBlockAccessor accessor = World.Api.World.BlockAccessor;
        foreach (BlockPos neighbour in neighbours)
        {
            World.SetBlock(GraniteCode, neighbour);
            accessor.TriggerNeighbourBlockUpdate(neighbour);
        }

        AssertAll(neighbours, graniteId, "the six changed neighbours");

        List<NeighbourCall> calls = await CollectNeighbourCalls(
            neighbours.Length, new[] { counter }, counterId, "six neighbour changes");
        AssertDeliveredExactly(
            calls,
            neighbours.Select(n => Edge(counter, n)).ToHashSet(),
            "one notification per changed neighbour expected");
    }

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task NeighbourUpdates_Should_DrainEveryChange_When_TwoThousandBlocksSetInOneTick()
    {
        BlockPos origin = ColumnCorner(World.Spawn.AddCopy(0, 0, 200));
        await LoadKeptColumn(origin);
        Assert.True(
            World.Api.World.BlockAccessor.GetChunkAtBlockPos(origin.AddCopy(0, SourceLayers - 1, 0)) != null,
            $"the top source layer is not loaded on {ServerFlavor.Name}; setup is invalid");
        int counterId = ResolveBlockId(NeighbourCounterCode);
        int graniteId = ResolveBlockId(GraniteCode);

        // Sources every third block along x with one counter on their +x side: the cell on
        // their -x side is empty (the previous counter is two away) and the other four
        // neighbours are sources or empty, so every source has exactly one counter neighbour
        // and a change of it notifies exactly one block.
        var sources = new List<BlockPos>(SourceCols * SourceRows * SourceLayers);
        var counters = new List<BlockPos>(sources.Capacity);
        for (int layer = 0; layer < SourceLayers; layer++)
        {
            for (int row = 0; row < SourceRows; row++)
            {
                for (int col = 0; col < SourceCols; col++)
                {
                    BlockPos source = origin.AddCopy(3 * col, layer, row);
                    sources.Add(source);
                    counters.Add(source.AddCopy(1, 0, 0));
                }
            }
        }

        foreach (BlockPos counter in counters)
        {
            World.SetBlock(NeighbourCounterCode, counter);
        }

        AssertAll(counters, counterId, "the counter blocks");
        AssertAll(sources, 0, "the source cells (air before the change)");

        await World.Ticks(QuietTicks);

        ProbeLog.DrainNeighbourCalls(World);
        IBlockAccessor accessor = World.Api.World.BlockAccessor;
        foreach (BlockPos source in sources)
        {
            World.SetBlock(GraniteCode, source);
            accessor.TriggerNeighbourBlockUpdate(source);
        }

        AssertAll(sources, graniteId, "the changed sources");

        List<NeighbourCall> calls = await CollectNeighbourCalls(
            sources.Count, counters, counterId, $"{sources.Count} neighbour changes in one tick");
        AssertDeliveredExactly(
            calls,
            sources.Select(s => Edge(s.AddCopy(1, 0, 0), s)).ToHashSet(),
            "one notification per changed source expected");

        // The queue drains in passes of at most 500 positions, one pass per 100 ms listener
        // fire; each pass logs under its own tick stamp (see ProbeLog.NeighbourCall.Tick).
        var perPass = calls.GroupBy(c => c.Tick).Select(g => g.Count()).ToList();
        int expectedPasses = (sources.Count + PositionsPerPass - 1) / PositionsPerPass;
        output.WriteLine(
            $"neighbour drain on {ServerFlavor.Name} {ServerFlavor.Version ?? "none"}: {calls.Count} notifications in {perPass.Count} passes, " +
            $"largest pass {perPass.Max()}");
        Assert.True(perPass.Max() <= PositionsPerPass,
            $"a single pass delivered {perPass.Max()} notifications on {ServerFlavor.Name}: " +
            $"the queue drains at most {PositionsPerPass} positions per pass (per pass: {string.Join(",", perPass)})");
        Assert.True(perPass.Count >= expectedPasses,
            $"{calls.Count} notifications arrived in {perPass.Count} passes on {ServerFlavor.Name}, " +
            $"at least {expectedPasses} expected for {sources.Count} sources (per pass: {string.Join(",", perPass)})");
    }

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task BulkCommit_Should_NotifyEachModifiedBlock_When_AccessorCommitted()
    {
        BlockPos origin = ColumnCorner(World.Spawn.AddCopy(200, 0, 200));
        await LoadKeptColumn(origin);
        int counterId = ResolveBlockId(NeighbourCounterCode);

        var positions = new List<BlockPos>(BulkEdge * BulkEdge);
        for (int x = 0; x < BulkEdge; x++)
        {
            for (int z = 0; z < BulkEdge; z++)
            {
                positions.Add(origin.AddCopy(4 + x, 4, 4 + z));
            }
        }

        AssertAll(positions, 0, "the cells for the bulk placement (air before the commit)");

        // Synchronous from the drain to the commit, as in the other scenarios.
        ProbeLog.DrainNeighbourCalls(World);
        IBulkBlockAccessor bulk = World.Api.World.GetBlockAccessorBulkUpdate(synchronize: true, relight: true);
        foreach (BlockPos pos in positions)
        {
            bulk.SetBlock(counterId, pos);
        }

        var committed = bulk.Commit();
        Assert.True(committed.Count == positions.Count,
            $"the commit applied {committed.Count} of {positions.Count} staged blocks on {ServerFlavor.Name}; setup is invalid");
        AssertAll(positions, counterId, "the committed counter blocks");

        List<NeighbourCall> calls = await CollectNeighbourCalls(
            positions.Count, positions, counterId, "a bulk commit");
        AssertDeliveredExactly(
            calls,
            positions.Select(p => Edge(p, p)).ToHashSet(),
            "one self notification (pos equal to neibpos) per committed block expected");
    }

    /// <summary>Polls the probe log every tick until the expected number of records has been
    /// drained, then keeps draining through a quiet period so that records beyond the
    /// expected count (duplicates, strays) are counted too.</summary>
    private async Task<List<NeighbourCall>> CollectNeighbourCalls(
        int expectedCount, IReadOnlyList<BlockPos> counters, int counterId, string what)
    {
        var collected = new List<NeighbourCall>();
        try
        {
            await World.Until(
                () =>
                {
                    collected.AddRange(ProbeLog.DrainNeighbourCalls(World));
                    return collected.Count >= expectedCount;
                },
                timeoutTicks: DrainTimeoutTicks);
        }
        catch (ScenarioTimeoutException)
        {
            // An unloaded column or a replaced counter is a broken setup, not a lost notification.
            AssertAll(counters, counterId, "the counter blocks while waiting for the notifications");
            Assert.Fail(
                $"{what}: only {collected.Count} of {expectedCount} notifications arrived within {DrainTimeoutTicks} ticks on {ServerFlavor.Name}");
        }

        await World.Ticks(QuietTicks);
        collected.AddRange(ProbeLog.DrainNeighbourCalls(World));
        return collected;
    }

    /// <summary>Exact comparison of the delivered records against the expected set of
    /// (counter position, changed position) pairs: nothing missing, nothing extra, nothing twice.</summary>
    private static void AssertDeliveredExactly(IReadOnlyList<NeighbourCall> calls, HashSet<string> expected, string expectation)
    {
        var seen = new Dictionary<string, int>();
        foreach (NeighbourCall call in calls)
        {
            string key = Edge(call.Pos, call.NeibPos);
            seen[key] = seen.GetValueOrDefault(key) + 1;
        }

        List<string> missing = expected.Where(key => !seen.ContainsKey(key)).ToList();
        List<string> unexpected = seen.Keys.Where(key => !expected.Contains(key)).ToList();
        List<string> duplicated = seen.Where(pair => pair.Value > 1 && expected.Contains(pair.Key)).Select(pair => pair.Key).ToList();

        Assert.True(
            calls.Count == expected.Count && missing.Count == 0 && unexpected.Count == 0 && duplicated.Count == 0,
            $"{expectation}, got {calls.Count} of {expected.Count} on {ServerFlavor.Name}: " +
            $"missing {missing.Count} [{Sample(missing)}], unexpected {unexpected.Count} [{Sample(unexpected)}], " +
            $"duplicated {duplicated.Count} [{Sample(duplicated)}] (format: counter position <- changed position)");
    }

    private static string Sample(List<string> keys) => string.Join("; ", keys.Take(5));

    private static string Edge(BlockPos pos, BlockPos neibPos) =>
        $"{pos.X},{pos.Y},{pos.Z}<-{neibPos.X},{neibPos.Y},{neibPos.Z}";

    private void AssertAll(IReadOnlyList<BlockPos> positions, int blockId, string what)
    {
        IBlockAccessor accessor = World.Api.World.BlockAccessor;
        int matching = positions.Count(pos => accessor.GetBlockId(pos) == blockId);
        Assert.True(matching == positions.Count,
            $"only {matching} of {positions.Count} positions hold block id {blockId} ({what}) on {ServerFlavor.Name}; setup is invalid");
    }

    private int ResolveBlockId(string code)
    {
        Block? block = World.Api.World.GetBlock(new AssetLocation(code));
        Assert.True(block != null,
            $"block {code} is not registered on {ServerFlavor.Name}: the class must stage mods/randomtickprobe; setup is invalid");
        return block!.BlockId;
    }

    /// <summary>Loads the column of a position and keeps it loaded: nobody stands near it, and
    /// an unloaded chunk would silently drop both the changes and the notifications.</summary>
    private async Task LoadKeptColumn(BlockPos pos)
    {
        World.Api.WorldManager.LoadChunkColumnPriority(pos.X / 32, pos.Z / 32, new ChunkLoadOptions { KeepLoaded = true });
        try
        {
            await World.Until(
                () => World.Api.World.BlockAccessor.GetChunkAtBlockPos(pos) != null,
                timeoutTicks: ColumnTimeoutTicks);
        }
        catch (ScenarioTimeoutException)
        {
            Assert.Fail($"the column at {pos} never loaded within {ColumnTimeoutTicks} ticks on {ServerFlavor.Name}; setup is invalid");
        }
    }

    /// <summary>The chunk-aligned corner of a position's column, a few blocks above the terrain
    /// (World.Spawn is resolved to terrain height), so offsets inside the column stay in it.</summary>
    private BlockPos ColumnCorner(BlockPos near) => new((near.X / 32) * 32, World.Spawn.Y + 4, (near.Z / 32) * 32, 0);

    private long ChunkIndexOf(BlockPos pos) => World.Api.WorldManager.ChunkIndex3D(pos.X / 32, pos.Y / 32, pos.Z / 32);

    /// <summary>Handler runs (andesite) and rewritten kept positions (granite) in the slab,
    /// counted in one pass.</summary>
    private (int Andesite, int Granite) Tally(List<BlockPos> slab, int andesiteId, int graniteId)
    {
        IBlockAccessor accessor = World.Api.World.BlockAccessor;
        int andesite = 0;
        int granite = 0;
        foreach (BlockPos pos in slab)
        {
            int id = accessor.GetBlockId(pos);
            if (id == andesiteId)
            {
                andesite++;
            }
            else if (id == graniteId)
            {
                granite++;
            }
        }

        return (andesite, granite);
    }
}
