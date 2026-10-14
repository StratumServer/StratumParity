using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Xunit;
using Xunit.Abstractions;

namespace StratumParity.Scenarios;

/// <summary>
/// Block light, sun light and relight parity for Stratum's rewrite of the ChunkIlluminator: one multi
/// source BFS per light bucket (the brightest first), face aware absorption for partial blocks, Top-4
/// colour tracking per cell, sun light flushed once per relight pass, and a FullRelight that restores
/// block light from the centroid of the sources. The staged stratumparitylight mod supplies full cube
/// lamps with an exact lightHsv per colour and no light absorption, so the closed forms below control
/// every source value and lamps never shade each other. Nothing here needs a player.
///
/// Expectations. Every open air check is a closed form, never a golden: the level of a cell is the
/// maximum over the sources of V minus the Manhattan distance (floored at 0), V read from
/// Block.GetLightHsv. A 1 wide corridor (a granite tube around it, so no other path exists) adds the
/// absorption of the cells the light leaves: level(k) = level(k - 1) - absorption(k - 1) - 1, and an
/// absorbing cell still holds the light that reached it. The per flavor tables (partial blocks,
/// FullRelight) were derived from the vanilla ChunkIlluminator and from the Stratum patch of the pinned
/// and the scouted build (the two carry the same one), and the lamp colour mix and the sealed hollow
/// were measured on vanilla and on both Stratum builds; every value that depends on engine internals is a named
/// constant next to the scenario that uses it, so a game bump or a Stratum change re-pins one line.
///
/// Reading light. Lighting runs on its own thread (vanilla every 10 ms, Stratum flushes the sun light
/// at the end of each pass), so a scenario never counts ticks: it waits for the light to react
/// (a positive signal, the source cell lit or the sampled state changed) and then for
/// <see cref="WorldStable"/> (identical reads for a few ticks). GetLightLevel answers the sun
/// brightness for a chunk that is not loaded, so every sampled chunk is checked loaded first. Block
/// light is read through GetLightLevel(OnlyBlockLight); the colour of a cell through the packed light of
/// its chunk (GetLightRGBs throws on the vanilla server, its tables are client side).
///
/// Anchors. Each scenario owns a slot of a 3 x 3 grid of chunk columns 8 columns (256 blocks) apart,
/// loaded kept and unloaded in finally; the sampled volumes sit inside one column unless the
/// scenario is about a chunk border.
/// </summary>
[AtlasWorld(Mods = new[] { "mods/lightprobe" })]
public class LightingScenarios : AtlasScenarioBase
{
    private const int ChunkSize = 32;
    private const int SlotSpacingChunks = 8;
    private const int LampY = 16;

    // Vanilla's relight keeps at most 15 light sources per cell (LightSourcesAtBlock.AddHsv ignores the 16th): a cluster
    // of 16 lamps leaves the last lamp placed at the 14 its neighbours give it. Stratum's Top-4 has no such cell cap, but
    // the cluster stays below it so the closed form holds on both flavors.
    private const int ClusterLamps = 12;
    private const int LoadTimeoutTicks = 3000;
    private const int ReactTimeoutTicks = 600;
    private const int SettleTimeoutTicks = 600;
    private const int QuietTicks = 3;
    private const int SunQuietTicks = 5;
    // The sealed hollow can settle late (vanilla needs a lighting task per roof block): the plan asks for 60 quiet ticks.
    private const int HollowQuietTicks = 60;

    private const string AirCode = "game:air";
    private const string GraniteCode = "game:rock-granite";
    private const string TorchCode = "game:torch-basic-lit-up";
    private const string GlassCode = "game:glass-plain";
    private const string LeavesCode = "game:leaves-placed-oak";
    private const string WaterCode = "game:water-still-7";
    private const string LampWhiteCode = "stratumparitylight:lamp-white";
    private const string ChiseledCode = "game:chiseledblock";
    private const string MicroblockEntityTypeName = "Vintagestory.GameContent.BlockEntityMicroBlock";

    // The five lamp mix: red, yellow, green, cyan and blue lamps two blocks apart (x = 0, 2, 4, 6, 8), read from the
    // cell at (4, 0, 2). The brightness is the maximum (the green lamp at distance 2 gives 14) on both flavors. The
    // colour is the level weighted mix of the sources that reached the cell: vanilla mixes all five (levels 14, 12, 12,
    // 10, 10), Stratum tracks the four brightest per cell (Top-4, documented in its ChunkIlluminator header) and drops
    // the last of the two sources at level 10. Measured on vanilla and on both Stratum builds.
    private const int MixedBlockLevel = 14;
    private static readonly (int Hue, int Sat) VanillaMix = (21, 3);
    private static readonly (int Hue, int Sat) StratumMix = (18, 5);

    // Hue (0..52, the engine quantizes 53 down to 52) and saturation of one lamp read from the next cell, the same on
    // every flavor: it pins the colour table the mix starts from.
    private static readonly (string Color, int Hue, int Sat)[] SingleLamps =
    {
        ("red", 0, 7), ("yellow", 11, 7), ("green", 21, 7), ("cyan", 32, 7),
        ("blue", 43, 7), ("magenta", 52, 7), ("white", 0, 0),
    };

    // The sealed hollow: interior sun light with a granite roof on. Vanilla and Stratum both read 0: Stratum recomputes
    // the columns below an edit from the sky, and vanilla's incremental pass clears the light the roof cut off block
    // by block (measured on both, plain parity).
    private const int SealedInteriorSun = 0;

    private readonly ITestOutputHelper output;

