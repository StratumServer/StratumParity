using System.Globalization;
using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Xunit;
using Xunit.Abstractions;

namespace StratumParity.Scenarios;

/// <summary>
/// Probes what Stratum does to the time of block entities. Two of its patches are in play, both in
/// the event manager. First, a positioned block game tick listener (every block entity timer is one)
/// is not run at all while its column is outside the active square around the Playing clients
/// (Performance.SimulationDistance.LimitBlockGameTickListeners, radius 128 blocks, so 4 chunks
/// around the client chunk); the skipped listener's clock is reset on every skip, so it lost that time
/// instead of catching up. Second, a listener registered with no initial delay gets one derived from its
/// position (Performance.BlockEntityInit, up to MaxStaggerMs). Vanilla runs every listener, and gives
/// every one the same first fire.
///
/// What a block entity does with the time it was not ticked decides what a player sees, so the first
/// two scenarios read it off two vanilla blocks, one far from the player and one next to them:
/// - the basic torch (BlockEntityTorch, 48 in-game hours) keeps its own calendar time stamp, so a far
///   torch that missed a calendar jump is still lit on Stratum and burns out within a few of its checks
///   once a player comes near ("catches up");
/// - a fire (BEBehaviorBurning, the burn duration of its fuel) counts the delta time of its own
///   listener, so a far fire loses the seconds it was not ticked and burns its full duration from the
///   moment a player comes near ("lost time, no catch-up").
/// The third scenario runs both with the listener limit switched off through /stratum set and expects
/// the vanilla result on both flavors. The fourth reads the stagger off the first fire of two listeners
/// registered at the same position.
///
/// Setup rules the scenarios share:
/// - The far column is requested WITHOUT keep loaded: a force-loaded column is exempt from the limit
///   (TickForceLoadedBlockListeners, default on), which would void the very thing probed. An unforced
///   column unloads about 15 s after the last use of its map chunk, which is shorter than a scenario, so
///   every wait in the far column also rewrites a marker block every 15 ticks (a SetBlock relights, and
///   the relight marks the map chunk fresh). The marker stands at least 24 blocks from anything probed.
/// - Far means 200 blocks from the anchor in a direction no other scenario of the class uses (chunk
///   distance 6 or more, the active square reaches 4), and an anchor that teleports in ends up there:
///   the four far directions are +x, +z, -x and -z, so no resting anchor ever makes another scenario's
///   far column active. All anchors are joined at the world spawn.
/// - Every torch and every fire has a roof 3 blocks above it: rain would extinguish a torch (the
///   TemperatureSensitive behavior) or a fire, and would read as a divergence.
/// - The torch only runs its transition check on 30% of its 1 s timer, so how long the near torches take
///   to burn out is a random variable. Waits are bounded by 60 s of ticks, four torches stand on each
///   side, and the far side of the frozen branch is observed only after the near side finished plus a
///   hold, so a limit that does not hold shows up as a burned far torch with near certainty.
/// - The calendar jump (Calendar.Add) moves the shared world clock for good; nothing else in the class
///   reads it.
/// </summary>
public class BlockEntityTimeProbes : AtlasScenarioBase
{
    private const string TorchCode = "game:torch-basic-lit-up";
    private const string BurnedOutPath = "torch-basic-burnedout-up";
    private const string TorchEntityClass = "BlockEntityTorch";
    private const string FireCode = "game:fire";
    // Hay burns for 5 s (combustibleProps.burnDuration), the shortest full block that nothing else interferes with.
    private const string FuelCode = "game:hay-normal-ud";
    private const string RoofCode = "game:rock-granite";
    private const string MarkerCodeA = "game:rock-granite";
    private const string MarkerCodeB = "game:rock-andesite";
    private const string BurningBehavior = "BEBehaviorBurning";
    private const string FireSpreadKey = "allowFireSpread";

    private const string LimitKey = "Performance.SimulationDistance.LimitBlockGameTickListeners";
    private const string LimitRadiusKey = "Performance.SimulationDistance.BlockGameTickListenerDistanceBlocks";
    private const string StaggerEnabledKey = "Performance.BlockEntityInit.Enabled";
    private const string StaggerMaxKey = "Performance.BlockEntityInit.MaxStaggerMs";

    private const int ChunkSize = 32;
    private const int FarOffset = 200;
    private const int TorchCount = 4;
    private const int CellSpacing = 3;
    // The basic torch lasts 48 in-game hours; one more clears the hour the loop needs to see pass.
    private const float CalendarJumpHours = 49f;
    private const double CalendarJumpFloorHours = 48.5;
    // 60 s of ticks: a torch check burns the torch with probability 0.3, so a torch that survives this long
    // (0.7 to the power of 60) is a bug, not luck.
    private const int TorchBurnTimeoutTicks = 1800;
    private const int FrozenHoldTicks = 120;
    private const int LitTimeoutTicks = 300;
    private const int ColumnTimeoutTicks = 600;
    private const int FireTimeoutTicks = 900;
    private const int FrozenFireHoldTicks = 90;
    // A fire that frozen far away burns its full duration after the anchor arrives, give or take the ticks
    // between the player's position update and the end of the teleport task.
    private const int LostTimeSlackMs = 1000;
    private const float FrozenBurnSlackSeconds = 0.5f;
    // Every poll of a wait is this long, and rewrites the marker of the far column.
    private const int StepTicks = 15;

