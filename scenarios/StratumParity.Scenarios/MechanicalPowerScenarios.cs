using System.Collections;
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
/// Mechanical power parity. Stratum patched MechanicalPowerMod: the network registry is guarded by a
/// lock (network discovery runs on the chunk loading thread when a column loads, the tick loop on the
/// main thread) and the per tick copy of the registry is a reused list. Whatever those changes do, a
/// creative rotor with its axles must still build ONE network, tick it at the vanilla rate, settle at
/// the vanilla speed and grind with a quern, also after the column was unloaded and loaded again.
///
/// Building. Blocks are placed with World.SetBlock, which creates the block entity at once but never
/// runs the placement code that joins a neighbour to a network, so a power producer is placed LAST:
/// the rotor's Initialize then discovers the network and spreads it over every axle, the angled gear
/// and the quern already standing (the order a column load has, too). The quern goes up after the gear
/// beneath it, because a quern over air would start to fall. The rotor outputs on the side opposite to
/// the one in its code, so game:creativerotor-east feeds the axles to its west; game:angledgears-eu
/// connects the axle on its east to the quern above it (the quern only takes power from above or below).
///
/// Expected values are derived, not captured. Network members are compared as positions relative to the
/// rotor, never as block ids or network ids. The settled speed is the fixed point of
/// MechanicalNetwork.updateNetwork for one creative rotor (see <see cref="MechanicalPowerModel"/>), with a
/// tolerance wide enough for the float steps and tight enough to tell a powered line from a stuck one.
/// The network angle is advanced by every tick of the mod, by speed * 5 per second of listener time, so
/// the angle gained over a window against the clock reads whether the network is ticked at all (the
/// symptom Stratum's own comment describes for a network the tick thread does not see as loaded) and
/// whether it is ticked twice.
///
/// The classes of VSSurvivalMod are not referenced by this project: networks, nodes and the mod
/// system are read by reflection (every missing member fails as "setup is invalid"), the quern through
/// IBlockEntityContainer, the rotor's power and speed setting through its public tree attributes.
///
/// Sites. Scenarios of a class share one world and run in no order. Each one has its own site: the
/// axle lines 200 blocks east of the spawn, the quern 64 blocks east of it, the reloaded structure 400
/// blocks south, far from the spawn so the unload sticks. All of a structure lies inside one chunk, so
/// its network depends on one column only. No player joins: PrepareChain force loads the column, and a
/// force loaded column is what keeps Stratum ticking the quern's block entity listener (it only ticks
/// them near a Playing client or in a force loaded column, TickForceLoadedBlockListeners being on by default).
/// </summary>
public class MechanicalPowerScenarios : AtlasScenarioBase
{
    private const string RotorCode = "game:creativerotor-east";
    private const string AxleCode = "game:woodenaxle-we";
    private const string GearCode = "game:angledgears-eu";
    private const string QuernCode = "game:quern-granite";
    private const string GrainCode = "game:grain-spelt";
    private const string ModSystemName = "Vintagestory.GameContent.Mechanics.MechanicalPowerMod";

    private const int ChunkSize = ChunkPersistence.ChunkSize;

    // The rotor sits at this local x of its chunk: everything else of a chain is west of it, within 9 blocks.
    private const int RotorLocalX = 20;

    // The speed changes every fifth tick of the mod, so a constant reading over 40 harness ticks is a settled network.
    private const int SettleQuietTicks = 40;
    private const int SettleTimeoutTicks = 1200;
    private const int AngleWindowTicks = 150;

    private const double SpeedTolerance = 0.01;
    private const double AngleRatioLow = 0.9;
    private const double AngleRatioHigh = 1.1;

    private const int GrainCount = 8;
    private const int GrindTimeoutTicks = 3200;

    // Rotor settings used below: power 3 (the default torque factor 1.5) and a speed setting per scenario.
    private const int RotorPower = 3;
    private const int SlowSpeedSetting = 3;
    private const int FastSpeedSetting = 6;
    private const int QuernSpeedSetting = 10;

