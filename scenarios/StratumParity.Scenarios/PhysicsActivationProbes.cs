using System.Globalization;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Xunit;
using Xunit.Abstractions;

namespace StratumParity.Scenarios;

/// <summary>
/// Probes Stratum's activation range for entity physics (Performance.Physics.ActivationRangeEnabled,
/// on by default). The physics manager walks its tickable list on every server tick that owes at
/// least one 1/30 s physics step and calls <c>OnPhysicsTick</c> once per owed step on every tickable
/// whose entity is tracked (within 128 blocks of a player). Vanilla does so for every tracked entity.
/// Stratum picks a stride per entity from <c>Entity.NearestPlayerDistance</c> and only steps the
/// entity on the server ticks where <c>(physics tick counter + EntityId) % stride == 0</c>: stride 1
/// under 24 blocks (NearActivationRadiusBlocks), FarTrackedTickStride (2) under 60
/// (MidActivationRadiusBlocks), FarBandTickStride (3) beyond. The skipped steps are not made up
/// for: an entity in the mid band lives at half speed, one in the far band at a third.
///
/// What is counted. Every dummy gets a <see cref="PhysicsTap"/>, a decorator that takes the place of
/// the dummy's own physics behavior in the manager's tickable list: it counts every
/// <c>OnPhysicsTick</c> (one per physics step the entity really takes) and every
/// <c>AfterPhysicsTick</c> (one per server tick that ran physics, never throttled, so it is the
/// control that every tap was registered for the same ticks) and forwards both to the behavior it
/// replaced, so the dummy keeps its real physics. The plan registered an additional counting
/// tickable per dummy; replacing is the same count without two tickables sharing one entity, which
/// would run the entity's thread safe behaviors twice per tick and, once Stratum moves to its
/// parallel physics pass (32 tickables), from two threads at once. The swap is done after the
/// dummies registered, so the activation cap (MaxActivationsPerTick, 50) never defers it.
///
/// Geometry. One Playing anchor is pinned at the world spawn (nearest player distance is 3D, from
/// the anchor's position). Scenario one and three put six dummies in each of three rows, at +8, +40
/// and +80 blocks, spread over z -10 to +10 so they never repulse each other: the whole near row is
/// under 13 blocks, the mid row 40 to 42, the far row 80 to 81. Every row stays at least 6 blocks
/// from the band edges it was configured against (the Stratum leg reads the radii and strides back
/// with /stratum get, vanilla uses Stratum's defaults, and a setup that does not fit fails as
/// invalid). The six dummies are spawned in one synchronous step, so their ids are consecutive: any
/// six consecutive ids hold exactly six, three and two entities whose stride lines up on a given
/// step (strides 1, 2, 3), so the totals of a row keep the exact ratio 1, 1/2 and 1/3 whatever the
/// catch-up pattern of the server ticks is. The assertions keep wide bounds around it anyway (a
/// count with a bound, not a rate): 25 percent around 1/stride.
///
/// Per flavor. Vanilla: every dummy of every row takes exactly as many steps as the others. Stratum:
/// the near row is exact and equal inside, the mid row sits within 25 percent of 1/2 of it and the
/// far row within 25 percent of 1/3 (the plan's [0.4, 0.6] and [0.25, 0.42]). The fall scenario
/// measures the same effect in time: three dummies fall the same height from rest and the ticks to
/// land are compared inside the run (vanilla equal within two ticks, Stratum about 1, 2 and 3 times
/// the near one, each within 20 percent), while the physics steps each one needed to land stay the
/// same on both flavors, which is what "skipped steps are not compensated" means. The toggle
/// scenario repeats the bands with ActivationRangeEnabled false: exact equality on both flavors.
///
/// Two departures from the plan, both forced by the engine. The plan lets the three fall 30 blocks
/// at +8, +40 and +90, but the distance that picks the band is the 3D one: a dummy that starts 30
/// blocks up at +8 is 31 blocks from the anchor, in the mid band, and only drops into the near band
/// halfway down, so the near fall would not be the full rate reference. The falls are 14 blocks,
/// with the near dummy at +4 (14.6 blocks at the start, 24 is the edge), and still take 38 steps.
/// The three dummies are teleported with Entity.TeleportToDouble and each fall is timed from the
/// tick its own teleport callback ran.
///
/// Anchors and clean-up. The three scenarios each join their own anchor, and the join helper pins
/// every anchor at the world spawn, so the players that persist across the scenarios of a class
/// always sit where the next one measures from (the nearest player decides the band). Every dummy
/// and its tap are removed again in finally, and the kept-loaded columns of the rows stay loaded.
/// </summary>
public class PhysicsActivationProbes : AtlasScenarioBase
{
    private const string DummyCode = "game:strawdummy";