    private const int StaggerIntervalMs = 1000;
    private const int StaggerMinOffsetMs = 180;
    private const int DefaultStaggerMaxMs = 250;
    private const int StaggerMaxTicks = 150;
    private const int StaggerAttempts = 3;
    // Reading the clock inside a callback is a little later than the time the engine stamped the pass with.
    private const long ClockJitterMs = 10;

    private readonly ITestOutputHelper output;
    private int markerWrites;

    public BlockEntityTimeProbes(ITestOutputHelper output) => this.output = output;

    // ---------------------------------------------------------------------------------------------
    // Torches
    // ---------------------------------------------------------------------------------------------

    [AtlasScenario(TimeoutMs = 300_000)]
    public async Task FarTorch_Should_BurnOutOnVanillaAndCatchUpOnStratum_When_CalendarJumps()
    {
        await AssertLimitDefaults();
        ITestPlayer anchor = await EntityProbeRig.JoinAnchor(World, "bet-torch");
        await RunTorchProbe(anchor, FarOffset, 0, nearZ: 12, farFrozen: ServerFlavor.IsStratum);
    }

    // ---------------------------------------------------------------------------------------------
    // Fires
    // ---------------------------------------------------------------------------------------------

    [AtlasScenario(TimeoutMs = 240_000)]
    public async Task FarFire_Should_BurnOutOnVanillaAndStayFrozenOnStratum_When_NoPlayerNear()
    {
        await AssertLimitDefaults();
        ITestPlayer anchor = await EntityProbeRig.JoinAnchor(World, "bet-fire");
        await RunFireProbe(anchor, 0, FarOffset, nearZ: 24, farFrozen: ServerFlavor.IsStratum);
    }

    // ---------------------------------------------------------------------------------------------
    // The limit switched off
    // ---------------------------------------------------------------------------------------------

    [AtlasScenario(TimeoutMs = 420_000)]
    public async Task FarBlockEntities_Should_BehaveLikeVanilla_When_ListenerLimitDisabled()
    {
        // Hot toggle: ShouldTriggerBlockGameTick reads the config on every listener check. On vanilla the
        // handle is a no-op, so this is the plain vanilla run of both probes there.
        ITestPlayer anchor = await EntityProbeRig.JoinAnchor(World, "bet-limitoff");
        await using IAsyncDisposable toggle = await StratumSetting.Set(World, LimitKey, "false");
        if (ServerFlavor.IsStratum)
        {
            await AssertSetting(LimitKey, "false");
        }

        await RunTorchProbe(anchor, -FarOffset, 0, nearZ: 16, farFrozen: false);
        await RunFireProbe(anchor, 0, -FarOffset, nearZ: 20, farFrozen: false);
    }