    // The reload scenario sets values that are NOT the constructor defaults of the creative rotor (power 3,
    // speed setting 3): a reload that dropped the block entity would bring back a fresh rotor reading 3/3, so
    // with the defaults the settings and the speed checks after the reload would pass for nothing. The power
    // has to stay within 1..10 or FromTreeAttributes resets it to 3.
    private const int ReloadPower = 5;
    private const int ReloadSpeedSetting = FastSpeedSetting;

    private readonly ITestOutputHelper output;

    public MechanicalPowerScenarios(ITestOutputHelper output) => this.output = output;

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task CreativeRotorNetwork_Should_ReachVanillaSpeed_When_AxleLineBuilt()
    {
        // Two independent lines in one chunk, with different lengths and speed settings: the tick loop
        // has to carry two networks through the same pass and keep them apart.
        Chain slow = await PrepareChain(dx: 200, dz: 0, localZ: 6, axles: 7, quern: false);
        Chain fast = await PrepareChain(dx: 200, dz: 0, localZ: 12, axles: 4, quern: false);
        try
        {
            int registeredBefore = Registry().Count;
            Build(slow);
            Build(fast);
            ConfigureRotor(slow, RotorPower, SlowSpeedSetting);
            ConfigureRotor(fast, RotorPower, FastSpeedSetting);

            object slowNetwork = AssertOneNetwork(slow, "slow line");
            object fastNetwork = AssertOneNetwork(fast, "fast line");
            Assert.False(ReferenceEquals(slowNetwork, fastNetwork),
                $"the two lines share one network on {ServerFlavor.Name}: separate rotors must build separate networks");
            int registered = Registry().Count - registeredBefore;
            Assert.True(registered == 2,
                $"building two rotor lines registered {registered} networks instead of 2 on {ServerFlavor.Name}");

            await WorldStable.Until(World,
                () => SpeedKey(slow.Rotor) + "|" + SpeedKey(fast.Rotor),
                quietTicks: SettleQuietTicks, timeoutTicks: SettleTimeoutTicks, what: "the speed of the two rotor lines");

            AssertSettledSpeed(slowNetwork, slow, RotorPower, SlowSpeedSetting, "slow line");
            AssertSettledSpeed(fastNetwork, fast, RotorPower, FastSpeedSetting, "fast line");
            Assert.True(Get<float>(fastNetwork, "Speed") > Get<float>(slowNetwork, "Speed") * 1.5,
                $"the line with speed setting {FastSpeedSetting} is not clearly faster than the one with {SlowSpeedSetting} on {ServerFlavor.Name}");

            AngleStart slowStart = StartAngle(slowNetwork);
            AngleStart fastStart = StartAngle(fastNetwork);
            await World.Ticks(AngleWindowTicks);
            AssertAngleRate(slowStart, "slow line");
            AssertAngleRate(fastStart, "fast line");

            // Nothing was lost on the way: still one network per line, still all members.
            AssertOneNetwork(slow, "slow line after the window");
            AssertOneNetwork(fast, "fast line after the window");
            Assert.True(NetworksAt(slow.Rotor, liveOnly: true) == 1 && NetworksAt(fast.Rotor, liveOnly: true) == 1,
                $"a line is in more than one live network on {ServerFlavor.Name}");
        }
        finally
        {
            World.Api.WorldManager.UnloadChunkColumn(slow.Cx, slow.Cz);
        }
    }

