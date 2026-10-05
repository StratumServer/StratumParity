using System.Globalization;
using System.Text;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Xunit;
using Xunit.Abstractions;

namespace StratumParity.Scenarios;

/// <summary>
/// Parity of four block world mechanics that Stratum's patches touch: finite spreading water, sand
/// falling as entities, the activation cap of the physics manager, and the entity queries of the
/// world accessor (GetNearestEntity and the selection ray). Every scenario compares the world with an
/// exact expectation derived in the test (a closed form or a brute force over a fixed scene), never
/// with a golden, and never with a block id: blocks are read by code.
///
/// Water: a source placed on a flat granite plate has to spread to the finite liquid footprint, level 7
/// minus the Manhattan distance (the engine feeds a block from the neighbour one level above it, so the
/// diamond of radius 6 is stable), and to drain to nothing once the source is gone. A plain SetBlock
/// starts nothing, so each change is followed by TriggerNeighbourBlockUpdate (the engine then notifies
/// the liquid block at that very position). The spread is a chain of 75 ms callbacks, so the footprint
/// is read until it has been quiet for 45 ticks, never for a fixed count.
///
/// Sand: placing a block with UnstableFalling runs OnBlockPlaced, which starts a falling entity when
/// nothing supports it and the world rule allowFallingBlocks is on (the survival mod declares it with a
/// default of true, but the scenarios set it explicitly and restore it, so a changed default cannot
/// matter). Three engine rules shaped the scenes, all read from the sources. The falling block entity
/// is only simulated within 96 blocks of a player (its
/// simulation range, three quarters of 128): beyond it the entity stays inactive and never falls, on
/// both flavors, so the far leg sits 76 blocks out instead of the plan's 150. On landing the entity
/// slides sideways whenever the neighbouring cell and the one below it are replaceable (it is always
/// created able to), so a stack that lands on a pile next to air would spread unpredictably: the
/// collapse scene is a lattice of one block wide shafts walled in granite, the many block scene keeps
/// every sand block alone on a flat plate. And the entity lingers for 50 of its own ticks after the
/// landing, so "no falling entity left" is waited for after seeing at least one (the stack falls one
/// layer after the other, each fall starting from the previous entity's first ticks).
///
/// Nearest entity and selection: the scene holds stationary straw dummies and one granite block. No
/// player is within 128 blocks of it, so every entity stays inactive (no gravity, no repulsion) and keeps
/// the position it was spawned at; the nearest entity (including a tie, which both flavors break by load
/// order within the chunk) and the selection rays are checked against a brute force model of the
/// documented rules: the horizontal radius and vertical band around the query position, squared 3D
/// distance, and for a ray the nearest block face or entity selection box along it. Rays stay at least
/// 0.25 blocks from every edge, and the distance of an entity hit and a block hit never come close, so
/// the float versus double distance comparison of the Stratum rewrite is not probed (a window of a few
/// float steps around an exact tie would be a probe of its own).
/// </summary>
public class BlockMechanicsScenarios : AtlasScenarioBase
{
    private const string GraniteCode = "game:rock-granite";
    private const string SandCode = "game:sand-granite";
    private const string WaterSourceCode = "game:water-still-7";
    private const string DummyCode = "game:strawdummy";
    private const string AirName = "air";
    private const string FallingBlockPath = "blockfalling";
    private const string FallingBlocksKey = "allowFallingBlocks";
    private const string ActivationCapKey = "Performance.Physics.MaxActivationsPerTick";

    // Scenes sit this far from the world spawn, so that no two scenes (and no two players) are less than
    // 200 blocks apart. The sand scene is the one at the spawn itself (its anchor stands there).
    private const int ManyAnchorOffsetZ = 300;
    private const int WaterOffsetX = 300;
    private const int NearestOffsetX = -300;

    // Water.
    private const int WaterFloorRadius = 10;
    private const int WaterReadRadius = 8;
    private const int WaterQuietTicks = 45;
    private const int WaterTimeoutTicks = 900;
    private const int MaxLiquidLevel = 7;

    // Sand lattice: shafts at odd indices of an 11 by 11 cell square, granite everywhere else, four layers
    // high. Four sand blocks per shaft rest on a support block in the top wall layer and fall into the
    // shaft when it is removed: 25 shafts, 100 sand blocks, and the bottom four layers full at the end.
    private const int LatticeSize = 11;
    private const int LatticeHalf = LatticeSize / 2;
    private const int WallLayers = 4;
    private const int SandLayers = 4;
    private const int ScanHeight = 12;
    private const int ShaftsPerSide = (LatticeSize - 1) / 2;
    private const int SandPerLattice = ShaftsPerSide * ShaftsPerSide * SandLayers;
    private const int SettleBeforeReleaseTicks = 15;
    private const int StartTimeoutTicks = 120;
    private const int CollapseTimeoutTicks = 1800;
    private const int FinalQuietTicks = 20;

    // Many falling blocks: a 12 by 10 grid of lone sand blocks two cells apart, released in one step.
    private const int GridColumns = 12;
    private const int GridRows = 10;
    private const int GridSpacing = 2;
    private const int ReleaseLayer = 4;
    private const int GridScanHeight = 8;
    private const int GridCount = GridColumns * GridRows;

    private static readonly SandBand[] SandBands =
    {
        new("near", 12),
        new("mid", 48),
        new("far", 76),
    };

    private readonly ITestOutputHelper output;

    public BlockMechanicsScenarios(ITestOutputHelper output) => this.output = output;

