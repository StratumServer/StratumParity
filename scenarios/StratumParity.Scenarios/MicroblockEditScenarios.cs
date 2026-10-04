using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Xunit;

namespace StratumParity.Scenarios;

/// <summary>
/// Parity for the editable microblock (chiseled block) voxel pipeline that Stratum rewrote in
/// BlockEntityMicroBlock: ConvertToVoxels now fills per-thread voxel and material grids instead
/// of allocating fresh ones, BoolArray16x16x16 is a ulong[64] with flat accessors, and
/// RebuildCuboidList runs its greedy cuboid growth on those shared grids and a fixed cuboid
/// buffer. Vanilla hands every BeginEdit its own grids and keeps no state between rebuilds, so
/// everything below is a contract both flavors must meet: a block's shape depends on its own
/// grids only.
///
/// The microblock types live in VSSurvivalMod, which the scenario project does not reference, so
/// the block entity is reached by reflection on its runtime type (the chiseledblock entity
/// derives from BlockEntityMicroBlock) and every lookup that fails reads "setup is invalid"
/// naming the member. Edits go through the same public pair the WorldEdit microblock brush
/// uses: BeginEdit hands out the voxel and material grids, EndEdit rebuilds the cuboid list from
/// them. Expectations are derived in the test from the voxel set itself, no golden. Nothing
/// here needs a player, a probe mod or a tick wait: the only await loads the chunk column.
/// </summary>
public class MicroblockEditScenarios : AtlasScenarioBase
{
    private const string GraniteCode = "game:rock-granite";
    private const string AndesiteCode = "game:rock-andesite";
    private const string EditableBlockCode = "game:chiseledblock";
    private const string MicroblockEntityTypeName = "Vintagestory.GameContent.BlockEntityMicroBlock";

    private const int Edge = 16;
    private const int VoxelsPerBlock = Edge * Edge * Edge;

    // ConvertToVoxels hands every BeginEdit on the game thread the same two grids, so opening a
    // second block overwrites the shape the first one is about to be rebuilt from.
    private static readonly KnownDivergence SharedEditGrids =
        new("StratumServer/Stratum#356", StratumBuild.Stable2, StratumBuild.Indev1);

    // Engine rules read from RebuildCuboidList (the Stratum rewrite keeps both): a face is
    // "almost solid" (what Block.SideIsSolid answers) while at most 32 of its 256 edge voxels
    // are missing, and its centre is attachable (Block.CanAttachBlockAt without an area) while
    // fewer than 5 voxels are missing from the 9x9 window whose in-plane coordinates are 4..12.
    private const int AlmostSolidMissingLimit = 32;
    private const int CentreMissingLimit = 5;
    private const int CentreWindowFrom = 4;
    private const int CentreWindowTo = 12;

    // Material index per voxel, or -1 for no voxel. Index 0 is the block's first material.
    private delegate int MaterialAt(int x, int y, int z);

    private sealed record ShapeCase(string Name, MaterialAt Voxel);

    // Each shape builds one block with the materials granite (0) and andesite (1).
    private static readonly ShapeCase[] Shapes =
    {
        // A staircase: a full bottom half, and an andesite step on the back half above it.
        new("stairs", (x, y, z) => y < 8 ? 0 : z >= 8 ? 1 : -1),
        // Every voxel touches the others by an edge only, so nothing can merge: 2048 cuboids.
        new("checkerboard", (x, y, z) => ((x + y + z) & 1) == 0 ? ((x / 4 + z / 4) & 1) : -1),
        // One voxel on a corner: the smallest possible block, no face anywhere near solid.
        new("lone-corner-voxel", (x, y, z) => x == 15 && y == 15 && z == 15 ? 0 : -1),
        // A full cube missing one corner voxel: three faces lose a single voxel each.
        new("cube-minus-corner-voxel", (x, y, z) => x == 0 && y == 0 && z == 0 ? -1 : 0),
        // Exactly 32 voxels missing from the down face, the last count that still counts as
        // solid, with a material seam down the middle that cuboid growth must not cross.
        new("down-face-missing-32", (x, y, z) => y == 0 && x < 2 ? -1 : x < 8 ? 0 : 1),
        // One voxel more than the limit: the down face stops being solid.
        new("down-face-missing-33", (x, y, z) => y == 0 && (x < 2 || (x == 2 && z == 0)) ? -1 : 0),
        // Three disjoint tunnels through a full cube, each straddling the centre rule
        // differently. Along X (faces east and west): 6 voxels missing, 3 inside the window.
        // Along Z (north and south): exactly 5 missing, all inside. Along Y (up and down):
        // 8 missing, 4 inside and 4 just outside the 4..12 window, so a one-off error in the
        // window flips a centre verdict. Every face stays almost solid.
        new("tunnels", (x, y, z) => InTunnels(x, y, z) ? -1 : 0),
    };

