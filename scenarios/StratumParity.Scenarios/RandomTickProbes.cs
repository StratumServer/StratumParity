using Atlas.XUnit;
using Vintagestory.API.MathTools;
using Xunit;

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
        // The anchor player centers the 96-block random tick radius on spawn. Since
        // Atlas 0.9.1, JoinPlayer itself waits for the server assets packet build to
        // complete (issue #84, born from this suite's CI crash), so the mass SetBlock
        // below no longer races the build's off-thread item enumeration.
        Atlas.Api.ITestPlayer anchor = await world.JoinPlayer(anchorName);
        anchor.Player.WorldData.DesiredViewDistance = 256;

        // The join scatters players around world spawn (SpawnPlayerRandomlyAround, up to
        // the world's spawnRadius: 50 blocks here; the 15 passed to it is the number of
        // placement tries, not a radius). Spawn sits on a chunk corner, so the scatter
        // shifts the anchor's CHUNK (by up to two) on most joins: geometry derived from
        // World.Spawn therefore varied by whole chunks per run.
        // Pin the anchor to a fixed position, then derive everything from its ACTUAL chunk.
        await anchor.TeleportTo(world.Spawn);
        await world.Ticks(5);

        BlockPos anchorPos = anchor.Position;
        int anchorChunkX = anchorPos.X / 32;
        int anchorChunkZ = anchorPos.Z / 32;
        long anchorChunkIndex = ChunkIndexOfPosition(world, anchor);

        // Pinning the position is not enough. Random tick candidacy is centred on the
        // chunk the ENGINE recorded for the player entity (Entity.InChunkIndex3d), and a
        // teleport only moves the position: the chunk is resynchronised by a 1000 ms
        // listener, so it keeps pointing at the join-scatter chunk for up to a second.
        // The "distance 4" far column below is then at distance 3 from that stale centre,
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
}
