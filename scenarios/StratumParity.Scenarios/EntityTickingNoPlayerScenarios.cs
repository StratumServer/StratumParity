using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.MathTools;
using Xunit;
using Xunit.Abstractions;

namespace StratumParity.Scenarios;

/// <summary>
/// Entity ticking on a server with nobody online. Stratum's distance bands
/// (Performance.EntityTicking) are measured from the nearest Playing client, and its throttle
/// does nothing when the server has no client at all (the tier decision is skipped while the
/// player position list is empty, and the opt-in hard despawn never runs either), so on an empty
/// server every entity must tick once per entity-simulation tick on BOTH flavors, wherever it
/// stands: next to the spawn, and 200 blocks out, where a player at the spawn would put it in the
/// very far band (1 tick in 10). The exact counts are against Atlas's EntitySimulationTicks, the
/// engine's own simulation tick counter, so nothing here is a rate.
///
/// The measurement phase never joins a player and guards that nobody is online at both ends of
/// the window ("setup is invalid" otherwise). A full rate count on Stratum only means something
/// if the same dummy WOULD be throttled by a player, so a control phase follows on Stratum: one
/// player joins at the spawn, the dummies settle, and the far dummy must now tick at most one
/// time in two. The control turns "the far dummy was never throttled for some other reason" (the
/// throttle switched off, a moving entity, an excluded mod domain) into a setup failure instead of
/// a silent pass. Vanilla needs no control, it never throttles.
///
/// The control joins a player and test players persist across the scenarios of a class, so this
/// class holds this one scenario: another scenario that relied on an empty server would fail its
/// own guard depending on the order the scenarios run in.
///
/// A second exact check rides on the same window: with no throttle the far dummy is handed the
/// plain per-tick delta time, so its DtSum over the window equals the near dummy's (both dummies
/// are ticked in every pass with the same delta). A catch-up of skipped time would show as a
/// difference. Both columns are loaded and kept loaded, since an empty server is exactly where the
/// engine unloads a column nobody watches (see ChunkPersistenceScenarios).
/// </summary>
public class EntityTickingNoPlayerScenarios : AtlasScenarioBase
{
    private const int MeasurementTicks = 150;
    private const int SettleTicks = 60;
    private const int NearOffsetBlocks = 8;
    private const int FarOffsetBlocks = 200;
    // The very far band starts at 96 blocks: the control keeps the far dummy well beyond it.
    private const double ControlMinFarDistanceBlocks = 112;
    private const double DtSumToleranceSeconds = 1e-3;
    private const string DummyCode = "game:strawdummy";

    private readonly ITestOutputHelper output;

    public EntityTickingNoPlayerScenarios(ITestOutputHelper output) => this.output = output;

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Entities_Should_TickFullRate_When_NoPlayerIsOnline()
    {
        AssertNoPlayerOnline("at the start of the scenario");

        BlockPos spawn = World.Spawn;
        RigEntity near = await SpawnOnGround(spawn.AddCopy(NearOffsetBlocks, 0, 0));
        RigEntity far = await SpawnOnGround(spawn.AddCopy(FarOffsetBlocks, 0, 0));
        await World.Ticks(SettleTicks);
        AssertNoPlayerOnline("after the dummies were spawned");

        int nearBefore = near.Counter.Ticks;
        int farBefore = far.Counter.Ticks;
        double nearDtBefore = near.Counter.DtSum;
        double farDtBefore = far.Counter.DtSum;
        long simBefore = World.EntitySimulationTicks;
        await World.Ticks(MeasurementTicks);
        long simDelta = World.EntitySimulationTicks - simBefore;
        int nearDelta = near.Counter.Ticks - nearBefore;
        int farDelta = far.Counter.Ticks - farBefore;
        double nearDt = near.Counter.DtSum - nearDtBefore;
        double farDt = far.Counter.DtSum - farDtBefore;

        AssertNoPlayerOnline("at the end of the measurement window");
        output.WriteLine(
            $"{ServerFlavor.Name} no player: sim {simDelta}, near {nearDelta} (dt {nearDt:F4}), far {farDelta} (dt {farDt:F4})");
        Assert.True(simDelta > 0, $"no entity-simulation ticks elapsed on {ServerFlavor.Name}; setup is invalid");

        Assert.True(nearDelta == simDelta,
            $"near dummy not exact with no player online on {ServerFlavor.Name}: {nearDelta} ticks vs {simDelta} sim ticks; " +
            Describe(nearDelta, farDelta, simDelta));
        Assert.True(farDelta == simDelta,
            $"far dummy ({FarOffsetBlocks} blocks out) not exact with no player online on {ServerFlavor.Name}: " +
            $"{farDelta} ticks vs {simDelta} sim ticks; " + Describe(nearDelta, farDelta, simDelta));
        Assert.True(Math.Abs(farDt - nearDt) <= DtSumToleranceSeconds,
            $"far dummy received {farDt:F4} s of delta time against {nearDt:F4} s for the near dummy over the same window " +
            $"on {ServerFlavor.Name}: a catch-up of skipped time with no player online");

        if (ServerFlavor.IsStratum)
        {
            await AssertFarDummyThrottledByAPlayer(far);
        }
    }

