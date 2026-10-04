using System.Diagnostics;
using System.Text;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Xunit;
using Xunit.Abstractions;

namespace StratumParity.Scenarios;

/// <summary>
/// Tree shape parity. Stratum patched <c>TreeGen.growBranch</c> (the generator behind every worldgen
/// tree and every grown sapling) to take its block positions from a manual stack indexed by branch
/// depth instead of allocating one per branch, and routes the vine and moss paths of
/// <c>PlaceBlockEtc</c> through a scratch position and reordered conditions. All of it must leave the
/// shape of a tree exactly as it was: the same random numbers in, the same blocks out.
///
/// For every registered <c>Vintagestory.ServerMods.TreeGen</c> generator (35 in the base game) the
/// scenario grows four trees at one fixed spot of a flat world with an <see cref="LCGRandom"/> that
/// is seeded per generator and sample, then compares a digest of every block each one staged with a
/// golden captured from the vanilla leg: two samples shaped like a grown sapling (<c>BlockEntitySapling</c>:
/// no vines, no moss, no stray logs) and two shaped like worldgen (<c>WgenTreeSupplier</c>: vines and
/// moss on, either hemisphere, stray logs at their default chance).
///
/// What the plan said, and what it became after reading the engine:
/// <list type="bullet">
/// <item>Only the generators that are a <c>TreeGen</c> instance are grown. The others in
/// <c>TreeGenerators</c> (bamboo, fern tree, fruit trees) carry no Stratum patch, and the fruit trees ignore the
/// random they are handed and draw from their own, so no golden can pin them.</item>
/// <item>The trees are never placed in the world. Each one is grown through a fresh bulk block accessor with
/// <c>ReadFromStagedByDefault</c> (what a sapling does), so the generator sees the real ground and its own blocks,
/// and the digest reads what the accessor staged. Nothing is committed: no light update, no neighbour update, no
/// cleanup between trees, and the world stays the same for the next sample.</item>
/// <item>The forest floor is skipped (<c>skipForestFloor</c>, as a sapling does). It reads the worldgen climate and
/// replaces soil through block patches that a flat world never initialises, so it would grow nothing here, and
/// Stratum did not touch it.</item>
/// <item><c>new LCGRandom(seed)</c> alone gives the same sequence for every seed (the constructor derives the
/// current seed from the previous one), so each sample calls <c>InitPositionSeed</c> as the generator does.</item>
/// </list>
///
/// Setup guards (all "setup is invalid"): the 3x3 columns around the base are loaded, the ground is solid with air
/// above it over a 81 x 81 block footprint and 64 blocks above the base, at least 30 TreeGen generators are
/// registered, and the whole set of trees grown a second time, generators and samples in reverse order, gives the
/// very same digests (a generator that keeps state from one tree to the next cannot be pinned by a golden).
///
/// The golden is <c>fixtures/treegeneration-goldens/trees-&lt;game version&gt;.json</c>, read from the fixtures folder next
/// to the scenario assembly. Capture it once per game version from a vanilla run with
/// <c>PARITY_TREEGEN_CAPTURE=&lt;absolute file&gt;</c> (the scenario writes the file and fails on purpose, so a capture run
/// is never green, and it refuses to run on Stratum), commit it, and refresh it on a game bump. When a digest differs,
/// run both flavors with <c>PARITY_TREEGEN_DUMP=&lt;dir&gt;</c> and diff <c>vanilla-trees.txt</c> against
/// <c>stratum-trees.txt</c>: they hold the canonical block list behind every digest.
/// </summary>
public class TreeGenerationScenarios : AtlasScenarioBase
{
    private const string TreeGenTypeName = "Vintagestory.ServerMods.TreeGen";
    private const int MinGenerators = 30;
    private const int RandomSeed = 4242;

    // Ground checked around the base: 81 x 81 blocks (every fourth column), loaded through the 3x3 columns around it.
    private const int FootprintRadius = 40;
    private const int FootprintStep = 4;
    private const int AirAboveBase = 64;