    public LightingScenarios(ITestOutputHelper output) => this.output = output;

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task TorchLight_Should_FallOffByManhattanDistance_When_SingleTorchInOpenAir()
    {
        // Two sources of different strength in turn (the white lamp, V 16, and the vanilla torch, V 14): the
        // cube of 15 blocks around the source must read max(0, V - distance) everywhere, and read 0 everywhere
        // once the source is gone (the darkness pass reaches as far as the light did).
        (int cx, int cz) = Slot(0);
        await LoadColumns((cx, cz));
        try
        {
            BlockPos source = At(cx, cz, 16, LampY, 16);
            Cells cube = Cells.Around(source, 7, 7, 7);
            AssertOpenAir(cube, "the cube around the source");

            foreach (string code in new[] { LampWhiteCode, TorchCode })
            {
                int v = LightValue(code, source);
                int[] want = Falloff(cube, (source, v));
                Assert.True(want.Max() == v && want.Min() == 0,
                    $"the closed form of {code} (V {v}) is flat inside the cube on {ServerFlavor.Name}; setup is invalid");

                int[] got = await PlaceAndSettle(code, source, cube, $"{code} falloff");
                output.WriteLine($"{code} on {ServerFlavor.Name}, block light along +x: {string.Join(",", cube.Slice(got, source, 8, 1, 0, 0))}");
                string? diff = Compare(cube, got, want, source);
                Assert.True(diff == null, $"block light around {code} (V {v}) is not max(0, V - distance) on {ServerFlavor.Name}: {diff}");

                int[] dark = await RemoveAndSettle(source, cube, $"{code} removal");
                string? left = Compare(cube, dark, new int[cube.Count], source);
                Assert.True(left == null, $"block light stays after {code} is removed on {ServerFlavor.Name}: {left}");
            }
        }
        finally
        {
            UnloadColumns((cx, cz));
        }
    }

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task TorchLight_Should_TakeBrighterSourceAndClear_When_SecondTorchAddedThenRemoved()
    {
        // A torch (V 14) and, six blocks away, a brighter lamp (V 16): the volume reads the maximum of the two
        // closed forms. Removing the lamp must give the torch alone back (the darkness pass of the lamp wipes
        // the cells the torch lit too, and the relight has to restore them), removing the torch gives 0.
        (int cx, int cz) = Slot(1);
        await LoadColumns((cx, cz));
        try
        {
            BlockPos torch = At(cx, cz, 13, LampY, 16);
            BlockPos lamp = At(cx, cz, 19, LampY, 16);
            Cells volume = Cells.Box(torch.AddCopy(-7, -7, -7), lamp.AddCopy(7, 7, 7));
            AssertOpenAir(volume, "the volume around both sources");
            int vTorch = LightValue(TorchCode, torch);
            int vLamp = LightValue(LampWhiteCode, lamp);
            Assert.True(vLamp > vTorch, $"the lamp (V {vLamp}) is not brighter than the torch (V {vTorch}) on {ServerFlavor.Name}; setup is invalid");

            int[] wantTorch = Falloff(volume, (torch, vTorch));
            int[] wantBoth = Falloff(volume, (torch, vTorch), (lamp, vLamp));
            Assert.True(!wantTorch.SequenceEqual(wantBoth),
                $"the lamp changes nothing inside the volume on {ServerFlavor.Name}; setup is invalid");

            int[] got = await PlaceAndSettle(TorchCode, torch, volume, "first torch");
            string? diff = Compare(volume, got, wantTorch, torch);
            Assert.True(diff == null, $"block light of the first torch is not max(0, V - distance) on {ServerFlavor.Name}: {diff}");

            got = await PlaceAndSettle(LampWhiteCode, lamp, volume, "second source");
            output.WriteLine($"torch and lamp on {ServerFlavor.Name}, block light along +x: {string.Join(",", volume.Slice(got, torch.AddCopy(-7, 0, 0), 21, 1, 0, 0))}");
            diff = Compare(volume, got, wantBoth, torch);
            Assert.True(diff == null, $"block light of two sources is not the maximum of the two closed forms on {ServerFlavor.Name}: {diff}");

            got = await RemoveAndSettle(lamp, volume, "second source removal");
            diff = Compare(volume, got, wantTorch, torch);
            Assert.True(diff == null, $"block light does not return to the first torch alone after the second source is removed on {ServerFlavor.Name}: {diff}");

            got = await RemoveAndSettle(torch, volume, "first torch removal");
            diff = Compare(volume, got, new int[volume.Count], torch);
            Assert.True(diff == null, $"block light stays after both sources are removed on {ServerFlavor.Name}: {diff}");
        }
        finally
        {
            UnloadColumns((cx, cz));
        }
    }

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task TorchLight_Should_CrossChunkBorder_When_TorchSitsOnTheEdge()
    {
        // A lamp on the last block of its chunk (local 31) along each axis in turn: the closed form must hold
        // across the border, the light has to enter the loaded neighbour. Upstream Stratum has a branch about
        // light at chunk seams (fix/lighting-seam-fullcube-exception, sun light of full cube faces): block
        // light never goes through those paths, and this pins that it stays exact.
        (int cx, int cz) = Slot(2);
        (int, int)[] columns = { (cx, cz), (cx + 1, cz), (cx, cz + 1) };
        await LoadColumns(columns);
        try
        {
            (string Axis, BlockPos Pos)[] lamps =
            {
                ("x", At(cx, cz, 31, LampY, 16)),
                ("z", At(cx, cz, 16, LampY, 31)),
                ("y", At(cx, cz, 16, 31, 16)),
            };

            foreach ((string axis, BlockPos source) in lamps)
            {
                Cells cube = Cells.Around(source, 7, 7, 7);
                AssertOpenAir(cube, $"the cube around the {axis} edge lamp");
                int v = LightValue(LampWhiteCode, source);
                int[] want = Falloff(cube, (source, v));

                int[] got = await PlaceAndSettle(LampWhiteCode, source, cube, $"{axis} edge lamp");
                (int dx, int dy, int dz) = axis switch { "x" => (1, 0, 0), "y" => (0, 1, 0), _ => (0, 0, 1) };
                output.WriteLine($"lamp on the {axis} edge on {ServerFlavor.Name}, block light along {axis} from -7 to +7: " +
                                 $"{string.Join(",", cube.Slice(got, source.AddCopy(-7 * dx, -7 * dy, -7 * dz), 15, dx, dy, dz))}");
                string? diff = Compare(cube, got, want, source);
                Assert.True(diff == null, $"block light of a lamp on the {axis} chunk edge is not max(0, V - distance) across the border on {ServerFlavor.Name}: {diff}");

                int[] dark = await RemoveAndSettle(source, cube, $"{axis} edge lamp removal");
                string? left = Compare(cube, dark, new int[cube.Count], source);
                Assert.True(left == null, $"block light stays across the {axis} chunk edge after the lamp is removed on {ServerFlavor.Name}: {left}");
            }
        }
        finally
        {
            UnloadColumns(columns);
        }
    }

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task TorchLight_Should_MatchVanillaThroughFullBlocks_When_GlassLeavesWaterStoneInPath()
    {
        // A lamp at the end of a 1 wide corridor (granite all around, so the only path is the corridor) that
        // runs through glass (absorption 0), leaves (1), water (1, in the fluid layer, held in by the glass
        // on both sides) and a granite plug (99), then open air. The expectation is derived from the
        // absorption each placed block reports, and the same on both flavors: the light the corridor
        // carries is V minus 1 per cell minus the absorption of every cell it leaves, the plug holds the light
        // that reaches it and passes none, and the air behind it is dark. Every absorbing cell is entered with
        // more light than its absorption: Stratum gives a partial block (and water, whose solid layer is air) no
        // light at all when the arriving level is below its absorption, a quirk the margin keeps out of this
        // scenario (the partial blocks scenario pins it).
        (int cx, int cz) = Slot(3);
        await LoadColumns((cx, cz));
        try
        {
            BlockPos Cell(int i) => At(cx, cz, 6 + i, LampY, 16);
            string[] contents = { LampWhiteCode, GlassCode, LeavesCode, WaterCode, GlassCode, GraniteCode };
            Block[] blocks = contents.Select(ResolveBlock).ToArray();
            int[] absorption = blocks.Select(b => b.LightAbsorption).ToArray();
            Assert.True(absorption[0] == 0 && absorption[1] == 0 && absorption[2] > 0 && absorption[3] > 0 && absorption[5] >= 99,
                $"absorption of lamp, glass, leaves, water, granite is [{string.Join(",", absorption)}] on {ServerFlavor.Name}; setup is invalid");

            Cells volume = Cells.Box(At(cx, cz, 5, LampY - 1, 15), At(cx, cz, 21, LampY + 1, 17));
            AssertOpenAir(volume, "the volume of the tube");
            BuildTube(cx, cz, 6, 20, LampY, 16);
            for (int i = 1; i < contents.Length; i++)
            {
                World.SetBlock(contents[i], Cell(i));
            }

            AssertContents(contents, Cell, "before the lamp");

            const int Sampled = 9;
            int v = LightValue(LampWhiteCode, Cell(0));
            int[] want = Corridor(v, absorption, Sampled);
            for (int i = 1; i < contents.Length - 1; i++)
            {
                Assert.True(want[i] > absorption[i],
                    $"the light arriving at {contents[i]} ({want[i]}) does not exceed its absorption ({absorption[i]}) on {ServerFlavor.Name}; setup is invalid");
            }

            Assert.True(want[Sampled - 1] == 0 && want[contents.Length - 1] > 0,
                $"the corridor closed form [{string.Join(",", want)}] must hold light at the plug and none past it on {ServerFlavor.Name}; setup is invalid");

            Cells corridor = Cells.Of(Enumerable.Range(0, Sampled).Select(Cell));
            int[] got = await PlaceAndSettle(LampWhiteCode, Cell(0), corridor, "corridor lamp");
            output.WriteLine($"corridor lamp, glass, leaves, water, glass, granite, air on {ServerFlavor.Name}: [{string.Join(",", got)}], expected [{string.Join(",", want)}]");
            string? diff = Compare(corridor, got, want, Cell(0));
            Assert.True(diff == null,
                $"the corridor does not carry V minus distance minus absorption on {ServerFlavor.Name}: {diff} " +
                $"(read [{string.Join(",", got)}], expected [{string.Join(",", want)}])");

            AssertContents(contents, Cell, "after the light settled");
        }
        finally
        {
            UnloadColumns((cx, cz));
        }
    }

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task PartialBlocks_Should_DimLightPerFlavorGolden_When_SlabStairsChiseledInPath()
    {
        // Four corridors, each: lamp, one partial block, then air (granite tube around, so one path). Vanilla
        // absorbs 99 in a cell of every one of these blocks: the cell holds the light that reaches it (15) and
        // the air behind is dark. Stratum's directional absorption (ChunkIlluminator.GetEffectiveAbsorption)
        // reads the faces: a block with some solid faces lets a ray through that neither enters nor leaves
        // through a solid face, at an absorption of the incoming level times 32 / 64 (so a down slab and a
        // stairs seen from the open side pass about half, 15 becomes 7 and then falls 1 per cell), and it
        // gives a block no light at all when the arriving level is below its absorption and not every face
        // is solid: a stairs whose solid face is the one the ray leaves through, and a chiseled block (the
        // block type declares no solid face, whatever its voxels cover), read 0 in their own cell.
        (int cx, int cz) = Slot(4);
        await LoadColumns((cx, cz));
        try
        {
            PartialLane[] lanes =
            {
                new("slab (down)", 8, "game:plankslab-aged-down-free", false, new[] { false, false, false, false, false, true },
                    new[] { 16, 15, 0, 0, 0, 0, 0 }, new[] { 16, 15, 7, 6, 5, 4, 3 }),
                new("stairs (up, north: open along the ray)", 13, "game:plankstairs-aged-up-north-free", false, new[] { true, false, false, false, false, true },
                    new[] { 16, 15, 0, 0, 0, 0, 0 }, new[] { 16, 15, 7, 6, 5, 4, 3 }),
                new("stairs (up, east: solid face on the ray)", 18, "game:plankstairs-aged-up-east-free", false, new[] { false, true, false, false, false, true },
                    new[] { 16, 15, 0, 0, 0, 0, 0 }, new[] { 16, 0, 0, 0, 0, 0, 0 }),
                new("chiseled block (bottom half)", 23, ChiseledCode, true, new[] { false, false, false, false, false, false },
                    new[] { 16, 15, 0, 0, 0, 0, 0 }, new[] { 16, 0, 0, 0, 0, 0, 0 }),
            };

            const int LaneLength = 7;
            Cells volume = Cells.Box(At(cx, cz, 5, LampY - 1, 6), At(cx, cz, 13, LampY + 1, 24));
            AssertOpenAir(volume, "the volume of the four tubes");
            foreach (PartialLane lane in lanes)
            {
                BuildTube(cx, cz, 6, 12, LampY, lane.LocalZ);
                BlockPos partial = At(cx, cz, 7, LampY, lane.LocalZ);
                World.SetBlock(lane.Code, partial);
                if (lane.Chiseled)
                {
                    Chiseled.MakeBottomHalf(World, partial, ResolveBlock(GraniteCode));
                }

                Block placed = World.BlockAt(partial);
                Assert.True(placed.Code.ToString() == lane.Code,
                    $"{lane.Name}: {placed.Code} reads back at {partial} on {ServerFlavor.Name}; setup is invalid");
                Assert.True(placed.GetLightAbsorption(World.Api.World.BlockAccessor, partial) == 99,
                    $"{lane.Name}: absorption is {placed.GetLightAbsorption(World.Api.World.BlockAccessor, partial)} instead of 99 on {ServerFlavor.Name}; setup is invalid");
                bool[] solid = Enumerable.Range(0, 6).Select(f => placed.SideSolid[f]).ToArray();
                Assert.True(solid.SequenceEqual(lane.SolidFaces),
                    $"{lane.Name}: solid faces (N, E, S, W, U, D) are [{string.Join(",", solid)}], expected [{string.Join(",", lane.SolidFaces)}] on {ServerFlavor.Name}; setup is invalid");
            }

            // The lamps last, all in one step: their light is computed against the finished blocks.
            Cells cells = Cells.Of(lanes.SelectMany(l => Enumerable.Range(0, LaneLength).Select(i => At(cx, cz, 6 + i, LampY, l.LocalZ))));
            foreach (PartialLane lane in lanes)
            {
                World.SetBlock(LampWhiteCode, At(cx, cz, 6, LampY, lane.LocalZ));
            }

            foreach (PartialLane lane in lanes)
            {
                Assert.True(World.BlockAt(At(cx, cz, 6, LampY, lane.LocalZ)).Code.ToString() == LampWhiteCode,
                    $"{lane.Name}: the lamp is not in place on {ServerFlavor.Name}; setup is invalid");
            }

            await Reacts(() => lanes.All(l => LevelAt(At(cx, cz, 6, LampY, l.LocalZ)) > 0), "the four lamps lighting");
            int[] got = Levels(await Stable(cells, EnumLightLevelType.OnlyBlockLight, "the four corridors"));

            var problems = new List<string>();
            for (int laneIndex = 0; laneIndex < lanes.Length; laneIndex++)
            {
                PartialLane lane = lanes[laneIndex];
                int[] read = got.Skip(laneIndex * LaneLength).Take(LaneLength).ToArray();
                int[] want = ServerFlavor.IsStratum ? lane.Stratum : lane.Vanilla;
                output.WriteLine($"{lane.Name} on {ServerFlavor.Name}: [{string.Join(",", read)}], expected [{string.Join(",", want)}]");
                if (!read.SequenceEqual(want))
                {
                    problems.Add($"{lane.Name} reads [{string.Join(",", read)}], expected [{string.Join(",", want)}]");
                }
            }

            Assert.True(problems.Count == 0, $"partial blocks dim the light differently from the {ServerFlavor.Name} table: {string.Join("; ", problems)}");
        }
        finally
        {
            UnloadColumns((cx, cz));
        }
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task SunLight_Should_MatchGolden_When_HollowIsRoofedThenReopened()
    {
        // A 3 x 3 x 3 hollow in a granite shell (floor, walls) floating in the open: open to the sky its
        // sun light is the sky's everywhere, with the roof on it is 0 on both flavors, with the
        // roof gone again it is the sky's again. The sun light flush is deferred on Stratum and the
        // incremental pass of vanilla needs a task per block: each state is awaited until it stops changing.
        (int cx, int cz) = Slot(5);
        await LoadColumns((cx, cz));
        try
        {
            int sky = World.Api.World.SunBrightness;
            Cells shell = Cells.Box(At(cx, cz, 13, LampY, 13), At(cx, cz, 17, LampY + 4, 17));
            Cells interior = Cells.Box(At(cx, cz, 14, LampY + 1, 14), At(cx, cz, 16, LampY + 3, 16));
            BlockPos outside = At(cx, cz, 4, LampY + 2, 4);
            AssertOpenAir(shell, "the volume of the shell");
            Assert.True(LevelAt(outside, EnumLightLevelType.OnlySunLight) == sky,
                $"the open air reads sun light {LevelAt(outside, EnumLightLevelType.OnlySunLight)} instead of the sky's {sky} on {ServerFlavor.Name}; setup is invalid");

            int baseX = cx * ChunkSize;
            int baseZ = cz * ChunkSize;
            foreach (BlockPos pos in shell.Positions)
            {
                int lx = pos.X - baseX;
                int lz = pos.Z - baseZ;
                bool floor = pos.Y == LampY;
                bool roof = pos.Y == LampY + 4;
                bool wall = lx == 13 || lx == 17 || lz == 13 || lz == 17;
                if (floor || (wall && !roof))
                {
                    World.SetBlock(GraniteCode, pos);
                }
            }

            int[] open = Levels(await Stable(interior, EnumLightLevelType.OnlySunLight, "the open hollow", HollowQuietTicks));
            string? diff = Compare(interior, open, Enumerable.Repeat(sky, interior.Count).ToArray(), interior.Positions[0]);
            Assert.True(diff == null, $"the hollow open to the sky does not read the sky's sun light ({sky}) on {ServerFlavor.Name}: {diff}");
            string openDigest = Digest(open);

            BlockPos[] roof2 = shell.Positions.Where(p => p.Y == LampY + 4).ToArray();
            foreach (BlockPos pos in roof2)
            {
                World.SetBlock(GraniteCode, pos);
            }

            bool sealedReacted = await TryUntil(() => Digest(Read(interior, EnumLightLevelType.OnlySunLight)) != openDigest);
            int[] sealedLevels = Levels(await Stable(interior, EnumLightLevelType.OnlySunLight, "the sealed hollow", HollowQuietTicks));
            output.WriteLine($"sealed hollow on {ServerFlavor.Name}: sun light of the 27 interior cells {string.Join(",", sealedLevels)} (reacted: {sealedReacted}), expected {SealedInteriorSun} everywhere");
            diff = Compare(interior, sealedLevels, Enumerable.Repeat(SealedInteriorSun, interior.Count).ToArray(), interior.Positions[0]);
            Assert.True(diff == null,
                $"the sealed hollow does not read sun light {SealedInteriorSun} on {ServerFlavor.Name} (the roof took effect: {sealedReacted}): {diff}");
            string sealedDigest = Digest(sealedLevels);

            foreach (BlockPos pos in roof2)
            {
                World.SetBlock(AirCode, pos);
            }

            bool reopenReacted = await TryUntil(() => Digest(Read(interior, EnumLightLevelType.OnlySunLight)) != sealedDigest);
            int[] reopened = Levels(await Stable(interior, EnumLightLevelType.OnlySunLight, "the reopened hollow", HollowQuietTicks));
            output.WriteLine($"reopened hollow on {ServerFlavor.Name}: sun light of the 27 interior cells {string.Join(",", reopened)} (reacted: {reopenReacted})");
            diff = Compare(interior, reopened, Enumerable.Repeat(sky, interior.Count).ToArray(), interior.Positions[0]);
            Assert.True(diff == null,
                $"the reopened hollow does not read the sky's sun light ({sky}) again on {ServerFlavor.Name} (the roof removal took effect: {reopenReacted}): {diff}");
        }
        finally
        {
            UnloadColumns((cx, cz));
        }
    }

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task ColoredLamps_Should_MixPerFlavorGolden_When_FiveSourcesOverlap()
    {
        // The colour of a cell lit by five coloured lamps: the brightness is the plain maximum on both flavors, the
        // hue and saturation are a per flavor golden (Stratum keeps the four brightest sources of a cell).
        // Before the mix, every colour of lamp read from the next cell gives its own hue on both flavors.
        (int cx, int cz) = Slot(6);
        await LoadColumns((cx, cz));
        try
        {
            BlockPos first = At(cx, cz, 8, LampY, 16);
            foreach ((string color, int hue, int sat) in SingleLamps)
            {
                string code = $"stratumparitylight:lamp-{color}";
                BlockPos next = first.AddCopy(1, 0, 0);
                AssertOpenAir(Cells.Around(first, 1, 1, 1), $"the cells around the {color} lamp");
                World.SetBlock(code, first);
                Assert.True(World.BlockAt(first).Code.ToString() == code, $"{code} is not in place on {ServerFlavor.Name}; setup is invalid");
                await Reacts(() => LevelAt(next) > 0, $"the {color} lamp lighting");
                string settled = await WorldStable.Until(World, () => Describe(ReadPacked(next)), QuietTicks, SettleTimeoutTicks, $"the light next to the {color} lamp");
                (int Sun, int Block, int Hue, int Sat) read = ReadPacked(next);
                output.WriteLine($"{color} lamp on {ServerFlavor.Name}: next cell {settled}");
                Assert.True(read.Hue == hue && read.Sat == sat,
                    $"the {color} lamp reads hue {read.Hue} saturation {read.Sat} next to it, expected {hue} and {sat}, on {ServerFlavor.Name}");

                World.SetBlock(AirCode, first);
                await Reacts(() => LevelAt(first) == 0, $"the {color} lamp removal");
                await Stable(Cells.Around(first, 2, 2, 2), EnumLightLevelType.OnlyBlockLight, $"darkness after the {color} lamp");
            }

            string[] colors = { "red", "yellow", "green", "cyan", "blue" };
            BlockPos[] lamps = colors.Select((_, i) => first.AddCopy(2 * i, 0, 0)).ToArray();
            BlockPos mixCell = first.AddCopy(4, 0, 2);
            AssertOpenAir(Cells.Box(first.AddCopy(-2, -3, -2), first.AddCopy(10, 3, 5)), "the volume of the five lamps");
            for (int i = 0; i < colors.Length; i++)
            {
                World.SetBlock($"stratumparitylight:lamp-{colors[i]}", lamps[i]);
            }

            await Reacts(() => lamps.All(l => LevelAt(l) == 16) && LevelAt(mixCell) > 0, "the five lamps lighting");
            string mix = await WorldStable.Until(World, () => Describe(ReadPacked(mixCell)), QuietTicks, SettleTimeoutTicks, "the mixed light");
            (int Sun, int Block, int Hue, int Sat) mixed = ReadPacked(mixCell);
            (int Hue, int Sat) golden = ServerFlavor.IsStratum ? StratumMix : VanillaMix;
            output.WriteLine($"five lamps on {ServerFlavor.Name}: {mix}, golden hue {golden.Hue} saturation {golden.Sat}");
            Assert.True(mixed.Block == MixedBlockLevel,
                $"the five lamps give block light {mixed.Block} at the mix cell, expected the maximum {MixedBlockLevel}, on {ServerFlavor.Name}");
            Assert.True(mixed.Hue == golden.Hue && mixed.Sat == golden.Sat,
                $"the five lamps mix to hue {mixed.Hue} saturation {mixed.Sat}, the {ServerFlavor.Name} golden is {golden.Hue} and {golden.Sat}");
        }
        finally
        {
            UnloadColumns((cx, cz));
        }
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task FullRelight_Should_RestoreSunlight_When_RegionRelit()
    {
        // FullRelight over a region holding a cluster of 12 lamps under a granite plate and one more lamp 150 blocks
        // away. Both flavors clear the light of the region and recompute the sun light, which must come back as it
        // was (a plate shades the cell below it, so a relight that forgets the horizontal spread would show).
        // Block light is a per flavor golden: vanilla re-places every light source at a doubled chunk offset (its
        // FullRelight adds the chunk base to a coordinate that already has it), which is off the map and unloaded,
        // so the whole region reads dark afterwards; Stratum restores the sources within reach of the centroid
        // of all the sources (the cluster, pulled slightly toward the far lamp), and leaves the far lamp dark
        // since its chunk is outside the 3 x 3 x 3 chunks around the centroid (the clip of its 128 block grid
        // and neighbourhood, which Stratum documents as a limit). The sampled cells are the lamp hull and two
        // blocks around it: the multi source pass drops a cell once its distance to the centroid reaches the
        // range plus its level, which only touches cells further out.
        (int cx, int cz) = Slot(7);
        (int, int)[] columns = { (cx, cz), (cx + 5, cz) };
        await LoadColumns(columns);
        try
        {
            BlockPos[] cluster = Enumerable.Range(0, ClusterLamps).Select(i => At(cx, cz, 12 + 2 * (i % 4), LampY, 14 + 2 * (i / 4))).ToArray();
            BlockPos far = At(cx + 5, cz, 5, LampY, 17);
            int distance = far.X - (cx * ChunkSize + 15);
            Assert.True(distance == 150, $"the far lamp is {distance} blocks from the middle of the cluster instead of 150 on {ServerFlavor.Name}; setup is invalid");

            Cells clusterCells = Cells.Box(At(cx, cz, 10, LampY - 2, 12), At(cx, cz, 20, LampY + 2, 20));
            Cells farCells = Cells.Around(far, 2, 2, 2);
            Cells plate = Cells.Box(At(cx, cz, 12, LampY + 6, 14), At(cx, cz, 18, LampY + 6, 20));
            AssertOpenAir(clusterCells, "the cluster volume");
            AssertOpenAir(farCells, "the far lamp volume");
            AssertOpenAir(plate, "the plate");
            int v = LightValue(LampWhiteCode, cluster[0]);

            foreach (BlockPos pos in plate.Positions)
            {
                World.SetBlock(GraniteCode, pos);
            }

            foreach (BlockPos pos in cluster.Append(far))
            {
                World.SetBlock(LampWhiteCode, pos);
            }

            BlockPos[] sunSamples =
            {
                At(cx, cz, 15, LampY + 5, 17),   // under the middle of the plate: shaded
                At(cx, cz, 12, LampY + 5, 14),   // under its corner
                At(cx, cz, 9, LampY, 17),        // open air beside the cluster
                At(cx, cz, 15, LampY + 12, 17),  // above the plate
                At(cx + 5, cz, 5, LampY + 3, 19), // open air near the far lamp
            };
            Cells sun = Cells.Of(sunSamples);

            if (!await TryUntil(() => cluster.Append(far).All(p => LevelAt(p) == v)))
            {
                string[] unlit = cluster.Append(far).Where(p => LevelAt(p) != v).Select(p => $"{p} block {LevelAt(p)} holds {World.BlockAt(p).Code}").ToArray();
                Assert.Fail($"the cluster and the far lamp lighting did not happen within {ReactTimeoutTicks} ticks on {ServerFlavor.Name}; {unlit.Length} of {ClusterLamps + 1} lamps read other than {v}: {string.Join("; ", unlit.Take(6))}; setup is invalid");
            }

            await Reacts(() => LevelAt(sunSamples[0], EnumLightLevelType.OnlySunLight) < World.Api.World.SunBrightness, "the plate shading the cell below it");
            int[] sunBefore = Levels(await Stable(sun, EnumLightLevelType.OnlySunLight, "the sun light before the relight", SunQuietTicks));
            int[] clusterBefore = Levels(await Stable(clusterCells, EnumLightLevelType.OnlyBlockLight, "the cluster light before the relight"));
            int[] farBefore = Levels(await Stable(farCells, EnumLightLevelType.OnlyBlockLight, "the far lamp light before the relight"));

            int sky = World.Api.World.SunBrightness;
            Assert.True(sunBefore[0] < sky && sunBefore[2] == sky && sunBefore[3] == sky,
                $"the sun light samples before the relight are [{string.Join(",", sunBefore)}] (the sky is {sky}) on {ServerFlavor.Name}; setup is invalid");
            int[] wantCluster = Falloff(clusterCells, cluster.Select(p => (p, v)).ToArray());
            int[] wantFar = Falloff(farCells, (far, v));
            string? diff = Compare(clusterCells, clusterBefore, wantCluster, cluster[0]) ?? Compare(farCells, farBefore, wantFar, far);
            Assert.True(diff == null, $"block light before the relight is not the closed form on {ServerFlavor.Name}: {diff}; setup is invalid");

            World.Api.WorldManager.FullRelight(clusterCells.Positions[0], far);

            int[] sunAfter = Levels(await Stable(sun, EnumLightLevelType.OnlySunLight, "the sun light after the relight", SunQuietTicks));
            int[] clusterAfter = Levels(await Stable(clusterCells, EnumLightLevelType.OnlyBlockLight, "the cluster light after the relight"));
            int[] farAfter = Levels(await Stable(farCells, EnumLightLevelType.OnlyBlockLight, "the far lamp light after the relight"));
            output.WriteLine($"FullRelight on {ServerFlavor.Name}: sun {string.Join(",", sunBefore)} -> {string.Join(",", sunAfter)}; " +
                             $"cluster lit cells {clusterBefore.Count(l => l > 0)} -> {clusterAfter.Count(l => l > 0)} (sum {clusterBefore.Sum()} -> {clusterAfter.Sum()}); " +
                             $"far lamp lit cells {farBefore.Count(l => l > 0)} -> {farAfter.Count(l => l > 0)}");

            Assert.True(sunAfter.SequenceEqual(sunBefore),
                $"FullRelight changes the sun light on {ServerFlavor.Name}: [{string.Join(",", sunBefore)}] before, [{string.Join(",", sunAfter)}] after");

            int[] wantClusterAfter = ServerFlavor.IsStratum ? wantCluster : new int[clusterCells.Count];
            diff = Compare(clusterCells, clusterAfter, wantClusterAfter, cluster[0]);
            Assert.True(diff == null,
                $"block light of the cluster after FullRelight is not the {ServerFlavor.Name} golden ({(ServerFlavor.IsStratum ? "restored" : "dark")}): {diff}");

            diff = Compare(farCells, farAfter, new int[farCells.Count], far);
            Assert.True(diff == null, $"block light of the far lamp after FullRelight is not dark on {ServerFlavor.Name}: {diff}");
        }
        finally
        {
            UnloadColumns(columns);
        }
    }

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task RainHeightMap_Should_FollowRoof_When_RoofPlacedAndRemoved()
    {
        // The rain height of a column is the top rain tight block. Two stacks of two roofs: in the first the upper
        // roof comes off first (the map falls to the lower one, then to the ground), in the second the lower one
        // comes off first (the map keeps the upper roof, then falls to the ground). Synchronous: the map is
        // updated by SetBlock itself.
        (int cx, int cz) = Slot(8);
        await LoadColumns((cx, cz));
        try
        {
            IMapChunk? map = World.Api.WorldManager.GetMapChunk(cx, cz);
            Assert.True(map != null, $"the map chunk of the column is not loaded on {ServerFlavor.Name}; setup is invalid");
            ushort[] rain = map!.RainHeightMap;
            int IndexOf(BlockPos p) => (p.Z & 31) * ChunkSize + (p.X & 31);

            BlockPos[] stackOne = Roof(cx, cz, 8);
            BlockPos[] stackTwo = Roof(cx, cz, 20);
            int ground = rain[IndexOf(stackOne[0])];
            Assert.True(ground > 0 && ground < 8,
                $"the rain height of the ground is {ground} on {ServerFlavor.Name}; setup is invalid");
            foreach (BlockPos pos in stackOne.Concat(stackTwo))
            {
                Assert.True(rain[IndexOf(pos)] == ground,
                    $"the rain height at {pos} is {rain[IndexOf(pos)]} instead of the ground's {ground} on {ServerFlavor.Name}; setup is invalid");
            }

            int lower = ground + 6;
            int upper = ground + 10;
            BlockPos[] lowerOne = stackOne.Select(p => new BlockPos(p.X, lower, p.Z, 0)).ToArray();
            BlockPos[] upperOne = stackOne.Select(p => new BlockPos(p.X, upper, p.Z, 0)).ToArray();
            BlockPos[] lowerTwo = stackTwo.Select(p => new BlockPos(p.X, lower, p.Z, 0)).ToArray();
            BlockPos[] upperTwo = stackTwo.Select(p => new BlockPos(p.X, upper, p.Z, 0)).ToArray();
            AssertOpenAir(Cells.Of(lowerOne.Concat(upperOne).Concat(lowerTwo).Concat(upperTwo)), "the roof cells");

            void Set(string code, BlockPos[] positions)
            {
                foreach (BlockPos pos in positions)
                {
                    World.SetBlock(code, pos);
                }
            }

            void Expect(BlockPos[] columnsOf, int height, string step)
            {
                string[] wrong = columnsOf.Where(p => rain[IndexOf(p)] != height).Select(p => $"{p}: {rain[IndexOf(p)]}").ToArray();
                Assert.True(wrong.Length == 0,
                    $"after {step} the rain height is not {height} on {ServerFlavor.Name} in {wrong.Length} of {columnsOf.Length} columns ({string.Join("; ", wrong.Take(4))})");
            }

            Set(GraniteCode, lowerOne);
            Set(GraniteCode, lowerTwo);
            Expect(stackOne, lower, "the lower roofs are placed");
            Expect(stackTwo, lower, "the lower roofs are placed");
            Set(GraniteCode, upperOne);
            Set(GraniteCode, upperTwo);
            Expect(stackOne, upper, "the upper roofs are placed");
            Expect(stackTwo, upper, "the upper roofs are placed");

            Set(AirCode, upperOne);
            Expect(stackOne, lower, "the upper roof is removed over a lower one");
            Set(AirCode, lowerTwo);
            Expect(stackTwo, upper, "the lower roof is removed under an upper one");

            Set(AirCode, lowerOne);
            Expect(stackOne, ground, "both roofs are removed (upper first)");
            Set(AirCode, upperTwo);
            Expect(stackTwo, ground, "both roofs are removed (lower first)");
        }
        finally
        {
            UnloadColumns((cx, cz));
        }
    }

    private sealed record PartialLane(string Name, int LocalZ, string Code, bool Chiseled, bool[] SolidFaces, int[] Vanilla, int[] Stratum);

    /// <summary>A set of cells, with the arithmetic to index a box and to print a line of it.</summary>
    private sealed class Cells
    {
        private readonly int minX;
        private readonly int minY;
        private readonly int minZ;
        private readonly int sizeY;
        private readonly int sizeZ;

        private Cells(BlockPos[] positions, int minX, int minY, int minZ, int sizeY, int sizeZ)
        {
            Positions = positions;
            this.minX = minX;
            this.minY = minY;
            this.minZ = minZ;
            this.sizeY = sizeY;
            this.sizeZ = sizeZ;
        }

        public BlockPos[] Positions { get; }

        public int Count => Positions.Length;

        public static Cells Box(BlockPos min, BlockPos max)
        {
            int sizeX = max.X - min.X + 1;
            int sizeY = max.Y - min.Y + 1;
            int sizeZ = max.Z - min.Z + 1;
            var positions = new BlockPos[sizeX * sizeY * sizeZ];
            int n = 0;
            for (int x = 0; x < sizeX; x++)
            {
                for (int y = 0; y < sizeY; y++)
                {
                    for (int z = 0; z < sizeZ; z++)
                    {
                        positions[n++] = new BlockPos(min.X + x, min.Y + y, min.Z + z, 0);
                    }
                }
            }

            return new Cells(positions, min.X, min.Y, min.Z, sizeY, sizeZ);
        }

        public static Cells Around(BlockPos center, int rx, int ry, int rz) =>
            Box(center.AddCopy(-rx, -ry, -rz), center.AddCopy(rx, ry, rz));

        /// <summary>An arbitrary list: <see cref="Slice"/> is not available on it.</summary>
        public static Cells Of(IEnumerable<BlockPos> positions) => new(positions.ToArray(), 0, 0, 0, 0, 0);

        /// <summary>The values of <paramref name="count"/> cells starting at <paramref name="from"/> and stepping by the
        /// given offsets, taken out of a reading of this box.</summary>
        public int[] Slice(int[] values, BlockPos from, int count, int dx, int dy, int dz)
        {
            Assert.True(sizeY > 0, "a slice needs a box; setup is invalid");
            var slice = new int[count];
            for (int i = 0; i < count; i++)
            {
                int index = (((from.X + i * dx - minX) * sizeY) + (from.Y + i * dy - minY)) * sizeZ + (from.Z + i * dz - minZ);
                slice[i] = values[index];
            }

            return slice;
        }
    }

    // ----- placement helpers -----

    private (int Cx, int Cz) Slot(int index) =>
        (World.Spawn.X / ChunkSize + SlotSpacingChunks * (index % 3), World.Spawn.Z / ChunkSize + SlotSpacingChunks * (index / 3));

    private static BlockPos At(int cx, int cz, int localX, int y, int localZ) =>
        new(cx * ChunkSize + localX, y, cz * ChunkSize + localZ, 0);

    /// <summary>Requests the columns kept loaded and waits until each one is generated to Done (the sun light of a
    /// column is final then, and its chunks answer the light queries).</summary>
    private async Task LoadColumns(params (int Cx, int Cz)[] columns)
    {
        foreach ((int cx, int cz) in columns)
        {
            World.Api.WorldManager.LoadChunkColumnPriority(cx, cz, new ChunkLoadOptions { KeepLoaded = true });
        }

        if (!await TryUntil(() => columns.All(c => WorldgenArea.IsDone(World, c.Cx, c.Cz)), LoadTimeoutTicks))
        {
            Assert.Fail($"the columns {string.Join(", ", columns)} never reached Done within {LoadTimeoutTicks} ticks on {ServerFlavor.Name}; setup is invalid");
        }
    }

    private void UnloadColumns(params (int Cx, int Cz)[] columns)
    {
        foreach ((int cx, int cz) in columns)
        {
            World.Api.WorldManager.UnloadChunkColumn(cx, cz);
        }
    }

    /// <summary>Fails as an invalid setup unless every cell is air in a loaded chunk.</summary>
    private void AssertOpenAir(Cells cells, string what)
    {
        IBlockAccessor accessor = World.Api.World.BlockAccessor;
        foreach (BlockPos pos in cells.Positions)
        {
            if (accessor.GetChunkAtBlockPos(pos) == null)
            {
                Assert.Fail($"{what}: the chunk of {pos} is not loaded on {ServerFlavor.Name}; setup is invalid");
            }

            int id = accessor.GetBlockId(pos);
            if (id != 0)
            {
                Assert.Fail($"{what}: {pos} holds {accessor.GetBlock(pos).Code} instead of air on {ServerFlavor.Name}; setup is invalid");
            }
        }
    }

    private Block ResolveBlock(string code)
    {
        Block? block = World.Api.World.GetBlock(new AssetLocation(code));
        Assert.True(block != null && block.BlockId != 0, $"block {code} does not exist on {ServerFlavor.Name}; setup is invalid");
        return block!;
    }

    /// <summary>The V of a block's lightHsv, which must be a real light.</summary>
    private int LightValue(string code, BlockPos pos)
    {
        int v = ResolveBlock(code).GetLightHsv(World.Api.World.BlockAccessor, pos)[2];
        Assert.True(v > 0, $"{code} gives no light (V {v}) on {ServerFlavor.Name}; setup is invalid");
        return v;
    }

    /// <summary>A granite tube along +x around the line (x0..x1, y, z): the eight cells around each cell of the line,
    /// and a cap on the line at both ends. The line itself is left to the caller.</summary>
    private void BuildTube(int cx, int cz, int localX0, int localX1, int y, int localZ)
    {
        for (int x = localX0; x <= localX1; x++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dy != 0 || dz != 0)
                    {
                        World.SetBlock(GraniteCode, At(cx, cz, x, y + dy, localZ + dz));
                    }
                }
            }
        }