    private const string RangeKey = "Performance.Physics.ActivationRangeEnabled";
    private const string NearRadiusKey = "Performance.Physics.NearActivationRadiusBlocks";
    private const string MidRadiusKey = "Performance.Physics.MidActivationRadiusBlocks";
    private const string MidStrideKey = "Performance.Physics.FarTrackedTickStride";
    private const string FarStrideKey = "Performance.Physics.FarBandTickStride";

    // Stratum's defaults, which vanilla's geometry borrows (vanilla has no bands, every row must
    // match anyway). 128 is MagicNum.DefaultEntityTrackingRange (4 chunks of 32 blocks): past it an
    // entity is untracked and the manager skips its physics on both flavors.
    private const double DefaultNearRadius = 24;
    private const double DefaultMidRadius = 60;
    private const double TrackingRange = 128;
    private const double EdgeMargin = 6;

    private const int GroupSize = 6;
    private static readonly int[] RowX = { 8, 40, 80 };
    private static readonly int[] RowZ = { -10, -6, -2, 2, 6, 10 };

    // The real behaviors register on the spawn tick, the taps replace them after this many ticks.
    private const int RegisterTicks = 5;
    // Lets IsTracked and NearestPlayerDistance be computed (every 0.2 s, 0.5 s on Stratum) and the
    // dummies come to rest before the first reading.
    private const int SettleTicks = 60;
    private const int WindowTicks = 240;
    // The anchor may move a little before a row would cross an edge it keeps 6 blocks from.
    private const double AnchorDriftBlocks = 3;
    private const double BandTolerance = 0.25;

    private const int FallHeight = 14;
    private static readonly (int X, int Z)[] FallOffsets = { (4, 0), (40, 8), (90, -8) };
    private const int TeleportTimeoutTicks = 120;
    private const int LandingTimeoutTicks = 600;
    private const int MinFallTicks = 10;
    private const int FallStepSpread = 3;
    private const int VanillaFallSpreadTicks = 2;
    private const double FallTolerance = 0.2;

    private readonly ITestOutputHelper output;

    public PhysicsActivationProbes(ITestOutputHelper output) => this.output = output;

    [AtlasScenario(TimeoutMs = 150_000)]
    public async Task MidAndFarEntities_Should_GetFewerPhysicsSteps_When_ActivationRangeEnabled()
    {
        Bands bands = await ReadBands(requireRangeEnabled: true);
        BandWindow window = await MeasureBands("pa-bands", bands);
        output.WriteLine($"{ServerFlavor.Name}: {Describe(window)}");

        AssertNearRowIsExact(window);
        if (!ServerFlavor.IsStratum)
        {
            AssertAllRowsEqual(window, "vanilla steps every tracked entity on every physics step");
            return;
        }

        long near = window.Total(0);
        AssertRatio(window, 1, near, bands.MidStride, "mid", bands);
        AssertRatio(window, 2, near, bands.FarStride, "far", bands);
    }

    [AtlasScenario(TimeoutMs = 150_000)]
    public async Task MidBandFall_Should_TakeLonger_When_ActivationRangeEnabled()
    {
        Bands bands = await ReadBands(requireRangeEnabled: true);
        var live = new List<Dummy>();
        FallResult fall;
        try
        {
            fall = await MeasureFalls(bands, live);
        }
        finally
        {
            Cleanup(live);
        }

        output.WriteLine($"{ServerFlavor.Name}: fall of {FallHeight} blocks, ticks to land near/mid/far " +
            $"{fall.Ticks[0]}/{fall.Ticks[1]}/{fall.Ticks[2]}, physics steps to land {fall.Steps[0]}/{fall.Steps[1]}/{fall.Steps[2]}");

        // The physics itself is the same on both flavors: each dummy needs the same number of steps
        // to fall the same height. What differs is how many server ticks those steps are spread over.
        long stepSpread = fall.Steps.Max() - fall.Steps.Min();
        Assert.True(stepSpread <= FallStepSpread,
            $"the three falls needed {fall.Steps[0]}, {fall.Steps[1]} and {fall.Steps[2]} physics steps to land on {ServerFlavor.Name} " +
            $"(spread {stepSpread}, at most {FallStepSpread}): a throttled entity must take the same steps, only later");

        if (!ServerFlavor.IsStratum)
        {
            long spread = fall.Ticks.Max() - fall.Ticks.Min();
            Assert.True(spread <= VanillaFallSpreadTicks,
                $"the falls at +4, +40 and +90 last {fall.Ticks[0]}, {fall.Ticks[1]} and {fall.Ticks[2]} ticks on vanilla " +
                $"(spread {spread}, at most {VanillaFallSpreadTicks}): vanilla steps every tracked entity on every physics step");
            return;
        }

        AssertFallRatio(fall, 1, bands.MidStride, "mid", bands);
        AssertFallRatio(fall, 2, bands.FarStride, "far", bands);
    }