    // ---------------------------------------------------------------------------------------------
    // The stagger of the first fire
    // ---------------------------------------------------------------------------------------------

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task BlockEntityListener_Should_FirstFireWithPositionStagger_When_RegisteredTogether()
    {
        // Two listeners of 1000 ms at the same position, registered back to back: the control with an
        // initial delay of 1 ms (a nonzero delay is passed through untouched), the other with none.
        // Vanilla: both first fire on the first tick after 1000 ms, within a tick of each other. Stratum
        // (BlockEntityInit on, the default) gives the one with no delay an offset of
        // (position hash & int.MaxValue) % min(MaxStaggerMs, 1000) ms, so it first fires that long after
        // the control. The position is picked for the largest offset (at least 180 ms of the 250 window).
        // Both are read off the engine clock with a tolerance of the longest tick gap seen in the window.
        ITestPlayer anchor = await EntityProbeRig.JoinAnchor(World, "bet-stagger");
        BlockPos origin = anchor.Position;
        await World.Ticks(5);

        int window = DefaultStaggerMaxMs;
        if (ServerFlavor.IsStratum)
        {
            await AssertSetting(StaggerEnabledKey, "true");
            string? max = await StratumSetting.Get(World, StaggerMaxKey);
            Assert.True(int.TryParse(max, NumberStyles.Integer, CultureInfo.InvariantCulture, out window),
                $"{StaggerMaxKey} reads {max ?? "nothing"} on {ServerFlavor.Name}, not a number; setup is invalid");
            window = Math.Min(window, StaggerIntervalMs);
        }

        (BlockPos pos, int offset) = PickStaggeredPosition(origin, window);
        Assert.True(offset >= StaggerMinOffsetMs,
            $"no position near the anchor has a stagger offset of {StaggerMinOffsetMs} ms or more in a window of {window} ms " +
            $"(best {offset} ms at {pos}) on {ServerFlavor.Name}; setup is invalid");
        output.WriteLine($"{ServerFlavor.Name}: listener position {pos}, hash offset {offset} ms of a {window} ms window");

        FirstFires defaults = await MeasureFirstFires(pos, offset);
        output.WriteLine($"{ServerFlavor.Name} defaults: {defaults.Describe()}");
        long afterListener = defaults.Listener - defaults.Registered;
        long afterControl = defaults.Control - defaults.Registered;
        long tolerance = defaults.MaxTickGap + ClockJitterMs;

        Assert.True(afterControl > StaggerIntervalMs && afterControl <= StaggerIntervalMs + 1 + tolerance,
            $"the control (initial delay 1 ms) first fired {afterControl} ms after registration on {ServerFlavor.Name}, " +
            $"expected more than {StaggerIntervalMs} ms and at most {StaggerIntervalMs + 1 + tolerance} ms ({defaults.Describe()})");
        if (ServerFlavor.IsStratum)
        {
            Assert.True(afterListener >= StaggerIntervalMs + offset && afterListener <= StaggerIntervalMs + offset + tolerance,
                $"the staggered listener first fired {afterListener} ms after registration on stratum, expected between " +
                $"{StaggerIntervalMs + offset} and {StaggerIntervalMs + offset + tolerance} ms ({defaults.Describe()})");
            Assert.True(defaults.Listener - defaults.Control >= offset - 1 - tolerance,
                $"the staggered listener first fired only {defaults.Listener - defaults.Control} ms after the control on stratum, " +
                $"expected at least {offset - 1 - tolerance} ms ({defaults.Describe()})");
        }
        else
        {
            AssertVanillaShape(defaults, "on vanilla");
        }

        // Switched off, Stratum registers listeners as vanilla does (on vanilla this is a no-op handle).
        await using (IAsyncDisposable toggle = await StratumSetting.Set(World, StaggerEnabledKey, "false"))
        {
            if (ServerFlavor.IsStratum)
            {
                await AssertSetting(StaggerEnabledKey, "false");
            }

            FirstFires off = await MeasureFirstFires(pos, offset);
            output.WriteLine($"{ServerFlavor.Name} stagger off: {off.Describe()}");
            AssertVanillaShape(off, $"on {ServerFlavor.Name} with BlockEntityInit off");
        }

        EntityProbeRig.AssertAnchorStayed(anchor, origin);
    }

    private static void AssertVanillaShape(FirstFires fires, string where)
    {
        long afterListener = fires.Listener - fires.Registered;
        long tolerance = fires.MaxTickGap + ClockJitterMs;
        Assert.True(afterListener > StaggerIntervalMs && afterListener <= StaggerIntervalMs + tolerance,
            $"the listener first fired {afterListener} ms after registration {where}, expected more than {StaggerIntervalMs} ms " +
            $"and at most {StaggerIntervalMs + tolerance} ms ({fires.Describe()})");
        Assert.True(Math.Abs(fires.Listener - fires.Control) <= tolerance,
            $"the listener and the control first fired {fires.Listener - fires.Control} ms apart {where}, expected within " +
            $"{tolerance} ms, a tick ({fires.Describe()})");
    }

    /// <summary>The first fire of the two listeners, as read off the engine clock, with the longest gap
    /// between two consecutive ticks of the window (the resolution of every reading).</summary>
    private sealed record FirstFires(long Registered, long Listener, long Control, long MaxTickGap)
    {
        public string Describe() =>
            $"registered at {Registered} ms, listener +{Listener - Registered} ms, control +{Control - Registered} ms, longest tick gap {MaxTickGap} ms";
    }

