using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Xunit;
using Xunit.Abstractions;

namespace StratumParity.Scenarios;

/// <summary>
/// Same probe as <see cref="EntityTickingProbes"/>, but the seeded
/// stratum-performance.json turns Performance.EntityTicking off before boot. With the
/// throttle off, BOTH dummies are unthrottled everywhere, so the Atlas tick contract
/// guarantees exact counts on both flavors: near and far must each tick exactly once per
/// entity-simulation tick. One pair of exact assertions proves parity AND proves the
/// config toggle genuinely restores vanilla behavior on Stratum.
///
/// The second scenario puts one dummy in each of the bands the defaults throttle (the mid band at
/// +48 blocks, ticked 1 in 2 by default, and the far band at +80, ticked 1 in 5) next to a near
/// control at +8, and asks the same exact question: every one of them ticks once per
/// entity-simulation tick. On Stratum it first reads Performance.EntityTicking.Enabled back
/// through /stratum get, so a fixture that was never read fails as an invalid setup instead of
/// as a throttle; an entity left outside its band by a drifting player would pass for the wrong
/// reason, so the geometry is re-read from the Playing players at both ends of the window.
///
/// The fixture must also seed a stratum.json: StratumRuntime.LoadOrCreateConfig only
/// reads stratum-performance.json when stratum.json already exists, otherwise it writes
/// defaults over everything.
/// </summary>
[AtlasDataFiles("fixtures/stratum-entityticking-off", TargetPath = "")]
public class EntityTickingDisabledScenarios : AtlasScenarioBase
{
    private const int BandMeasurementTicks = 150;
    private const int BandSettleTicks = 60;
    // The two scenarios of the class anchor 400 blocks apart: the test players, and the dummies
    // of the first scenario, persist for the rest of the class.
    private const int BandAnchorShiftZ = 400;
    private const string EntityTickingKey = "Performance.EntityTicking.Enabled";
    private const string DummyCode = "game:strawdummy";

    /// <summary>One probe of the band scenario: where it stands from the anchor (a different z per
    /// dummy, so they never repulse each other) and the distance to the nearest Playing player the
    /// defaults would throttle it at. Stratum's bands are 32, 64 and 96 blocks; every range keeps
    /// at least 4 blocks from each edge.</summary>
    private sealed record BandProbe(string Label, int Dx, int Dz, double MinDistance, double MaxDistance);

    private static readonly BandProbe[] BandProbes =
    [
        new("near", 8, 0, 0, 28),
        new("mid", 48, 4, 36, 60),
        new("far", 80, -4, 68, 92),
    ];

    private readonly ITestOutputHelper output;