    // ---------------------------------------------------------------------------------------------------
    // Water
    // ---------------------------------------------------------------------------------------------------

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task WaterSource_Should_SpreadToVanillaFootprintAndDrain_When_PlacedOnFlatFloor()
    {
        BlockPos spawn = World.Spawn;
        int cx = ColumnCenter(spawn.X + WaterOffsetX);
        int cz = ColumnCenter(spawn.Z);
        int groundY = await PrepareFloor(cx - WaterFloorRadius, cz - WaterFloorRadius, cx + WaterFloorRadius, cz + WaterFloorRadius, clearHeight: 3);

        IBlockAccessor ba = World.Api.World.BlockAccessor;
        var source = new BlockPos(cx, groundY + 1, cz, 0);
        string dry = LevelGrid(ba, source, WaterReadRadius);
        Assert.True(dry == ExpectedLevels(WaterReadRadius, withSource: false),
            $"liquid is present around the source position before the source exists on {ServerFlavor.Name}; setup is invalid:\n{dry}");

        Block water = Resolve(WaterSourceCode);
        Assert.True(water.LiquidLevel == MaxLiquidLevel && water.ForFluidsLayer,
            $"{WaterSourceCode} is not a level {MaxLiquidLevel} fluid layer block on {ServerFlavor.Name}; setup is invalid");

        // SetBlock only writes the layer, it registers no spread callback: the update notifies the liquid
        // block at the position itself (ServerMain.TriggerNeighbourBlocksUpdate), which starts the spread.
        ba.SetBlock(water.BlockId, source, BlockLayersAccess.Fluid);
        Assert.True(ba.GetBlock(source, BlockLayersAccess.Fluid).LiquidLevel == MaxLiquidLevel,
            $"the source does not read back as level {MaxLiquidLevel} on {ServerFlavor.Name}; setup is invalid");
        ba.TriggerNeighbourBlockUpdate(source);

        string spread = await WorldStable.Until(World, () => LevelGrid(ba, source, WaterReadRadius),
            quietTicks: WaterQuietTicks, timeoutTicks: WaterTimeoutTicks, what: "the water footprint");
        string footprint = ExpectedLevels(WaterReadRadius, withSource: true);
        Assert.True(spread == footprint,
            $"water footprint differs from level {MaxLiquidLevel} minus the Manhattan distance on {ServerFlavor.Name} " +
            $"(rows are z, columns are x, digits are liquid levels).\nexpected:\n{footprint}\nactual:\n{spread}");

        // Removing the source leaves the flowing water unfed: every block lowers until nothing is left.
        ba.SetBlock(0, source, BlockLayersAccess.Fluid);
        ba.TriggerNeighbourBlockUpdate(source);

        string drained = await WorldStable.Until(World, () => LevelGrid(ba, source, WaterReadRadius),
            quietTicks: WaterQuietTicks, timeoutTicks: WaterTimeoutTicks, what: "the drained water");
        Assert.True(drained == dry,
            $"water remains after the source was removed on {ServerFlavor.Name}.\nactual:\n{drained}");
    }

    // ---------------------------------------------------------------------------------------------------
    // Sand collapse
    // ---------------------------------------------------------------------------------------------------

    [AtlasScenario(TimeoutMs = 240_000)]
    public async Task SandBlock_Should_CollapseToExactLayers_When_SupportRemoved()
    {
        using IDisposable falling = EnableFallingBlocks();
        ITestPlayer anchor = await EntityProbeRig.JoinAnchor(World, "bm-sand-anchor");
        BlockPos origin = anchor.Position;

        foreach (SandBand band in SandBands)
        {
            await CollapseLattice(band, origin);
        }

        EntityProbeRig.AssertAnchorStayed(anchor, origin);
    }

    private async Task CollapseLattice(SandBand band, BlockPos origin)
    {
        int x0 = origin.X + band.Dx - LatticeHalf;
        int z0 = origin.Z - LatticeHalf;
        int x1 = x0 + LatticeSize - 1;
        int z1 = z0 + LatticeSize - 1;
        int groundY = await PrepareFloor(x0 - 1, z0 - 1, x1 + 1, z1 + 1, ScanHeight);

        IBlockAccessor ba = World.Api.World.BlockAccessor;
        int granite = Resolve(GraniteCode).BlockId;
        int sand = Resolve(SandCode).BlockId;
        Assert.True(HasFallingBehavior(Resolve(SandCode)),
            $"{SandCode} has no UnstableFalling behavior on {ServerFlavor.Name}; setup is invalid");

        // The falling block entity is only simulated within its simulation range of a player.
        double reach = GlobalConstants.DefaultSimulationRange * 3 / 4 - 8;
        double farthest = Math.Sqrt(Math.Pow(Math.Abs(x1 - origin.X) + 1, 2) + Math.Pow(Math.Abs(z1 - origin.Z) + 1, 2) + Math.Pow(WallLayers + SandLayers, 2));
        Assert.True(farthest <= reach,
            $"the {band.Label} lattice reaches {farthest:F0} blocks from the anchor, past the {reach:F0} blocks a falling block is simulated at, on {ServerFlavor.Name}; setup is invalid");

        // Bottom up, so that every sand block is supported when it is placed (placing it runs OnBlockPlaced).
        for (int layer = 1; layer <= WallLayers + SandLayers; layer++)
        {
            for (int xi = 0; xi < LatticeSize; xi++)
            {
                for (int zi = 0; zi < LatticeSize; zi++)
                {
                    string want = BeforeRelease(xi, zi, layer);
                    if (want != AirName)
                    {
                        ba.SetBlock(want == SandCode ? sand : granite, new BlockPos(x0 + xi, groundY + layer, z0 + zi, 0));
                    }
                }
            }
        }

        Region area = new(x0 - 3, groundY, z0 - 3, x1 + 3, groundY + ScanHeight + 4, z1 + 3);
        AssertCells(ba, x0, z0, groundY, ScanHeight, BeforeRelease, $"the {band.Label} lattice before the release");
        await World.Ticks(SettleBeforeReleaseTicks);
        AssertCells(ba, x0, z0, groundY, ScanHeight, BeforeRelease, $"the {band.Label} lattice {SettleBeforeReleaseTicks} ticks after it was built");
        Assert.True(FallingCount(area) == 0,
            $"a block is already falling in the {band.Label} lattice before the release on {ServerFlavor.Name}; setup is invalid");

        // Release: take the support out of every shaft and tell the sand above it.
        for (int xi = 1; xi < LatticeSize; xi += 2)
        {
            for (int zi = 1; zi < LatticeSize; zi += 2)
            {
                var support = new BlockPos(x0 + xi, groundY + WallLayers, z0 + zi, 0);
                ba.SetBlock(0, support);
                ba.TriggerNeighbourBlockUpdate(support);
            }
        }

        long releasedAt = World.CurrentTick;
        await WaitForCollapse(area, band.Label);
        long collapsedAt = World.CurrentTick;
        Snapshot settled = await WorldStable.Until(World, () => Take(ba, area, area), quietTicks: FinalQuietTicks, timeoutTicks: 300,
            what: $"the {band.Label} lattice after the collapse");

        AssertCells(ba, x0, z0, groundY, ScanHeight, AfterCollapse, $"the {band.Label} lattice after the collapse");
        Assert.True(settled.Sand == SandPerLattice,
            $"{settled.Sand} sand blocks stand around the {band.Label} lattice after the collapse, {SandPerLattice} expected, on {ServerFlavor.Name}");
        Assert.True(settled.Falling == 0,
            $"{settled.Falling} falling blocks are left around the {band.Label} lattice on {ServerFlavor.Name}");
        output.WriteLine($"{band.Label} lattice at {band.Dx} blocks on {ServerFlavor.Name}: {settled.Sand} sand blocks in the bottom {WallLayers} layers, " +
            $"no falling entity left {collapsedAt - releasedAt} ticks after the release");
    }