    [AtlasScenario(TimeoutMs = 300_000)]
    public async Task PoweredQuern_Should_GrindAllGrain_When_PoweredForFixedTicks()
    {
        // The speed setting of the rotor is raised to 10 (the most its right click gives): at 3 a grain would
        // take 17 s and the eight about two minutes of server time, at 10 a grain takes 4.3 s. The grain
        // count and the flour count are exact, the clock is only a timeout.
        Chain chain = await PrepareChain(dx: 64, dz: 0, localZ: 10, axles: 3, quern: true);
        try
        {
            Build(chain);
            ConfigureRotor(chain, RotorPower, QuernSpeedSetting);
            object network = AssertOneNetwork(chain, "quern line");

            await WorldStable.Until(World, () => SpeedKey(chain.Rotor),
                quietTicks: SettleQuietTicks, timeoutTicks: SettleTimeoutTicks, what: "the speed of the quern line");
            AssertSettledSpeed(network, chain, RotorPower, QuernSpeedSetting, "quern line");

            BlockEntityBehavior consumer = MpBehavior(chain.Quern);
            float trueSpeed = Get<float>(consumer, "TrueSpeed");
            Assert.True(trueSpeed > 0.5f,
                $"the quern turns at {trueSpeed:F3} although the network runs at {Get<float>(network, "Speed"):F3} on {ServerFlavor.Name}");

            IBlockEntityContainer? container = World.Api.World.BlockAccessor.GetBlockEntity(chain.Quern) as IBlockEntityContainer;
            Setup(container != null, $"the block entity at the quern is {World.Api.World.BlockAccessor.GetBlockEntity(chain.Quern)?.GetType().Name ?? "missing"}, not a container");
            ItemSlot input = container!.Inventory[0]!;
            ItemSlot ground = container.Inventory[1]!;
            Setup(input.Empty && ground.Empty, "the quern is not empty before the grain goes in");

            Item? grain = World.Api.World.GetItem(new AssetLocation(GrainCode));
            Setup(grain != null, $"{GrainCode} does not resolve");
            var grainStack = new ItemStack(grain!, GrainCount);
            GrindingProperties? props = grainStack.Collectible.GetGrindingProperties(World.Api.World, grainStack);
            Setup(props?.GroundStack?.ResolvedItemstack != null, $"{GrainCode} has no resolved grinding result");
            ItemStack perGrain = props!.GroundStack.ResolvedItemstack!;
            int expectedFlour = GrainCount * perGrain.StackSize;
            string flourCode = perGrain.Collectible.Code.ToString();

            input.Itemstack = grainStack;
            input.MarkDirty();
            Setup(input.StackSize == GrainCount, "the grain did not go into the quern input slot");

            int ticks = 0;
            try
            {
                await World.Until(() => { ticks++; return ground.StackSize >= expectedFlour; }, timeoutTicks: GrindTimeoutTicks);
            }
            catch (ScenarioTimeoutException)
            {
                Assert.Fail($"the quern ground {ground.StackSize} of {expectedFlour} flour within {GrindTimeoutTicks} ticks on {ServerFlavor.Name} " +
                            $"({input.StackSize} grain left, rotor line speed {Get<float>(network, "Speed"):F3}, quern speed {Get<float>(consumer, "TrueSpeed"):F3})");
            }

            output.WriteLine($"{ServerFlavor.Name} {ServerFlavor.Version ?? "none"}: {GrainCount} grain ground in {ticks} ticks at speed {Get<float>(network, "Speed"):F3}");

            Assert.True(input.Empty, $"{input.StackSize} grain are left in the quern after the flour is complete on {ServerFlavor.Name}");
            Assert.True(ground.StackSize == expectedFlour,
                $"the quern holds {ground.StackSize} flour instead of exactly {expectedFlour} on {ServerFlavor.Name}");
            string producedCode = ground.Itemstack?.Collectible.Code.ToString() ?? "nothing";
            Assert.True(producedCode == flourCode,
                $"the quern output is {producedCode} instead of {flourCode} on {ServerFlavor.Name}");
        }
        finally
        {
            World.Api.WorldManager.UnloadChunkColumn(chain.Cx, chain.Cz);
        }
    }