    /// <summary>Registers the pair at the position, waits for both first fires and returns when they were.
    /// A window with a tick gap too long to tell the two shapes apart (a stall of the runner) is repeated
    /// with fresh listeners, up to three times, and fails as a setup problem after that.</summary>
    private async Task<FirstFires> MeasureFirstFires(BlockPos pos, int offsetMs)
    {
        IWorldAccessor engine = World.Api.World;
        long worstGap = 0;
        for (int attempt = 1; attempt <= StaggerAttempts; attempt++)
        {
            long listenerAt = 0;
            long controlAt = 0;
            long registered = engine.ElapsedMilliseconds;
            long controlId = World.Api.Event.RegisterGameTickListener(
                _ => { if (controlAt == 0) controlAt = engine.ElapsedMilliseconds; },
                pos, errorHandler: null, millisecondInterval: StaggerIntervalMs, initialDelayOffsetMs: 1);
            long listenerId = World.Api.Event.RegisterGameTickListener(
                _ => { if (listenerAt == 0) listenerAt = engine.ElapsedMilliseconds; },
                pos, errorHandler: null, millisecondInterval: StaggerIntervalMs, initialDelayOffsetMs: 0);

            var stamps = new List<long> { registered };
            try
            {
                for (int tick = 0; tick < StaggerMaxTicks && (listenerAt == 0 || controlAt == 0); tick++)
                {
                    await World.Ticks(1);
                    stamps.Add(engine.ElapsedMilliseconds);
                }
            }
            finally
            {
                World.Api.Event.UnregisterGameTickListener(controlId);
                World.Api.Event.UnregisterGameTickListener(listenerId);
            }

            Assert.True(listenerAt != 0 && controlAt != 0,
                $"a listener of {StaggerIntervalMs} ms at {pos} never fired within {StaggerMaxTicks} ticks on {ServerFlavor.Name} " +
                $"(listener {(listenerAt == 0 ? "no" : "yes")}, control {(controlAt == 0 ? "no" : "yes")}); the block is in the active column of the anchor, setup is invalid");

            long maxGap = 0;
            for (int i = 1; i < stamps.Count; i++)
            {
                maxGap = Math.Max(maxGap, stamps[i] - stamps[i - 1]);
            }

            var fires = new FirstFires(registered, listenerAt, controlAt, maxGap);
            // The two shapes are an offset apart (at least 180 ms), the readings a tick gap wide: tell them
            // apart only when the gap is well under half of it.
            if (maxGap + ClockJitterMs < (offsetMs - 1) / 2)
            {
                return fires;
            }

            worstGap = Math.Max(worstGap, maxGap);
            output.WriteLine($"{ServerFlavor.Name}: attempt {attempt} discarded, {fires.Describe()}");
        }

        Assert.Fail($"every one of {StaggerAttempts} windows had a tick gap of up to {worstGap} ms on {ServerFlavor.Name}, too long to tell a stagger " +
            $"of {offsetMs} ms from none; the runner stalled, setup is invalid");
        return null!;
    }

    /// <summary>The candidate cell near the anchor whose stagger offset is the largest: the formula of the
    /// patch, evaluated on the same BlockPos hash code the engine uses.</summary>
    private static (BlockPos Pos, int Offset) PickStaggeredPosition(BlockPos origin, int window)
    {
        BlockPos best = origin;
        int bestOffset = -1;
        for (int dx = 4; dx < 36; dx++)
        {
            for (int dz = 4; dz < 20; dz++)
            {
                var pos = new BlockPos(origin.X + dx, origin.Y + 2, origin.Z + dz, 0);
                int offset = (pos.GetHashCode() & int.MaxValue) % window;
                if (offset > bestOffset)
                {
                    best = pos;
                    bestOffset = offset;
                }
            }
        }

        return (best, bestOffset);
    }

    // ---------------------------------------------------------------------------------------------
    // The torch probe
    // ---------------------------------------------------------------------------------------------

