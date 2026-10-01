using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Xunit;

namespace StratumParity.Scenarios;

/// <summary>
/// Probes Stratum's distance-banded entity ticking (Performance.EntityTicking, enabled by
/// default) with EXACT counts against Atlas 0.10.0's EntitySimulationTicks, the engine's own
/// entity-simulation tick counter. Per the Atlas tick contract, an entity that stays
/// unthrottled for the whole window ticks exactly once per entity-simulation tick, so:
/// - the near dummy (8 blocks from the anchor, well inside the fork's 32-block near band
///   with drift margin) must tick EXACTLY simDelta times on both flavors;
/// - the far dummy (200 blocks, beyond every band) must tick exactly simDelta on vanilla,
///   and simDelta/VeryFarTickInterval (1 in 10, within stride phase) on Stratum.
///
/// Anchoring rules from the contract: the player is teleported to a fixed position first
/// (the join scatters players up to the world's spawnRadius, 50 blocks, which once put
/// this very probe's near dummy in the fork's mid band on some runs), geometry derives from the anchor's actual position
/// read after the teleport settles, and the end of the window re-checks the anchor-dummy
/// distance as setup-failure semantics so a drift can never read as a wrong count.
/// </summary>
public class EntityTickingProbes : AtlasScenarioBase
{
    private const int MeasurementTicks = 150;
    private const int StratumVeryFarInterval = 10;
    // Every active raccoon AI task's movespeed is at or below the SkipMovingEntities
    // threshold (0.01 blocks/tick; wander is 0.008), and its player-seeking tasks
    // (meleeattack, seekentity, fleeentity) all need a player within 16 blocks, unreachable
    // at the 200-block far position. See FarCreatureDriftBoundBlocks for the setup-failure
    // guard that catches it if that assumption ever stops holding.
    private const string FarCreatureCode = "game:raccoon-common-adult-male";
    private const double FarCreatureDriftBoundBlocks = 2.0;

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task FarEntity_Should_TickFullRateOnVanillaAndThrottledOnStratum_When_DefaultsActive()
    {
        ProbePair pair = await SpawnProbePair(World, "tick-anchor", "game:strawdummy");

        int nearBefore = pair.Near.Ticks;
        int farBefore = pair.Far.Ticks;
        long simBefore = World.EntitySimulationTicks;
        await World.Ticks(MeasurementTicks);
        long simDelta = World.EntitySimulationTicks - simBefore;
        int nearDelta = pair.Near.Ticks - nearBefore;
        int farDelta = pair.Far.Ticks - farBefore;

        AssertAnchorStillNear(pair);
        Assert.True(simDelta > 0, $"no entity-simulation ticks elapsed on {ServerFlavor.Name}");

        // Exact on both flavors: an unthrottled entity ticks once per entity-simulation tick.
        Assert.True(nearDelta == simDelta,
            $"near probe not exact on {ServerFlavor.Name}: {nearDelta} ticks vs {simDelta} sim ticks");

        if (ServerFlavor.IsStratum)
        {
            // Throttled at VeryFarTickInterval: exact up to stride phase at the window edges.
            long expected = simDelta / StratumVeryFarInterval;
            Assert.True(Math.Abs(farDelta - expected) <= 2,
                $"far probe off the 1-in-{StratumVeryFarInterval} stride on stratum: " +
                $"{farDelta} ticks vs {expected} expected over {simDelta} sim ticks");
        }
        else
        {
            Assert.True(farDelta == simDelta,
                $"far probe not exact on vanilla: {farDelta} ticks vs {simDelta} sim ticks");
        }
    }

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task FarCreature_Should_TickFullRateOnVanillaAndThrottledOnStratum_When_DefaultsActive()
    {
        // ThrottleCreatures (default true) is never exercised by the dummy probe above: a
        // straw dummy is inanimate, covered by ThrottleInanimate instead. SkipMovingEntities
        // exempts entities moving above 0.01 blocks/tick, so a creature whose idle AI wanders
        // would silently land on the unthrottled path; see FarCreatureCode's comment for why
        // a raccoon does not.
        ProbePair pair = await SpawnProbePair(World, "tick-anchor-2", FarCreatureCode);
        EntityPos farPos = pair.FarEntity.Pos;
        double farStartX = farPos.X, farStartY = farPos.Y, farStartZ = farPos.Z;

        int nearBefore = pair.Near.Ticks;
        int farBefore = pair.Far.Ticks;
        long simBefore = World.EntitySimulationTicks;
        await World.Ticks(MeasurementTicks);
        long simDelta = World.EntitySimulationTicks - simBefore;
        int nearDelta = pair.Near.Ticks - nearBefore;
        int farDelta = pair.Far.Ticks - farBefore;

        AssertAnchorStillNear(pair);
        AssertFarCreatureStayedPut(farStartX, farStartY, farStartZ, farPos);
        Assert.True(simDelta > 0, $"no entity-simulation ticks elapsed on {ServerFlavor.Name}");

        Assert.True(nearDelta == simDelta,
            $"near creature not exact on {ServerFlavor.Name}: {nearDelta} ticks vs {simDelta} sim ticks");

        if (ServerFlavor.IsStratum)
        {
            long expected = simDelta / StratumVeryFarInterval;
            Assert.True(Math.Abs(farDelta - expected) <= 2,
                $"far creature off the 1-in-{StratumVeryFarInterval} stride on stratum: " +
                $"{farDelta} ticks vs {expected} expected over {simDelta} sim ticks");
        }
        else
        {
            Assert.True(farDelta == simDelta,
                $"far creature not exact on vanilla: {farDelta} ticks vs {simDelta} sim ticks");
        }
    }