    [AtlasScenario(TimeoutMs = 150_000)]
    public async Task PhysicsSteps_Should_BeEqual_When_ActivationRangeDisabled()
    {
        // Vanilla has no activation range and no /stratum: Set returns a no-op handle and the same
        // body proves the default behavior. On Stratum the flag is read at every physics tick.
        await using IAsyncDisposable toggle = await StratumSetting.Set(World, RangeKey, "false");
        await AssertRangeReads("false", "after the toggle");
        Bands bands = await ReadBands(requireRangeEnabled: false);

        BandWindow window = await MeasureBands("pa-off", bands);
        await AssertRangeReads("false", "after the window");
        output.WriteLine($"{ServerFlavor.Name}: {Describe(window)}");

        AssertNearRowIsExact(window);
        AssertAllRowsEqual(window, ServerFlavor.IsStratum
            ? $"{RangeKey} is false, so no entity may be throttled"
            : "vanilla steps every tracked entity on every physics step");
    }

    /// <summary>Joins an anchor, spawns three rows of six dummies, taps them, lets them settle and
    /// reads the physics steps each one takes over the window.</summary>
    private async Task<BandWindow> MeasureBands(string anchorName, Bands bands)
    {
        var live = new List<Dummy>();
        try
        {
            ITestPlayer anchor = await EntityProbeRig.JoinAnchor(World, anchorName);
            BlockPos origin = anchor.Position;

            var positions = new List<BlockPos>();
            foreach (int x in RowX)
            {
                foreach (int z in RowZ)
                {
                    positions.Add(origin.AddCopy(x, 1, z));
                }
            }

            Dummy[] all = await SpawnTapped(positions, live);
            Dummy[][] rows = RowX.Select((_, row) => all.Skip(row * GroupSize).Take(GroupSize).ToArray()).ToArray();
            foreach (Dummy dummy in all)
            {
                dummy.Tap.Mark();
            }

            await World.Ticks(WindowTicks);

            EntityProbeRig.AssertAnchorStayed(anchor, origin, AnchorDriftBlocks);
            AssertDistances(anchor, "near row", rows[0], 0, bands.NearRadius - EdgeMargin);
            AssertDistances(anchor, "mid row", rows[1], bands.NearRadius + EdgeMargin, bands.MidRadius - EdgeMargin);
            AssertDistances(anchor, "far row", rows[2], bands.MidRadius + EdgeMargin, TrackingRange - EdgeMargin);

            long[] passes = all.Select(d => d.Tap.PassesSinceMark).Distinct().ToArray();
            Assert.True(passes.Length == 1 && passes[0] > 0,
                $"the physics manager ran {string.Join("/", passes)} physics passes over the taps on {ServerFlavor.Name} " +
                "(one count, above zero, expected: it runs AfterPhysicsTick on every tickable); setup is invalid");

            return new BandWindow(
                rows.Select(row => row.Select(d => d.Tap.StepsSinceMark).ToArray()).ToArray(),
                passes[0]);
        }
        finally
        {
            Cleanup(live);
        }
    }