        World.SetBlock(GraniteCode, At(cx, cz, localX0 - 1, y, localZ));
        World.SetBlock(GraniteCode, At(cx, cz, localX1 + 1, y, localZ));
    }

    private void AssertContents(string[] contents, System.Func<int, BlockPos> cell, string when)
    {
        for (int i = 1; i < contents.Length; i++)
        {
            string read = World.BlockAt(cell(i)).Code.ToString();
            Assert.True(read == contents[i], $"corridor cell {i} holds {read} instead of {contents[i]} {when} on {ServerFlavor.Name}; setup is invalid");
        }
    }

    private static BlockPos[] Roof(int cx, int cz, int localX0) =>
        Enumerable.Range(0, 9).Select(i => At(cx, cz, localX0 + i % 3, 0, 8 + i / 3)).ToArray();

    // ----- light reading and waiting -----

    private int LevelAt(BlockPos pos, EnumLightLevelType type = EnumLightLevelType.OnlyBlockLight) =>
        World.Api.World.BlockAccessor.GetLightLevel(pos, type);

    private int[] Read(Cells cells, EnumLightLevelType type)
    {
        IBlockAccessor accessor = World.Api.World.BlockAccessor;
        var levels = new int[cells.Count];
        for (int i = 0; i < levels.Length; i++)
        {
            levels[i] = accessor.GetLightLevel(cells.Positions[i], type);
        }

        return levels;
    }

    private (int Sun, int Block, int Hue, int Sat) ReadPacked(BlockPos pos)
    {
        IWorldChunk? chunk = World.Api.World.BlockAccessor.GetChunkAtBlockPos(pos);
        Assert.True(chunk != null, $"the chunk of {pos} is not loaded on {ServerFlavor.Name}; setup is invalid");
        int index = MapUtil.Index3d(pos.X & 31, pos.Y & 31, pos.Z & 31, 32, 32);
        ushort packed = chunk!.Unpack_AndReadLight(index, out int sat);
        return (packed & 0x1F, (packed >> 5) & 0x1F, packed >> 10, sat);
    }

    private static string Describe((int Sun, int Block, int Hue, int Sat) light) =>
        $"sun {light.Sun} block {light.Block} hue {light.Hue} saturation {light.Sat}";

    private static string Digest(int[] levels) => LightingModel.Digest(levels);

    private static int[] Levels(string digest) => LightingModel.Levels(digest);

    private Task<string> Stable(Cells cells, EnumLightLevelType type, string what, int quietTicks = QuietTicks) =>
        WorldStable.Until(World, () => Digest(Read(cells, type)), quietTicks, SettleTimeoutTicks, what);

    private async Task<bool> TryUntil(Func<bool> condition, int timeoutTicks = ReactTimeoutTicks)
    {
        try
        {
            await World.Until(condition, timeoutTicks);
            return true;
        }
        catch (ScenarioTimeoutException)
        {
            return false;
        }
    }

    /// <summary>Waits for the positive signal that the light reacted to a change (the lamp lit, the lamp's light gone).
    /// Without it two identical reads of the stale state would read as settled.</summary>
    private async Task Reacts(Func<bool> signal, string what)
    {
        if (!await TryUntil(signal))
        {
            Assert.Fail($"{what} did not happen within {ReactTimeoutTicks} ticks on {ServerFlavor.Name}; setup is invalid");
        }
    }

    /// <summary>Runs a change, waits for the block light of the cells to react to it (a positive signal: two identical
    /// reads of the stale state would otherwise read as settled) and then to stop changing.</summary>
    private async Task<int[]> ChangeAndSettle(Action change, Cells cells, string what)
    {
        string before = Digest(Read(cells, EnumLightLevelType.OnlyBlockLight));
        change();
        await Reacts(() => Digest(Read(cells, EnumLightLevelType.OnlyBlockLight)) != before, $"{what}: the block light changing");
        return Levels(await Stable(cells, EnumLightLevelType.OnlyBlockLight, what));
    }

    private Task<int[]> PlaceAndSettle(string code, BlockPos source, Cells cells, string what) =>
        ChangeAndSettle(
            () =>
            {
                World.SetBlock(code, source);
                Assert.True(World.BlockAt(source).Code.ToString() == code,
                    $"{what}: {code} reads back as {World.BlockAt(source).Code} at {source} on {ServerFlavor.Name}; setup is invalid");
            },
            cells,
            what);

    private Task<int[]> RemoveAndSettle(BlockPos source, Cells cells, string what) =>
        ChangeAndSettle(() => World.SetBlock(AirCode, source), cells, what);

    // ----- expectations -----

    private static int[] Falloff(Cells cells, params (BlockPos Pos, int V)[] sources) => LightingModel.Falloff(cells.Positions, sources);

    private static int[] Corridor(int v, IReadOnlyList<int> absorption, int length) => LightingModel.Corridor(v, absorption, length);

    private static string? Compare(Cells cells, int[] got, int[] want, BlockPos origin)
    {
        var shown = new List<string>();
        int different = 0;
        for (int i = 0; i < got.Length; i++)
        {
            if (got[i] == want[i])
            {
                continue;
            }

            different++;
            if (shown.Count < 8)
            {
                BlockPos pos = cells.Positions[i];
                shown.Add($"({pos.X - origin.X},{pos.Y - origin.Y},{pos.Z - origin.Z}) read {got[i]} expected {want[i]}");
            }
        }

        return different == 0 ? null : $"{different} of {got.Length} cells differ; first {string.Join("; ", shown)}";
    }

    /// <summary>The chiseled block, shaped the way the chisel tool leaves it: a full cube of the material, then the
    /// top half carved away. Reflection on the microblock entity, as in the microblock scenarios (the scenario
    /// project does not reference VSSurvivalMod), every missing member fails as an invalid setup.</summary>
    private static class Chiseled
    {
        public static void MakeBottomHalf(IWorldSession world, BlockPos pos, Block material)
        {
            BlockEntity? entity = world.Api.World.BlockAccessor.GetBlockEntity(pos);
            Assert.True(entity != null, $"no block entity at {pos} on {ServerFlavor.Name}; setup is invalid");
            Type? micro = entity!.GetType();
            while (micro != null && micro.FullName != MicroblockEntityTypeName)
            {
                micro = micro.BaseType;
            }

            Assert.True(micro != null, $"{entity.GetType().FullName} does not derive from {MicroblockEntityTypeName} on {ServerFlavor.Name}; setup is invalid");
            MethodInfo wasPlaced = Member(micro!.GetMethod("WasPlaced", new[] { typeof(Block), typeof(string) }), "WasPlaced(Block, string)");
            MethodInfo beginEdit = Member(micro.GetMethod("BeginEdit"), "BeginEdit");
            MethodInfo endEdit = Member(micro.GetMethod("EndEdit"), "EndEdit");
            Type gridType = Member(beginEdit.GetParameters()[0].ParameterType.GetElementType(), "BeginEdit voxel grid type");
            PropertyInfo indexer = Member(
                gridType.GetProperty("Item", typeof(bool), new[] { typeof(int), typeof(int), typeof(int) }),
                $"{gridType.Name}[int, int, int]");

            wasPlaced.Invoke(entity, BindingFlags.DoNotWrapExceptions, null, new object?[] { material, null }, null);
            var grids = new object?[2];
            beginEdit.Invoke(entity, BindingFlags.DoNotWrapExceptions, null, grids, null);
            Assert.True(grids[0] != null && grids[1] is byte[,,], $"BeginEdit returned no voxel and material grids on {ServerFlavor.Name}; setup is invalid");
            for (int x = 0; x < 16; x++)
            {
                for (int y = 8; y < 16; y++)
                {
                    for (int z = 0; z < 16; z++)
                    {
                        indexer.SetValue(grids[0], false, new object[] { x, y, z });
                    }
                }
            }

            endEdit.Invoke(entity, BindingFlags.DoNotWrapExceptions, null, new[] { grids[0], grids[1] }, null);
        }

        private static T Member<T>(T? member, string description)
            where T : class
        {
            Assert.True(member != null, $"{description} not found on {MicroblockEntityTypeName} on {ServerFlavor.Name}; setup is invalid");
            return member!;
        }
    }
}