    /// <summary>The control: a player at the spawn must put the far dummy in the throttled very far
    /// band, or the full rate measured above proves nothing about the empty server.</summary>
    private async Task AssertFarDummyThrottledByAPlayer(RigEntity far)
    {
        ITestPlayer anchor = await EntityProbeRig.JoinAnchor(World, "np-ctrl-anchor");
        BlockPos origin = anchor.Position;
        // Stratum only throttles entities at rest: settle with the player online.
        await World.Ticks(SettleTicks);

        int farBefore = far.Counter.Ticks;
        long simBefore = World.EntitySimulationTicks;
        await World.Ticks(MeasurementTicks);
        long simDelta = World.EntitySimulationTicks - simBefore;
        int farDelta = far.Counter.Ticks - farBefore;

        EntityProbeRig.AssertAnchorStayed(anchor, origin);
        BlockPos anchorPos = anchor.Position;
        double dx = anchorPos.X - far.Entity.Pos.X;
        double dz = anchorPos.Z - far.Entity.Pos.Z;
        double distance = Math.Sqrt(dx * dx + dz * dz);
        output.WriteLine($"stratum control with a player: sim {simDelta}, far {farDelta}, far dummy {distance:F1} blocks away");

        Assert.True(simDelta > 0, $"no entity-simulation ticks elapsed during the control on {ServerFlavor.Name}; setup is invalid");
        Assert.True(distance >= ControlMinFarDistanceBlocks,
            $"the far dummy is only {distance:F1} blocks from the control player (the very far band starts at 96) on {ServerFlavor.Name}; setup is invalid");
        Assert.True(farDelta * 2 <= simDelta,
            $"the far dummy still ticked {farDelta} of {simDelta} sim ticks with a player {distance:F1} blocks away on {ServerFlavor.Name}, " +
            "so the throttle never engaged and the full rate counted with no player proves nothing; setup is invalid");
    }

    private void AssertNoPlayerOnline(string when)
    {
        int online = World.Api.World.AllOnlinePlayers.Length;
        Assert.True(online == 0, $"{online} player(s) online {when} on {ServerFlavor.Name}; setup is invalid");
    }

    /// <summary>Loads and keeps the column of <paramref name="column"/>, then spawns a counted dummy two
    /// blocks above that column's terrain (the spawn height is only known once a column is loaded).</summary>
    private async Task<RigEntity> SpawnOnGround(BlockPos column)
    {
        await EntityProbeRig.LoadColumnKept(World, column);
        int ground = World.Api.World.BlockAccessor.GetTerrainMapheightAt(column);
        Assert.True(ground > 1, $"terrain height {ground} at {column.X}, {column.Z} on {ServerFlavor.Name}; setup is invalid");
        return EntityProbeRig.SpawnCounted(World, new BlockPos(column.X, ground + 2, column.Z, column.dimension), DummyCode);
    }

    private static string Describe(int nearDelta, int farDelta, long simDelta) =>
        $"deltas: near {nearDelta}, far {farDelta}, sim {simDelta}. Reading: a probe short of sim on both flavors with " +
        "nobody online means the engine did not tick it (a column unloaded, an exception through the entity pass: see " +
        "server-main.log); one short only on stratum means the throttle ran without a player.";
}