    /// <summary>Joins an anchor, spawns one tapped dummy per fall offset, drops each by
    /// <see cref="FallHeight"/> blocks and times the way down in server ticks and physics steps.</summary>
    private async Task<FallResult> MeasureFalls(Bands bands, List<Dummy> live)
    {
        ITestPlayer anchor = await EntityProbeRig.JoinAnchor(World, "pa-fall");
        BlockPos origin = anchor.Position;
        Dummy[] dummies = await SpawnTapped(
            FallOffsets.Select(o => origin.AddCopy(o.X, 1, o.Z)).ToList(), live);
        double[] restY = dummies.Select(d => d.Entity.Pos.Y).ToArray();

        int count = dummies.Length;
        var startTick = new long[count];
        var startSteps = new long[count];
        var lifted = new bool[count];
        for (int i = 0; i < count; i++)
        {
            int k = i;
            EntityPos pos = dummies[k].Entity.Pos;
            dummies[k].Entity.TeleportToDouble(pos.X, restY[k] + FallHeight, pos.Z, () =>
            {
                startTick[k] = World.CurrentTick;
                startSteps[k] = dummies[k].Tap.Steps;
                lifted[k] = true;
            });
        }

        try
        {
            await World.Until(() => lifted.All(l => l), timeoutTicks: TeleportTimeoutTicks);
        }
        catch (Exception)
        {
            Assert.Fail($"{lifted.Count(l => l)} of {count} dummies were teleported up within {TeleportTimeoutTicks} ticks " +
                $"on {ServerFlavor.Name}; setup is invalid");
        }

        // The distance that matters is the one at the start of the fall, where the dummies are highest.
        AssertDistances(anchor, "near faller at the start of its fall", new[] { dummies[0] }, 0, bands.NearRadius - EdgeMargin);
        AssertDistances(anchor, "mid faller at the start of its fall", new[] { dummies[1] },
            bands.NearRadius + EdgeMargin, bands.MidRadius - EdgeMargin);
        AssertDistances(anchor, "far faller at the start of its fall", new[] { dummies[2] },
            bands.MidRadius + EdgeMargin, TrackingRange - EdgeMargin);

        var landTick = new long?[count];
        var landSteps = new long[count];
        try
        {
            await World.Until(() =>
            {
                for (int i = 0; i < count; i++)
                {
                    if (landTick[i] == null && HasLanded(dummies[i].Entity, restY[i]))
                    {
                        landTick[i] = World.CurrentTick;
                        landSteps[i] = dummies[i].Tap.Steps;
                    }
                }

                return landTick.All(t => t != null);
            }, timeoutTicks: LandingTimeoutTicks);
        }
        catch (Exception)
        {
            Assert.Fail($"only {landTick.Count(t => t != null)} of {count} dummies landed within {LandingTimeoutTicks} ticks " +
                $"after a {FallHeight} block fall on {ServerFlavor.Name}; setup is invalid");
        }

        EntityProbeRig.AssertAnchorStayed(anchor, origin, AnchorDriftBlocks);
        foreach (Dummy dummy in dummies)
        {
            Assert.True(dummy.Entity.Alive,
                $"a dummy died in its {FallHeight} block fall on {ServerFlavor.Name}; setup is invalid");
        }

        long[] ticks = Enumerable.Range(0, count).Select(i => landTick[i]!.Value - startTick[i]).ToArray();
        long[] steps = Enumerable.Range(0, count).Select(i => landSteps[i] - startSteps[i]).ToArray();
        Assert.True(ticks[0] >= MinFallTicks,
            $"the near dummy landed {ticks[0]} ticks after its teleport on {ServerFlavor.Name}: the fall did not happen; setup is invalid");
        return new FallResult(ticks, steps);
    }

    /// <summary>Loads the columns, spawns one dummy per position in a single synchronous step
    /// (consecutive ids), swaps each dummy's own physics behavior for a tap and waits until the dummies
    /// rest. Every dummy is added to <paramref name="live"/> before anything can fail, so the caller's
    /// finally always removes what exists.</summary>
    private async Task<Dummy[]> SpawnTapped(IReadOnlyList<BlockPos> positions, List<Dummy> live)
    {
        foreach (BlockPos column in positions.DistinctBy(p => (p.X / 32, p.Z / 32)))
        {
            await EntityProbeRig.LoadColumnKept(World, column);
        }

        var entities = new Entity[positions.Count];
        for (int i = 0; i < entities.Length; i++)
        {
            entities[i] = World.SpawnEntity(DummyCode, positions[i]);
        }

        await World.Ticks(RegisterTicks);

        var dummies = new Dummy[entities.Length];
        for (int i = 0; i < entities.Length; i++)
        {
            Entity entity = entities[i];
            Assert.True(World.Api.World.GetEntityById(entity.EntityId) != null,
                $"dummy {i} is not loaded {RegisterTicks} ticks after its spawn on {ServerFlavor.Name}; setup is invalid");
            IPhysicsTickable[] own = entity.SidedProperties.Behaviors.OfType<IPhysicsTickable>().ToArray();
            Assert.True(own.Length == 1,
                $"dummy {i} has {own.Length} physics behaviors on {ServerFlavor.Name}, expected exactly 1; setup is invalid");
            dummies[i] = new Dummy(entity, new PhysicsTap(own[0]));
            live.Add(dummies[i]);
        }

        // Adds drain before removals inside one manager tick, so the entity is never without a tickable.
        foreach (Dummy dummy in dummies)
        {
            World.Api.Server.RemovePhysicsTickable(dummy.Tap.Inner);
            World.Api.Server.AddPhysicsTickable(dummy.Tap);
        }

        await World.Ticks(SettleTicks);

        for (int i = 0; i < dummies.Length; i++)
        {
            Assert.True(dummies[i].Tap.Passes > 0,
                $"the physics manager never ran the tap of dummy {i} on {ServerFlavor.Name}; setup is invalid");
            Assert.True(dummies[i].Entity.OnGround,
                $"dummy {i} is not at rest {SettleTicks} ticks after its spawn on {ServerFlavor.Name}; setup is invalid");
        }

        return dummies;
    }