    private static bool InTunnels(int x, int y, int z) =>
        (y >= 3 && y <= 4 && z >= 5 && z <= 7)
        || (x >= 5 && x <= 7 && y == 9)
        || (x >= 5 && x <= 6 && y == 10)
        || (x >= 12 && x <= 13 && z >= 8 && z <= 11);

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task MicroblockEdits_Should_KeepEachShape_When_TwoBlocksAreOpenAtOnce()
    {
        // The WorldEdit microblock brush opens every block the brush touches before it
        // writes any of them back. On vanilla each BeginEdit owns its grids; on Stratum
        // ConvertToVoxels hands every caller on the thread the same two grids, so the second
        // block's shape overwrites the first's before the first is rebuilt.
        int graniteId = BlockIdOf(GraniteCode);
        int andesiteId = BlockIdOf(AndesiteCode);
        int[] firstMaterials = { graniteId };
        int[] secondMaterials = { andesiteId };

        BlockPos origin = await LoadAnchorColumn(World.Spawn.AddCopy(200, 2, 0));
        BlockPos firstPos = origin;
        BlockPos secondPos = origin.AddCopy(3, 0, 0);
        PlaceEditableBlocks(firstPos, secondPos);

        Microblock first = Microblock.At(World, firstPos);
        Microblock second = Microblock.At(World, secondPos);
        first.WasPlaced(World.Api.World.GetBlock(graniteId));
        second.WasPlaced(World.Api.World.GetBlock(andesiteId));

        // Second block: bottom-half slab, shaped by an ordinary single edit (one open block
        // is fine on both flavors, which is what the guard below pins down).
        Reshape(second, BottomHalf);

        AssertSetup(first, "first block, a full granite cube", FullCube, firstMaterials);
        AssertSetup(second, "second block, a bottom-half andesite slab", BottomHalf, secondMaterials);

        SharedGrids shared = EditBothAtOnce(first, second);

        Decoded firstShape = first.Decode();
        Decoded secondShape = second.Decode();
        var problems = new List<string>();

        int firstGranite = firstShape.CountOf(graniteId);
        if (firstGranite != VoxelsPerBlock - 1)
        {
            problems.Add($"first block holds {firstGranite} granite voxels, expected {VoxelsPerBlock - 1}");
        }

        int secondAndesite = secondShape.CountOf(andesiteId);
        if (secondAndesite != VoxelsPerBlock / 2)
        {
            problems.Add($"second block holds {secondAndesite} andesite voxels, expected {VoxelsPerBlock / 2}");
        }

        string? firstDiff = firstShape.Diff(Expected(FullCubeMinusTopCorner, firstMaterials), BlockName);
        if (firstDiff != null)
        {
            problems.Add($"first block shape differs from a full cube minus voxel (0,15,0): {firstDiff}");
        }

        string? secondDiff = secondShape.Diff(Expected(BottomHalf, secondMaterials), BlockName);
        if (secondDiff != null)
        {
            problems.Add($"second block shape differs from the bottom-half slab: {secondDiff}");
        }

        string sharing = $"both edits shared one voxel grid: {shared.Voxels}, one material grid: {shared.Materials}";
        if (SharedEditGrids.Applies)
        {
            // The bug shape: the first block was rebuilt from the second block's grids, so it
            // is now the second block's slab (in its own material), and the second is intact.
            string? slabDiff = firstShape.Diff(Expected(BottomHalf, firstMaterials), BlockName);
            Assert.True(
                shared.Voxels && shared.Materials && firstGranite == VoxelsPerBlock / 2 && slabDiff == null
                    && secondAndesite == VoxelsPerBlock / 2 && secondDiff == null,
                $"{SharedEditGrids.Tag}: bug shape changed or gone, expected the first block to become the " +
                $"second block's bottom-half slab ({sharing}); narrow or drop the exemption " +
                $"(first granite {firstGranite}, second andesite {secondAndesite}; {string.Join("; ", problems)})");
            return;
        }

        Assert.True(problems.Count == 0,
            $"opening two microblocks at once corrupted a shape on {ServerFlavor.Name}: {string.Join("; ", problems)} ({sharing})");
    }

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task MicroblockRebuild_Should_PreserveVoxelsAndFaceSolidity_When_CuboidsRebuilt()
    {
        int graniteId = BlockIdOf(GraniteCode);
        int andesiteId = BlockIdOf(AndesiteCode);
        int[] materials = { graniteId, andesiteId };

        BlockPos origin = await LoadAnchorColumn(World.Spawn.AddCopy(0, 2, 200));
        var positions = new BlockPos[Shapes.Length];
        for (int i = 0; i < Shapes.Length; i++)
        {
            // Two blocks apart on a grid inside one chunk column: no neighbours, no shared chunk edge.
            positions[i] = origin.AddCopy(2 * (i % 4), 0, 2 * (i / 4));
        }
        PlaceEditableBlocks(positions);

        // Build every shape first, verify afterwards: a later rebuild that disturbed an earlier
        // block (anything left behind in shared state) would show in the second pass.
        var blocks = new Microblock[Shapes.Length];
        for (int i = 0; i < Shapes.Length; i++)
        {
            Microblock block = Microblock.At(World, positions[i]);
            block.WasPlaced(World.Api.World.GetBlock(graniteId));
            int andesiteIndex = block.AddMaterial(World.Api.World.GetBlock(andesiteId));
            Assert.True(andesiteIndex == 1 && block.BlockIds.SequenceEqual(materials),
                $"shape '{Shapes[i].Name}': materials are [{string.Join(", ", block.BlockIds)}] (andesite index {andesiteIndex}), " +
                $"expected [{string.Join(", ", materials)}]; setup is invalid");
            Reshape(block, Shapes[i].Voxel);
            blocks[i] = block;
        }

        var problems = new List<string>();
        var almostSolidVerdicts = new HashSet<bool>();
        var attachVerdicts = new HashSet<bool>();
        IBlockAccessor accessor = World.Api.World.BlockAccessor;

        for (int i = 0; i < Shapes.Length; i++)
        {
            ShapeCase shape = Shapes[i];
            Microblock block = blocks[i];
            BlockPos pos = positions[i];

            // The cuboids must reproduce the voxel set, each voxel exactly once, with its material.
            string? diff = block.Decode().Diff(Expected(shape.Voxel, materials), BlockName);
            if (diff != null)
            {
                problems.Add($"{shape.Name}: decoded cuboids differ from the voxels: {diff}");
            }

            // sizeRel is counted straight from the voxels, VolumeRel from the cuboids (after
            // RegenSelectionBoxes, which EndEdit does not call), so both must land on the same fraction.
            int voxelCount = CountVoxels(shape.Voxel);
            float expectedRel = voxelCount / (float)VoxelsPerBlock;
            block.RegenSelectionBoxes(World.Api.World);
            if (Math.Abs(block.SizeRel - expectedRel) > 1e-6f)
            {
                problems.Add($"{shape.Name}: sizeRel {block.SizeRel} vs {expectedRel} ({voxelCount} voxels)");
            }

            if (Math.Abs(block.VolumeRel - expectedRel) > 1e-6f)
            {
                problems.Add($"{shape.Name}: VolumeRel {block.VolumeRel} vs {expectedRel} ({voxelCount} voxels)");
            }

            Block placed = accessor.GetBlock(pos);
            foreach (BlockFacing face in BlockFacing.ALLFACES)
            {
                (int missing, int centreMissing) = CountMissingOnFace(shape.Voxel, face);
                bool expectSolid = missing <= AlmostSolidMissingLimit;
                bool expectAttach = centreMissing < CentreMissingLimit;
                almostSolidVerdicts.Add(expectSolid);
                attachVerdicts.Add(expectAttach);

                bool solid = placed.SideIsSolid(accessor, pos, face.Index);
                if (solid != expectSolid)
                {
                    problems.Add($"{shape.Name}: SideIsSolid({face.Code}) is {solid}, expected {expectSolid} ({missing} of 256 face voxels missing)");
                }

                bool attach = placed.CanAttachBlockAt(accessor, placed, pos, face);
                if (attach != expectAttach)
                {
                    problems.Add($"{shape.Name}: CanAttachBlockAt({face.Code}) is {attach}, expected {expectAttach} ({centreMissing} centre voxels missing)");
                }
            }
        }

        // Vacuity guard: the table must hold both verdicts for both rules, or the loop above
        // would pass on a block that answers a constant.
        Assert.True(almostSolidVerdicts.Count == 2 && attachVerdicts.Count == 2,
            "the shape table yields only one SideIsSolid or CanAttachBlockAt verdict; setup is invalid");
        Assert.True(problems.Count == 0,
            $"microblock rebuild diverged on {ServerFlavor.Name}: {string.Join("; ", problems)}");
    }

