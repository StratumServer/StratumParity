using System.Globalization;
using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Xunit;
using Xunit.Abstractions;

namespace StratumParity.Scenarios;

/// <summary>
/// Cadence probes for the body temperature behavior (Performance.BodyTemperature, enabled by
/// default). Vanilla runs the ambient temperature and wetness update once the behavior's
/// accumulator passes 1 s of entity tick time; Stratum replaces that threshold with
/// UpdateIntervalSeconds (3 s) times a per-entity jitter of plus or minus 25 percent, drawn
/// when the behavior initializes (so when the player joins). Every update stamps the
/// "lastWetnessUpdateTotalHours" watched attribute with the calendar time, so the number of
/// times that attribute changes IS the number of updates.
///
/// The count is taken by a class-local behavior appended to the player entity: it runs after
/// the engine's body temperature behavior in the same entity tick, so it sees every update
/// exactly once, and it sums the delta time the engine hands over (the update gate is an
/// accumulator of that delta time, not a tick count). The expected bands are closed forms of
/// the measured window length S (seconds of delta time), not wall-clock guesses: an update
/// fires when the accumulator passes T, so consecutive updates are more than T and at most
/// T plus the largest tick delta apart, which gives floor(S / (T_max + dtMax)) as the floor
/// and floor(S / T_min) + 1 as the ceiling. At the nominal 600 ticks of 33.3 ms (S = 20 s)
/// that is 5 to 9 on Stratum defaults, and 19 to 21 on vanilla, where the band keeps the
/// plan's slack of two below (17 to 21).
///
/// Known divergence: with Performance.BodyTemperature.Enabled false the patched behavior keeps its
/// field defaults (2 s update, 5 s heat source pass) instead of falling back to vanilla (1 s,
/// 3 s), see <see cref="DisabledFallback"/>.
///
/// Shared facts: the update returns early unless the player is Playing and in survival (a
/// joined test player is an admin, whose role default is creative, so each scenario switches
/// to survival and guards it), and it needs climate data at the player's position (the
/// superflat world generates a climate map, guarded too).
/// </summary>
[AtlasWorld(PlayStyle = "surviveandbuild")]
public class BodyTemperatureProbes : AtlasScenarioBase
{
    private const string UpdateStampKey = "lastWetnessUpdateTotalHours";
    private const int WindowSimTicks = 600;
    private const int WarmupTicks = 30;

    // Documented StratumBodyTemperatureConfig defaults (UpdateIntervalSeconds, JitterPercent).
    private const double StratumInterval = 3.0;
    private const double StratumJitter = 0.25;

    // Disabled by config, Stratum still gates the update at its 2 s field default, not vanilla's 1 s.
    // Confirmed on both builds (same patch file at both tags).
    private static readonly KnownDivergence DisabledFallback = new(
        "StratumServer/Stratum#364", StratumBuild.Stable2, StratumBuild.Indev1);

    private static readonly Band FallbackCadence = new(2.0, 2.0, LowSlack: 0);
    private static readonly Band VanillaCadence = new(1.0, 1.0, LowSlack: 2);
    private static readonly Band StratumCadence = new(
        StratumInterval * (1 - StratumJitter), StratumInterval * (1 + StratumJitter), LowSlack: 0);

    private readonly ITestOutputHelper output;

    public BodyTemperatureProbes(ITestOutputHelper output) => this.output = output;

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task BodyTemperature_Should_UpdateEverySecondOnVanillaAndEveryThreeOnStratum_When_DefaultsActive()
    {
        if (ServerFlavor.IsStratum)
        {
            await AssertDocumentedDefaults();
        }

        Window window = await MeasureUpdates("bt-defaults");

        AssertUpdateCount("defaults", window,
            ServerFlavor.IsStratum ? StratumCadence : VanillaCadence,
            ServerFlavor.IsStratum
                ? "stratum defaults should update about every 3 s (2.25 to 3.75 s with the 25% jitter)"
                : "vanilla should update every second");
    }

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task BodyTemperature_Should_UpdateEverySecond_When_DisabledByConfig()
    {
        // The thresholds are read when the behavior initializes, so the toggle comes first and
        // the player is joined under a fresh name afterwards. On vanilla the toggle is a no-op
        // handle and the same body proves the vanilla cadence.
        await using IAsyncDisposable toggle =
            await StratumSetting.Set(World, "Performance.BodyTemperature.Enabled", "false");
        if (ServerFlavor.IsStratum)
        {
            string? enabled = await StratumSetting.Get(World, "Performance.BodyTemperature.Enabled");
            Assert.True(enabled == "false",
                $"Performance.BodyTemperature.Enabled reads '{enabled}' after the toggle on stratum; setup is invalid");
        }

        Window window = await MeasureUpdates("bt-disabled");

        // Disabling the throttle should hand the vanilla behavior back. It does not, read from the
        // patch and confirmed by running: the fallback thresholds are the field defaults of the
        // patched behavior, 2 s for the update (vanilla 1 s) and 5 s for the heat source pass
        // (vanilla 3 s), so about 10 updates instead of about 20 over the window.
        if (DisabledFallback.Applies)
        {
            AssertUpdateCount("disabled", window, FallbackCadence,
                $"{DisabledFallback.Tag}: the 2 s fallback gate should still hold (about 10 updates); " +
                "about 20 would mean the fix landed, drop the exemption");
        }
        else
        {
            AssertUpdateCount("disabled", window, VanillaCadence,
                "body temperature disabled by config should restore the vanilla 1 s update");
        }
    }