    private const string CaptureVariable = "PARITY_TREEGEN_CAPTURE";
    private const string DumpVariable = "PARITY_TREEGEN_DUMP";
    private const string GoldenFolder = "treegeneration-goldens";

    private readonly ITestOutputHelper output;

    public TreeGenerationScenarios(ITestOutputHelper output) => this.output = output;

    private sealed record SampleSpec(string Profile, int Index, float Size, EnumHemisphere Hemisphere, float Vines, float Moss, float OtherBlock)
    {
        public TreeGenParams ToParams() => new()
        {
            skipForestFloor = true,
            size = Size,
            hemisphere = Hemisphere,
            vinesGrowthChance = Vines,
            mossGrowthChance = Moss,
            otherBlockChance = OtherBlock,
        };
    }

    private static readonly SampleSpec[] Specs =
    {
        // BlockEntitySapling: size 0.6 to 1.1, no forest floor, no vines, no moss, no stray logs.
        new("sapling", 1, 0.7f, EnumHemisphere.North, 0f, 0f, 0f),
        new("sapling", 2, 1.0f, EnumHemisphere.North, 0f, 0f, 0f),
        // Worldgen: vines and moss from the climate, the hemisphere picks the mossy face, stray logs at the default chance.
        new("worldgen", 1, 1.4f, EnumHemisphere.North, 0.7f, 0.9f, 1f),
        new("worldgen", 2, 1.1f, EnumHemisphere.South, 0.3f, 0.5f, 1f),
    };

    private sealed record NamedGenerator(string Name, ITreeGenerator Generator);

    private sealed record Ground(BlockPos Base, string Description, int ChunkX, int ChunkZ);

    /// <summary>The digest row of one grown tree, how many positions it staged, the exception type when growing threw,
    /// and the canonical text behind the row (kept for the dump only).</summary>
    private sealed record Grown(string Row, int Positions, string? Error, string Canonical);

    [AtlasScenario(TimeoutMs = 240_000)]
    public async Task GrownTrees_Should_MatchVanillaShapes_When_GrownWithFixedRandom()
    {
        Ground ground = await LoadGround();
        try
        {
            await GrowAndCompare(ground);
        }
        finally
        {
            WorldgenArea.Unload(World, ground.ChunkX - 1, ground.ChunkZ - 1, 3, 3);
        }
    }

    private async Task GrowAndCompare(Ground ground)
    {
        string? capturePath = Environment.GetEnvironmentVariable(CaptureVariable);
        string? dumpDir = Environment.GetEnvironmentVariable(DumpVariable);

        // The generators are registered by the vegetation pass's world generator init; give a slow boot a moment before
        // the guard below counts them.
        try
        {
            await World.Until(() => TreeGenCount() >= MinGenerators, timeoutTicks: 600);
        }
        catch (Exception)
        {
            // TreeGenGenerators fails with the count it found.
        }

        List<NamedGenerator> generators = TreeGenGenerators();
        output.WriteLine($"{generators.Count} TreeGen generators, base {ground.Base} on {ground.Description}, {ServerFlavor.Name} ({ServerFlavor.Version ?? "vanilla"})");

        var watch = Stopwatch.StartNew();
        Dictionary<string, Grown> forward = await GrowAll(ground, generators, reverse: false, keepText: !string.IsNullOrEmpty(dumpDir));
        Dictionary<string, Grown> backward = await GrowAll(ground, generators, reverse: true, keepText: false);
        output.WriteLine($"grew {forward.Count} trees twice in {watch.ElapsedMilliseconds} ms");

        // A golden only means something when the generator is a pure function of its inputs: the same sample must come
        // out the same whether it is grown first or last.
        List<string> unstable = forward
            .Where(f => backward[f.Key].Row != f.Value.Row)
            .Select(f => $"{f.Key}: {f.Value.Row} then {backward[f.Key].Row}")
            .ToList();
        Assert.True(unstable.Count == 0,
            $"{unstable.Count} of {forward.Count} trees came out differently when grown again in reverse order on {ServerFlavor.Name}: " +
            $"{string.Join("; ", unstable.Take(8))}; a generator that keeps state between trees cannot be pinned by a golden (setup is invalid)");

        var actual = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, Grown> tree in forward)
        {
            actual[tree.Key] = tree.Value.Row;
            output.WriteLine($"{tree.Key} {tree.Value.Row}");
        }