    /// <summary>Four lit torches next to the anchor and four in a far column, a calendar jump past their
    /// 48 hours, then the outcome of each side. With <paramref name="farFrozen"/> (Stratum defaults) the near
    /// torches burn out and the far ones stay lit until the anchor teleports in, then burn out; without it
    /// (vanilla, or the limit off) everything burns out with nobody near the far ones.</summary>
    private async Task RunTorchProbe(ITestPlayer anchor, int farDx, int farDz, int nearZ, bool farFrozen)
    {
        BlockPos origin = anchor.Position;
        FarColumn far = await LoadFarColumn(origin, farDx, farDz);
        BlockPos[] nearCells = Cells(origin.X + 8, origin.Z + nearZ, TorchCount, above: 1);
        BlockPos[] farCells = Cells(far.Corner.X + 4, far.Corner.Z + 4, TorchCount, above: 1);
        BlockPos[] all = nearCells.Concat(farCells).ToArray();
        AssertLoaded(all);

        foreach (BlockPos cell in all)
        {
            World.SetBlock(RoofCode, cell.AddCopy(0, 3, 0));
            World.SetBlock(TorchCode, cell);
        }

        AssertTorches(all, "after placing them");
        await WaitFor(() => Levels(all).All(level => level > 0), LitTimeoutTicks, far.Marker,
            () => $"block light {string.Join(",", Levels(all))}", "the torches lighting up");

        double hoursBefore = World.Calendar.TotalHours;
        World.Calendar.Add(CalendarJumpHours);
        double jumped = World.Calendar.TotalHours - hoursBefore;
        Assert.True(jumped >= CalendarJumpFloorHours,
            $"the calendar advanced {jumped:F2} hours after Add({CalendarJumpHours}) on {ServerFlavor.Name}; setup is invalid");

        if (!farFrozen)
        {
            int ticks = await WaitFor(() => all.All(IsBurnedOut), TorchBurnTimeoutTicks, far.Marker,
                () => $"torches {Describe(nearCells)} near, {Describe(farCells)} far", "every torch burning out");
            output.WriteLine($"{ServerFlavor.Name}: all {all.Length} torches burned out within {ticks} ticks of the jump, nobody near the far ones");
            await WaitFor(() => Levels(all).All(level => level == 0), ColumnTimeoutTicks, far.Marker,
                () => $"block light {string.Join(",", Levels(all))}", "the burned out torches going dark");
            EntityProbeRig.AssertAnchorStayed(anchor, origin);
            return;
        }

        int nearTicks = await WaitFor(() => nearCells.All(IsBurnedOut), TorchBurnTimeoutTicks, far.Marker,
            () => $"near torches {Describe(nearCells)}", "the near torches burning out");
        await WaitFor(() => Levels(nearCells).All(level => level == 0), ColumnTimeoutTicks, far.Marker,
            () => $"near block light {string.Join(",", Levels(nearCells))}", "the near torches going dark");
        await Hold(FrozenHoldTicks, far.Marker);
        EntityProbeRig.AssertAnchorStayed(anchor, origin);
        AssertColumnLoaded(far, "after the hold");

        // The far torches missed the whole jump: lit, still a torch block entity, still giving light.
        Assert.True(farCells.All(IsLitTorch),
            $"a far torch is no longer lit {nearTicks + FrozenHoldTicks} ticks after the calendar jump on stratum, with nobody near: {Describe(farCells)}; " +
            $"the listener limit did not freeze it");
        AssertTorches(farCells, "after the hold");
        Assert.True(Levels(farCells).All(level => level > 0),
            $"a far torch gives no light after the hold on stratum: block light {string.Join(",", Levels(farCells))}");

        await anchor.TeleportTo(far.Arrival);
        int catchUpTicks = await WaitFor(() => farCells.All(IsBurnedOut), TorchBurnTimeoutTicks, null,
            () => $"far torches {Describe(farCells)}", "the far torches burning out once the anchor is near");
        output.WriteLine($"stratum: near torches burned out within {nearTicks} ticks, far ones {catchUpTicks} ticks after the anchor arrived");
        await WaitFor(() => Levels(farCells).All(level => level == 0), ColumnTimeoutTicks, null,
            () => $"far block light {string.Join(",", Levels(farCells))}", "the far torches going dark");
    }

    private bool IsLitTorch(BlockPos pos) => World.BlockAt(pos).Code.ToString() == TorchCode;

    private bool IsBurnedOut(BlockPos pos) => World.BlockAt(pos).Code.Path == BurnedOutPath;

    private string Describe(IEnumerable<BlockPos> cells) =>
        string.Join(",", cells.Select(pos => World.BlockAt(pos).Code.Path));

    private int[] Levels(IEnumerable<BlockPos> cells) =>
        cells.Select(pos => World.Api.World.BlockAccessor.GetLightLevel(pos, EnumLightLevelType.OnlyBlockLight)).ToArray();

