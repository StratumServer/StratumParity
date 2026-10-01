using Atlas.XUnit;
using Xunit;

namespace StratumParity.Scenarios;

/// <summary>
/// Same probe as <see cref="EntityTickingProbes"/>, but the seeded
/// stratum-performance.json turns Performance.EntityTicking off before boot. With the
/// throttle off, BOTH dummies are unthrottled everywhere, so the Atlas tick contract
/// guarantees exact counts on both flavors: near and far must each tick exactly once per
/// entity-simulation tick. One pair of exact assertions proves parity AND proves the
/// config toggle genuinely restores vanilla behavior on Stratum.
///
/// The fixture must also seed a stratum.json: StratumRuntime.LoadOrCreateConfig only
/// reads stratum-performance.json when stratum.json already exists, otherwise it writes
/// defaults over everything.
/// </summary>
[AtlasDataFiles("fixtures/stratum-entityticking-off", TargetPath = "")]
public class EntityTickingDisabledScenarios : AtlasScenarioBase
{
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
}