    private static int FullCube(int x, int y, int z) => 0;

    private static int BottomHalf(int x, int y, int z) => y < Edge / 2 ? 0 : -1;

    private static int FullCubeMinusTopCorner(int x, int y, int z) => x == 0 && y == Edge - 1 && z == 0 ? -1 : 0;

    /// <summary>The edit the brush performs when it carries air: clears voxel (0,15,0) of the
    /// first block's own grid, then rebuilds both blocks from the grids they were handed.
    /// Deliberately synchronous: nothing may run between the two BeginEdit calls and the two
    /// EndEdit calls, exactly as in the brush loop.</summary>
    private static SharedGrids EditBothAtOnce(Microblock first, Microblock second)
    {
        OpenEdit openFirst = first.BeginEdit();
        OpenEdit openSecond = second.BeginEdit();
        first.SetVoxel(openFirst, 0, Edge - 1, 0, present: false);
        first.EndEdit(openFirst);
        second.EndEdit(openSecond);
        return new SharedGrids(
            ReferenceEquals(openFirst.Voxels, openSecond.Voxels),
            ReferenceEquals(openFirst.Materials, openSecond.Materials));
    }

    /// <summary>Overwrites a block's whole grid with a shape through one ordinary edit.</summary>
    private static void Reshape(Microblock block, MaterialAt shape)
    {
        OpenEdit open = block.BeginEdit();
        for (int x = 0; x < Edge; x++)
        {
            for (int y = 0; y < Edge; y++)
            {
                for (int z = 0; z < Edge; z++)
                {
                    int material = shape(x, y, z);
                    block.SetVoxel(open, x, y, z, present: material >= 0);
                    if (material >= 0)
                    {
                        open.Materials[x, y, z] = (byte)material;
                    }
                }
            }
        }

        block.EndEdit(open);
    }