    [AtlasScenario(TimeoutMs = 240_000)]
    public async Task MechanicalNetwork_Should_Reform_When_ColumnReloaded()
    {
        // The mixed chain (rotor, three axles, an angled gear and a quern) is saved, its column unloaded and
        // loaded again. Unloading stops the network (it is no longer fully loaded, so the tick loop skips it);
        // the load runs the discovery from the rotor on the chunk thread, which has to rebuild a network
        // with the very same members that settles at the very same speed. The stale network of the
        // unloaded column stays in the registry (it held the rotor in every run, and the registry size
        // also depends on what the other scenarios left behind), so the count asserted is the live
        // networks that hold the rotor, not the registry size. The rotor carries non-default settings,
        // which only come back from the saved block entity.
        Chain chain = await PrepareChain(dx: 0, dz: 400, localZ: 10, axles: 3, quern: true);
        try
        {
            Build(chain);
            ConfigureRotor(chain, ReloadPower, ReloadSpeedSetting);
            object before = AssertOneNetwork(chain, "chain before the reload");
            await WorldStable.Until(World, () => SpeedKey(chain.Rotor),
                quietTicks: SettleQuietTicks, timeoutTicks: SettleTimeoutTicks, what: "the speed of the chain");
            AssertSettledSpeed(before, chain, ReloadPower, ReloadSpeedSetting, "chain before the reload");
            float speedBefore = Get<float>(before, "Speed");
            string membersBefore = MemberKey(before, chain.Rotor);
            Assert.True(membersBefore == chain.ExpectedMembers,
                $"the chain's members are [{membersBefore}] instead of [{chain.ExpectedMembers}] before the reload on {ServerFlavor.Name}; setup is invalid");

            // Nothing in the chunk changes after this point but the reload; the moddata is the reload gate
            // (it can only come back from the database).
            ChunkPersistence.SetColumnModdata(World, chain.Rotor, new byte[] { 0x6D, 0x50 });
            await ChunkPersistence.SaveNow(World);
            await ChunkPersistence.UnloadColumn(World, chain.Rotor);

            try
            {
                await World.Until(() => !Get<bool>(before, "fullyLoaded"), timeoutTicks: 120);
            }
            catch (ScenarioTimeoutException)
            {
                Assert.Fail($"the network of the unloaded column is still fully loaded 120 ticks after the unload on {ServerFlavor.Name}: the tick loop would keep running it");
            }
            output.WriteLine($"{ServerFlavor.Name} {ServerFlavor.Version ?? "none"}: after the unload the registry holds {Registry().Count} networks, " +
                             $"{NetworksAt(chain.Rotor, liveOnly: false)} with the rotor, {NetworksAt(chain.Rotor, liveOnly: true)} of them live");

            await ChunkPersistence.ReloadColumn(World, chain.Rotor, expectModdata: true, keepLoaded: true);
            try
            {
                await World.Until(() => ReformedMembers(chain) == chain.ExpectedMembers, timeoutTicks: ChunkPersistence.DefaultTimeoutTicks);
            }
            catch (ScenarioTimeoutException)
            {
                Assert.Fail($"the network did not reform with its members within {ChunkPersistence.DefaultTimeoutTicks} ticks of the reload on {ServerFlavor.Name}: " +
                            $"members now [{ReformedMembers(chain)}], expected [{chain.ExpectedMembers}]");
            }

            AssertBlocks(chain, setup: false, when: "after the reload");
            (int power, int speedSetting) = ReadRotorSettings(chain);
            Assert.True(power == ReloadPower && speedSetting == ReloadSpeedSetting,
                $"the rotor settings are {power}/{speedSetting} after the reload instead of {ReloadPower}/{ReloadSpeedSetting} on {ServerFlavor.Name}");

            object after = AssertOneNetwork(chain, "chain after the reload");
            await WorldStable.Until(World, () => SpeedKey(chain.Rotor),
                quietTicks: SettleQuietTicks, timeoutTicks: SettleTimeoutTicks, what: "the speed of the reformed chain");
            AssertSettledSpeed(after, chain, ReloadPower, ReloadSpeedSetting, "chain after the reload");
            float speedAfter = Get<float>(after, "Speed");
            Assert.True(Math.Abs(speedAfter - speedBefore) <= SpeedTolerance / 2,
                $"the reformed network settles at {speedAfter:F4} instead of {speedBefore:F4} on {ServerFlavor.Name}");
            Assert.True(NetworksAt(chain.Rotor, liveOnly: true) == 1,
                $"{NetworksAt(chain.Rotor, liveOnly: true)} live networks hold the rotor after the reload instead of 1 on {ServerFlavor.Name}");
            output.WriteLine($"{ServerFlavor.Name} {ServerFlavor.Version ?? "none"}: after the reload the registry holds {Registry().Count} networks, " +
                             $"{NetworksAt(chain.Rotor, liveOnly: false)} with the rotor, {NetworksAt(chain.Rotor, liveOnly: true)} of them live");

            AngleStart start = StartAngle(after);
            await World.Ticks(AngleWindowTicks);
            AssertAngleRate(start, "reformed chain");
        }
        finally
        {
            World.Api.WorldManager.UnloadChunkColumn(chain.Cx, chain.Cz);
        }
    }