/// <summary>The pure part of the lighting expectations, world free so <see cref="LightingModelTests"/> can check it.</summary>
internal static class LightingModel
{
    internal static int Manhattan(BlockPos a, BlockPos b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) + Math.Abs(a.Z - b.Z);

    /// <summary>The closed form of open air: the strongest source at each cell, V minus the Manhattan distance, floored at 0.</summary>
    internal static int[] Falloff(IReadOnlyList<BlockPos> cells, params (BlockPos Pos, int V)[] sources)
    {
        var levels = new int[cells.Count];
        for (int i = 0; i < levels.Length; i++)
        {
            int best = 0;
            foreach ((BlockPos pos, int v) in sources)
            {
                best = Math.Max(best, v - Manhattan(cells[i], pos));
            }

            levels[i] = best;
        }

        return levels;
    }

    /// <summary>The levels along a corridor: a cell holds what the previous one lets out, V at the source, and loses
    /// 1 plus its own absorption on the way to the next cell. A cell past the absorbers that kill the light is 0.</summary>
    internal static int[] Corridor(int v, IReadOnlyList<int> absorption, int length)
    {
        var levels = new int[length];
        int level = v;
        for (int i = 0; i < length; i++)
        {
            levels[i] = Math.Max(level, 0);
            level = levels[i] == 0 ? 0 : level - (i < absorption.Count ? absorption[i] : 0) - 1;
        }

        return levels;
    }

    /// <summary>One character per level (levels run 0 to 31), so a reading compares and hashes as a string.</summary>
    internal static string Digest(int[] levels) =>
        string.Create(levels.Length, levels, static (span, values) =>
        {
            for (int i = 0; i < values.Length; i++)
            {
                span[i] = (char)('0' + values[i]);
            }
        });

    internal static int[] Levels(string digest) => digest.Select(c => c - '0').ToArray();
}