    private void AssertSetup(Microblock block, string label, MaterialAt shape, int[] materials)
    {
        string? diff = block.Decode().Diff(Expected(shape, materials), BlockName);
        Assert.True(diff == null, $"{label} does not hold its starting shape on {ServerFlavor.Name}: {diff}; setup is invalid");
    }

    /// <summary>Expected block id per voxel (0 for no voxel) for a shape over a material list.</summary>
    private static System.Func<int, int, int, int> Expected(MaterialAt shape, int[] materials) =>
        (x, y, z) =>
        {
            int material = shape(x, y, z);
            return material < 0 ? 0 : materials[material];
        };

    private static int CountVoxels(MaterialAt shape)
    {
        int count = 0;
        for (int x = 0; x < Edge; x++)
        {
            for (int y = 0; y < Edge; y++)
            {
                for (int z = 0; z < Edge; z++)
                {
                    if (shape(x, y, z) >= 0)
                    {
                        count++;
                    }
                }
            }
        }

        return count;
    }

    /// <summary>Voxels missing from the 16x16 plane on one face, and from the centre window of
    /// that plane, mirroring the engine's north z=0, east x=15, south z=15, west x=0, up y=15,
    /// down y=0 convention. Both in-plane coordinates are treated alike by the engine.</summary>
    private static (int Missing, int CentreMissing) CountMissingOnFace(MaterialAt shape, BlockFacing face)
    {
        int missing = 0;
        int centreMissing = 0;
        for (int a = 0; a < Edge; a++)
        {
            for (int b = 0; b < Edge; b++)
            {
                (int x, int y, int z) = face.Index switch
                {
                    BlockFacing.indexNORTH => (a, b, 0),
                    BlockFacing.indexEAST => (Edge - 1, a, b),
                    BlockFacing.indexSOUTH => (a, b, Edge - 1),
                    BlockFacing.indexWEST => (0, a, b),
                    BlockFacing.indexUP => (a, Edge - 1, b),
                    _ => (a, 0, b),
                };
                if (shape(x, y, z) >= 0)
                {
                    continue;
                }

                missing++;
                if (a >= CentreWindowFrom && a <= CentreWindowTo && b >= CentreWindowFrom && b <= CentreWindowTo)
                {
                    centreMissing++;
                }
            }
        }

        return (missing, centreMissing);
    }