    /// <summary>The assumptions the torch outcomes rest on, each failing as a setup problem: a lit torch
    /// block with its own block entity, a roof over it, and a climate to run its transition check with.</summary>
    private void AssertTorches(IEnumerable<BlockPos> cells, string when)
    {
        IBlockAccessor accessor = World.Api.World.BlockAccessor;
        foreach (BlockPos cell in cells)
        {
            string code = World.BlockAt(cell).Code.ToString();
            Assert.True(code == TorchCode, $"the block at {cell} is {code} {when} on {ServerFlavor.Name}, not {TorchCode}; setup is invalid");
            string? entity = accessor.GetBlockEntity(cell)?.GetType().Name;
            Assert.True(entity == TorchEntityClass,
                $"the block entity at {cell} is {entity ?? "missing"} {when} on {ServerFlavor.Name}, not {TorchEntityClass}; setup is invalid");
            Assert.True(accessor.GetRainMapHeightAt(cell.X, cell.Z) > cell.Y,
                $"the torch at {cell} is not under a roof {when} on {ServerFlavor.Name}; setup is invalid");
            Assert.True(accessor.GetClimateAt(cell, EnumGetClimateMode.WorldGenValues) != null,
                $"there is no climate at {cell} {when} on {ServerFlavor.Name}, the torch could never run its transition check; setup is invalid");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // The fire probe
    // ---------------------------------------------------------------------------------------------

    /// <summary>One fire next to the anchor and one in a far column, each on a hay block (5 s) under a
    /// roof, spread off. With <paramref name="farFrozen"/> the near fire burns out while the far one has lost
    /// all of that time, and burns its full duration once the anchor arrives; without it both burn out and
    /// consume their fuel with nobody near the far one.</summary>
    private async Task RunFireProbe(ITestPlayer anchor, int farDx, int farDz, int nearZ, bool farFrozen)
    {
        BlockPos origin = anchor.Position;
        ITreeAttribute config = World.Api.World.Config;
        bool hadSpreadKey = config.HasAttribute(FireSpreadKey);
        bool previousSpread = config.GetBool(FireSpreadKey);
        // Read by the behavior when the fire block entity is created: set before the fires are placed.
        config.SetBool(FireSpreadKey, false);
        try
        {
            FarColumn far = await LoadFarColumn(origin, farDx, farDz);
            BlockPos nearFuel = Cells(origin.X + 8, origin.Z + nearZ, 1, above: 1)[0];
            BlockPos farFuel = Cells(far.Corner.X + 4, far.Corner.Z + 4, 1, above: 1)[0];
            AssertLoaded(new[] { nearFuel, farFuel });

            Block? fuelBlock = World.Api.World.GetBlock(new AssetLocation(FuelCode));
            Assert.True(fuelBlock?.CombustibleProps != null && fuelBlock.CombustibleProps.BurnDuration > 0,
                $"{FuelCode} has no burn duration on {ServerFlavor.Name}; setup is invalid");
            float burnSeconds = fuelBlock!.CombustibleProps.BurnDuration;
            int burnMs = (int)(burnSeconds * 1000);

            BurningFire nearFire = PlaceFire(nearFuel);
            BurningFire farFire = PlaceFire(farFuel);
            foreach (BurningFire fire in new[] { nearFire, farFire })
            {
                Assert.True(fire.IsBurning && Math.Abs(fire.Start - burnSeconds) < 0.01f && Math.Abs(fire.Remaining - burnSeconds) < 0.5f,
                    $"the fire at {fire.FirePos} is not burning a fresh {burnSeconds} s fuel on {ServerFlavor.Name} " +
                    $"(burning {fire.IsBurning}, start {fire.Start}, remaining {fire.Remaining}); setup is invalid");
                Assert.True(World.Api.World.BlockAccessor.GetRainMapHeightAt(fire.FirePos.X, fire.FirePos.Z) > fire.FirePos.Y,
                    $"the fire at {fire.FirePos} is not under a roof on {ServerFlavor.Name}; setup is invalid");
            }

            if (!farFrozen)
            {
                int ticks = await WaitFor(() => nearFire.IsConsumed(World) && farFire.IsConsumed(World), FireTimeoutTicks, far.Marker,
                    () => $"near {nearFire.Describe(World)}, far {farFire.Describe(World)}", "both fires burning out");
                output.WriteLine($"{ServerFlavor.Name}: both fires burned out and consumed their fuel within {ticks} ticks, nobody near the far one");
                EntityProbeRig.AssertAnchorStayed(anchor, origin);
                return;
            }

            int nearTicks = await WaitFor(() => nearFire.IsConsumed(World), FireTimeoutTicks, far.Marker,
                () => $"near {nearFire.Describe(World)}", "the near fire burning out");
            await Hold(FrozenFireHoldTicks, far.Marker);
            EntityProbeRig.AssertAnchorStayed(anchor, origin);
            AssertColumnLoaded(far, "after the hold");

            // Every second the near fire burned, the far one lost: nothing of its fuel is gone.
            Assert.True(World.BlockAt(farFire.FirePos).Code.ToString() == FireCode
                    && World.BlockAt(farFire.FuelPos).Code.ToString() == FuelCode
                    && farFire.IsBurning
                    && farFire.Remaining >= farFire.Start - FrozenBurnSlackSeconds,
                $"the far fire lost time while nobody was near on stratum: {farFire.Describe(World)} after the near fire " +
                $"({nearTicks} ticks) and a hold of {FrozenFireHoldTicks} ticks; the listener limit did not freeze it");

            await anchor.TeleportTo(far.Arrival);
            long arrivedAt = World.Api.World.ElapsedMilliseconds;
            int farTicks = await WaitFor(() => farFire.IsConsumed(World), FireTimeoutTicks, null,
                () => $"far {farFire.Describe(World)}", "the far fire burning out once the anchor is near");
            long survivedMs = World.Api.World.ElapsedMilliseconds - arrivedAt;
            output.WriteLine($"stratum: near fire burned out within {nearTicks} ticks, the far one lasted {survivedMs} ms ({farTicks} ticks) after the anchor arrived, burn duration {burnMs} ms");

            // No catch-up: the time spent frozen is lost, so the far fire still has its whole duration.
            Assert.True(survivedMs >= burnMs - LostTimeSlackMs,
                $"the far fire burned out only {survivedMs} ms after the anchor arrived on stratum, expected at least {burnMs - LostTimeSlackMs} ms " +
                $"of its {burnMs} ms (the frozen time must not be caught up)");
        }
        finally
        {
            if (hadSpreadKey)
            {
                config.SetBool(FireSpreadKey, previousSpread);
            }
            else
            {
                config.RemoveAttribute(FireSpreadKey);
            }
        }
    }

    /// <summary>Fuel on the ground cell, the fire on top of it, a roof three blocks over the fire; the fire is
    /// lit the way the game lights it from a fire starter (OnFirePlaced from the clicked face, up).</summary>
    private BurningFire PlaceFire(BlockPos fuelPos)
    {
        BlockPos firePos = fuelPos.AddCopy(0, 1, 0);
        World.SetBlock(RoofCode, firePos.AddCopy(0, 3, 0));
        World.SetBlock(FuelCode, fuelPos);
        World.SetBlock(FireCode, firePos);
        BlockEntity? entity = World.Api.World.BlockAccessor.GetBlockEntity(firePos);
        Assert.True(entity != null, $"the fire block at {firePos} has no block entity on {ServerFlavor.Name}; setup is invalid");
        var fire = new BurningFire(entity!, firePos, fuelPos);
        fire.Light();
        return fire;
    }

    /// <summary>The Burning behavior of a fire block entity, reached by reflection: VSSurvivalMod is not
    /// referenced by this project. Every member is looked up by name and fails as a setup problem when gone.</summary>
    private sealed class BurningFire
    {
        private readonly object behavior;

        public BurningFire(BlockEntity entity, BlockPos firePos, BlockPos fuelPos)
        {
            FirePos = firePos;
            FuelPos = fuelPos;
            BlockEntityBehavior? found = entity.Behaviors.FirstOrDefault(b => b.GetType().Name == BurningBehavior);
            Assert.True(found != null, $"the fire block entity at {firePos} has no {BurningBehavior} on {ServerFlavor.Name}; setup is invalid");
            behavior = found!;
        }

        public BlockPos FirePos { get; }

        public BlockPos FuelPos { get; }

        public bool IsBurning => (bool)Read("IsBurning");

        public float Remaining => (float)Read("remainingBurnDuration");

        public float Start => (float)Read("startDuration");

        public void Light()
        {
            MethodInfo? method = behavior.GetType().GetMethod("OnFirePlaced", new[] { typeof(BlockFacing), typeof(string) });
            Assert.True(method != null, $"{BurningBehavior}.OnFirePlaced(BlockFacing, string) is missing on {ServerFlavor.Name}; setup is invalid");
            method!.Invoke(behavior, new object[] { BlockFacing.UP, string.Empty });
        }

        /// <summary>Burned out the way a fire dies of its fuel: fire block and fuel block both gone.</summary>
        public bool IsConsumed(IWorldSession world) =>
            world.BlockAt(FirePos).Id == 0 && world.BlockAt(FuelPos).Id == 0;

        public string Describe(IWorldSession world) =>
            $"fire {world.BlockAt(FirePos).Code?.Path ?? "air"}, fuel {world.BlockAt(FuelPos).Code?.Path ?? "air"}, " +
            $"burning {IsBurningOrNull()}, remaining {RemainingOrNull()} s of {StartOrNull()} s";

        private object Read(string field)
        {
            FieldInfo? info = behavior.GetType().GetField(field);
            Assert.True(info != null, $"{BurningBehavior}.{field} is missing on {ServerFlavor.Name}; setup is invalid");
            return info!.GetValue(behavior)!;
        }

        private string IsBurningOrNull() => behavior.GetType().GetField("IsBurning")?.GetValue(behavior)?.ToString() ?? "unknown";

        private string RemainingOrNull() => behavior.GetType().GetField("remainingBurnDuration")?.GetValue(behavior)?.ToString() ?? "unknown";

        private string StartOrNull() => behavior.GetType().GetField("startDuration")?.GetValue(behavior)?.ToString() ?? "unknown";
    }

    // ---------------------------------------------------------------------------------------------
    // Shared setup
    // ---------------------------------------------------------------------------------------------

    /// <summary>A far column: its chunk aligned corner, the marker block that keeps its map chunk fresh and the
    /// cell the anchor teleports to (more than 3 blocks from anything probed).</summary>
    private sealed record FarColumn(int ChunkX, int ChunkZ, BlockPos Corner, BlockPos Marker, BlockPos Arrival);

    /// <summary>Requests the column around the position 200 blocks out and waits for it, not kept loaded (see the
    /// class comment). Nobody is near it: an anchor within 128 blocks of it would void the probe.</summary>
    private async Task<FarColumn> LoadFarColumn(BlockPos origin, int dx, int dz)
    {
        int chunkX = (origin.X + dx) / ChunkSize;
        int chunkZ = (origin.Z + dz) / ChunkSize;
        BlockPos corner = new(chunkX * ChunkSize, 0, chunkZ * ChunkSize, 0);
        BlockPos probe = At(corner.X + 16, World.Spawn.Y, corner.Z + 16);
        World.Api.WorldManager.LoadChunkColumnPriority(chunkX, chunkZ);
        try
        {
            await World.Until(() => World.Api.World.BlockAccessor.GetChunkAtBlockPos(probe) != null, timeoutTicks: ColumnTimeoutTicks);
        }
        catch (ScenarioTimeoutException)
        {
            Assert.Fail($"the column at {probe} never loaded within {ColumnTimeoutTicks} ticks on {ServerFlavor.Name}; setup is invalid");
        }

        BlockPos marker = Cells(corner.X + 28, corner.Z + 28, 1, above: 1)[0];
        BlockPos arrival = Cells(corner.X + 20, corner.Z + 20, 1, above: 1)[0];
        return new FarColumn(chunkX, chunkZ, corner, marker, arrival);
    }

    private static BlockPos At(int x, int y, int z) => new(x, y, z, 0);

    /// <summary>Cells in a row along x on the ground of a flat world, <paramref name="above"/> blocks over the
    /// highest block with a collision box.</summary>
    private BlockPos[] Cells(int x0, int z, int count, int above)
    {
        var cells = new BlockPos[count];
        for (int i = 0; i < count; i++)
        {
            int x = x0 + CellSpacing * i;
            int ground = GroundTop(x, z);
            Assert.True(ground >= 0, $"there is no ground at x {x}, z {z} on {ServerFlavor.Name} (the column is not loaded?); setup is invalid");
            cells[i] = At(x, ground + above, z);
        }

        return cells;
    }

    /// <summary>Y of the highest block with a collision box in a column (grass and flowers have none), or -1
    /// while the column is not loaded or has nothing solid.</summary>
    private int GroundTop(int x, int z)
    {
        for (int y = World.Spawn.Y + 12; y > 0; y--)
        {
            Block block = World.BlockAt(At(x, y, z));
            if (block.Id != 0 && block.CollisionBoxes is { Length: > 0 })
            {
                return y;
            }
        }

        return -1;
    }

    private void AssertLoaded(IEnumerable<BlockPos> cells)
    {
        foreach (BlockPos cell in cells)
        {
            Assert.True(World.Api.World.BlockAccessor.GetChunkAtBlockPos(cell) != null,
                $"the chunk of {cell} is not loaded on {ServerFlavor.Name}; setup is invalid");
        }
    }

    /// <summary>A far column that unloaded would make "still lit" and "still burning" vacuous.</summary>
    private void AssertColumnLoaded(FarColumn far, string when) =>
        Assert.True(World.Api.World.BlockAccessor.GetChunkAtBlockPos(far.Marker) != null,
            $"the far column ({far.ChunkX}, {far.ChunkZ}) is not loaded {when} on {ServerFlavor.Name}; setup is invalid");

    /// <summary>Rewrites the marker block of a far column, alternating two blocks: the relight of a SetBlock
    /// marks the column's map chunk fresh, which keeps an unforced column from unloading under the wait.</summary>
    private void Touch(BlockPos? marker)
    {
        if (marker != null)
        {
            World.SetBlock(markerWrites++ % 2 == 0 ? MarkerCodeA : MarkerCodeB, marker);
        }
    }

    /// <summary>Polls the condition every <see cref="StepTicks"/> ticks (the first time at once) and returns the
    /// ticks waited; fails naming the flavor and the state when it never held.</summary>
    private async Task<int> WaitFor(Func<bool> condition, int timeoutTicks, BlockPos? marker, Func<string> describe, string what)
    {
        for (int waited = 0; ; waited += StepTicks)
        {
            if (condition())
            {
                return waited;
            }

            if (waited >= timeoutTicks)
            {
                Assert.Fail($"{what} did not happen within {timeoutTicks} ticks on {ServerFlavor.Name}: {describe()}");
            }

            Touch(marker);
            await World.Ticks(StepTicks);
        }
    }

    private async Task Hold(int ticks, BlockPos? marker)
    {
        for (int held = 0; held < ticks; held += StepTicks)
        {
            Touch(marker);
            await World.Ticks(StepTicks);
        }
    }

    /// <summary>Stratum only: the limit is on and its radius leaves the far column (6 chunks out, the active square
    /// reaches ceil(radius / 32) chunks) outside, so a changed default reads as a broken setup, not as a failure.</summary>
    private async Task AssertLimitDefaults()
    {
        if (!ServerFlavor.IsStratum)
        {
            return;
        }

        await AssertSetting(LimitKey, "true");
        string? radius = await StratumSetting.Get(World, LimitRadiusKey);
        Assert.True(int.TryParse(radius, NumberStyles.Integer, CultureInfo.InvariantCulture, out int blocks) && blocks <= 5 * ChunkSize,
            $"{LimitRadiusKey} is {radius ?? "unreadable"} on {ServerFlavor.Name}, the far column at {FarOffset} blocks needs at most {5 * ChunkSize}; setup is invalid");
    }

    private async Task AssertSetting(string path, string expected)
    {
        string? actual = await StratumSetting.Get(World, path);
        Assert.True(string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            $"{path} is {actual ?? "unreadable"} on {ServerFlavor.Name}, the scenario expects {expected}; setup is invalid");
    }
}
