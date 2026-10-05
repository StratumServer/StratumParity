using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Xunit;
using Xunit.Abstractions;
using static System.FormattableString;

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
///
/// The band scenarios (mid and far strides, the skipped delta time hand-out, a falling far entity,
/// the ambient fauna tier) use <see cref="EntityProbeRig"/> instead, with the distances they need:
/// one anchor at the world spawn and every probe entity west of it (-x), at its own z slot four
/// blocks from the next so the dummies never repulse each other, and at least 8 blocks inside the
/// band they stand for (the nearest Playing client sets the band, in 3D). The existing pair of
/// scenarios stays east of the anchor, so neither group meets the other's leftovers. A scenario
/// removes its entities in a finally block and checks that its probe spots are free before it
/// spawns, because the world and its players are shared by every scenario of the class.
///
/// The staged stratumparityentity mod adds one entity type, chicken-dummy: a copy of the straw
/// dummy whose code path starts with chicken-, one of Stratum's ambient fauna prefixes. Windows
/// are counted in entity-simulation ticks (an exact count against World.EntitySimulationTicks,
/// or a stride of it within the phase of the window edges), never in time.
/// </summary>
[AtlasWorld(Mods = new[] { "mods/entityprobe" })]
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

    // The band scenarios. Distances are along -x from the anchor; each is 16 blocks from the edges
    // of its band (32, 64 and 96 blocks by default), and the guards keep 8 of those 16.
    private const int BandWindowTicks = 200;
    private const int SettleTicks = 60;
    private const int SlotSpacing = 4;
    private const int NearDistance = 8;
    private const int MidDistance = 48;
    private const int FarDistance = 80;
    private const int VeryFarDistance = 110;
    private const int StratumMidInterval = 2;
    private const int StratumFarInterval = 5;
    private const int StratumAmbientMultiplier = 2;
    private const double RestDriftBlocks = 0.1;
    private const double VanillaDtTolerance = 1e-3;
    // The falling entity. The engine simulates the physics of an entity only while it is tracked,
    // that is within 128 blocks (3D) of a player; beyond that it hangs in the air and never falls,
    // on either flavor. So the dummy stands 100 blocks out (the lift added: 112 at the start, 8 blocks
    // of anchor drift still inside) and is lifted 50, which leaves a fall long enough for the window
    // even at the physics stride Stratum applies out there (one step in three). The window starts only
    // once the dummy fell fast enough to be clearly above the speed Stratum treats as moving.
    private const int FallDistance = 100;
    private const int LiftBlocks = 50;
    private const int FallWindowTicks = 30;
    private const double FallingMotion = 0.1;
    private const int FallWaitTicks = 300;
    private const string DummyCode = "game:strawdummy";
    private const string ChickenCode = "stratumparityentity:chicken-dummy";

    private static readonly DistanceBand NearBand = new("near", 0, 24);
    private static readonly DistanceBand MidBand = new("mid", 40, 56);
    private static readonly DistanceBand FarBand = new("far", 72, 88);
    private static readonly DistanceBand VeryFarBand = new("very far", 104, 10_000);
    private static readonly DistanceBand FallingBand = new("falling", 98, 122);

    // Defaults the expectations below are written for; a changed default reads as what it is.
    private static readonly (string Key, string Expected)[] BandDefaults =
    {
        ("Enabled", "true"), ("SkipMovingEntities", "true"),
        ("NearDistanceBlocks", "32"), ("MidDistanceBlocks", "64"), ("FarEntityDistanceBlocks", "96"),
        ("MidTickInterval", "2"), ("FarEntityTickInterval", "5"), ("VeryFarTickInterval", "10"),
    };

    private static readonly (string Key, string Expected)[] FallDefaults =
        BandDefaults.Append(("MovingEntitySpeedThreshold", "0.01")).ToArray();

    private static readonly (string Key, string Expected)[] AmbientDefaults =
    {
        ("Enabled", "true"), ("SkipMovingEntities", "true"), ("ThrottleCreatures", "true"),
        ("NearDistanceBlocks", "32"), ("FarEntityDistanceBlocks", "96"), ("VeryFarTickInterval", "10"),
        ("AmbientFaunaTierEnabled", "true"), ("AmbientFaunaTickMultiplier", "2"),
    };

    private readonly ITestOutputHelper output;

    public EntityTickingProbes(ITestOutputHelper output) => this.output = output;

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
            $"near probe not exact on {ServerFlavor.Name}: {nearDelta} ticks vs {simDelta} sim ticks; " +
            $"{DescribeTickDeltas(nearDelta, farDelta, simDelta)}");

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
                $"far probe not exact on vanilla: {farDelta} ticks vs {simDelta} sim ticks; " +
                $"{DescribeTickDeltas(nearDelta, farDelta, simDelta)}");
        }
    }

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task FarCreature_Should_TickFullRateOnVanillaAndThrottledOnStratum_When_DefaultsActive()
    {
        // The dummy probe above already takes the creature path: a straw dummy is an
        // EntityAgent, whose IsCreature is true, so it falls under ThrottleCreatures and the
        // creature budget (ThrottleInanimate covers only non-creatures such as items,
        // projectiles and armor stands). What it never exercises is a creature with a live
        // AI: SkipMovingEntities exempts entities moving above 0.01 blocks/tick, so a
        // creature whose idle AI wanders would silently land on the unthrottled path; see
        // FarCreatureCode's comment for why a raccoon does not.
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
            $"near creature not exact on {ServerFlavor.Name}: {nearDelta} ticks vs {simDelta} sim ticks; " +
            $"{DescribeTickDeltas(nearDelta, farDelta, simDelta)}");

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
                $"far creature not exact on vanilla: {farDelta} ticks vs {simDelta} sim ticks; " +
                $"{DescribeTickDeltas(nearDelta, farDelta, simDelta)}");
        }
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task MidAndFarEntities_Should_TickAtTheirBandStride_When_Stationary()
    {
        // The mid band (32 to 64 blocks) ticks 1 in MidTickInterval passes and the far band
        // (64 to 96) 1 in FarEntityTickInterval on Stratum; vanilla ticks every entity every
        // pass. A near dummy rides along as the control of the window (exact on both flavors).
        await PinDefaults(BandDefaults);
        var spawned = new List<RigEntity>();
        try
        {
            ITestPlayer anchor = await EntityProbeRig.JoinAnchor(World, "tick-bands");
            BlockPos origin = anchor.Position;
            RigEntity near = await Place(spawned, origin, NearDistance, 0, DummyCode);
            RigEntity mid = await Place(spawned, origin, MidDistance, 1, DummyCode);
            RigEntity far = await Place(spawned, origin, FarDistance, 2, DummyCode);
            await Settle(spawned);

            TickWindow window = await Measure(BandWindowTicks, ("near", near), ("mid", mid), ("far", far));
            EntityProbeRig.AssertAnchorStayed(anchor, origin);
            AssertInBand(anchor, near, NearBand);
            AssertInBand(anchor, mid, MidBand);
            AssertInBand(anchor, far, FarBand);
            window.AssertSetup();
            Report(window);

            AssertEverySimTick(window, "near");
            if (ServerFlavor.IsStratum)
            {
                AssertStride(window, "mid", StratumMidInterval, 1);
                AssertStride(window, "far", StratumFarInterval, 2);
            }
            else
            {
                AssertEverySimTick(window, "mid");
                AssertEverySimTick(window, "far");
            }
        }
        finally
        {
            Despawn(spawned);
        }
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task ThrottledEntity_Should_ReceiveSkippedDeltaTime_When_NextTickRuns()
    {
        // A throttled entity does not lose the time of the passes it skipped: it is handed the
        // sum on its next tick, so the delta time it received over a window matches an
        // unthrottled neighbour's, up to what it still holds undelivered at either edge of the
        // window (at most interval - 1 passes of time at each edge, bounded here by interval
        // times the largest single delta time the near dummy saw). Vanilla hands every entity
        // the same delta time every pass, so the sums are equal. TickCounterBehavior.DtSum is
        // the sum, MaxDt starts fresh with the settle so that a spawn time spike does not count.
        await PinDefaults(BandDefaults);
        var spawned = new List<RigEntity>();
        try
        {
            ITestPlayer anchor = await EntityProbeRig.JoinAnchor(World, "tick-dt");
            BlockPos origin = anchor.Position;
            RigEntity near = await Place(spawned, origin, NearDistance, 0, DummyCode);
            RigEntity mid = await Place(spawned, origin, MidDistance, 1, DummyCode);
            RigEntity far = await Place(spawned, origin, FarDistance, 2, DummyCode);
            RigEntity veryFar = await Place(spawned, origin, VeryFarDistance, 3, DummyCode);
            await Settle(spawned);

            TickWindow window = await Measure(BandWindowTicks,
                ("near", near), ("mid", mid), ("far", far), ("very far", veryFar));
            EntityProbeRig.AssertAnchorStayed(anchor, origin);
            AssertInBand(anchor, near, NearBand);
            AssertInBand(anchor, mid, MidBand);
            AssertInBand(anchor, far, FarBand);
            AssertInBand(anchor, veryFar, VeryFarBand);
            window.AssertSetup();
            Report(window);

            AssertEverySimTick(window, "near");
            Delta nearDelta = window["near"];
            (string Label, int Interval)[] throttled =
            {
                ("mid", StratumMidInterval), ("far", StratumFarInterval), ("very far", StratumVeryFarInterval),
            };
            foreach ((string label, int interval) in throttled)
            {
                Delta band = window[label];
                double difference = Math.Abs(band.DtSum - nearDelta.DtSum);
                if (ServerFlavor.IsStratum)
                {
                    Assert.True(band.Ticks < nearDelta.Ticks,
                        $"the {label} dummy ticked as often as the near one ({band.Ticks} times) on stratum, " +
                        $"so no time was skipped and nothing was handed out; setup is invalid ({window})");
                    double allowed = interval * nearDelta.MaxDt + VanillaDtTolerance;
                    Assert.True(difference <= allowed,
                        Invariant($"the {label} dummy was handed {band.DtSum:F3} s over the window against {nearDelta.DtSum:F3} s for the near one on stratum: ") +
                        Invariant($"{difference:F3} s apart, more than one stride ({interval} x {nearDelta.MaxDt:F3} s) allows, so the time of its skipped ticks is lost; {window}"));
                }
                else
                {
                    Assert.True(difference <= VanillaDtTolerance,
                        Invariant($"the {label} dummy was handed {band.DtSum:F3} s over the window against {nearDelta.DtSum:F3} s for the near one on vanilla; {window}"));
                }
            }
        }
        finally
        {
            Despawn(spawned);
        }
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task FallingFarEntity_Should_TickFullRate_When_MovingAboveThreshold()
    {
        // SkipMovingEntities: an entity moving faster than MovingEntitySpeedThreshold is never
        // throttled, wherever it is. A dummy far out (FallDistance, very far band at rest) is
        // lifted LiftBlocks and counted only after it fell fast enough to be clearly above the
        // threshold. A dummy that stays on the ground at the very far position is the control:
        // throttled on Stratum in the same window, which proves the exact count of the falling
        // one is the exemption and not a switched off throttle.
        await PinDefaults(FallDefaults);
        var spawned = new List<RigEntity>();
        try
        {
            ITestPlayer anchor = await EntityProbeRig.JoinAnchor(World, "tick-fall");
            BlockPos origin = anchor.Position;
            RigEntity near = await Place(spawned, origin, NearDistance, 0, DummyCode);
            RigEntity resting = await Place(spawned, origin, VeryFarDistance, 0, DummyCode);
            RigEntity falling = await Place(spawned, origin, FallDistance, 1, DummyCode);
            await Settle(spawned);

            Vec3d from = World.PositionOf(falling.Entity).XYZ;
            double groundY = from.Y;
            falling.Entity.TeleportTo(new Vec3d(from.X, groundY + LiftBlocks, from.Z));
            try
            {
                await World.Until(() => IsFalling(falling.Entity, groundY), timeoutTicks: FallWaitTicks);
            }
            catch (Exception)
            {
                EntityPos stuck = World.PositionOf(falling.Entity);
                Assert.Fail(Invariant(
                    $"the lifted dummy did not fall within {FallWaitTicks} ticks on {ServerFlavor.Name}: y {stuck.Y:F1} (ground {groundY:F1}, lifted by {LiftBlocks}), ") +
                    Invariant($"motion y {stuck.Motion.Y:F3}, on ground {falling.Entity.OnGround}; setup is invalid"));
            }

            TickWindow window = await Measure(FallWindowTicks,
                ("near", near), ("resting", resting), ("falling", falling));
            EntityProbeRig.AssertAnchorStayed(anchor, origin);
            AssertInBand(anchor, near, NearBand);
            AssertInBand(anchor, resting, VeryFarBand);
            AssertInBand(anchor, falling, FallingBand);
            window.AssertSetup("falling");
            EntityPos end = World.PositionOf(falling.Entity);
            Assert.True(!falling.Entity.OnGround && end.Y > groundY + LiftBlocks / 4.0 && end.Motion.Y < -FallingMotion,
                Invariant($"the falling dummy is no longer falling at the end of the window on {ServerFlavor.Name}: y {end.Y:F1} (ground {groundY:F1}, lifted by {LiftBlocks}), ") +
                Invariant($"motion y {end.Motion.Y:F3}, on ground {falling.Entity.OnGround}; setup is invalid ({window})"));
            Report(window);

            AssertEverySimTick(window, "near");
            AssertEverySimTick(window, "falling", "; a moving entity must be exempt from the throttle");
            if (ServerFlavor.IsStratum)
            {
                AssertStride(window, "resting", StratumVeryFarInterval, 2);
            }
            else
            {
                AssertEverySimTick(window, "resting");
            }
        }
        finally
        {
            Despawn(spawned);
        }
    }

    [AtlasScenario(TimeoutMs = 240_000)]
    public async Task AmbientFauna_Should_TickAtHalfRateNearPlayers_When_AmbientTierEnabled()
    {
        // Stratum's ambient fauna tier doubles the interval of an IsCreature entity whose code
        // path starts with one of AmbientFaunaCodePrefixes (chicken- is one): near the player it
        // ticks 1 in 2 passes, at the very far position 1 in 20. A plain dummy beside it is not
        // ambient and stays exact. With the tier switched off at run time (a hot setting) the
        // chicken is back to the plain bands. Vanilla ticks all of them every pass in both windows.
        await PinDefaults(AmbientDefaults);
        var spawned = new List<RigEntity>();
        try
        {
            ITestPlayer anchor = await EntityProbeRig.JoinAnchor(World, "tick-ambient");
            BlockPos origin = anchor.Position;
            RigEntity plain = await Place(spawned, origin, NearDistance, 0, DummyCode);
            RigEntity chickenNear = await Place(spawned, origin, NearDistance, 1, ChickenCode);
            RigEntity chickenFar = await Place(spawned, origin, VeryFarDistance, 0, ChickenCode);
            await Settle(spawned);

            TickWindow tiered = await Measure(BandWindowTicks,
                ("plain near", plain), ("chicken near", chickenNear), ("chicken far", chickenFar));
            EntityProbeRig.AssertAnchorStayed(anchor, origin);
            AssertInBand(anchor, plain, NearBand);
            AssertInBand(anchor, chickenNear, NearBand);
            AssertInBand(anchor, chickenFar, VeryFarBand);
            tiered.AssertSetup();
            Report(tiered);

            AssertEverySimTick(tiered, "plain near");
            if (ServerFlavor.IsStratum)
            {
                AssertStride(tiered, "chicken near", StratumAmbientMultiplier, 1);
                AssertStride(tiered, "chicken far", StratumVeryFarInterval * StratumAmbientMultiplier, 2);
            }
            else
            {
                AssertEverySimTick(tiered, "chicken near");
                AssertEverySimTick(tiered, "chicken far");
            }

            await using IAsyncDisposable toggle =
                await StratumSetting.Set(World, "Performance.EntityTicking.AmbientFaunaTierEnabled", "false");
            await PinDefaults(("AmbientFaunaTierEnabled", "false"));
            // Hot: the engine refreshes the cached switch once per entity pass.
            await World.Ticks(5);

            TickWindow plainBands = await Measure(BandWindowTicks,
                ("plain near", plain), ("chicken near", chickenNear), ("chicken far", chickenFar));
            EntityProbeRig.AssertAnchorStayed(anchor, origin);
            plainBands.AssertSetup();
            Report(plainBands);

            AssertEverySimTick(plainBands, "plain near");
            AssertEverySimTick(plainBands, "chicken near", "; the ambient tier is off");
            if (ServerFlavor.IsStratum)
            {
                AssertStride(plainBands, "chicken far", StratumVeryFarInterval, 2);
            }
            else
            {
                AssertEverySimTick(plainBands, "chicken far");
            }
        }
        finally
        {
            Despawn(spawned);
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

    /// <summary>Counter deltas for the exact-count failure messages, with a reading key.
    /// The assertions stay exact. The counter is stamped before the engine's entity pass
    /// runs, so a pass that throws still counts as a simulation tick for Atlas while
    /// ticking nothing. The key is a hint, not a verdict: far is only comparable when it is
    /// unthrottled, and on Stratum defaults only the server log separates the two shapes.</summary>
    internal static string DescribeTickDeltas(int nearDelta, int farDelta, long simDelta) =>
        $"deltas: near {nearDelta}, far {farDelta}, sim {simDelta}. Reading: every unthrottled probe short of sim " +
        "by the same count suggests whole entity simulation passes were aborted (exception through " +
        "ServerSystemEntitySimulation.OnServerTick in server-main.log); one probe short while the other " +
        "unthrottled probe is exact suggests an exception inside that entity's own OnGameTick " +
        "('Exception while ticking entity' in server-main.log). far is only comparable when unthrottled " +
        "(vanilla, or EntityTicking off): on Stratum defaults it runs at about sim/10 and only the log " +
        "separates the two.";

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

    // Helpers of the band scenarios.

    private sealed record DistanceBand(string Name, double Min, double Max);

    private sealed record Delta(string Label, int Ticks, double DtSum, float MaxDt, double Drift);

    /// <summary>What the watched entities did over a window: the engine's entity-simulation ticks that
    /// elapsed, and per entity the ticks and delta time it received and how far it moved.</summary>
    private sealed class TickWindow
    {
        private readonly Delta[] entities;

        public TickWindow(long simDelta, int harnessTicks, Delta[] entities)
        {
            SimDelta = simDelta;
            HarnessTicks = harnessTicks;
            this.entities = entities;
        }

        public long SimDelta { get; }

        public int HarnessTicks { get; }

        public Delta this[string label] => entities.Single(e => e.Label == label);

        /// <summary>The window really ran and every entity but the named movers stayed at rest
        /// (Stratum never throttles a moving entity, so a drifting probe voids the count).</summary>
        public void AssertSetup(params string[] movers)
        {
            Assert.True(SimDelta >= HarnessTicks / 2,
                $"only {SimDelta} entity-simulation ticks elapsed over {HarnessTicks} harness ticks on {ServerFlavor.Name}; setup is invalid ({this})");
            foreach (Delta e in entities.Where(e => !movers.Contains(e.Label)))
            {
                Assert.True(e.Drift <= RestDriftBlocks,
                    Invariant($"the {e.Label} dummy moved {e.Drift:F2} blocks over the window on {ServerFlavor.Name}, so it was not at rest; setup is invalid ({this})"));
            }
        }

        public override string ToString() =>
            Invariant($"sim {SimDelta} over {HarnessTicks} harness ticks; ") +
            string.Join("; ", entities.Select(e =>
                Invariant($"{e.Label} {e.Ticks} ticks, {e.DtSum:F3} s, max {e.MaxDt:F3} s, moved {e.Drift:F2}")));
    }

    private void Report(TickWindow window) =>
        output.WriteLine($"{ServerFlavor.Name} {ServerFlavor.Version ?? "none"}: {window}");

    /// <summary>Waits for each column, checks that nothing already stands at the probe spot and spawns a counted
    /// entity at <c>distance</c> blocks west of the origin and <c>slot</c> times four blocks south of it.</summary>
    private async Task<RigEntity> Place(List<RigEntity> spawned, BlockPos origin, int distance, int slot, string code)
    {
        BlockPos pos = origin.AddCopy(-distance, 1, SlotSpacing * slot);
        await EntityProbeRig.LoadColumnKept(World, pos);
        Entity[] neighbours = World.EntitiesIn(
            new Cuboidi(pos.X - 3, pos.Y - 2, pos.Z - 3, pos.X + 3, pos.Y + 4, pos.Z + 3)).ToArray();
        Assert.True(neighbours.Length == 0,
            $"{neighbours.Length} entities already stand within 3 blocks of the probe spot {distance} blocks out on " +
            $"{ServerFlavor.Name} ({string.Join(", ", neighbours.Select(n => n.Code.ToString()))}); setup is invalid");
        RigEntity rig = EntityProbeRig.SpawnCounted(World, pos, code);
        spawned.Add(rig);
        return rig;
    }

    /// <summary>Lets the entities come to rest and the throttle settle into its stride (Stratum throttles
    /// only an entity at rest), and starts a fresh MaxDt window.</summary>
    private async Task Settle(List<RigEntity> spawned)
    {
        foreach (RigEntity rig in spawned)
        {
            rig.Counter.ResetMaxDt();
        }

        await World.Ticks(SettleTicks);
    }

    private void Despawn(List<RigEntity> spawned)
    {
        foreach (RigEntity rig in spawned)
        {
            if (World.Api.World.GetEntityById(rig.Entity.EntityId) != null)
            {
                World.Api.World.DespawnEntity(rig.Entity, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
            }
        }
    }

    /// <summary>Runs the window and returns what each watched entity received. The counters, the positions and
    /// the engine's tick count are all read at the same instant on both edges.</summary>
    private async Task<TickWindow> Measure(int ticks, params (string Label, RigEntity Rig)[] watched)
    {
        Vec3d[] startPos = watched.Select(w => World.PositionOf(w.Rig.Entity).XYZ).ToArray();
        int[] startTicks = watched.Select(w => w.Rig.Counter.Ticks).ToArray();
        double[] startDt = watched.Select(w => w.Rig.Counter.DtSum).ToArray();
        long simBefore = World.EntitySimulationTicks;

        await World.Ticks(ticks);

        long simDelta = World.EntitySimulationTicks - simBefore;
        var deltas = new Delta[watched.Length];
        for (int i = 0; i < watched.Length; i++)
        {
            TickCounterBehavior counter = watched[i].Rig.Counter;
            Vec3d end = World.PositionOf(watched[i].Rig.Entity).XYZ;
            double dx = end.X - startPos[i].X, dy = end.Y - startPos[i].Y, dz = end.Z - startPos[i].Z;
            deltas[i] = new Delta(watched[i].Label, counter.Ticks - startTicks[i], counter.DtSum - startDt[i],
                counter.MaxDt, Math.Sqrt(dx * dx + dy * dy + dz * dz));
        }

        return new TickWindow(simDelta, ticks, deltas);
    }

    /// <summary>Setup guard: the probe still stands inside the band it stands for, 8 blocks from its edges
    /// (the distance is 3D, from the anchor, as Stratum measures it).</summary>
    private void AssertInBand(ITestPlayer anchor, RigEntity rig, DistanceBand band)
    {
        BlockPos a = anchor.Position;
        Vec3d e = World.PositionOf(rig.Entity).XYZ;
        double dx = e.X - a.X, dy = e.Y - a.Y, dz = e.Z - a.Z;
        double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        Assert.True(distance >= band.Min && distance <= band.Max,
            Invariant($"the {band.Name} probe stands {distance:F1} blocks from the anchor on {ServerFlavor.Name}, outside {band.Min:F0} to {band.Max:F0} (8 blocks inside its band); setup is invalid"));
    }

    private static void AssertEverySimTick(TickWindow window, string label, string reading = "")
    {
        Delta d = window[label];
        Assert.True(d.Ticks == window.SimDelta,
            $"the {label} dummy ticked {d.Ticks} times over {window.SimDelta} entity-simulation ticks on {ServerFlavor.Name}, " +
            $"expected exactly one per tick{reading}; {window}");
    }

    /// <summary>One tick in <paramref name="interval"/> passes, within the phase of the window edges.</summary>
    private static void AssertStride(TickWindow window, string label, int interval, int tolerance)
    {
        Delta d = window[label];
        double expected = (double)window.SimDelta / interval;
        Assert.True(Math.Abs(d.Ticks - expected) <= tolerance,
            Invariant($"the {label} dummy ticked {d.Ticks} times, expected about {expected:F1} (one in {interval}, within {tolerance}) ") +
            $"over {window.SimDelta} entity-simulation ticks on {ServerFlavor.Name}; {window}");
    }

    /// <summary>Lifted and already falling fast: well above the ground and moving down clearly above the
    /// speed Stratum treats as moving.</summary>
    private bool IsFalling(Entity entity, double groundY)
    {
        EntityPos pos = World.PositionOf(entity);
        return pos.Y > groundY + LiftBlocks / 2.0 && pos.Motion.Y < -FallingMotion;
    }

    /// <summary>Stratum only: fails "setup is invalid" when a default the scenario's expectation is written
    /// for is not the live value (every key is under Performance.EntityTicking).</summary>
    private async Task PinDefaults(params (string Key, string Expected)[] pins)
    {
        if (!ServerFlavor.IsStratum)
        {
            return;
        }

        foreach ((string key, string expected) in pins)
        {
            string path = "Performance.EntityTicking." + key;
            string? actual = await StratumSetting.Get(World, path);
            Assert.True(string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
                $"{path} is {actual ?? "unreadable"} on {ServerFlavor.Name}, the scenario expects {expected}; setup is invalid");
        }
    }
}