    private int BlockIdOf(string code)
    {
        Block? block = World.Api.World.GetBlock(new AssetLocation(code));
        Assert.True(block != null && block.BlockId != 0, $"block {code} does not exist on {ServerFlavor.Name}; setup is invalid");
        return block!.BlockId;
    }

    private string BlockName(int blockId) => World.Api.World.GetBlock(blockId)?.Code.ToString() ?? $"block id {blockId}";

    /// <summary>Loads the chunk column around a point and returns a position four blocks into
    /// it, so every block placed at small offsets from it stays in that one column. The Y of
    /// the argument is kept.</summary>
    private async Task<BlockPos> LoadAnchorColumn(BlockPos around)
    {
        int chunkX = around.X / 32;
        int chunkZ = around.Z / 32;
        BlockPos anchor = new BlockPos(chunkX * 32 + 4, around.Y, chunkZ * 32 + 4, 0);

        // KeepLoaded: no player is near, and the unload timer must not take the column mid-scenario.
        World.Api.WorldManager.LoadChunkColumnPriority(chunkX, chunkZ, new ChunkLoadOptions { KeepLoaded = true });
        await World.Until(
            () => World.Api.World.BlockAccessor.GetChunkAtBlockPos(anchor) != null,
            timeoutTicks: 600);
        return anchor;
    }

    /// <summary>Places the editable chiseled block at each position and reads every placement
    /// back (a column still loading swallows writes silently). The positions must be air.</summary>
    private void PlaceEditableBlocks(params BlockPos[] positions)
    {
        foreach (BlockPos pos in positions)
        {
            Assert.True(World.BlockAt(pos).Id == 0,
                $"{pos} holds {World.BlockAt(pos).Code} instead of air on {ServerFlavor.Name}; setup is invalid");
            World.SetBlock(EditableBlockCode, pos);
        }

        foreach (BlockPos pos in positions)
        {
            string placed = World.BlockAt(pos).Code.ToString();
            Assert.True(placed == EditableBlockCode && World.Api.World.BlockAccessor.GetBlockEntity(pos) != null,
                $"{EditableBlockCode} at {pos} reads back as {placed} (block entity present: " +
                $"{World.Api.World.BlockAccessor.GetBlockEntity(pos) != null}) on {ServerFlavor.Name}; setup is invalid");
        }
    }

    /// <summary>The voxel and material grids one BeginEdit handed out.</summary>
    private sealed record OpenEdit(object Voxels, byte[,,] Materials);

    private sealed record SharedGrids(bool Voxels, bool Materials);

    /// <summary>The cuboid list of one block unpacked into per-voxel coverage counts and block ids.</summary>
    private sealed class Decoded
    {
        private readonly int[] cover = new int[VoxelsPerBlock];
        private readonly int[] blockId = new int[VoxelsPerBlock];

        public Decoded(IEnumerable<(int X0, int Y0, int Z0, int X1, int Y1, int Z1, int Block)> cuboids)
        {
            foreach ((int x0, int y0, int z0, int x1, int y1, int z1, int block) in cuboids)
            {
                for (int x = x0; x < x1; x++)
                {
                    for (int y = y0; y < y1; y++)
                    {
                        for (int z = z0; z < z1; z++)
                        {
                            cover[IndexOf(x, y, z)]++;
                            blockId[IndexOf(x, y, z)] = block;
                        }
                    }
                }
            }
        }

        private static int IndexOf(int x, int y, int z) => (x * Edge + y) * Edge + z;