    // ---- the structure ----

    /// <summary>A rotor with its axle line to the west and optionally an angled gear and a quern at the end of it.</summary>
    private sealed record Chain(BlockPos Rotor, int Axles, bool HasQuern)
    {
        public int Cx => Rotor.X / ChunkSize;

        public int Cz => Rotor.Z / ChunkSize;

        public BlockPos Gear => Rotor.AddCopy(-1 - Axles, 0, 0);

        public BlockPos Quern => Gear.UpCopy();

        /// <summary>Rotor, axles and, with a quern, the gear and the quern itself.</summary>
        public int Nodes => 1 + Axles + (HasQuern ? 2 : 0);

        /// <summary>The constant resistance of every node but the rotor: 0.0005 per axle and per gear, 0.1 for the quern.</summary>
        public double PassiveResistance => Axles * 0.0005 + (HasQuern ? 0.0005 + 0.1 : 0);

        /// <summary>The blocks in placement order: the line, the gear, the quern, and the producer last.</summary>
        public IEnumerable<(BlockPos Pos, string Code)> Blocks()
        {
            for (int i = 0; i < Axles; i++)
            {
                yield return (Rotor.AddCopy(-1 - i, 0, 0), AxleCode);
            }

            if (HasQuern)
            {
                yield return (Gear, GearCode);
                yield return (Quern, QuernCode);
            }

            yield return (Rotor, RotorCode);
        }

        /// <summary>The members of the network as the sorted positions relative to the rotor.</summary>
        public string ExpectedMembers => string.Join(";", Blocks().Select(b => Rel(b.Pos, Rotor)).OrderBy(s => s, StringComparer.Ordinal));
    }

    /// <summary>Loads the column of a site and fixes the chain on the floor of its superflat world. The
    /// chain lies in the chunk at (spawn + dx, spawn + dz), the rotor at local x 20 and the given local z.</summary>
    private async Task<Chain> PrepareChain(int dx, int dz, int localZ, int axles, bool quern)
    {
        int cx = (World.Spawn.X + dx) / ChunkSize;
        int cz = (World.Spawn.Z + dz) / ChunkSize;
        var column = new BlockPos(cx * ChunkSize + RotorLocalX, World.Spawn.Y, cz * ChunkSize + localZ, 0);
        await ChunkPersistence.LoadColumn(World, column, keepLoaded: true);

        int floor = FloorY(column.X, column.Z);
        var chain = new Chain(new BlockPos(column.X, floor + 1, column.Z, 0), axles, quern);

        Setup(chain.Rotor.Y / ChunkSize == chain.Quern.Y / ChunkSize,
            $"the chain at {chain.Rotor} crosses a chunk level");
        Setup(chain.Gear.X / ChunkSize == chain.Cx,
            $"the chain at {chain.Rotor} crosses a column border (gear at x {chain.Gear.X})");
        foreach (var (pos, _) in chain.Blocks().Where(b => b.Pos.Y == chain.Rotor.Y))
        {
            Block below = World.Api.World.BlockAccessor.GetBlock(pos.DownCopy());
            Setup(below.SideSolid[BlockFacing.UP.Index], $"the block under {Rel(pos, chain.Rotor)} of the chain at {chain.Rotor} is {below.Code}, not a solid floor");
        }

        return chain;
    }