    /// <summary>The expected bands below are derived from the documented defaults; a changed
    /// default must read as a stale probe, not as a cadence divergence.</summary>
    private async Task AssertDocumentedDefaults()
    {
        string? enabled = await StratumSetting.Get(World, "Performance.BodyTemperature.Enabled");
        double interval = await ReadNumber("Performance.BodyTemperature.UpdateIntervalSeconds");
        double jitter = await ReadNumber("Performance.BodyTemperature.JitterPercent");
        Assert.True(enabled == "true" && interval == StratumInterval && jitter == StratumJitter,
            $"stratum body temperature defaults are not the documented ones (Enabled={enabled}, " +
            $"UpdateIntervalSeconds={interval}, JitterPercent={jitter}; expected true, {StratumInterval}, " +
            $"{StratumJitter}): the expected band of this probe is stale; setup is invalid");
    }

    private async Task<double> ReadNumber(string path)
    {
        string? text = await StratumSetting.Get(World, path);
        Assert.True(double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value),
            $"{path} reads '{text}' on stratum, not a number; setup is invalid");
        return value;
    }

    /// <summary>Joins a fresh survival player, lets it settle, then counts the body temperature
    /// updates over at least <see cref="WindowSimTicks"/> entity-simulation ticks.</summary>
    private async Task<Window> MeasureUpdates(string playerName)
    {
        ITestPlayer player = await World.JoinPlayer(playerName);
        EntityPlayer entity = player.Entity;

        CommandResult mode = await player.ExecuteCommand("/gamemode survival");
        Assert.True(mode.Ok,
            $"/gamemode survival failed on {ServerFlavor.Name} ({mode.Status}): {mode.Message}; setup is invalid");
        Assert.True(player.Player.WorldData.CurrentGameMode == EnumGameMode.Survival,
            $"player is in {player.Player.WorldData.CurrentGameMode} on {ServerFlavor.Name}, the update skips it; setup is invalid");
        Assert.True(player.Player.ConnectionState == EnumClientState.Playing,
            $"player is {player.Player.ConnectionState} on {ServerFlavor.Name}, the update skips it; setup is invalid");
        Assert.True(entity.HasBehavior("bodytemperature"),
            $"player entity has no bodytemperature behavior on {ServerFlavor.Name}; setup is invalid");
        Assert.True(entity.WatchedAttributes.HasAttribute(UpdateStampKey),
            $"player has no {UpdateStampKey} attribute on {ServerFlavor.Name}; setup is invalid");
        Assert.True(World.Api.World.BlockAccessor.GetClimateAt(player.Position, EnumGetClimateMode.NowValues) != null,
            $"no climate at the player position {player.Position} on {ServerFlavor.Name}, the update returns early; setup is invalid");

        var counter = new UpdateCounter(entity);
        entity.AddBehavior(counter);

        // Past the join, the first accumulator phase and any chunk loading around the spawn.
        await World.Ticks(WarmupTicks);

        int ticksBefore = counter.Ticks;
        int changesBefore = counter.Changes;
        double secondsBefore = counter.DtSum;
        long simBefore = World.EntitySimulationTicks;
        double hoursBefore = World.Calendar.TotalHours;
        counter.DtMax = 0;

        await World.Until(() => counter.Ticks - ticksBefore >= WindowSimTicks, timeoutTicks: 2 * WindowSimTicks);

        int ticks = counter.Ticks - ticksBefore;
        long simDelta = World.EntitySimulationTicks - simBefore;
        Assert.True(ticks == simDelta,
            $"the player ticked {ticks} times over {simDelta} entity-simulation ticks on {ServerFlavor.Name}; setup is invalid");
        Assert.True(entity.Alive, $"player died during the window on {ServerFlavor.Name}; setup is invalid");
        Assert.True(player.Player.WorldData.CurrentGameMode == EnumGameMode.Survival,
            $"player left survival during the window on {ServerFlavor.Name}; setup is invalid");
        double hours = World.Calendar.TotalHours - hoursBefore;
        Assert.True(hours > 0,
            $"the calendar did not advance over the window on {ServerFlavor.Name}, the stamp cannot change; setup is invalid");
        Assert.True(counter.DtMax < 1f,
            $"one tick delivered {counter.DtMax:F3} s on {ServerFlavor.Name} (overloaded server); setup is invalid");

        return new Window(ticks, counter.Changes - changesBefore, counter.DtSum - secondsBefore,
            counter.DtMax, hours, DescribeThresholds(entity));
    }

    private void AssertUpdateCount(string label, Window window, Band band, string expectation)
    {
        output.WriteLine($"{ServerFlavor.Name} {ServerFlavor.Version ?? "none"} {label}: {window.Changes} updates over " +
                         $"{window.SimTicks} entity ticks ({window.Seconds:F2} s of tick time, largest tick {window.DtMax:F3} s); {window.Thresholds}");

        Assert.True(window.Changes > 0,
            $"no update at all over {window.SimTicks} entity ticks on {ServerFlavor.Name}: the behavior never reached " +
            $"the stamp (look for a near heat source warning in server-main.log); setup is invalid");

        int low = Math.Max(0, (int)Math.Floor(window.Seconds / (band.MaxInterval + window.DtMax)) - band.LowSlack);
        int high = (int)Math.Floor(window.Seconds / band.MinInterval) + 1;
        Assert.True(window.Changes >= low && window.Changes <= high,
            $"{expectation}: {window.Changes} updates of {UpdateStampKey} over {window.SimTicks} entity ticks " +
            $"({window.Seconds:F2} s of tick time, largest tick {window.DtMax:F3} s, {window.CalendarHours:F4} calendar hours) " +
            $"on {ServerFlavor.Name}, expected {low} to {high}; {window.Thresholds}");
    }

    /// <summary>Best-effort failure hint: the thresholds this very entity drew at init, read
    /// from Stratum's private fields. Never asserted, so a renamed field only costs the hint.</summary>
    private static string DescribeThresholds(EntityPlayer entity)
    {
        if (!ServerFlavor.IsStratum)
        {
            return "vanilla gate is 1 s";
        }

        EntityBehavior? behavior = entity.GetBehavior("bodytemperature");
        object? update = Field(behavior, "stratumUpdateThreshold");
        object? heat = Field(behavior, "stratumHeatSourceThreshold");
        return $"this entity drew update {update ?? "n/a"} s and heat source {heat ?? "n/a"} s";

        static object? Field(EntityBehavior? behavior, string name) => behavior?.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?
            .GetValue(behavior);
    }

    private sealed record Band(double MinInterval, double MaxInterval, int LowSlack);

    private sealed record Window(
        int SimTicks, int Changes, double Seconds, float DtMax, double CalendarHours, string Thresholds);

    /// <summary>Counts the changes of the update stamp and sums the tick delta time, from
    /// inside the entity tick. Appended to the behavior list, so it runs after the engine's
    /// body temperature behavior of the same tick.</summary>
    private sealed class UpdateCounter : EntityBehavior
    {
        private double lastStamp;

        public int Ticks;
        public int Changes;
        public double DtSum;
        public float DtMax;

        public UpdateCounter(Entity entity) : base(entity)
        {
            lastStamp = Stamp();
        }

        public override void OnGameTick(float deltaTime)
        {
            Ticks++;
            DtSum += deltaTime;
            DtMax = Math.Max(DtMax, deltaTime);

            double stamp = Stamp();
            if (stamp != lastStamp)
            {
                Changes++;
                lastStamp = stamp;
            }
        }

        public override string PropertyName() => "stratumparity:bodytemperaturecounter";

        private double Stamp() => entity.WatchedAttributes.GetDouble(UpdateStampKey);
    }
}