        /// <summary>Voxels covered by at least one cuboid whose material resolves to the block.</summary>
        public int CountOf(int block)
        {
            int count = 0;
            for (int i = 0; i < VoxelsPerBlock; i++)
            {
                if (cover[i] > 0 && blockId[i] == block)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>Null when the cuboids cover exactly the expected voxels, each once, with the
        /// expected block (an expected id of 0 means no voxel); otherwise the counts.</summary>
        public string? Diff(System.Func<int, int, int, int> expected, System.Func<int, string> nameOf)
        {
            int expectedCount = 0;
            int covered = 0;
            int missing = 0;
            int extra = 0;
            int overlapping = 0;
            int wrongBlock = 0;
            string first = "";
            var byBlock = new SortedDictionary<int, int>();

            for (int x = 0; x < Edge; x++)
            {
                for (int y = 0; y < Edge; y++)
                {
                    for (int z = 0; z < Edge; z++)
                    {
                        int i = IndexOf(x, y, z);
                        int want = expected(x, y, z);
                        int times = cover[i];
                        bool problem = false;

                        if (want != 0)
                        {
                            expectedCount++;
                        }

                        if (times > 0)
                        {
                            covered++;
                            byBlock[blockId[i]] = (byBlock.TryGetValue(blockId[i], out int n) ? n : 0) + 1;
                        }

                        if (want != 0 && times == 0)
                        {
                            missing++;
                            problem = true;
                        }
                        else if (want == 0 && times > 0)
                        {
                            extra++;
                            problem = true;
                        }
                        else if (want != 0 && blockId[i] != want)
                        {
                            wrongBlock++;
                            problem = true;
                        }

                        if (times > 1)
                        {
                            overlapping++;
                            problem = true;
                        }

                        if (problem && first.Length == 0)
                        {
                            first = $"first at voxel ({x},{y},{z}): expected block {want}, covered {times}x by block {blockId[i]}";
                        }
                    }
                }
            }

            if (missing + extra + overlapping + wrongBlock == 0)
            {
                return null;
            }

            string perBlock = string.Join(", ", byBlock.Select(kv => $"{kv.Value} x {nameOf(kv.Key)}"));
            return $"{covered} voxels decoded vs {expectedCount} expected (missing {missing}, extra {extra}, " +
                   $"overlapping {overlapping}, wrong material {wrongBlock}; decoded {perBlock}; {first})";
        }
    }

    /// <summary>One block entity of the BlockEntityMicroBlock family, driven through reflection.</summary>
    private sealed class Microblock
    {
        private readonly BlockEntity entity;
        private readonly MethodInfo wasPlaced;
        private readonly MethodInfo addMaterial;
        private readonly MethodInfo beginEdit;
        private readonly MethodInfo endEdit;
        private readonly MethodInfo regenSelectionBoxes;
        private readonly MethodInfo fromUint;
        private readonly PropertyInfo voxelIndexer;
        private readonly FieldInfo voxelCuboids;
        private readonly FieldInfo blockIds;
        private readonly FieldInfo sizeRel;
        private readonly PropertyInfo volumeRel;

        private Microblock(BlockEntity entity)
        {
            this.entity = entity;
            Type? micro = entity.GetType();
            while (micro != null && micro.FullName != MicroblockEntityTypeName)
            {
                micro = micro.BaseType;
            }

            Assert.True(micro != null,
                $"{entity.GetType().FullName} does not derive from {MicroblockEntityTypeName} on {ServerFlavor.Name}; setup is invalid");

            wasPlaced = Member(micro!.GetMethod("WasPlaced", new[] { typeof(Block), typeof(string) }), "WasPlaced(Block, string)");
            beginEdit = Member(micro.GetMethod("BeginEdit"), "BeginEdit");
            endEdit = Member(micro.GetMethod("EndEdit"), "EndEdit");
            regenSelectionBoxes = Member(
                micro.GetMethod("RegenSelectionBoxes", new[] { typeof(IWorldAccessor), typeof(IPlayer) }),
                "RegenSelectionBoxes(IWorldAccessor, IPlayer)");
            fromUint = Member(
                micro.GetMethod(
                    "FromUint",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[]
                    {
                        typeof(uint),
                        typeof(int).MakeByRefType(), typeof(int).MakeByRefType(), typeof(int).MakeByRefType(),
                        typeof(int).MakeByRefType(), typeof(int).MakeByRefType(), typeof(int).MakeByRefType(),
                        typeof(int).MakeByRefType(),
                    },
                    null),
                "static FromUint(uint, out x0, out y0, out z0, out x1, out y1, out z1, out material)");
            voxelCuboids = Member(micro.GetField("VoxelCuboids"), "VoxelCuboids");
            blockIds = Member(micro.GetField("BlockIds"), "BlockIds");
            sizeRel = Member(micro.GetField("sizeRel"), "sizeRel");
            volumeRel = Member(micro.GetProperty("VolumeRel"), "VolumeRel");

            // BoolArray16x16x16 is not referenced either: take its type from BeginEdit's first
            // out parameter and use its (x, y, z) indexer, which both flavors keep.
            Type gridType = Member(beginEdit.GetParameters()[0].ParameterType.GetElementType(), "BeginEdit voxel grid type");
            voxelIndexer = Member(
                gridType.GetProperty("Item", typeof(bool), new[] { typeof(int), typeof(int), typeof(int) }),
                $"{gridType.Name}[int, int, int]");

            // AddMaterial is declared on BlockEntityChisel, the editable entity, not on the base.
            addMaterial = Member(entity.GetType().GetMethod("AddMaterial", new[] { typeof(Block) }), "AddMaterial(Block)");
        }

        public static Microblock At(IWorldSession world, BlockPos pos)
        {
            BlockEntity? entity = world.Api.World.BlockAccessor.GetBlockEntity(pos);
            Assert.True(entity != null, $"no block entity at {pos} on {ServerFlavor.Name}; setup is invalid");
            return new Microblock(entity!);
        }

        public int[] BlockIds => (int[])blockIds.GetValue(entity)!;

        public float SizeRel => (float)sizeRel.GetValue(entity)!;

        public float VolumeRel => (float)volumeRel.GetValue(entity)!;

        /// <summary>Turns the fresh entity into a full cube of the block, as the chisel does.</summary>
        public void WasPlaced(Block material) => wasPlaced.Invoke(entity, BindingFlags.DoNotWrapExceptions, null, new object?[] { material, null }, null);

        public int AddMaterial(Block material) =>
            (int)addMaterial.Invoke(entity, BindingFlags.DoNotWrapExceptions, null, new object?[] { material }, null)!;

        public OpenEdit BeginEdit()
        {
            var args = new object?[2];
            beginEdit.Invoke(entity, BindingFlags.DoNotWrapExceptions, null, args, null);
            Assert.True(args[0] != null && args[1] is byte[,,],
                $"BeginEdit returned no voxel and material grids on {ServerFlavor.Name}; setup is invalid");
            return new OpenEdit(args[0]!, (byte[,,])args[1]!);
        }

        public void EndEdit(OpenEdit open) =>
            endEdit.Invoke(entity, BindingFlags.DoNotWrapExceptions, null, new[] { open.Voxels, open.Materials }, null);

        public void SetVoxel(OpenEdit open, int x, int y, int z, bool present) =>
            voxelIndexer.SetValue(open.Voxels, present, new object[] { x, y, z });

        public void RegenSelectionBoxes(IWorldAccessor world) =>
            regenSelectionBoxes.Invoke(entity, BindingFlags.DoNotWrapExceptions, null, new object?[] { world, null }, null);

        public Decoded Decode()
        {
            var cuboids = new List<(int, int, int, int, int, int, int)>();
            int[] ids = BlockIds;
            var list = (List<uint>)voxelCuboids.GetValue(entity)!;
            var args = new object?[8];
            foreach (uint packed in list)
            {
                args[0] = packed;
                for (int i = 1; i < args.Length; i++)
                {
                    args[i] = 0;
                }

                fromUint.Invoke(null, BindingFlags.DoNotWrapExceptions, null, args, null);
                int material = (int)args[7]!;
                // A material index outside BlockIds decodes to block id -1, which no shape expects.
                cuboids.Add(((int)args[1]!, (int)args[2]!, (int)args[3]!, (int)args[4]!, (int)args[5]!, (int)args[6]!,
                    material < ids.Length ? ids[material] : -1));
            }

            return new Decoded(cuboids);
        }

        private static T Member<T>(T? member, string description)
            where T : class
        {
            Assert.True(member != null, $"{description} not found on {MicroblockEntityTypeName} on {ServerFlavor.Name}; setup is invalid");
            return member!;
        }
    }
}