    /// <summary>The highest solid block of the column at x, z, scanning down from just above the spawn.</summary>
    private int FloorY(int x, int z)
    {
        IBlockAccessor accessor = World.Api.World.BlockAccessor;
        for (int y = World.Spawn.Y + 4; y >= 0; y--)
        {
            if (accessor.GetBlock(new BlockPos(x, y, z, 0)).SideSolid[BlockFacing.UP.Index])
            {
                return y;
            }
        }

        Assert.Fail($"no solid floor in the column at {x},{z} on {ServerFlavor.Name}; setup is invalid");
        return -1;
    }

    private void Build(Chain chain)
    {
        foreach (var (pos, _) in chain.Blocks())
        {
            Setup(World.BlockAt(pos).BlockId == 0, $"{Rel(pos, chain.Rotor)} of the chain at {chain.Rotor} is {World.BlockAt(pos).Code}, not air, before the build");
        }

        foreach (var (pos, code) in chain.Blocks())
        {
            World.SetBlock(code, pos);
        }

        AssertBlocks(chain, setup: true, when: "after the build");
    }

    private void AssertBlocks(Chain chain, bool setup, string when)
    {
        foreach (var (pos, code) in chain.Blocks())
        {
            string found = World.BlockAt(pos).Code.ToString();
            Assert.True(found == code,
                $"{code} expected at {Rel(pos, chain.Rotor)} of the chain at {chain.Rotor} {when} but the block is {found} on {ServerFlavor.Name}" +
                (setup ? "; setup is invalid" : string.Empty));
        }
    }

    // ---- the rotor ----

    /// <summary>Sets the creative rotor's power and speed settings through its public tree attributes
    /// ("p" and "s", what a right click changes) and reads them back.</summary>
    private void ConfigureRotor(Chain chain, int power, int speedSetting)
    {
        BlockEntityBehavior rotor = MpBehavior(chain.Rotor);
        var tree = new TreeAttribute();
        rotor.ToTreeAttributes(tree);
        tree.SetInt("p", power);
        tree.SetInt("s", speedSetting);
        rotor.FromTreeAttributes(tree, World.Api.World);
        rotor.Blockentity.MarkDirty();

        (int readPower, int readSpeed) = ReadRotorSettings(chain);
        Setup(readPower == power && readSpeed == speedSetting,
            $"the rotor at {chain.Rotor} reads settings {readPower}/{readSpeed} after setting {power}/{speedSetting}");
    }

    private (int Power, int SpeedSetting) ReadRotorSettings(Chain chain)
    {
        var tree = new TreeAttribute();
        MpBehavior(chain.Rotor).ToTreeAttributes(tree);
        return (tree.GetInt("p"), tree.GetInt("s"));
    }

    // ---- reading the engine ----

    /// <summary>The mechanical power behavior (BEBehaviorMPBase or a subclass) of the block entity at the position.</summary>
    private BlockEntityBehavior MpBehavior(BlockPos pos)
    {
        BlockEntity? entity = World.Api.World.BlockAccessor.GetBlockEntity(pos);
        Setup(entity != null, $"there is no block entity at {pos}");
        BlockEntityBehavior? behavior = entity!.Behaviors.FirstOrDefault(IsMechanicalPowerBehavior);
        Setup(behavior != null, $"the block entity at {pos} ({entity.Block.Code}) has no mechanical power behavior");
        return behavior!;
    }