        if (!string.IsNullOrEmpty(dumpDir))
        {
            WriteDump(dumpDir, forward);
        }

        string version = RunningGameVersion();

        if (!string.IsNullOrEmpty(capturePath))
        {
            Assert.False(ServerFlavor.IsStratum, "the tree golden is captured from the vanilla leg only, never from Stratum; setup is invalid");
            // A tiny tree can legitimately grow nothing at a small size (pricklymoses at 0.7, say), and an empty digest still
            // pins that parity. What a golden must never pin is a generator that threw, or one that grew nothing in all of its samples.
            List<string> broken = forward
                .Where(f => f.Value.Error != null)
                .Select(f => $"{f.Key}: {f.Value.Row}")
                .Concat(forward
                    .GroupBy(f => f.Key[..f.Key.IndexOf('/')])
                    .Where(g => g.All(f => f.Value.Positions == 0))
                    .Select(g => $"{g.Key}: grew nothing in any sample"))
                .ToList();
            Assert.True(broken.Count == 0,
                $"{broken.Count} generators threw or grew nothing on vanilla, so a golden of them would pin a broken setup: " +
                $"{string.Join("; ", broken.Take(8))}; setup is invalid");

            string full = Path.GetFullPath(capturePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, TreeGenerationDigest.ToJson(version, actual));
            output.WriteLine($"tree golden captured to {full}: commit it as fixtures/{GoldenFolder}/{GoldenFileName(version)}");
            Assert.Fail($"tree golden captured to {full}: commit it as fixtures/{GoldenFolder}/{GoldenFileName(version)} " +
                $"and rerun without {CaptureVariable} (a capture run compares nothing, so it is never green)");
        }

        // Next to the scenario assembly: under Atlas AppContext.BaseDirectory is the game install, not the test output.
        string goldenPath = Path.Combine(
            Path.GetDirectoryName(typeof(TreeGenerationScenarios).Assembly.Location)!, "fixtures", GoldenFolder, GoldenFileName(version));
        Assert.True(File.Exists(goldenPath),
            $"no tree golden for game version {version} at {goldenPath}: capture it from a vanilla run with " +
            $"{CaptureVariable}=<absolute file> and commit it as fixtures/{GoldenFolder}/{GoldenFileName(version)}; setup is invalid");

        (string goldenVersion, SortedDictionary<string, string> goldenRows) = TreeGenerationDigest.FromJson(File.ReadAllText(goldenPath));
        Assert.True(goldenVersion == version,
            $"the golden was captured on game {goldenVersion} but {ServerFlavor.Name} runs {version}; refresh it; setup is invalid");