    /// <summary>Setup-failure guard for the far creature, same semantics as
    /// <see cref="AssertAnchorStillNear"/>: idle AI wander pushing the creature's motion
    /// above the SkipMovingEntities threshold would silently move it onto the unthrottled
    /// path and invalidate the exact-count assertion above.</summary>
    internal static void AssertFarCreatureStayedPut(double startX, double startY, double startZ, EntityPos pos)
    {
        double dx = pos.X - startX;
        double dy = pos.Y - startY;
        double dz = pos.Z - startZ;
        double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        Assert.True(distance < FarCreatureDriftBoundBlocks,
            $"far creature drifted {distance:F3} blocks over the measurement window on {ServerFlavor.Name}; setup is invalid");
    }

    internal sealed record ProbePair(
        ITestPlayer Anchor, BlockPos NearPos, TickCounterBehavior Near, TickCounterBehavior Far, Entity FarEntity);

    /// <summary>
    /// Shared setup: a Playing anchor player teleported to a fixed position, one counted
    /// entity 8 blocks from it (near band with drift margin), one 200 blocks out in a
    /// kept-loaded column, both settled to rest before measuring (Stratum does not throttle
    /// moving entities). The anchor name must be unique per scenario: test players are
    /// joined into the class's shared world and persist across scenarios in the class.
    /// </summary>
    internal static async Task<ProbePair> SpawnProbePair(IWorldSession world, string anchorName, string entityCode)
    {
        ITestPlayer anchor = await world.JoinPlayer(anchorName);
        await world.Ticks(2);
        // Pin the anchor: the join scatters players around spawn, and every distance band
        // is measured from the nearest Playing client. Read the position only after the
        // teleport settles.
        await anchor.TeleportTo(world.Spawn);
        await world.Ticks(2);
        BlockPos anchorPos = anchor.Position;

        BlockPos nearPos = anchorPos.AddCopy(8, 1, 0);
        BlockPos farPos = anchorPos.AddCopy(200, 1, 0);

        // KeepLoaded: no player is near the far column to keep it alive, and the entity
        // throttle has no force-loaded exemption (unlike the block listener limit), so
        // this cannot bias the measurement.
        world.Api.WorldManager.LoadChunkColumnPriority(farPos.X / 32, farPos.Z / 32,
            new Vintagestory.API.Server.ChunkLoadOptions { KeepLoaded = true });
        await world.Until(
            () => world.Api.World.BlockAccessor.GetChunkAtBlockPos(farPos) != null,
            timeoutTicks: 600);

        (TickCounterBehavior Counter, Entity Entity) near = SpawnCountedDummy(world, nearPos, entityCode);
        (TickCounterBehavior Counter, Entity Entity) far = SpawnCountedDummy(world, farPos, entityCode);

        await world.Ticks(60);
        return new ProbePair(anchor, nearPos, near.Counter, far.Counter, far.Entity);
    }

    /// <summary>End-of-window guard, setup-failure semantics: if the anchor drifted toward
    /// the band boundary (entity spawns can nudge players), the run is invalid rather than
    /// silently miscounted.</summary>
    internal static void AssertAnchorStillNear(ProbePair pair)
    {
        BlockPos p = pair.Anchor.Position;
        double dx = p.X - pair.NearPos.X;
        double dz = p.Z - pair.NearPos.Z;
        double distance = Math.Sqrt(dx * dx + dz * dz);
        Assert.True(distance < 32,
            $"anchor drifted to {distance:F1} blocks from the near dummy (band boundary is 32); setup is invalid");
    }

    private static (TickCounterBehavior Counter, Entity Entity) SpawnCountedDummy(
        IWorldSession world, BlockPos pos, string entityCode)
    {
        Entity entity = world.SpawnEntity(entityCode, pos);

        var counter = new TickCounterBehavior(entity);
        entity.SidedProperties.Behaviors.Add(counter);
        return (counter, entity);
    }
}
