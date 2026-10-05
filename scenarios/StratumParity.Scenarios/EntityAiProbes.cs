using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Xunit;
using Xunit.Abstractions;

namespace StratumParity.Scenarios;

/// <summary>
/// Probes the cadence of the AI task start scan. Vanilla's AiTaskManager.OnGameTick runs
/// StartNewTasks on every tick of the entity, so an idle creature asks each of its tasks
/// whether it should execute 30 times a second. Stratum runs the scan on a timer instead
/// (AiRuntimeConfig.TaskScanIntervalSeconds: 0.2 s, 0.3 s from 150 online players, 0.4 s from
/// 250, with a random phase per entity); tasks that are already running still continue every
/// tick. There is no toggle for it, the interval is a constant of the patched task manager, so
/// this is a documented probe and not a feature switch: one consequence worth knowing is that
/// a task that starts on a per scan chance starts about six times less often (30 scans a second
/// against 5).
///
/// The count is read off a counting task that the staged stratumparityai mod (mods/aiprobe)
/// adds to a raccoon: it owns slot 7, so the manager asks it on every scan whatever the other
/// tasks of the raccoon are doing, and it never executes, so the raccoon behaves as without it
/// (see <see cref="AiProbe"/>; the scenario project cannot implement IAiTask itself).
///
/// Expected values, measured on all three installs over 300 simulation ticks: vanilla 300 calls
/// (one per tick, equal to the raccoon's tick count), pinned Stratum 50 and indev Stratum 50
/// (the raccoon's summed delta time is 10.00 s, and 10 s of 0.2 s scans is 50, give or take the
/// phase at the two edges of the window). The Stratum side is therefore read against the
/// raccoon's own summed delta time, not against a tick count, so a throttled raccoon (which is
/// handed the time it skipped) would not change the expectation.
///
/// Setup guards, all failing with "setup is invalid": the world runs AI tasks at all, the raccoon
/// is alive and active before and after the window, it ticked exactly once per entity simulation
/// tick (so it stayed in the near band: this scenario measures the scan, not the tick bands), the
/// anchor stayed where it was pinned, and the sample is not empty. The raccoon is 8 blocks from
/// the anchor (inside the 32 block near band), the only player online keeps the scan interval at
/// its base value, and the raccoon may flee the anchor at 0.035 blocks per tick for the whole
/// window, which cannot take it out of the near band (it moves, so Stratum would not throttle
/// it even then).
/// </summary>
[AtlasWorld(Mods = new[] { "mods/aiprobe" })]
public class EntityAiProbes : AtlasScenarioBase
{
    private const string CreatureCode = "game:raccoon-common-adult-male";
    private const int SettleTicks = 60;
    private const int WindowTicks = 300;

    // Hard coded on purpose: the base interval of Stratum's patched AiTaskManager (a constant, no
    // config key), valid below 150 online players.
    private const double StratumScanIntervalSeconds = 0.2;
    private const int StratumScanTolerance = 2;

    private readonly ITestOutputHelper output;

    public EntityAiProbes(ITestOutputHelper output) => this.output = output;

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task AiTaskScan_Should_RunEveryAiTickOnVanillaAndEveryFifthSecondOnStratum_When_CreatureIdle()
    {
        Assert.True(World.Api.World.Config.GetAsBool("runAiTasks", true),
            $"the world config runAiTasks is false on {ServerFlavor.Name}: no creature runs its AI tasks; setup is invalid");

        ITestPlayer anchor = await EntityProbeRig.JoinAnchor(World, "ai-scan-anchor");
        BlockPos origin = anchor.Position;
        RigEntity raccoon = await EntityProbeRig.SpawnCountedLoaded(World, origin.AddCopy(8, 1, 0), CreatureCode);

        await World.Ticks(SettleTicks);
        AssertRaccoonRunning(raccoon, "before the window");

        // Attached after the settle and read at once, so the window starts on the same tick for
        // the scan counter, the raccoon's counter and the simulation counter.
        AiScanCounter scan = AiProbe.Attach(World, raccoon.Entity);
        long callsBefore = scan.ShouldExecuteCalls;
        int ticksBefore = raccoon.Counter.Ticks;
        double dtBefore = raccoon.Counter.DtSum;
        long simBefore = World.EntitySimulationTicks;

        await World.Ticks(WindowTicks);

        long calls = scan.ShouldExecuteCalls - callsBefore;
        int ticks = raccoon.Counter.Ticks - ticksBefore;
        double dtSum = raccoon.Counter.DtSum - dtBefore;
        long simDelta = World.EntitySimulationTicks - simBefore;
        double expectedScans = dtSum / StratumScanIntervalSeconds;
        output.WriteLine(Inv($"{ServerFlavor.Name} {ServerFlavor.Version}: scan calls {calls}, raccoon ticks {ticks}, sim ticks {simDelta}, raccoon dt sum {dtSum:F3} s, dt sum / {StratumScanIntervalSeconds} = {expectedScans:F2}"));

        EntityProbeRig.AssertAnchorStayed(anchor, origin);
        AssertRaccoonRunning(raccoon, "after the window");
        Assert.True(simDelta > 0, $"no entity-simulation ticks elapsed on {ServerFlavor.Name}; setup is invalid");
        Assert.True(ticks == simDelta,
            $"the raccoon ticked {ticks} times over {simDelta} simulation ticks on {ServerFlavor.Name}: it was throttled or " +
            "left the near band, so the sample does not measure the task scan; setup is invalid");
        Assert.True(dtSum > WindowTicks * 0.016,
            Inv($"the raccoon was handed only {dtSum:F3} s over {simDelta} simulation ticks on {ServerFlavor.Name}; setup is invalid"));
        Assert.True(calls > 0,
            $"the counting task was never asked whether it should execute over {WindowTicks} ticks on {ServerFlavor.Name}: " +
            "it never entered the task manager's scan; setup is invalid");

        if (ServerFlavor.IsStratum)
        {
            Assert.True(Math.Abs(calls - expectedScans) <= StratumScanTolerance,
                Inv($"the task start scan is off its {StratumScanIntervalSeconds} s timer on stratum: {calls} scans over {dtSum:F3} s (expected {expectedScans:F2} within {StratumScanTolerance}), {simDelta} simulation ticks"));
            Assert.True(calls <= simDelta / 4,
                $"the task start scan runs too often on stratum: {calls} scans over {simDelta} simulation ticks " +
                $"(at most {simDelta / 4} expected, vanilla would make {simDelta})");
        }
        else
        {
            Assert.True(calls == simDelta,
                $"the task start scan is not once per AI tick on vanilla: {calls} scans over {simDelta} simulation ticks " +
                $"and {ticks} raccoon ticks");
        }
    }

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    /// <summary>Setup guard: a dead or inactive raccoon runs no AI, which would read as a scan cadence.</summary>
    private static void AssertRaccoonRunning(RigEntity raccoon, string when) =>
        Assert.True(raccoon.Entity.Alive && raccoon.Entity.State == EnumEntityState.Active,
            $"the raccoon is not alive and active {when} on {ServerFlavor.Name} " +
            $"(alive {raccoon.Entity.Alive}, state {raccoon.Entity.State}); setup is invalid");
}