        List<string> mismatches = TreeGenerationDigest.Mismatches(goldenRows, actual);
        Assert.True(mismatches.Count == 0,
            $"{mismatches.Count} of {actual.Count} grown trees differ from the vanilla golden on {ServerFlavor.Name} ({ServerFlavor.Version ?? "vanilla"}): " +
            $"{string.Join("; ", mismatches.Take(10))}. Rerun both flavors with {DumpVariable}=<dir> and diff the vanilla and stratum trees files " +
            "to see which blocks differ.");
    }

    /// <summary>Loads the 3x3 columns around a base 200 blocks from spawn and finds the ground at its centre. The
    /// base is the top ground block, which is where worldgen hands a generator its position.</summary>
    private async Task<Ground> LoadGround()
    {
        BlockPos around = World.Spawn.AddCopy(200, 0, 200);
        int chunkX = around.X / WorldgenArea.ChunkSize;
        int chunkZ = around.Z / WorldgenArea.ChunkSize;
        int mapSizeY = World.Api.WorldManager.MapSizeY;
        int layers = mapSizeY / WorldgenArea.ChunkSize;
        IBlockAccessor accessor = World.Api.World.BlockAccessor;
        var probe = new BlockPos(0, 0, 0, 0);
        Block BlockAt(int x, int y, int z) => accessor.GetBlock(probe.Set(x, y, z));

        // KeepLoaded: no player is near, and the unload timer must not take a column mid-scenario.
        World.Api.WorldManager.LoadChunkColumnPriority(
            chunkX - 1, chunkZ - 1, chunkX + 1, chunkZ + 1, new ChunkLoadOptions { KeepLoaded = true });
        await World.Until(
            () => WorldgenArea.Columns(chunkX - 1, chunkZ - 1, 3, 3)
                .All(c => Enumerable.Range(0, layers).All(layer => accessor.GetChunk(c.Cx, layer, c.Cz) != null)),
            timeoutTicks: 1200);

        int x = chunkX * WorldgenArea.ChunkSize + WorldgenArea.ChunkSize / 2;
        int z = chunkZ * WorldgenArea.ChunkSize + WorldgenArea.ChunkSize / 2;

        int groundY = -1;
        for (int y = mapSizeY - 1; y > 0; y--)
        {
            Block block = BlockAt(x, y, z);
            if (block.Id != 0 && block.Replaceable < 6000)
            {
                groundY = y;
                break;
            }
        }

        Assert.True(groundY > 0 && groundY + AirAboveBase < mapSizeY,
            $"no ground with {AirAboveBase} blocks of room above it at ({x}, {z}) (found y {groundY}, world height {mapSizeY}) on {ServerFlavor.Name}; setup is invalid");

        var problems = new List<string>();
        for (int dx = -FootprintRadius; dx <= FootprintRadius; dx += FootprintStep)
        {
            for (int dz = -FootprintRadius; dz <= FootprintRadius; dz += FootprintStep)
            {
                Block ground = BlockAt(x + dx, groundY, z + dz);
                Block above = BlockAt(x + dx, groundY + 1, z + dz);
                if (ground.Id == 0 || ground.Replaceable >= 6000 || above.Id != 0)
                {
                    problems.Add($"({dx},{dz}): {ground.Code} under {above.Code}");
                }
            }
        }

        for (int dy = 1; dy <= AirAboveBase; dy++)
        {
            Block block = BlockAt(x, groundY + dy, z);
            if (block.Id != 0)
            {
                problems.Add($"column above the base: {block.Code} at +{dy}");
                break;
            }
        }

        Assert.True(problems.Count == 0,
            $"the ground around ({x}, {groundY}, {z}) is not flat solid ground with air above on {ServerFlavor.Name}: " +
            $"{string.Join("; ", problems.Take(6))}; setup is invalid");

        return new Ground(new BlockPos(x, groundY, z, 0), BlockAt(x, groundY, z).Code.ToString(), chunkX, chunkZ);
    }

    private int TreeGenCount() =>
        World.Api.World.TreeGenerators.Count(e => e.Value.GetType().FullName == TreeGenTypeName);

    private List<NamedGenerator> TreeGenGenerators()
    {
        var found = new List<NamedGenerator>();
        foreach (KeyValuePair<AssetLocation, ITreeGenerator> entry in World.Api.World.TreeGenerators)
        {
            if (entry.Value.GetType().FullName == TreeGenTypeName)
            {
                found.Add(new NamedGenerator(entry.Key.Path, entry.Value));
            }
        }

        found.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        Assert.True(found.Count >= MinGenerators,
            $"only {found.Count} {TreeGenTypeName} generators are registered on {ServerFlavor.Name} (of {World.Api.World.TreeGenerators.Count}), " +
            $"expected at least {MinGenerators}; setup is invalid");

        string[] duplicated = found.GroupBy(g => g.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        Assert.True(duplicated.Length == 0,
            $"generator names are not unique by path on {ServerFlavor.Name}: {string.Join(", ", duplicated)}; setup is invalid");
        return found;
    }

    /// <summary>Grows every sample of every generator once, one generator per tick so the game thread keeps
    /// breathing. Reverse order walks the generators and the samples backwards.</summary>
    private async Task<Dictionary<string, Grown>> GrowAll(Ground ground, List<NamedGenerator> generators, bool reverse, bool keepText)
    {
        var grown = new Dictionary<string, Grown>();
        for (int g = 0; g < generators.Count; g++)
        {
            NamedGenerator generator = generators[reverse ? generators.Count - 1 - g : g];
            for (int s = 0; s < Specs.Length; s++)
            {
                SampleSpec spec = Specs[reverse ? Specs.Length - 1 - s : s];
                grown[$"{generator.Name}/{spec.Profile}/{spec.Index}"] = Grow(ground, generator, spec, keepText);
            }

            await World.Ticks(1);
        }

        return grown;
    }

    private Grown Grow(Ground ground, NamedGenerator generator, SampleSpec spec, bool keepText)
    {
        // What a sapling grows through: staged blocks that the generator reads back, never committed.
        IBulkBlockAccessor accessor = World.Api.World.GetBlockAccessorBulkUpdate(synchronize: false, relight: false);
        accessor.ReadFromStagedByDefault = true;

        var random = new LCGRandom(RandomSeed);
        random.InitPositionSeed(NameSeed($"{generator.Name}/{spec.Profile}"), spec.Index);

        try
        {
            // GrowTree moves the position it is given (yOffset), so it gets its own copy.
            generator.Generator.GrowTree(accessor, ground.Base.Copy(), spec.ToParams(), random);
        }
        catch (Exception e)
        {
            return new Grown($"error:{e.GetType().Name}", 0, e.GetType().Name, "");
        }

        IList<Block> blocks = World.Api.World.Blocks;
        BlockPos origin = ground.Base;
        var staged = new List<StagedBlock>(accessor.StagedBlocks.Count);
        foreach (KeyValuePair<BlockPos, BlockUpdate> entry in accessor.StagedBlocks)
        {
            BlockUpdate update = entry.Value;
            string decors = update.Decors == null
                ? ""
                : string.Join(";", update.Decors
                    .Select(d => $"{d.faceAndSubposition}:{blocks[d.decorId].Code}")
                    .OrderBy(d => d, StringComparer.Ordinal));
            staged.Add(new StagedBlock(
                entry.Key.X - origin.X,
                entry.Key.Y - origin.Y,
                entry.Key.Z - origin.Z,
                update.NewSolidBlockId >= 0 ? blocks[update.NewSolidBlockId].Code.ToString() : "-",
                update.NewFluidBlockId >= 0 ? blocks[update.NewFluidBlockId].Code.ToString() : "-",
                decors));
        }

        string canonical = TreeGenerationDigest.Canonical(staged);
        return new Grown(TreeGenerationDigest.Row(staged.Count, canonical), staged.Count, null, keepText ? canonical : "");
    }

    /// <summary>A stable int from a name (FNV-1a), so a sample's random does not move when the generator list does.</summary>
    private static int NameSeed(string text)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (byte b in Encoding.UTF8.GetBytes(text))
            {
                hash ^= b;
                hash *= 16777619;
            }

            return (int)hash;
        }
    }

    private static void WriteDump(string dir, Dictionary<string, Grown> grown)
    {
        Directory.CreateDirectory(dir);
        using var writer = new StreamWriter(Path.Combine(dir, $"{ServerFlavor.Name}-trees.txt"));
        foreach (KeyValuePair<string, Grown> tree in grown.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            writer.Write($"## {tree.Key} {tree.Value.Row}\n");
            writer.Write(tree.Value.Canonical);
        }
    }

    private static string GoldenFileName(string gameVersion) => $"trees-{gameVersion}.json";

    /// <summary>GameVersion.ShortGameVersion is a const, so reading it directly would bake the build-time API version
    /// into the test assembly; the field read sees the version of the API the engine actually runs.</summary>
    private static string RunningGameVersion() =>
        (string?)typeof(Vintagestory.API.Config.GameVersion).GetField(nameof(Vintagestory.API.Config.GameVersion.ShortGameVersion))?.GetValue(null) ?? "unknown";
}