    private static bool IsMechanicalPowerBehavior(BlockEntityBehavior behavior)
    {
        for (Type? type = behavior.GetType(); type != null; type = type.BaseType)
        {
            if (type.Name == "BEBehaviorMPBase")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The network of the block at the position, or null while the block entity or its network is missing.</summary>
    private object? NetworkAt(BlockPos pos)
    {
        BlockEntity? entity = World.Api.World.BlockAccessor.GetBlockEntity(pos);
        BlockEntityBehavior? behavior = entity?.Behaviors.FirstOrDefault(IsMechanicalPowerBehavior);
        return behavior == null ? null : Member(behavior, "Network");
    }

    /// <summary>The speed of the network of the rotor at the position, rounded for the stability rule ("none" while there is none).</summary>
    private string SpeedKey(BlockPos rotor)
    {
        object? network = NetworkAt(rotor);
        return network == null ? "none" : Get<float>(network, "Speed").ToString("F2", CultureInfo.InvariantCulture);
    }

    /// <summary>The members of the rotor's network once the chain is whole again after a load, "" until then.</summary>
    private string ReformedMembers(Chain chain)
    {
        try
        {
            object? network = NetworkAt(chain.Rotor);
            return network == null || !Get<bool>(network, "fullyLoaded") ? string.Empty : MemberKey(network, chain.Rotor);
        }
        catch (InvalidOperationException)
        {
            // The chunk thread is still adding nodes to the dictionary: not whole yet.
            return string.Empty;
        }
    }

    private static string MemberKey(object network, BlockPos rotor)
    {
        IDictionary nodes = Get<IDictionary>(network, "nodes");
        var members = new List<string>();
        foreach (object key in nodes.Keys)
        {
            members.Add(Rel((BlockPos)key, rotor));
        }

        members.Sort(StringComparer.Ordinal);
        return string.Join(";", members);
    }

    /// <summary>Asserts that every block of the chain belongs to one network, holding exactly the chain's members, fully loaded and valid; returns it.</summary>
    private object AssertOneNetwork(Chain chain, string label)
    {
        object? network = NetworkAt(chain.Rotor);
        Assert.True(network != null, $"the rotor of the {label} has no network on {ServerFlavor.Name}");

        foreach (var (pos, _) in chain.Blocks())
        {
            object? own = NetworkAt(pos);
            Assert.True(ReferenceEquals(own, network),
                $"the block at {Rel(pos, chain.Rotor)} of the {label} is not in the rotor's network on {ServerFlavor.Name}");
        }

        string members = MemberKey(network!, chain.Rotor);
        Assert.True(members == chain.ExpectedMembers,
            $"the {label} network holds [{members}] instead of [{chain.ExpectedMembers}] on {ServerFlavor.Name}");
        Assert.True(Get<bool>(network!, "fullyLoaded") && Get<bool>(network!, "Valid"),
            $"the {label} network is not fully loaded and valid on {ServerFlavor.Name}");
        return network!;
    }

    private void AssertSettledSpeed(object network, Chain chain, int power, int speedSetting, string label)
    {
        double expected = MechanicalPowerModel.SettledSpeed(power, speedSetting, chain.PassiveResistance, chain.Nodes);
        float speed = Get<float>(network, "Speed");
        output.WriteLine($"{ServerFlavor.Name} {ServerFlavor.Version ?? "none"}: {label} settled at {speed:F4} (model {expected:F4})");
        Assert.True(Math.Abs(speed - expected) <= SpeedTolerance,
            $"the {label} settled at speed {speed:F4} instead of {expected:F4} (tolerance {SpeedTolerance}) on {ServerFlavor.Name}");
    }

    private sealed record AngleStart(object Network, float Angle, float Speed, long Ms);

    private AngleStart StartAngle(object network) =>
        new(network, Get<float>(network, "AngleRad"), Get<float>(network, "Speed"), World.Api.World.ElapsedMilliseconds);

    /// <summary>The network angle grows by speed * 5 per second of mod tick time; over a window the gain against the
    /// server clock is about 1.0 of that, 0 for a network nobody ticks, 2 for one ticked twice.</summary>
    private void AssertAngleRate(AngleStart start, string label)
    {
        float angle = Get<float>(start.Network, "AngleRad");
        float speed = Get<float>(start.Network, "Speed");
        long elapsedMs = World.Api.World.ElapsedMilliseconds - start.Ms;
        double expected = (start.Speed + speed) / 2.0 * 5.0 * elapsedMs / 1000.0;
        Setup(expected > 0, $"the {label} has speed {speed} over {elapsedMs} ms, nothing to measure the angle against");

        double ratio = (angle - start.Angle) / expected;
        output.WriteLine($"{ServerFlavor.Name} {ServerFlavor.Version ?? "none"}: {label} angle gain is {ratio:F3} of the expected over {elapsedMs} ms");
        Assert.True(ratio >= AngleRatioLow && ratio <= AngleRatioHigh,
            $"the {label} angle advanced {ratio:F3} of the expected {expected:F3} rad over {elapsedMs} ms on {ServerFlavor.Name} " +
            $"(bounds {AngleRatioLow} to {AngleRatioHigh}): the network is ticked {(ratio < AngleRatioLow ? "too rarely" : "too often")}");
    }

    private IDictionary Registry()
    {
        ModSystem? system = World.Api.ModLoader.GetModSystem(ModSystemName);
        Setup(system != null, $"mod system {ModSystemName} is not loaded");
        object data = Get<object>(system!, "data");
        return Get<IDictionary>(data, "networksById");
    }

    /// <summary>How many networks of the registry hold a node at the position (only the fully loaded ones with
    /// <paramref name="liveOnly"/>: the stale network of an unloaded column keeps its nodes but is not fully loaded).</summary>
    private int NetworksAt(BlockPos pos, bool liveOnly)
    {
        int count = 0;
        foreach (object network in Registry().Values)
        {
            if (liveOnly && !Get<bool>(network, "fullyLoaded"))
            {
                continue;
            }

            foreach (object key in Get<IDictionary>(network, "nodes").Keys)
            {
                if (key is BlockPos node && node.X == pos.X && node.Y == pos.Y && node.Z == pos.Z)
                {
                    count++;
                    break;
                }
            }
        }

        return count;
    }

    // ---- reflection and guards ----

    private static string Rel(BlockPos pos, BlockPos origin) =>
        FormattableString.Invariant($"{pos.X - origin.X},{pos.Y - origin.Y},{pos.Z - origin.Z}");

    private static void Setup(bool condition, string what) =>
        Assert.True(condition, $"{what} on {ServerFlavor.Name}; setup is invalid");

    private static object? Member(object target, string name)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        for (Type? type = target.GetType(); type != null; type = type.BaseType)
        {
            PropertyInfo? property = type.GetProperty(name, flags);
            if (property != null)
            {
                return property.GetValue(target);
            }

            FieldInfo? field = type.GetField(name, flags);
            if (field != null)
            {
                return field.GetValue(target);
            }
        }

        Assert.Fail($"{target.GetType().Name}.{name} is missing on {ServerFlavor.Name}; setup is invalid");
        return null;
    }

    private static T Get<T>(object target, string name)
    {
        object? value = Member(target, name);
        Assert.True(value is T,
            $"{target.GetType().Name}.{name} is {value?.GetType().Name ?? "null"}, not {typeof(T).Name}, on {ServerFlavor.Name}; setup is invalid");
        return (T)value!;
    }
}

/// <summary>
/// The speed a creative rotor line settles at, derived from MechanicalNetwork.updateNetwork. Each pass the
/// network adds up the torque of its nodes and their resistance: the rotor gives (target - speed) * torque
/// factor (target is 0.1 per speed setting, the factor 0.5 per power setting, both while the speed is below
/// the target), every node resists with its own constant plus speed squared over 1000 of air drag (all
/// gear ratios are 1), and the speed moves by the unused torque until it is zero. That zero is where
/// <see cref="SettledSpeed"/> bisects: torque * (target - speed) = passive + nodes * speed^2 / 1000.
/// </summary>
internal static class MechanicalPowerModel
{
    public static double SettledSpeed(int powerSetting, int speedSetting, double passiveResistance, int nodes)
    {
        double torqueFactor = 0.5 * powerSetting;
        double target = 0.1 * speedSetting;
        double low = 0;
        double high = target;
        for (int i = 0; i < 80; i++)
        {
            double mid = (low + high) / 2;
            double unused = torqueFactor * (target - mid) - passiveResistance - nodes * mid * mid / 1000.0;
            if (unused > 0)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return (low + high) / 2;
    }
}