    private static bool HasLanded(Entity entity, double restY) =>
        entity.OnGround && entity.Pos.Y <= restY + 0.25;

    private async Task<Bands> ReadBands(bool requireRangeEnabled)
    {
        if (!ServerFlavor.IsStratum)
        {
            return new Bands(DefaultNearRadius, DefaultMidRadius, 1, 1);
        }

        if (requireRangeEnabled)
        {
            await AssertRangeReads("true", "before the scenario");
        }

        Bands bands = new(
            await ReadNumber(NearRadiusKey),
            await ReadNumber(MidRadiusKey),
            (int)await ReadNumber(MidStrideKey),
            (int)await ReadNumber(FarStrideKey));
        output.WriteLine($"stratum bands: near {bands.NearRadius}, mid {bands.MidRadius}, strides {bands.MidStride} and {bands.FarStride}");
        Assert.True(bands.NearRadius > 0 && bands.MidRadius > bands.NearRadius && bands.MidStride >= 1 && bands.FarStride >= 1,
            $"the activation bands read back as near {bands.NearRadius}, mid {bands.MidRadius}, strides {bands.MidStride} and {bands.FarStride} on stratum; setup is invalid");
        return bands;
    }

    private async Task<double> ReadNumber(string key)
    {
        string? raw = await StratumSetting.Get(World, key);
        Assert.True(double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value),
            $"{key} answered '{raw}' on stratum, which is not a number; setup is invalid");
        return value;
    }

    /// <summary>On Stratum, proves the toggle is in the state the scenario needs (the read goes through
    /// the same path as the engine's own key lookup). A no-op on vanilla.</summary>
    private async Task AssertRangeReads(string expected, string when)
    {
        if (!ServerFlavor.IsStratum)
        {
            return;
        }

        string? applied = await StratumSetting.Get(World, RangeKey);
        Assert.True(string.Equals(applied, expected, StringComparison.OrdinalIgnoreCase),
            $"{RangeKey} reads '{applied}' {when} on stratum, expected '{expected}'; setup is invalid");
    }

    private static void AssertNearRowIsExact(BandWindow window)
    {
        long first = window.Steps[0][0];
        Assert.True(first > 0, $"the near dummies took no physics step on {ServerFlavor.Name}; setup is invalid");
        Assert.True(window.Steps[0].All(s => s == first),
            $"the near row is not exact on {ServerFlavor.Name}: {Describe(window)}");
    }

    private static void AssertAllRowsEqual(BandWindow window, string why)
    {
        long first = window.Steps[0][0];
        Assert.True(window.Steps.All(row => row.All(s => s == first)),
            $"the three rows do not take the same number of physics steps on {ServerFlavor.Name} ({why}): {Describe(window)}");
    }

    private static void AssertRatio(BandWindow window, int row, long near, int stride, string name, Bands bands)
    {
        double ratio = (double)window.Total(row) / near;
        double lo = (1 - BandTolerance) / stride;
        double hi = (1 + BandTolerance) / stride;
        Assert.True(ratio >= lo && ratio <= hi,
            $"the {name} row took {ratio:F3} of the near row's physics steps on stratum, expected {lo:F3} to {hi:F3} " +
            $"(1/{stride} within {BandTolerance * 100:F0} percent; bands at {bands.NearRadius} and {bands.MidRadius} blocks): {Describe(window)}");
    }

    private static void AssertFallRatio(FallResult fall, int index, int stride, string name, Bands bands)
    {
        double ratio = (double)fall.Ticks[index] / fall.Ticks[0];
        double lo = (1 - FallTolerance) * stride;
        double hi = (1 + FallTolerance) * stride;
        Assert.True(ratio >= lo && ratio <= hi,
            $"the {name} fall took {ratio:F2} times the near fall on stratum ({fall.Ticks[index]} ticks against {fall.Ticks[0]}), " +
            $"expected {lo:F2} to {hi:F2} (stride {stride} within {FallTolerance * 100:F0} percent; bands {bands.NearRadius} and " +
            $"{bands.MidRadius}); skipped physics steps are not made up for");
    }

    /// <summary>Setup guard, same semantics as the anchor drift check: a row that is not where the
    /// band geometry needs it would make the count meaningless.</summary>
    private static void AssertDistances(ITestPlayer anchor, string what, IEnumerable<Dummy> dummies, double lo, double hi)
    {
        EntityPos from = anchor.Entity.Pos;
        double[] distances = dummies.Select(d =>
        {
            EntityPos at = d.Entity.Pos;
            double dx = at.X - from.X, dy = at.Y - from.Y, dz = at.Z - from.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }).ToArray();

        Assert.True(distances.Min() > lo,
            $"the {what} is {distances.Min():F1} blocks from the anchor on {ServerFlavor.Name}, it needs more than {lo:F1}; setup is invalid");
        Assert.True(distances.Max() < hi,
            $"the {what} is {distances.Max():F1} blocks from the anchor on {ServerFlavor.Name}, it needs less than {hi:F1}; setup is invalid");
    }

    private static string Describe(BandWindow window) =>
        $"physics steps per dummy near [{string.Join(",", window.Steps[0])}] mid [{string.Join(",", window.Steps[1])}] " +
        $"far [{string.Join(",", window.Steps[2])}] over {window.Passes} server ticks that ran physics";

    private void Cleanup(List<Dummy> live)
    {
        foreach (Dummy dummy in live)
        {
            World.Api.Server.RemovePhysicsTickable(dummy.Tap);
        }

        foreach (Dummy dummy in live)
        {
            if (World.Api.World.GetEntityById(dummy.Entity.EntityId) != null)
            {
                World.Api.World.DespawnEntity(dummy.Entity, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
            }
        }
    }

    private sealed record Bands(double NearRadius, double MidRadius, int MidStride, int FarStride);

    private sealed record Dummy(Entity Entity, PhysicsTap Tap);

    private sealed record BandWindow(long[][] Steps, long Passes)
    {
        public long Total(int row) => Steps[row].Sum();
    }

    private sealed record FallResult(long[] Ticks, long[] Steps);

    /// <summary>Stands in for one entity's physics behavior in the manager's tickable list. Counts the
    /// physics steps (<c>OnPhysicsTick</c>) and the server ticks that ran physics (<c>AfterPhysicsTick</c>)
    /// and forwards both, plus <c>Ticking</c> and <c>Entity</c>, to the behavior it replaced. The manager
    /// may call it from a physics worker thread, hence the interlocked counters.</summary>
    private sealed class PhysicsTap : IPhysicsTickable
    {
        private long steps;
        private long passes;
        private long markedSteps;
        private long markedPasses;

        public PhysicsTap(IPhysicsTickable inner) => Inner = inner;

        public IPhysicsTickable Inner { get; }

        public Entity Entity => Inner.Entity;

        public bool Ticking
        {
            get => Inner.Ticking;
            set => Inner.Ticking = value;
        }

        public long Steps => Interlocked.Read(ref steps);

        public long Passes => Interlocked.Read(ref passes);

        public long StepsSinceMark => Steps - Interlocked.Read(ref markedSteps);

        public long PassesSinceMark => Passes - Interlocked.Read(ref markedPasses);

        /// <summary>Starts a measurement window: the counts since mark are read against this point.</summary>
        public void Mark()
        {
            Interlocked.Exchange(ref markedSteps, Steps);
            Interlocked.Exchange(ref markedPasses, Passes);
        }

        public void OnPhysicsTick(float dt)
        {
            Interlocked.Increment(ref steps);
            Inner.OnPhysicsTick(dt);
        }

        public void AfterPhysicsTick(float dt)
        {
            Interlocked.Increment(ref passes);
            Inner.AfterPhysicsTick(dt);
        }
    }
}