    public EntityTickingDisabledScenarios(ITestOutputHelper output) => this.output = output;

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task FarEntity_Should_TickFullRate_When_EntityTickingDisabledByConfig()
    {
        EntityTickingProbes.ProbePair pair =
            await EntityTickingProbes.SpawnProbePair(World, "tick-anchor", "game:strawdummy");

        int nearBefore = pair.Near.Ticks;
        int farBefore = pair.Far.Ticks;
        long simBefore = World.EntitySimulationTicks;
        await World.Ticks(150);
        long simDelta = World.EntitySimulationTicks - simBefore;
        int nearDelta = pair.Near.Ticks - nearBefore;
        int farDelta = pair.Far.Ticks - farBefore;

        EntityTickingProbes.AssertAnchorStillNear(pair);
        Assert.True(simDelta > 0, $"no entity-simulation ticks elapsed on {ServerFlavor.Name}");

        Assert.True(nearDelta == simDelta,
            $"near probe not exact on {ServerFlavor.Name}: {nearDelta} ticks vs {simDelta} sim ticks; " +
            $"{EntityTickingProbes.DescribeTickDeltas(nearDelta, farDelta, simDelta)}");
        Assert.True(farDelta == simDelta,
            $"far probe throttled on {ServerFlavor.Name} despite EntityTicking.Enabled=false: " +
            $"{farDelta} ticks vs {simDelta} sim ticks; " +
            $"{EntityTickingProbes.DescribeTickDeltas(nearDelta, farDelta, simDelta)}");
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task MidAndFarEntities_Should_TickFullRate_When_EntityTickingDisabledByConfig()
    {
        await AssertFixtureRead("before the setup");

        ITestPlayer anchor = await EntityProbeRig.JoinAnchor(World, "tick-off-bands");
        await anchor.TeleportTo(World.Spawn.AddCopy(0, 0, BandAnchorShiftZ));
        await World.Ticks(2);
        BlockPos origin = anchor.Position;

        var rigs = new List<RigEntity>();
        try
        {
            foreach (BandProbe probe in BandProbes)
            {
                rigs.Add(await EntityProbeRig.SpawnCountedLoaded(
                    World, origin.AddCopy(probe.Dx, 1, probe.Dz), DummyCode));
            }

            await World.Ticks(BandSettleTicks);
            AssertSetupHolds(anchor, origin, rigs, "after the settle");

            int[] before = rigs.Select(r => r.Counter.Ticks).ToArray();
            long simBefore = World.EntitySimulationTicks;
            await World.Ticks(BandMeasurementTicks);
            long simDelta = World.EntitySimulationTicks - simBefore;
            int[] deltas = rigs.Select((r, i) => r.Counter.Ticks - before[i]).ToArray();

            AssertSetupHolds(anchor, origin, rigs, "after the window");
            await AssertFixtureRead("after the window");
            Assert.True(simDelta > 0, $"no entity-simulation ticks elapsed on {ServerFlavor.Name}; setup is invalid");

            string table = string.Join(", ", BandProbes.Select((p, i) => $"{p.Label} (+{p.Dx}) {deltas[i]}")) +
                           $", sim {simDelta}";
            output.WriteLine($"{ServerFlavor.Name} {ServerFlavor.Version ?? "none"}: ticks over the window: {table}");

            // Exact on both flavors: the near control first (a pass that threw ticks nothing for
            // everybody), then the two bands the defaults throttle.
            for (int i = 0; i < BandProbes.Length; i++)
            {
                BandProbe probe = BandProbes[i];
                Assert.True(deltas[i] == simDelta,
                    $"the {probe.Label} dummy at +{probe.Dx} ticked {deltas[i]} times against {simDelta} entity-simulation ticks " +
                    $"on {ServerFlavor.Name} with EntityTicking.Enabled=false; ticks over the window: {table}" +
                    (ServerFlavor.IsStratum
                        ? ". With the default config the mid band ticks about 1 in 2 and the far band about 1 in 5: " +
                          "a count near those means the throttle still runs although the setting reads false."
                        : string.Empty));
            }
        }
        finally
        {
            // The world is shared with the other scenario of the class: leave no dummy behind.
            foreach (RigEntity rig in rigs)
            {
                if (World.Api.World.GetEntityById(rig.Entity.EntityId) != null)
                {
                    World.Api.World.DespawnEntity(rig.Entity, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
                }
            }
        }
    }

    /// <summary>Stratum only: the seeded stratum-performance.json really was read, so
    /// Performance.EntityTicking.Enabled answers false. Vanilla has no /stratum and ignores the file.</summary>
    private async Task AssertFixtureRead(string when)
    {
        if (!ServerFlavor.IsStratum)
        {
            return;
        }

        string? enabled = await StratumSetting.Get(World, EntityTickingKey);
        Assert.True(string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase),
            $"{EntityTickingKey} reads '{enabled}' {when} on stratum, the seeded fixture was not read; setup is invalid");
    }

    /// <summary>Setup guards of the band scenario, setup-failure semantics: every dummy is alive and
    /// loaded, the anchor stayed where it was pinned, and each dummy still stands in the band the
    /// defaults would throttle it in, measured from the nearest Playing player (Stratum measures in 3D).
    /// Without the last one a drifted player could put the mid dummy in the near band and the exact
    /// count would pass for the wrong reason.</summary>
    private void AssertSetupHolds(ITestPlayer anchor, BlockPos origin, List<RigEntity> rigs, string when)
    {
        EntityProbeRig.AssertAnchorStayed(anchor, origin);
        for (int i = 0; i < rigs.Count; i++)
        {
            BandProbe probe = BandProbes[i];
            Entity entity = rigs[i].Entity;
            Assert.True(entity.Alive && World.Api.World.GetEntityById(entity.EntityId) != null,
                $"the {probe.Label} dummy at +{probe.Dx} is gone {when} on {ServerFlavor.Name}; setup is invalid");

            double distance = NearestPlayingPlayerDistance(entity);
            Assert.True(distance >= probe.MinDistance && distance <= probe.MaxDistance,
                $"the {probe.Label} dummy at +{probe.Dx} is {distance:F1} blocks from the nearest Playing player {when} " +
                $"on {ServerFlavor.Name}, expected {probe.MinDistance:F0} to {probe.MaxDistance:F0}; setup is invalid");
        }
    }

    private double NearestPlayingPlayerDistance(Entity entity)
    {
        double best = double.MaxValue;
        EntityPos pos = entity.Pos;
        foreach (IPlayer player in World.Api.World.AllOnlinePlayers)
        {
            if (player is not IServerPlayer { ConnectionState: EnumClientState.Playing } || player.Entity == null)
            {
                continue;
            }

            EntityPos p = player.Entity.Pos;
            double dx = p.X - pos.X, dy = p.Y - pos.Y, dz = p.Z - pos.Z;
            best = Math.Min(best, Math.Sqrt(dx * dx + dy * dy + dz * dz));
        }

        Assert.True(best < double.MaxValue, $"no Playing player is online on {ServerFlavor.Name}; setup is invalid");
        return best;
    }
}