    /// <summary>Layer 0 is the plate, layers 1 to 4 the lattice (shafts hold air, the top shaft layer holds
    /// the support), layers 5 to 8 the four sand blocks of each shaft, everything above is air.</summary>
    private static string BeforeRelease(int xi, int zi, int layer)
    {
        bool shaft = IsShaft(xi, zi);
        if (layer == 0)
        {
            return GraniteCode;
        }

        if (layer <= WallLayers)
        {
            return !shaft || layer == WallLayers ? GraniteCode : AirName;
        }

        return shaft && layer <= WallLayers + SandLayers ? SandCode : AirName;
    }

    /// <summary>The sand ended in the shafts of the bottom four layers, the walls and the plate stayed.</summary>
    private static string AfterCollapse(int xi, int zi, int layer)
    {
        if (layer == 0)
        {
            return GraniteCode;
        }

        if (layer <= WallLayers)
        {
            return IsShaft(xi, zi) ? SandCode : GraniteCode;
        }

        return AirName;
    }

    private static bool IsShaft(int xi, int zi) => (xi & 1) == 1 && (zi & 1) == 1;

    private async Task WaitForCollapse(Region area, string label)
    {
        try
        {
            await World.Until(() => FallingCount(area) > 0, timeoutTicks: StartTimeoutTicks);
        }
        catch (ScenarioTimeoutException)
        {
            Assert.Fail($"no block started to fall in the {label} lattice within {StartTimeoutTicks} ticks of the release on {ServerFlavor.Name}; setup is invalid");
        }

        try
        {
            await World.Until(() => FallingCount(area) == 0, timeoutTicks: CollapseTimeoutTicks);
        }
        catch (ScenarioTimeoutException)
        {
            Assert.Fail($"{FallingCount(area)} falling blocks are still in the air around the {label} lattice {CollapseTimeoutTicks} ticks after the release on {ServerFlavor.Name}");
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Many falling blocks
    // ---------------------------------------------------------------------------------------------------

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task ManyFallingBlocks_Should_AllLand_When_MoreThanFiftyReleasedInOneTick()
    {
        string capNote = await AssertCapDefersTheBatch(GridCount);
        using IDisposable falling = EnableFallingBlocks();

        BlockPos spawn = World.Spawn;
        ITestPlayer anchor = await JoinAnchorAt("bm-many-anchor", spawn.AddCopy(0, 0, ManyAnchorOffsetZ));
        BlockPos origin = anchor.Position;

        int gridWidth = (GridColumns - 1) * GridSpacing;
        int gridDepth = (GridRows - 1) * GridSpacing;
        int x0 = origin.X + 20 - gridWidth / 2;
        int z0 = origin.Z - gridDepth / 2;
        int x1 = x0 + gridWidth;
        int z1 = z0 + gridDepth;
        int groundY = await PrepareFloor(x0 - 1, z0 - 1, x1 + 1, z1 + 1, GridScanHeight);

        IBlockAccessor ba = World.Api.World.BlockAccessor;
        int sand = Resolve(SandCode).BlockId;
        Assert.True(HasFallingBehavior(Resolve(SandCode)),
            $"{SandCode} has no UnstableFalling behavior on {ServerFlavor.Name}; setup is invalid");

        Region area = new(x0 - 4, groundY, z0 - 4, x1 + 4, groundY + GridScanHeight + 4, z1 + 4);
        Assert.True(FallingCount(area) == 0,
            $"a block is already falling at the grid before the release on {ServerFlavor.Name}; setup is invalid");

        // One synchronous step: no server tick can run between the first and the last placement, and each
        // placement queues its falling entity for the next drain of the main thread tasks.
        for (int gx = 0; gx < GridColumns; gx++)
        {
            for (int gz = 0; gz < GridRows; gz++)
            {
                ba.SetBlock(sand, new BlockPos(x0 + gx * GridSpacing, groundY + ReleaseLayer, z0 + gz * GridSpacing, 0));
            }
        }

        try
        {
            await World.Until(() => FallingCount(area) > 0, timeoutTicks: StartTimeoutTicks);
        }
        catch (ScenarioTimeoutException)
        {
            Assert.Fail($"no block started to fall within {StartTimeoutTicks} ticks of the release on {ServerFlavor.Name}{capNote}; setup is invalid");
        }

        int released = FallingCount(area);
        long releasedAt = World.CurrentTick;
        Assert.True(released == GridCount,
            $"{released} of {GridCount} blocks became falling entities in the first tick that had any on {ServerFlavor.Name}{capNote}; setup is invalid");

        try
        {
            await World.Until(() => FallingCount(area) == 0, timeoutTicks: CollapseTimeoutTicks);
        }
        catch (ScenarioTimeoutException)
        {
            Assert.Fail($"{FallingCount(area)} of {GridCount} falling blocks are still in the air {CollapseTimeoutTicks} ticks after the release on {ServerFlavor.Name}{capNote}");
        }

        long goneAt = World.CurrentTick;

        Snapshot settled = await WorldStable.Until(World, () => Take(ba, area, area), quietTicks: FinalQuietTicks, timeoutTicks: 300,
            what: "the landed blocks");

        // The expected map: one sand block above the plate at every grid cell, air in every other cell.
        string AfterLanding(int xi, int zi, int layer) =>
            layer == 0 ? GraniteCode
            : layer == 1 && xi % GridSpacing == 0 && zi % GridSpacing == 0 && xi / GridSpacing < GridColumns && zi / GridSpacing < GridRows ? SandCode
            : AirName;
        AssertCells(ba, x0, z0, groundY, GridScanHeight, AfterLanding, "the grid after the landing", width: gridWidth + 1, depth: gridDepth + 1, capNote: capNote);
        Assert.True(settled.Sand == GridCount,
            $"{settled.Sand} sand blocks stand around the grid after the landing, {GridCount} expected, on {ServerFlavor.Name}{capNote}");
        Assert.True(settled.Falling == 0,
            $"{settled.Falling} falling blocks are left on {ServerFlavor.Name}{capNote}");

        EntityProbeRig.AssertAnchorStayed(anchor, origin);
        output.WriteLine($"{released} blocks released in one tick, {settled.Sand} landed on {ServerFlavor.Name}{capNote}, " +
            $"the last falling entity gone {goneAt - releasedAt} ticks after the first sighting");
    }

    /// <summary>On Stratum the batch must exceed the activation cap, otherwise nothing is deferred and the
    /// scenario is a different one: it fails as an invalid setup. Returns a message fragment naming the cap
    /// (empty on vanilla, which has none).</summary>
    private async Task<string> AssertCapDefersTheBatch(int batch)
    {
        string? raw = await StratumSetting.Get(World, ActivationCapKey);
        if (raw == null)
        {
            return string.Empty;
        }

        Assert.True(int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cap),
            $"{ActivationCapKey} answered '{raw}' on stratum, which is not a number; setup is invalid");
        Assert.True(cap > 0 && cap < batch,
            $"{ActivationCapKey} is {cap} on stratum: a batch of {batch} falling blocks is not deferred by it; setup is invalid");
        return $" ({ActivationCapKey}={cap})";
    }

    // ---------------------------------------------------------------------------------------------------
    // Nearest entity and selection
    // ---------------------------------------------------------------------------------------------------

    // Straw dummies, spawn order = load order within their chunk. Offsets are from the scene origin O.
    // n0 and n1 are 6 apart on x, n5 is spawned before n6 although it sits at the higher x, n4 hangs 4
    // blocks above n0 (a vertical band case), n2 sits at distance 4 from n0 on z.
    private static readonly (string Name, int Dx, int Dy, int Dz)[] NearestSceneEntities =
    {
        ("n0", 0, 0, 0),
        ("n1", 6, 0, 0),
        ("n2", 0, 0, 8),
        ("n3", -8, 0, -4),
        ("n4", 0, 4, 0),
        ("n5", 10, 0, 10),
        ("n6", 4, 0, 10),
    };

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task NearestEntityAndSelection_Should_MatchVanilla_When_SceneIsFixed()
    {
        BlockPos spawn = World.Spawn;
        int colX = (spawn.X + NearestOffsetX) >> 5;
        int colZ = spawn.Z >> 5;
        // O is the origin of the entity cluster, W the granite block of the ray cluster and D the dummy
        // three blocks west of it. Both clusters live in one chunk (the world is flat and low: every block
        // used here is below block 32), so the load order inside one chunk decides every tie.
        int groundY = await PrepareFloor(colX * 32 + 4, colZ * 32 + 1, colX * 32 + 27, colZ * 32 + 27, clearHeight: 8);
        var o = new BlockPos(colX * 32 + 14, groundY + 1, colZ * 32 + 5, 0);
        var w = new BlockPos(colX * 32 + 18, groundY + 1, colZ * 32 + 20, 0);
        Assert.True((groundY + 1) >> 5 == (groundY + 12) >> 5,
            $"the scene would cross a chunk boundary (ground at {groundY}) on {ServerFlavor.Name}; setup is invalid");

        World.SetBlock(GraniteCode, w);
        var scene = new List<Entity>();
        try
        {
            foreach ((_, int dx, int dy, int dz) in NearestSceneEntities)
            {
                scene.Add(World.SpawnEntity(DummyCode, o.AddCopy(dx, dy, dz)));
            }

            Entity d = World.SpawnEntity(DummyCode, w.AddCopy(-3, 0, 0));
            scene.Add(d);
            await AssertSceneIsStatic(scene);

            AssertNearestQueries(o, scene);
            AssertSelectionRays(w, scene, d);
        }
        finally
        {
            foreach (Entity entity in scene)
            {
                if (World.Api.World.GetEntityById(entity.EntityId) != null)
                {
                    World.Api.World.DespawnEntity(entity, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
                }
            }
        }
    }

    /// <summary>The model reads the spawn positions, so no entity may have moved, and a tie is only decided
    /// by the load order of one chunk's entity list.</summary>
    private async Task AssertSceneIsStatic(List<Entity> scene)
    {
        long chunk = scene[0].InChunkIndex3d;
        foreach (Entity entity in scene)
        {
            Assert.True(World.Api.World.GetEntityById(entity.EntityId) != null,
                $"entity {entity.EntityId} is not loaded after its spawn on {ServerFlavor.Name}; setup is invalid");
            Assert.True(entity.InChunkIndex3d == chunk,
                $"the scene entities are not all in one chunk on {ServerFlavor.Name}; setup is invalid");
        }

        var before = scene.Select(e => (e.Pos.X, e.Pos.InternalY, e.Pos.Z)).ToList();
        await World.Ticks(10);
        for (int i = 0; i < scene.Count; i++)
        {
            Assert.True(before[i] == (scene[i].Pos.X, scene[i].Pos.InternalY, scene[i].Pos.Z),
                $"entity {i} moved during the settle ticks on {ServerFlavor.Name}; setup is invalid");
        }
    }

    private void AssertNearestQueries(BlockPos o, List<Entity> scene)
    {
        Vec3d At(double dx, double dy, double dz) => new(o.X + dx, o.Y + dy, o.Z + dz);
        long n4 = scene[4].EntityId;

        // The brute force model and the hand written expectation (the scene index of the answer, -1 for
        // none) must agree before the engine is asked: a mismatch is a bug of this test.
        AssertNearest("nearest of a plain query", At(1.5, 0, 0.5), 20, 10, null, 0, scene);
        AssertNearest("tie between n0 and n1 goes to the one loaded first", At(3, 0, 0), 20, 10, null, 0, scene);
        AssertNearest("tie between n5 and n6 goes to n5, loaded first although at the higher x", At(7, 0, 10), 20, 10, null, 5, scene);
        AssertNearest("tie between n0 and n2 goes to n0", At(0, 0, 4), 30, 30, null, 0, scene);
        AssertNearest("horizontal radius excludes everything", At(2.5, 0, 0), 2, 10, null, -1, scene);
        AssertNearest("vertical band picks the hanging dummy", At(0, 5, 0), 3, 2, null, 4, scene);
        AssertNearest("vertical band excludes everything", At(0, 5, 0), 3, 0.5f, null, -1, scene);
        AssertNearest("filter skips the nearest", At(0, 5, 0), 3, 6, e => e.EntityId != n4, 0, scene);
        AssertNearest("filter skips n0, the hanging dummy is next", At(1.5, 0, 0.5), 20, 10, e => e.EntityId != scene[0].EntityId, 4, scene);
        AssertNearest("the query position equals an entity position", At(0, 0, 0), 20, 10, null, 0, scene);
    }

    private void AssertNearest(string label, Vec3d position, float horRange, float vertRange, System.Func<Entity, bool>? filter, int expectedIndex, List<Entity> scene)
    {
        int modelIndex = ModelNearest(position, horRange, vertRange, filter, scene);
        Assert.True(modelIndex == expectedIndex,
            $"{label}: the model answers scene entity {modelIndex}, the scenario expects {expectedIndex}; setup is invalid");

        ActionConsumable<Entity>? matches = filter == null ? null : e => filter(e);
        Entity? actual = World.Api.World.GetNearestEntity(position, horRange, vertRange, matches);
        int actualIndex = actual == null ? -1 : scene.FindIndex(e => e.EntityId == actual.EntityId);
        Assert.True(actualIndex == expectedIndex,
            $"{label}: GetNearestEntity answered {Describe(actualIndex)}, expected {Describe(expectedIndex)}, on {ServerFlavor.Name}");
    }

    private static string Describe(int index) => index < 0 ? "no entity" : $"scene entity {index}";

    /// <summary>The documented rule: entities inside the horizontal circle and the vertical band around the
    /// position, the smallest squared 3D distance wins and the first one in load order keeps a tie.</summary>
    private static int ModelNearest(Vec3d position, float horRange, float vertRange, System.Func<Entity, bool>? filter, List<Entity> scene)
    {
        float horSq = horRange * horRange;
        int best = -1;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < scene.Count; i++)
        {
            Entity entity = scene[i];
            double dx = entity.Pos.X - position.X;
            double dz = entity.Pos.Z - position.Z;
            if (dx * dx + dz * dz > horSq || Math.Abs(entity.Pos.InternalY - position.Y) > vertRange || (filter != null && !filter(entity)))
            {
                continue;
            }

            double distance = entity.Pos.SquareDistanceTo(position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    private void AssertSelectionRays(BlockPos w, List<Entity> scene, Entity d)
    {
        double bx = w.X, by = w.Y, bz = w.Z;
        System.Func<Entity, bool> notD = e => e.EntityId != d.EntityId;

        // Origins and targets are placed relative to the block W and the dummy D three blocks west of it
        // (its selection box is 0.7 wide and 2 high around its position). An entity is only considered when
        // its position (its feet) is within the ray's length of the origin, horizontally and vertically, so a
        // ray that runs straight down onto the dummy's head is too short in height to ever select it.
        var rays = new[]
        {
            new RayCase("along +x onto the west face of the block", new(bx - 4, by + 0.5, bz + 0.5), new(bx + 0.5, by + 0.5, bz + 0.5), null, RayKind.Block, BlockFacing.WEST),
            new RayCase("straight down onto the top face of the block", new(bx + 0.5, by + 5.5, bz + 0.5), new(bx + 0.5, by + 0.5, bz + 0.5), null, RayKind.Block, BlockFacing.UP),
            new RayCase("oblique onto the west face of the block", new(bx - 3, by + 3, bz + 0.5), new(bx + 0.5, by + 0.5, bz + 0.5), null, RayKind.Block, BlockFacing.WEST),
            new RayCase("a ray that ends before the block", new(bx - 6, by + 0.5, bz + 0.5), new(bx - 2, by + 0.5, bz + 0.5), null, RayKind.None, null),
            new RayCase("a ray that passes beside the block", new(bx - 4, by + 0.5, bz + 1.5), new(bx + 0.5, by + 0.5, bz + 1.5), null, RayKind.None, null),
            new RayCase("the dummy stands in front of the block", new(bx - 6, by + 0.5, bz + 0.25), new(bx + 0.5, by + 0.5, bz + 0.25), null, RayKind.Entity, BlockFacing.WEST),
            new RayCase("the block stands in front of the dummy", new(bx + 4, by + 0.5, bz + 0.25), new(bx - 5, by + 0.5, bz + 0.25), null, RayKind.Block, BlockFacing.EAST),
            new RayCase("an entity filter lets the ray through to the block", new(bx - 6, by + 0.5, bz + 0.25), new(bx + 0.5, by + 0.5, bz + 0.25), notD, RayKind.Block, BlockFacing.WEST),
            new RayCase("a steep ray onto the top of the dummy", new(bx - 5.8, by + 4.8, bz + 0.1), new(bx - 1.3, by + 0.3, bz + 0.1), null, RayKind.Entity, BlockFacing.UP),
            new RayCase("a straight down ray never finds the dummy: its feet are out of the ray's range", new(bx - 2.9, by + 6, bz + 0.1), new(bx - 2.9, by + 1, bz + 0.1), null, RayKind.None, null),
        };

        foreach (RayCase ray in rays)
        {
            RayResult model = ModelRay(ray, w, scene);
            Assert.True(model.Kind == ray.Expect && model.Face == ray.Face,
                $"{ray.Label}: the model finds {model.Kind} {model.Face?.Code}, the scenario expects {ray.Expect} {ray.Face?.Code}; setup is invalid");

            BlockSelection? blockSelection = null;
            EntitySelection? entitySelection = null;
            EntityFilter? entityFilter = ray.Filter == null ? null : e => ray.Filter(e);
            World.Api.World.RayTraceForSelection(ray.From, ray.To, ref blockSelection, ref entitySelection, null, entityFilter);

            RayKind kind = entitySelection != null ? RayKind.Entity : blockSelection != null ? RayKind.Block : RayKind.None;
            Assert.True(kind == ray.Expect && !(entitySelection != null && blockSelection != null),
                $"{ray.Label}: the ray selected {kind} (block {blockSelection?.Position}, entity {entitySelection?.Entity?.EntityId}), expected {ray.Expect}, on {ServerFlavor.Name}");

            switch (kind)
            {
                case RayKind.Block:
                    Assert.True(blockSelection!.Position.X == w.X && blockSelection.Position.Y == w.Y && blockSelection.Position.Z == w.Z,
                        $"{ray.Label}: the ray selected the block at {blockSelection.Position}, expected {w}, on {ServerFlavor.Name}");
                    Assert.True(blockSelection.Face == ray.Face,
                        $"{ray.Label}: the ray hit the {blockSelection.Face.Code} face, expected {ray.Face!.Code}, on {ServerFlavor.Name}");
                    AssertPoint(ray, "block", blockSelection.HitPosition, model.Point.X - w.X, model.Point.Y - w.Y, model.Point.Z - w.Z);
                    break;
                case RayKind.Entity:
                    Assert.True(entitySelection!.Entity.EntityId == d.EntityId,
                        $"{ray.Label}: the ray selected entity {entitySelection.Entity.EntityId}, expected the dummy {d.EntityId}, on {ServerFlavor.Name}");
                    Assert.True(entitySelection.Face == ray.Face,
                        $"{ray.Label}: the ray hit the {entitySelection.Face.Code} face of the dummy, expected {ray.Face!.Code}, on {ServerFlavor.Name}");
                    AssertPoint(ray, "entity", entitySelection.HitPosition, model.Point.X - d.Pos.X, model.Point.Y - d.Pos.InternalY, model.Point.Z - d.Pos.Z);
                    break;
            }
        }
    }

    private static void AssertPoint(RayCase ray, string what, Vec3d actual, double x, double y, double z)
    {
        const double tolerance = 1e-6;
        Assert.True(Math.Abs(actual.X - x) < tolerance && Math.Abs(actual.Y - y) < tolerance && Math.Abs(actual.Z - z) < tolerance,
            $"{ray.Label}: the hit position on the {what} is ({actual.X:R}, {actual.Y:R}, {actual.Z:R}), expected ({x:R}, {y:R}, {z:R}), on {ServerFlavor.Name}");
    }

    private enum RayKind
    {
        None,
        Block,
        Entity,
    }

    private sealed record RayCase(string Label, Vec3d From, Vec3d To, System.Func<Entity, bool>? Filter, RayKind Expect, BlockFacing? Face);

    private sealed record RayResult(RayKind Kind, BlockFacing? Face, Vec3d Point);

    /// <summary>What the selection ray has to find, from the documented rules: the nearest face of the block's
    /// full cube along the segment, and among the entities inside the cylinder of the ray's length around its
    /// origin the one whose position is nearest and whose selection box the line crosses; the entity wins only
    /// when its hit point is nearer than the block's.</summary>
    private static RayResult ModelRay(RayCase ray, BlockPos w, List<Entity> scene)
    {
        Vec3d dir = new(ray.To.X - ray.From.X, ray.To.Y - ray.From.Y, ray.To.Z - ray.From.Z);
        double length = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y + dir.Z * dir.Z);

        BoxHit? block = IntersectBox(ray.From, dir, w.X, w.Y, w.Z, w.X + 1, w.Y + 1, w.Z + 1);
        if (block != null && block.T > 1)
        {
            block = null;
        }

        Entity? entity = null;
        BoxHit? entityHit = null;
        double nearest = double.MaxValue;
        foreach (Entity candidate in scene)
        {
            double dx = candidate.Pos.X - ray.From.X;
            double dz = candidate.Pos.Z - ray.From.Z;
            if (dx * dx + dz * dz > length * length || Math.Abs(candidate.Pos.InternalY - ray.From.Y) > length || (ray.Filter != null && !ray.Filter(candidate)))
            {
                continue;
            }

            Cuboidf box = candidate.SelectionBox;
            BoxHit? hit = IntersectBox(ray.From, dir,
                candidate.Pos.X + box.X1, candidate.Pos.InternalY + box.Y1, candidate.Pos.Z + box.Z1,
                candidate.Pos.X + box.X2, candidate.Pos.InternalY + box.Y2, candidate.Pos.Z + box.Z2);
            double distance = candidate.Pos.SquareDistanceTo(ray.From);
            if (hit != null && distance < nearest)
            {
                nearest = distance;
                entity = candidate;
                entityHit = hit;
            }
        }

        if (entity != null && entityHit != null && (block == null || entityHit.T < block.T))
        {
            return new RayResult(RayKind.Entity, entityHit.Face, entityHit.Point);
        }

        return block != null ? new RayResult(RayKind.Block, block.Face, block.Point) : new RayResult(RayKind.None, null, ray.From);
    }

    private sealed record BoxHit(double T, BlockFacing Face, Vec3d Point);

    /// <summary>Slab method on an axis aligned box; T is the parameter along the segment (1 at its end), the
    /// face is the one the line enters through (west for a line moving to +x).</summary>
    private static BoxHit? IntersectBox(Vec3d from, Vec3d dir, double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
    {
        double[] origin = { from.X, from.Y, from.Z };
        double[] step = { dir.X, dir.Y, dir.Z };
        double[] min = { minX, minY, minZ };
        double[] max = { maxX, maxY, maxZ };
        BlockFacing[] minFaces = { BlockFacing.WEST, BlockFacing.DOWN, BlockFacing.NORTH };
        BlockFacing[] maxFaces = { BlockFacing.EAST, BlockFacing.UP, BlockFacing.SOUTH };

        double enter = double.NegativeInfinity;
        double exit = double.PositiveInfinity;
        BlockFacing? enterFace = null;
        for (int axis = 0; axis < 3; axis++)
        {
            if (Math.Abs(step[axis]) < 1e-12)
            {
                if (origin[axis] < min[axis] || origin[axis] > max[axis])
                {
                    return null;
                }

                continue;
            }

            double t1 = (min[axis] - origin[axis]) / step[axis];
            double t2 = (max[axis] - origin[axis]) / step[axis];
            double near = step[axis] > 0 ? t1 : t2;
            double far = step[axis] > 0 ? t2 : t1;
            if (near > enter)
            {
                enter = near;
                enterFace = step[axis] > 0 ? minFaces[axis] : maxFaces[axis];
            }

            exit = Math.Min(exit, far);
        }

        if (enterFace == null || enter < 0 || enter > exit)
        {
            return null;
        }

        return new BoxHit(enter, enterFace, new Vec3d(from.X + dir.X * enter, from.Y + dir.Y * enter, from.Z + dir.Z * enter));
    }

    // ---------------------------------------------------------------------------------------------------
    // Shared steps
    // ---------------------------------------------------------------------------------------------------

    private sealed record SandBand(string Label, int Dx);

    private readonly record struct Region(int X1, int Y1, int Z1, int X2, int Y2, int Z2)
    {
        public Cuboidi Cuboid => new(X1, Y1, Z1, X2, Y2, Z2);
    }

    /// <summary>What a settling check reads: falling entities in the area, sand blocks in it and a hash of their positions.</summary>
    private readonly record struct Snapshot(int Falling, int Sand, long SandHash);

    private Snapshot Take(IBlockAccessor ba, Region entities, Region blocks)
    {
        int sand = 0;
        long hash = 1469598103934665603L;
        for (int x = blocks.X1; x <= blocks.X2; x++)
        {
            for (int y = blocks.Y1; y <= blocks.Y2; y++)
            {
                for (int z = blocks.Z1; z <= blocks.Z2; z++)
                {
                    if (ba.GetBlock(new BlockPos(x, y, z, 0)).Code?.ToString() == SandCode)
                    {
                        sand++;
                        hash = unchecked(((hash ^ x) * 1099511628211L ^ y) * 1099511628211L ^ z) * 1099511628211L;
                    }
                }
            }
        }

        return new Snapshot(FallingCount(entities), sand, hash);
    }

    private int FallingCount(Region area) =>
        World.EntitiesIn(area.Cuboid).Count(e => e.Code.Path == FallingBlockPath);

    /// <summary>The block entity with the UnstableFalling behavior is looked up by name: the scenario project
    /// references neither VSSurvivalMod nor VSEssentials.</summary>
    private static bool HasFallingBehavior(Block block) =>
        block.BlockBehaviors.Any(b => b.GetType().Name == "BlockBehaviorUnstableFalling");

    private Block Resolve(string code)
    {
        Block? block = World.Api.World.GetBlock(new AssetLocation(code));
        Assert.True(block != null && block.Id != 0, $"block {code} does not resolve on {ServerFlavor.Name}; setup is invalid");
        return block!;
    }

    /// <summary>The world rule is unset in the default playstyle, and read by the falling block behavior on
    /// every placement: set it for the scenario and put the world back afterwards.</summary>
    private IDisposable EnableFallingBlocks()
    {
        ITreeAttribute config = World.Api.World.Config;
        bool had = config.HasAttribute(FallingBlocksKey);
        bool previous = config.GetBool(FallingBlocksKey);
        config.SetBool(FallingBlocksKey, true);
        Assert.True(config.GetBool(FallingBlocksKey), $"{FallingBlocksKey} could not be switched on on {ServerFlavor.Name}; setup is invalid");
        return new RestoreAction(() =>
        {
            if (had)
            {
                config.SetBool(FallingBlocksKey, previous);
            }
            else
            {
                config.RemoveAttribute(FallingBlocksKey);
            }
        });
    }

    private sealed class RestoreAction(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    /// <summary>A second anchor away from the spawn: its column is loaded first so the teleport does not wait on
    /// a lazy chunk load.</summary>
    private async Task<ITestPlayer> JoinAnchorAt(string name, BlockPos target)
    {
        ITestPlayer anchor = await World.JoinPlayer(name);
        await World.Ticks(2);
        await EntityProbeRig.LoadColumnKept(World, target);
        await anchor.TeleportTo(target);
        await World.Ticks(5);
        return anchor;
    }

    private static int ColumnCenter(int blockX) => (blockX >> 5) * 32 + 16;

    /// <summary>Loads every column of the rectangle, lays a granite plate over it at the terrain height and
    /// clears the air above it, so the scene stands on a known flat floor whatever the world made of the
    /// surface. Returns the Y of the plate; the first cell of a scene is Y + 1.</summary>
    private async Task<int> PrepareFloor(int x1, int z1, int x2, int z2, int clearHeight)
    {
        int y = World.Spawn.Y;
        var seen = new HashSet<(int, int)>();
        for (int cx = x1 >> 5; cx <= x2 >> 5; cx++)
        {
            for (int cz = z1 >> 5; cz <= z2 >> 5; cz++)
            {
                if (seen.Add((cx, cz)))
                {
                    await EntityProbeRig.LoadColumnKept(World, new BlockPos(cx * 32, y, cz * 32, 0));
                }
            }
        }

        IBlockAccessor ba = World.Api.World.BlockAccessor;
        int groundY = ba.GetTerrainMapheightAt(new BlockPos(x1, 0, z1, 0));
        Assert.True(ba.GetTerrainMapheightAt(new BlockPos(x2, 0, z2, 0)) == groundY && ba.GetTerrainMapheightAt(new BlockPos((x1 + x2) / 2, 0, (z1 + z2) / 2, 0)) == groundY,
            $"the terrain is not flat across the scene on {ServerFlavor.Name}; setup is invalid");

        int granite = Resolve(GraniteCode).BlockId;
        for (int x = x1; x <= x2; x++)
        {
            for (int z = z1; z <= z2; z++)
            {
                ba.SetBlock(granite, new BlockPos(x, groundY, z, 0));
                for (int dy = 1; dy <= clearHeight; dy++)
                {
                    ba.SetBlock(0, new BlockPos(x, groundY + dy, z, 0));
                }
            }
        }

        int wrong = 0;
        for (int x = x1; x <= x2; x++)
        {
            for (int z = z1; z <= z2; z++)
            {
                wrong += ba.GetBlock(new BlockPos(x, groundY, z, 0)).Code.ToString() == GraniteCode ? 0 : 1;
                for (int dy = 1; dy <= clearHeight; dy++)
                {
                    wrong += ba.GetBlock(new BlockPos(x, groundY + dy, z, 0)).Id == 0 ? 0 : 1;
                }
            }
        }

        Assert.True(wrong == 0,
            $"{wrong} cells of the floor do not read back as the granite plate and the air above it on {ServerFlavor.Name}; setup is invalid");
        return groundY;
    }

    /// <summary>Compares every cell of a scene (layer 0 is the plate, layer 1 the first cell above it) with
    /// the expected content, by block code, and fails with the first differences.</summary>
    private void AssertCells(IBlockAccessor ba, int x0, int z0, int groundY, int height, System.Func<int, int, int, string> expected, string what,
        int width = LatticeSize, int depth = LatticeSize, string capNote = "")
    {
        var differences = new List<string>();
        int cells = 0;
        for (int layer = 0; layer <= height; layer++)
        {
            for (int xi = 0; xi < width; xi++)
            {
                for (int zi = 0; zi < depth; zi++)
                {
                    cells++;
                    string want = expected(xi, zi, layer);
                    Block block = ba.GetBlock(new BlockPos(x0 + xi, groundY + layer, z0 + zi, 0));
                    string got = block.Id == 0 ? AirName : block.Code.ToString();
                    if (got != want)
                    {
                        differences.Add($"({xi},{zi}) layer {layer}: expected {want}, got {got}");
                    }
                }
            }
        }

        if (differences.Count > 0)
        {
            var message = new StringBuilder();
            message.Append(CultureInfo.InvariantCulture, $"{what}: {differences.Count} of {cells} cells differ from the expected content on {ServerFlavor.Name}{capNote}");
            foreach (string line in differences.Take(10))
            {
                message.Append("\n  ").Append(line);
            }

            Assert.Fail(message.ToString());
        }
    }

    /// <summary>The liquid levels of the square around the source, one row per z and one digit per x.</summary>
    private static string LevelGrid(IBlockAccessor ba, BlockPos source, int radius)
    {
        var grid = new StringBuilder();
        for (int dz = -radius; dz <= radius; dz++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                grid.Append(ba.GetBlock(new BlockPos(source.X + dx, source.Y, source.Z + dz, 0), BlockLayersAccess.Fluid).LiquidLevel);
            }

            grid.Append('\n');
        }

        return grid.ToString();
    }

    private static string ExpectedLevels(int radius, bool withSource)
    {
        var grid = new StringBuilder();
        for (int dz = -radius; dz <= radius; dz++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                grid.Append(withSource ? Math.Max(0, MaxLiquidLevel - (Math.Abs(dx) + Math.Abs(dz))) : 0);
            }

            grid.Append('\n');
        }

        return grid.ToString();
    }
}
