using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Xunit;
using Xunit.Abstractions;

namespace StratumParity.Scenarios;

/// <summary>
/// Terrain generation parity on a fixed-seed standard world: what the worldgen leaves in the
/// ground has to be what vanilla leaves, because a divergence here changes every world a server
/// ever generates and cannot be fixed afterwards. One class, one world, seed
/// <see cref="WorldgenGoldens.Seed"/>, the golden rectangle of <see cref="WorldgenGoldens"/>
/// (a 6x6 block of columns about 2000 blocks from spawn, the inner 4x4 is hashed).
///
/// What each scenario pins, and where its expectation comes from:
/// <list type="bullet">
/// <item>TerrainLayers: the committed vanilla golden of <see cref="WorldgenGoldens.TerrainLayers"/>
/// (height map, strata, cave air of 16 columns). Already pinned by the foundation, nothing new here.</item>
/// <item>OreDeposits: the ore blocks of each column of the golden rectangle against a golden captured from the vanilla leg (see below).
/// A probe of a deliberate Stratum trade-off (see "Ore blocks" below): vanilla equals the golden, Stratum keeps the same deposits at a
/// comparable count and is not held to the same blocks.</item>
/// <item>Rivulets: the water sources of 36 peeked columns (a wet corner, 19 sources on vanilla) against a golden captured from
/// the vanilla leg, one row per source. Peeked and not loaded, because the fluid simulation of a loaded column moves the water
/// cells between boots; the golden rectangle itself holds no water rivulet (its one source is a lava cave cell).</item>
/// <item>ProspectingRockColumn: the prospecting pick's own strata simulation (the ProPickWorkSpace the
/// prospecting item caches, which Stratum rewrote with a snapshot wrapper) against the column the
/// worldgen actually made. No new constants: the vanilla anchor is TerrainLayers, which pins the real column to
/// vanilla, so agreeing with it pins the pick to vanilla too.</item>
/// <item>PeekedColumns: PeekChunkColumn up to each pass of one column of the golden rectangle. The passes are told apart: the
/// Terrain peek has no ore block, the TerrainFeatures peek the ores of the ore golden (on Stratum a comparable count of them, see
/// "Ore blocks" below), the Vegetation peek a height map and a cave air count of its own (trees; pinned). The Terrain and
/// TerrainFeatures peeks have the golden height map, rock and cave
/// air (their terrain digests are equal on vanilla, the ores are what separates them), the Vegetation peek the golden rock,
/// a peek never loads the column and repeating a peek gives the same blocks. The golden rectangle is generated and released
/// first: the ores of a peeked column depend on the map regions around it.</item>
/// <item>MassGeneration: 200 columns requested at once (100 one by one, 100 as one rectangle) all reach
/// Done with every chunk, OnLoaded fires once per request, and the request queues drain back.</item>
/// <item>PregeneratedColumns: Stratum's /stratum pregen over the golden rectangle queues and completes all 36 columns and
/// produces the golden terrain; vanilla has no such command and loads the rectangle normally.</item>
/// </list>
/// Three confirmed Stratum divergences are exempted by build (see <see cref="KnownDivergence"/>), each with its issue on
/// StratumServer/Stratum: the rewritten GenCaves carve moves the rock of five columns (StratumServer/Stratum#366: TerrainLayers,
/// PregeneratedColumns), the changed height bound of GenRivulets adds a rivulet source in one more column (TerrainLayers,
/// PregeneratedColumns) and moves every water rivulet source of the peeked area (Rivulets; StratumServer/Stratum#368), and
/// PeekChunkColumn up to Terrain stops after the first half of Stratum's split Terrain pass (StratumServer/Stratum#367:
/// PeekedColumns). On the listed builds the Stratum branch asserts the observed shape; vanilla and every other build is strict parity.
///
/// Ore blocks (OreDeposits, PeekedColumns) are a probe of a deliberate trade-off that Stratum accepted, not a divergence to report:
/// pull request StratumServer/Stratum#33 (commit 3f621d7, "Optimize DiscDepositGenerator", patches/VSEssentials/Systems/WorldGen/Standard/ChunkGen/5.GenDeposits/Generators/DiscGenerator.cs.patch)
/// replaces the smooth simplex edge noise of a deposit disc with an integer hash of the block column and scales the deformation of the
/// disc instead of clamping it, which takes GenDeposit from 32.6 s to 9.1 s over 10,200 chunks by the patch's own measurement. The price is
/// that the same seed gets other ore blocks in every disc deposit, and Stratum took it for the speed. The deposits themselves stay: their
/// centres, kinds and grades do not come from the changed code, so vanilla must equal the golden and Stratum must keep the same deposits
/// per column at a comparable count, whatever the blocks. Measured on 1.22.7-stratum.2 and 1.22.7-stratum.2-indev.1, with the same rows
/// on every run: 17431 ore blocks of 21 ores in the 16 golden columns against vanilla's 17115 of 20 (+1.8%), a column count between -13.5%
/// and +19.8% of vanilla's, and the most plentiful ore of every column unchanged. The bands that Stratum is held to come from those
/// numbers (<see cref="WorldgenTerrainGoldenFiles.OreTotalBandPercent"/> and <see cref="WorldgenTerrainGoldenFiles.OreColumnBandPercent"/>),
/// on every Stratum build and without pinning a block.
///
/// The story structure scenario of the same plan entry lives in <see cref="WorldgenStoryStructureScenarios"/>:
/// this class runs on the creative building playstyle, which switches lore content (and with it every
/// story structure) off.
///
/// Why the deposit noise is aligned first (<see cref="Prepare"/>). Vanilla is not deterministic across
/// world boots in one process: the first standard world a process generates places ores, cracked rock,
/// saltpeter, flint and stalagmites differently from every later one (measured while building the shared
/// worldgen helpers). The cause, found by reading the engine code, is in
/// GenDeposits: its vertical distortion layers (GenMaps.GetDepositVerticalDistort) are built in initWorldGen
/// from the static TerraGenConfig.depositVerticalDistortScale, which is 8 until the first map region
/// generation of the process sets it to 2 and never resets it. The first world builds its layers with 8,
/// every later world with 2. Confirmed by a run without the alignment: with another standard world generated
/// first, OreDeposits fails on vanilla in all 16 columns. A dedicated server is always the first world of its process, so this class rebuilds the
/// layers with 8 (set the static, run GenDeposits.initWorldGen again) before anything in the golden
/// rectangle's map regions exists. Every world of every class order then generates the state a real server
/// does, on both flavors, and the ore golden stops depending on which class booted first.
/// The terrain digest does not depend on it (deposits are folded into their host rock) and is untouched.
///
/// Goldens of the two scenarios that need one live in fixtures/worldgenterrain-goldens (ores.txt,
/// rivulets.txt). Capture them from the VANILLA leg only, with PARITY_WORLDGEN_CAPTURE=&lt;directory&gt;
/// (a capture run writes the files and fails by design, like the registry capture of AssetMatchingScenarios),
/// commit them, and re-capture on a Vintage Story bump. A missing file fails as "setup is invalid".
/// </summary>
[AtlasWorld(WorldType = "standard", Seed = WorldgenGoldens.Seed)]
public class WorldgenTerrainScenarios : AtlasScenarioBase
{
    private const int ChunkSize = WorldgenArea.ChunkSize;
    private const int RectX = WorldgenGoldens.RectChunkX;
    private const int RectZ = WorldgenGoldens.RectChunkZ;
    private const int RectEdge = WorldgenGoldens.RectEdge;

    // Map regions that the golden rectangle's worldgen touches: the rectangle plus the deposit
    // range (3 columns) and the neighbour ring (1) on each side.
    private const int RegionMarginColumns = 4;

    // Value of TerraGenConfig.depositVerticalDistortScale while the first world of a process builds
    // its deposit layers: what a dedicated server always sees.
    private const int DedicatedServerDistortScale = 8;

    private const string DepositsSystem = "Vintagestory.ServerMods.GenDeposits";
    private const string TerraGenConfigType = "Vintagestory.ServerMods.TerraGenConfig";
    private const string ProPickWorkspaceKey = "propickworkspace";
    private const string CaptureVariable = "PARITY_WORLDGEN_CAPTURE";

    // 200 columns for the mass scenario: two blocks of 10 x 10 side by side, 81 to 100 columns west of the
    // spawn column (chunk 16000) and far from the golden rectangle and from the peek column.
    private const int MassX0 = 15900;
    private const int MassZ0 = 15900;
    private const int MassBlockEdge = 10;

    // The golden columns whose terrain digest matches vanilla on every Stratum build measured so far,
    // with a block column inside each (local x, local z). The six columns where the strata differ from
    // vanilla at y 1 to 7 are left out, so this scenario isolates the prospecting path from the
    // terrain divergence that TerrainLayers already reports.
    private static readonly (int Cx, int Cz, int Lx, int Lz)[] ProspectingColumns =
    {
        (16047, 16047, 5, 20),
        (16047, 16049, 17, 9),
        (16047, 16050, 26, 14),
        (16048, 16048, 11, 27),
        (16048, 16049, 2, 6),
        (16049, 16048, 21, 18),
        (16049, 16050, 8, 12),
        (16050, 16048, 14, 3),
    };

    // GenCaves carves the deepest caves (y below 12) as lava pools lined with basalt, whatever the strata say: the
    // prospecting pick only simulates the strata and the deposits, so the comparison starts above them. Measured on
    // vanilla: column (16050,16048) has basalt at y 6 to 11 in the generated world and granite in the pick.
    private const int LavaCaveCeiling = 12;

    // Inner column of the golden rectangle that the peek scenario uses (its height map is in the golden).
    private const int PeekChunkX = 16048;
    private const int PeekChunkZ = 16048;

    // What a peek of that column up to Vegetation has that the one up to TerrainFeatures lacks: the trees raise the height map and
    // move the cave air. Measured on 5 vanilla, 3 stable and 2 prerelease runs: the same on every one. Re-pin on a Vintage Story bump.
    private const string PeekVegetationHeightMap = "d3dcfcdf5e6b440f27908a1664f36c5dee84617f3dd696aaace167b37264485c";
    private const int PeekVegetationCaveAir = 2349;

    // The columns whose rivulets the Rivulets scenario peeks: 6 x 6 columns, 19 water sources on vanilla (this corner is wet; the
    // golden rectangle holds none), the same 19 on every boot. 36 peeks take about 5 s on vanilla and 9 s on Stratum.
    private const int RivuletChunkX = 16101;
    private const int RivuletChunkZ = 16141;
    private const int RivuletEdge = 6;
    private const int MinRivuletSources = 10;

    // The rewritten GenCaves.SetBlocks carves the lava layer (y 1 to 7) differently, so the rock layer of five columns moves.
    private static readonly KnownDivergence CaveCarve =
        new("StratumServer/Stratum#366", StratumBuild.Stable2, StratumBuild.Indev1);

    // GenRivulets draws the height from a bound one smaller than vanilla's: one more rivulet source, in one more column.
    private static readonly KnownDivergence RivuletBound =
        new("StratumServer/Stratum#368", StratumBuild.Stable2, StratumBuild.Indev1);

    // PeekChunkColumn maps a vanilla pass to the internal one that ends at the first half of the split pass: a Terrain peek
    // has no strata, no caves and no soil layer.
    private static readonly KnownDivergence PeekTerrain =
        new("StratumServer/Stratum#367", StratumBuild.Stable2, StratumBuild.Indev1);

    // Columns whose rock layer each divergence moves in the golden rectangle (measured by block diff, the same on every run).
    private static readonly (int Cx, int Cz)[] CaveColumns =
    {
        (16048, 16047), (16048, 16050), (16049, 16047), (16050, 16047), (16050, 16050),
    };

    private static readonly (int Cx, int Cz)[] RivuletColumns = { (16050, 16049) };

    private static object? preparedWorld;

    private readonly ITestOutputHelper output;

    public WorldgenTerrainScenarios(ITestOutputHelper output) => this.output = output;

    // ------------------------------------------------------------------ terrain layers

    [AtlasScenario(TimeoutMs = 240_000)]
    public async Task TerrainLayers_Should_MatchVanillaGolden_When_ColumnsGenerated()
    {
        await using GoldenRect rect = await GoldenRect.Open(World);
        AssertDefaultTopology(World);

        List<TerrainDigest> actual = rect.Inner.Select(c => WorldgenDigest.Terrain(World, c.Cx, c.Cz)).ToList();
        output.WriteLine($"terrain digests on {Describe()}, rows for a re-pin:\n{WorldgenGoldens.Format(actual)}");

        AssertTerrainGolden(actual, "the terrain layers of the golden rectangle");
    }

    // ------------------------------------------------------------------ ore deposits

    // A deliberate Stratum trade-off, not a bug: pull request StratumServer/Stratum#33 (commit 3f621d7, "Optimize DiscDepositGenerator",
    // patches/VSEssentials/Systems/WorldGen/Standard/ChunkGen/5.GenDeposits/Generators/DiscGenerator.cs.patch) swaps the smooth
    // simplex edge noise of a deposit disc for an integer hash of the block column and scales the deformation instead of clamping
    // it, for a GenDeposit that runs in 9.1 s instead of 32.6 s over 10,200 chunks. Every disc deposit gets other ore blocks for the
    // same seed (17431 ore blocks of 21 ores on stratum.2 and its prerelease against vanilla's 17115 of 20 in these 16 columns, the
    // same rows on every run), and Stratum accepted that. The deposits are the same ones: the count of each column stays close
    // (between -13.5% and +19.8%) and so does the total (+1.8%), and the most plentiful ore of every column is vanilla's. The
    // scenario asserts both sides: the vanilla golden on vanilla, the bands of WorldgenTerrainGoldenFiles on every Stratum build.
    [AtlasScenario(TimeoutMs = 240_000)]
    public async Task OreDeposits_Should_MatchVanillaGoldenOnVanillaAndHoldComparableCountsOnStratum_When_ColumnsGenerated()
    {
        await using GoldenRect rect = await GoldenRect.Open(World);
        AssertDefaultTopology(World);

        List<OreDigest> actual = rect.Inner.Select(c => WorldgenDigest.Ores(World, c.Cx, c.Cz)).ToList();
        int total = actual.Sum(d => d.Count);
        int codes = actual.SelectMany(d => d.PerOre.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=')[0])).Distinct().Count();
        output.WriteLine($"ore digests on {Describe()}: {total} ore blocks of {codes} ores in {actual.Count} columns\n{WorldgenTerrainGoldenFiles.FormatOres(actual)}");

        // A golden over a handful of ore blocks would pass against almost anything.
        Assert.True(total >= 100 && codes >= 3,
            $"the golden rectangle holds only {total} ore blocks of {codes} different ores on {Describe()}; the sample is vacuous, pick another rectangle; setup is invalid");

        CaptureOrCompare("ores.txt", WorldgenTerrainGoldenFiles.FormatOres(actual), golden =>
        {
            List<OreDigest> want = WorldgenTerrainGoldenFiles.ParseOres(golden);
            List<string> mismatches = WorldgenTerrainGoldenFiles.CompareOres(want, actual);
            AssertNoSetupError(mismatches, "the ore golden");
            int wantTotal = want.Sum(d => d.Count);
            output.WriteLine($"{mismatches.Count} of {actual.Count} columns hold other ore blocks than the golden on {Describe()}, " +
                $"{total} ore blocks in all against {wantTotal} ({(total - wantTotal) * 100.0 / wantTotal:+0.0;-0.0}%)");

            if (!ServerFlavor.IsStratum)
            {
                Assert.True(mismatches.Count == 0,
                    $"the ore deposits of the golden rectangle differ from the vanilla golden on {Describe()}: " +
                    $"{mismatches.Count} of {actual.Count} columns: {string.Join("; ", mismatches)}");
                return;
            }

            // The documented difference: other blocks, so no hash is compared, and the same deposits, so the counts are.
            List<string> outside = WorldgenTerrainGoldenFiles.OreCountsOutsideBand(want, actual);
            Assert.True(outside.Count == 0,
                $"the ore deposits of the golden rectangle left the band around the vanilla counts on {Describe()} " +
                $"(StratumServer/Stratum#33 trades the blocks for speed, not the deposits): {string.Join("; ", outside)}");
        });
    }

    // ------------------------------------------------------------------ rivulets

    [AtlasScenario(TimeoutMs = 300_000)]
    public async Task Rivulets_Should_MatchVanillaGolden_When_ColumnsGenerated()
    {
        Prepare(World);
        AssertDefaultTopology(World);

        // Peeked, not loaded: GenRivulets has run at the Vegetation pass and no block update has, so the water cells are what the
        // generator placed (a loaded column lets the fluid simulation move them within a few ticks of Done).
        List<(int Cx, int Cz)> columns = WorldgenArea.Columns(RivuletChunkX, RivuletChunkZ, RivuletEdge, RivuletEdge).ToList();
        foreach ((int cx, int cz) in columns)
        {
            Assert.True(!HasAnyChunk(World, cx, cz),
                $"rivulet column ({cx},{cz}) is loaded before the peek on {Describe()}; setup is invalid");
        }

        Dictionary<(int, int), IServerChunk[]> peeked = await PeekAll(World, columns, EnumWorldGenPass.Vegetation, timeoutTicks: 6000);
        var actual = new List<RivuletCell>();
        foreach ((int cx, int cz) in columns)
        {
            actual.AddRange(WorldgenDigest.RivuletSources(World, peeked[(cx, cz)], cx, cz));
        }

        output.WriteLine($"rivulet sources of {columns.Count} peeked columns on {Describe()}: {actual.Count}\n{WorldgenTerrainGoldenFiles.FormatRivulets(actual)}");

        CaptureOrCompare("rivulets.txt", WorldgenTerrainGoldenFiles.FormatRivulets(actual), golden =>
        {
            List<RivuletCell> want = WorldgenTerrainGoldenFiles.ParseRivulets(golden);
            // A golden over a handful of sources would pass against a generator that places none, or against almost anything.
            Assert.True(want.Count >= MinRivuletSources,
                $"the rivulet golden holds only {want.Count} sources on {Describe()}, expected at least {MinRivuletSources}; the sample is vacuous; setup is invalid");

            (List<RivuletCell> missing, List<RivuletCell> extra) = WorldgenTerrainGoldenFiles.CompareRivulets(want, actual);
            output.WriteLine($"{want.Count - missing.Count} of the {want.Count} golden rivulet sources are present on {Describe()}, {extra.Count} other sources found");
            if (!RivuletBound.Applies)
            {
                Assert.True(missing.Count == 0 && extra.Count == 0,
                    $"the rivulet sources of the {columns.Count} peeked columns differ from the vanilla golden on {Describe()}: " +
                    $"{missing.Count} of {want.Count} missing [{Show(missing)}], {extra.Count} extra [{Show(extra)}]");
                return;
            }

            // The shape measured on stratum.2 and the prerelease: the height bound moves every draw, so the sources are other cells
            // (0 of the 19 vanilla cells survive, 13 sources are found) and not a different amount of them.
            int survivors = want.Count - missing.Count;
            Assert.True(survivors <= want.Count / 4 && actual.Count >= MinRivuletSources / 2,
                $"{RivuletBound.Tag}: bug shape changed on {Describe()}: {survivors} of the {want.Count} vanilla sources survive (expected at most {want.Count / 4}) " +
                $"and {actual.Count} sources are found (expected at least {MinRivuletSources / 2}); narrow or drop the exemption");
        });
    }

    // ------------------------------------------------------------------ prospecting

    // The vanilla anchor of this scenario is TerrainLayers, not a constant of its own: the generated world is pinned to the
    // vanilla golden there, and the pick is compared with the generated world here.
    [AtlasScenario(TimeoutMs = 240_000)]
    public async Task ProspectingRockColumn_Should_MatchVanillaGolden_When_Probed()
    {
        await using GoldenRect rect = await GoldenRect.Open(World);
        AssertDefaultTopology(World);

        // The prospecting item caches one workspace per server in the api's object cache at load.
        Assert.True(World.Api.ObjectCache.TryGetValue(ProPickWorkspaceKey, out object? workspace) && workspace != null,
            $"the object cache has no '{ProPickWorkspaceKey}' on {Describe()}: the prospecting pick was not loaded; setup is invalid");
        MethodInfo? getRockColumn = workspace!.GetType().GetMethod("GetRockColumn",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(int), typeof(int) }, null);
        Assert.True(getRockColumn != null && getRockColumn.ReturnType == typeof(int[]),
            $"ProPickWorkSpace.GetRockColumn(int, int) was not found by reflection on {Describe()}; setup is invalid");

        var fold = new StoneFold(World);
        var mismatches = new List<string>();
        var strata = new HashSet<string>();
        int compared = 0;
        foreach ((int cx, int cz, int lx, int lz) in ProspectingColumns)
        {
            int posX = cx * ChunkSize + lx;
            int posZ = cz * ChunkSize + lz;
            IWorldChunk[] chunks = WorldgenDigest.Column(World, cx, cz);

            int[] reading;
            try
            {
                reading = (int[])getRockColumn!.Invoke(workspace, new object[] { posX, posZ })!;
            }
            catch (TargetInvocationException e)
            {
                throw new Xunit.Sdk.XunitException($"ProPickWorkSpace.GetRockColumn({posX},{posZ}) threw on {Describe()}: {e.InnerException}");
            }

            int columnCompared = 0;
            for (int y = 0; y < reading.Length; y++)
            {
                string? read = fold.Of(reading[y]);
                if (read != null)
                {
                    strata.Add(read);
                }

                if (y < LavaCaveCeiling)
                {
                    continue; // GenCaves fills the lava caves of the deepest layers with basalt, which the pick (strata and deposits only) cannot know
                }

                int actualId = chunks[y / ChunkSize].Data.GetBlockId(((y % ChunkSize) * ChunkSize + lz) * ChunkSize + lx, BlockLayersAccess.Solid);
                string? made = actualId == 0 ? null : fold.Of(actualId);
                if (made == null)
                {
                    continue; // cave, soil layer, fluid: the worldgen did not leave stone here
                }

                if (IsDepositRock(read) || IsDepositRock(made))
                {
                    continue; // marbles, obsidian, travertine and halite only exist where a deposit put them, and the pick draws its deposits at other positions on every call
                }

                columnCompared++;
                if (read != made && mismatches.Count < 12)
                {
                    mismatches.Add($"({posX},{y},{posZ}) world {made}, prospecting {read ?? "none"}");
                }
            }

            Assert.True(columnCompared >= 20,
                $"only {columnCompared} stone blocks of ({posX},{posZ}) could be compared on {Describe()}; the sample is vacuous; setup is invalid");
            compared += columnCompared;
        }

        Assert.True(strata.Count >= 2,
            $"the prospecting readings hold {strata.Count} different rocks on {Describe()}; a column without strata cannot show a stale read; setup is invalid");
        Assert.True(mismatches.Count == 0,
            $"the prospecting rock columns disagree with the generated world on {Describe()} (first {mismatches.Count} of {compared} compared blocks): {string.Join("; ", mismatches)}");
    }

    // ------------------------------------------------------------------ mass generation

    [AtlasScenario(TimeoutMs = 900_000)]
    public async Task MassGeneration_Should_CompleteAndDrainQueues_When_TwoHundredColumnsRequested()
    {
        Prepare(World);
        AssertDefaultTopology(World);
        ServerQueues queues = ServerQueues.Read(World);

        List<(int Cx, int Cz)> singles = WorldgenArea.Columns(MassX0, MassZ0, MassBlockEdge, MassBlockEdge).ToList();
        int rectX0 = MassX0 + MassBlockEdge;
        List<(int Cx, int Cz)> rectColumns = WorldgenArea.Columns(rectX0, MassZ0, MassBlockEdge, MassBlockEdge).ToList();
        List<(int Cx, int Cz)> all = singles.Concat(rectColumns).ToList();
        Assert.True(all.Count == 200, $"the mass area is {all.Count} columns, expected 200 on {Describe()}; setup is invalid");
        foreach ((int cx, int cz) in all)
        {
            Assert.True(!HasAnyChunk(World, cx, cz),
                $"mass column ({cx},{cz}) is already loaded before the request on {Describe()}; setup is invalid");
        }

        QueueCounts before = queues.Snapshot();
        output.WriteLine($"queues before the request on {Describe()}: {before}");

        var firedSingle = new Dictionary<(int, int), int>();
        int firedRect = 0;
        try
        {
            foreach ((int cx, int cz) in singles)
            {
                (int, int) key = (cx, cz);
                firedSingle[key] = 0;
                World.Api.WorldManager.LoadChunkColumnPriority(cx, cz, new ChunkLoadOptions { KeepLoaded = true, OnLoaded = () => firedSingle[key]++ });
            }

            World.Api.WorldManager.LoadChunkColumnPriority(
                rectX0, MassZ0, rectX0 + MassBlockEdge - 1, MassZ0 + MassBlockEdge - 1,
                new ChunkLoadOptions { KeepLoaded = true, OnLoaded = () => firedRect++ });

            await World.Until(
                () => all.All(c => WorldgenArea.IsDone(World, c.Cx, c.Cz)) && firedSingle.Values.All(v => v >= 1) && firedRect >= 1,
                timeoutTicks: 20_000);
            // A callback that fires twice does it right after the first one or when the queues are cleaned up.
            await World.Ticks(150);

            List<string> incomplete = all
                .Where(c => !HasEveryChunk(World, c.Cx, c.Cz))
                .Select(c => $"({c.Cx},{c.Cz})")
                .ToList();
            Assert.True(incomplete.Count == 0,
                $"{incomplete.Count} of 200 columns reached Done without all their chunks on {Describe()}: {string.Join(", ", incomplete.Take(10))}");

            List<string> wrongSingle = firedSingle.Where(p => p.Value != 1).Select(p => $"({p.Key.Item1},{p.Key.Item2}) fired {p.Value}x").ToList();
            Assert.True(wrongSingle.Count == 0,
                $"OnLoaded of a single column request must fire exactly once on {Describe()}: {string.Join(", ", wrongSingle.Take(10))}");
            Assert.True(firedRect == 1,
                $"OnLoaded of the rectangle request fired {firedRect} times on {Describe()}, expected exactly once");

            // None of the requested columns may stay in a request queue ...
            List<string> leftovers = queues.QueuedAmong(all.Select(c => World.Api.WorldManager.MapChunkIndex2D(c.Cx, c.Cz)).ToHashSet());
            Assert.True(leftovers.Count == 0,
                $"{leftovers.Count} of the 200 completed columns are still in a request queue on {Describe()}: {string.Join(", ", leftovers.Take(10))}");

            // ... and the queues as a whole are back to where they were before the request.
            QueueCounts after = queues.Snapshot();
            bool drained = await WaitFor(World, () => queues.Snapshot().AtMost(before), timeoutTicks: 1800);
            Assert.True(drained,
                $"the request queues did not drain back on {Describe()}: before {before}, after the wait {queues.Snapshot()} (right after completion {after})");
        }
        finally
        {
            foreach ((int cx, int cz) in all)
            {
                World.Api.WorldManager.UnloadChunkColumn(cx, cz);
            }
        }
    }

    // ------------------------------------------------------------------ peeked columns

    [AtlasScenario(TimeoutMs = 300_000)]
    public async Task PeekedColumns_Should_MatchVanillaGolden_When_PeekedUpToEachPass()
    {
        Prepare(World);
        AssertDefaultTopology(World);
        TerrainDigest golden = WorldgenGoldens.TerrainLayers.Single(d => d.ChunkX == PeekChunkX && d.ChunkZ == PeekChunkZ);
        OreDigest oreGolden = WorldgenTerrainGoldenFiles.ParseOres(ReadGolden("ores.txt")).Single(d => d.ChunkX == PeekChunkX && d.ChunkZ == PeekChunkZ);

        // The deposits of a column come from the columns around it and read the ore maps of the map regions around it, so a column
        // peeked in a world where those regions do not exist yet has other ore blocks (598 against the golden's 577, measured on
        // vanilla with this scenario run alone). Generate the regions the way every scenario of the class does, by taking the
        // golden rectangle to Done, and release it again before peeking: the result then does not depend on which scenario ran first.
        GoldenRect rect = await GoldenRect.Open(World);
        await rect.DisposeAsync();
        bool released = await WaitFor(World, () => !HasAnyChunk(World, PeekChunkX, PeekChunkZ), timeoutTicks: 1200);
        Assert.True(released,
            $"column ({PeekChunkX},{PeekChunkZ}) is still loaded after the golden rectangle was released on {Describe()}; setup is invalid");

        // The column is only ever peeked: a peek must not load it.
        EnumWorldGenPass[] passes = { EnumWorldGenPass.Terrain, EnumWorldGenPass.TerrainFeatures, EnumWorldGenPass.Vegetation };
        var digests = new Dictionary<EnumWorldGenPass, TerrainDigest>();
        var ores = new Dictionary<EnumWorldGenPass, OreDigest>();
        foreach (EnumWorldGenPass pass in passes)
        {
            IServerChunk[] chunks = await WorldgenPeek.Column(World, PeekChunkX, PeekChunkZ, pass, timeoutTicks: 3000);
            Assert.True(chunks.Length == World.Api.WorldManager.MapSizeY / ChunkSize && chunks.All(c => c != null),
                $"the peek up to {pass} returned {chunks.Length} chunks (some null: {chunks.Any(c => c == null)}) on {Describe()}");
            digests[pass] = WorldgenDigest.Terrain(World, chunks, PeekChunkX, PeekChunkZ);
            ores[pass] = WorldgenDigest.Ores(World, chunks, PeekChunkX, PeekChunkZ);
            output.WriteLine($"peek up to {pass} on {Describe()}: {digests[pass]}; {ores[pass]}");

            // A peek is a pure function of the seed: the same peek gives the same blocks (vegetation differs on every vanilla
            // boot, so the Vegetation peek is not repeated).
            if (pass != EnumWorldGenPass.Vegetation)
            {
                IServerChunk[] again = await WorldgenPeek.Column(World, PeekChunkX, PeekChunkZ, pass, timeoutTicks: 3000);
                string first = WorldgenDigest.ColumnBlocks(World, chunks);
                string second = WorldgenDigest.ColumnBlocks(World, again);
                Assert.True(first == second,
                    $"two peeks up to {pass} of column ({PeekChunkX},{PeekChunkZ}) give different blocks on {Describe()}: {first[..12]} then {second[..12]}");
            }
        }

        Assert.True(!HasAnyChunk(World, PeekChunkX, PeekChunkZ),
            $"the column ({PeekChunkX},{PeekChunkZ}) is loaded after it was only peeked on {Describe()}");

        // Where each pass ends. The terrain digest folds ores into their host rock, so the Terrain and TerrainFeatures peeks have
        // the same one on vanilla and cannot be told apart by it: the ore blocks do (GenDeposits runs at TerrainFeatures, so the
        // Terrain peek has none and the TerrainFeatures peek has the golden ores), and the Vegetation peek is told apart by its
        // pinned height map and cave air (its trees raise the height map and move the cave air). A peek that returned the state of
        // the pass before, or of the pass after, fails one of these.
        TerrainDigest terrainPeek = digests[EnumWorldGenPass.Terrain];
        TerrainDigest featuresPeek = digests[EnumWorldGenPass.TerrainFeatures];
        TerrainDigest vegetationPeek = digests[EnumWorldGenPass.Vegetation];
        OreDigest featuresOres = ores[EnumWorldGenPass.TerrainFeatures];

        Assert.True(ores[EnumWorldGenPass.Terrain].Count == 0,
            $"the peek up to Terrain holds {ores[EnumWorldGenPass.Terrain].Count} ore blocks on {Describe()}: the deposits are placed at TerrainFeatures, so the peek ran past its pass");
        if (ServerFlavor.IsStratum)
        {
            // The documented DiscGenerator difference (see the OreDeposits scenario): other ore blocks, so no hash is compared, and the
            // same deposits, so the count and the most plentiful ore are (602 ore blocks against the golden's 577 on stratum.2 and the prerelease).
            List<string> outside = WorldgenTerrainGoldenFiles.OreColumnOutsideBand(oreGolden, featuresOres);
            Assert.True(outside.Count == 0,
                $"the peek up to TerrainFeatures left the band around the vanilla ore golden on {Describe()} (hash {featuresOres.Hash[..12]}, " +
                $"the golden's is {oreGolden.Hash[..12]}): {string.Join("; ", outside)}");
        }
        else
        {
            Assert.True(featuresOres.Count > 0 && featuresOres.Hash == oreGolden.Hash && featuresOres.Count == oreGolden.Count,
                $"the peek up to TerrainFeatures holds {featuresOres.Count} ore blocks (hash {featuresOres.Hash[..12]}) on {Describe()}, " +
                $"the vanilla golden has {oreGolden.Count} (hash {oreGolden.Hash[..12]})");
        }

        Assert.True(ores[EnumWorldGenPass.Vegetation].Hash == featuresOres.Hash,
            $"the peek up to Vegetation holds other ore blocks than the one up to TerrainFeatures on {Describe()}: " +
            $"{ores[EnumWorldGenPass.Vegetation].Hash[..12]} then {featuresOres.Hash[..12]}");
        Assert.True(vegetationPeek.HeightMap == PeekVegetationHeightMap && vegetationPeek.CaveAir == PeekVegetationCaveAir
                    && vegetationPeek.HeightMap != featuresPeek.HeightMap && vegetationPeek.CaveAir != featuresPeek.CaveAir,
            $"the peek up to Vegetation has height map {vegetationPeek.HeightMap[..12]} and {vegetationPeek.CaveAir} cave air on {Describe()}, " +
            $"vanilla has {PeekVegetationHeightMap[..12]} and {PeekVegetationCaveAir} (the TerrainFeatures peek has {featuresPeek.HeightMap[..12]} and {featuresPeek.CaveAir})");

        // Terrain and TerrainFeatures peeks hold the finished terrain layers (the later passes only add soil decor, vegetation and
        // creatures); the Vegetation peek only the rock, its height map and cave air are pinned above.
        List<string> terrain = DifferingLayers(terrainPeek, golden, includeHeightAndAir: true);
        List<string> features = DifferingLayers(featuresPeek, golden, includeHeightAndAir: true);
        List<string> vegetation = DifferingLayers(vegetationPeek, golden, includeHeightAndAir: false);

        Assert.True(features.Count == 0, $"the peek up to TerrainFeatures differs from the vanilla golden in [{string.Join(", ", features)}] on {Describe()}");
        Assert.True(vegetation.Count == 0, $"the peek up to Vegetation differs from the vanilla golden in [{string.Join(", ", vegetation)}] on {Describe()}");

        if (PeekTerrain.Applies)
        {
            // The shape measured on stratum.2 and the prerelease: the first half of the pass ran (the height map is the golden),
            // the second did not (no strata, no soil layer, no cave carved: a column of granite).
            Assert.True(terrain.SequenceEqual(new[] { "rock", "cave air" }) && terrainPeek.CaveAir == 0,
                $"{PeekTerrain.Tag}: bug shape changed on {Describe()}: the peek up to Terrain differs from the golden in [{string.Join(", ", terrain)}] " +
                $"with {terrainPeek.CaveAir} cave air blocks, expected [rock, cave air] and none; narrow or drop the exemption");
        }
        else
        {
            Assert.True(terrain.Count == 0, $"the peek up to Terrain differs from the vanilla golden in [{string.Join(", ", terrain)}] on {Describe()}");
        }
    }

    private static List<string> DifferingLayers(TerrainDigest got, TerrainDigest golden, bool includeHeightAndAir)
    {
        var layers = new List<string>();
        if (includeHeightAndAir && got.HeightMap != golden.HeightMap)
        {
            layers.Add("height map");
        }

        if (got.Rock != golden.Rock)
        {
            layers.Add("rock");
        }

        if (includeHeightAndAir && got.CaveAir != golden.CaveAir)
        {
            layers.Add("cave air");
        }

        return layers;
    }

    // ------------------------------------------------------------------ pregeneration

    [AtlasScenario(TimeoutMs = 480_000, FreshWorld = true)]
    public async Task PregeneratedColumns_Should_MatchTerrainGolden_When_PregenRuns()
    {
        Prepare(World);
        AssertDefaultTopology(World);
        foreach ((int cx, int cz) in WorldgenArea.Columns(RectX, RectZ, RectEdge, RectEdge))
        {
            Assert.True(!HasAnyChunk(World, cx, cz),
                $"column ({cx},{cz}) of the golden rectangle is loaded before the pregeneration on {Describe()}; setup is invalid");
        }

        if (ServerFlavor.IsStratum)
        {
            string? enabled = await StratumSetting.Get(World, "Performance.Pregen.Enabled");
            Assert.True(string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase),
                $"Performance.Pregen.Enabled reads '{enabled}' on {Describe()}, the pregeneration cannot start; setup is invalid");

            CommandResult started = await World.ExecuteCommand(
                $"/stratum pregen start rect {RectX} {RectZ} {RectX + RectEdge - 1} {RectZ + RectEdge - 1}");
            Assert.True(started.Ok, $"/stratum pregen start failed on {Describe()} ({started.Status}): {started.Message}");

            object pregen = Pregen();
            bool complete = await WaitFor(World, () => PregenStatus(pregen) == "complete", timeoutTicks: 6000);
            CommandResult status = await World.ExecuteCommand("/stratum pregen");
            Assert.True(complete, $"the pregeneration of the golden rectangle did not complete on {Describe()}; status: {status.Message}");

            // Nothing of the rectangle was loaded, so the job had to generate every column of it itself: without this the
            // comparison below would just generate the rectangle the normal way.
            int columns = RectEdge * RectEdge;
            string area = $"rect {RectX},{RectZ} to {RectX + RectEdge - 1},{RectZ + RectEdge - 1} columns={columns}";
            Match progress = Regex.Match(status.Message ?? string.Empty, @"queued=(\d+) completed=(\d+)");
            Assert.True((status.Message ?? string.Empty).Contains(area) && progress.Success
                        && int.Parse(progress.Groups[1].Value) == columns && int.Parse(progress.Groups[2].Value) >= columns,
                $"the pregeneration status does not show '{area}' with {columns} queued and completed columns on {Describe()}: {status.Message}");
        }
        else
        {
            CommandResult result = await World.ExecuteCommand("/stratum pregen");
            Assert.True(result.Status == EnumCommandStatus.NoSuchCommand,
                $"/stratum pregen answered {result.Status} ({result.Message}) on {Describe()}, vanilla has no such command");
        }

        // Pregenerated or loaded the normal way, the rectangle holds the vanilla terrain.
        await using GoldenRect rect = await GoldenRect.Open(World);
        List<TerrainDigest> actual = rect.Inner.Select(c => WorldgenDigest.Terrain(World, c.Cx, c.Cz)).ToList();
        AssertTerrainGolden(actual, $"the terrain of the {(ServerFlavor.IsStratum ? "pregenerated" : "loaded")} golden rectangle");
    }

    // ------------------------------------------------------------------ setup

    /// <summary>
    /// Runs once per world, before any scenario generates anything of the golden rectangle: rebuilds the
    /// deposit vertical distortion layers the way the first world of a process (a dedicated server) has them.
    /// See the class summary. Fails as "setup is invalid" when a map region of the rectangle exists already,
    /// because its stored distortion data would be the wrong one.
    /// </summary>
    private static void Prepare(IWorldSession world)
    {
        object server = world.Api.World;
        if (ReferenceEquals(preparedWorld, server))
        {
            return;
        }

        IWorldManagerAPI manager = world.Api.WorldManager;
        int columnsPerRegion = manager.RegionSize / manager.ChunkSize;
        for (int rx = (RectX - RegionMarginColumns) / columnsPerRegion; rx <= (RectX + RectEdge + RegionMarginColumns) / columnsPerRegion; rx++)
        {
            for (int rz = (RectZ - RegionMarginColumns) / columnsPerRegion; rz <= (RectZ + RectEdge + RegionMarginColumns) / columnsPerRegion; rz++)
            {
                Assert.True(manager.GetMapRegion(rx, rz) == null,
                    $"map region ({rx},{rz}) of the golden rectangle exists before the deposit noise was aligned on {ServerFlavor.Name}: " +
                    "its stored distortion data would not be the dedicated server one; setup is invalid");
            }
        }

        ModSystem? deposits = world.Api.ModLoader.GetModSystem(DepositsSystem);
        Assert.True(deposits != null, $"mod system {DepositsSystem} is not loaded on {ServerFlavor.Name}; setup is invalid");

        FieldInfo? scale = deposits!.GetType().Assembly.GetType(TerraGenConfigType)?
            .GetField("depositVerticalDistortScale", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(scale != null && scale.FieldType == typeof(int),
            $"{TerraGenConfigType}.depositVerticalDistortScale was not found by reflection on {ServerFlavor.Name}; setup is invalid");

        MethodInfo? initWorldGen = deposits.GetType().GetMethod("initWorldGen",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
        Assert.True(initWorldGen != null,
            $"{DepositsSystem}.initWorldGen() was not found by reflection on {ServerFlavor.Name}; setup is invalid");

        scale!.SetValue(null, DedicatedServerDistortScale);
        initWorldGen!.Invoke(deposits, null);
        preparedWorld = server;
    }

    /// <summary>The default topology is what this class pins: Stratum with its split Terrain pass active (no foreign
    /// handler is staged), which WorldgenSplitDisabledScenarios covers from the other side.</summary>
    private static void AssertDefaultTopology(IWorldSession world)
    {
        if (ServerFlavor.IsStratum)
        {
            int late = WorldgenTopology.TerrainLateHandlers(world);
            Assert.True(late > 0,
                $"the split Terrain pass is not active on {ServerFlavor.Name} ({ServerFlavor.Version}): no handler runs in the late sub-stage, " +
                "so this class would pin the stock topology and not the default one; setup is invalid");
        }
    }

    private static string Describe() => $"{ServerFlavor.Name} ({ServerFlavor.Version ?? "vanilla"})";

    private static bool IsDepositRock(string? rock) => rock != null && WorldgenDigest.DepositRockPaths.Contains(rock, StringComparer.Ordinal);

    private static string Format(IEnumerable<(int Cx, int Cz)> columns) =>
        string.Join(", ", columns.OrderBy(c => c).Select(c => $"({c.Cx},{c.Cz})"));

    /// <summary>A mismatch list that says "setup is invalid" is a broken measurement or golden, not a divergence: fail with the flavor and the first message.</summary>
    private static void AssertNoSetupError(IEnumerable<string> mismatches, string what)
    {
        string? broken = mismatches.FirstOrDefault(m => m.Contains("setup is invalid", StringComparison.Ordinal));
        Assert.True(broken == null, $"{what} cannot be compared on {Describe()}: {broken}");
    }

    /// <summary>Strict parity with the terrain golden, except on the builds listed for the cave and rivulet divergences, where the
    /// rock layer must move in exactly the columns measured and nothing else may (the cave air of the rivulet column is one lower
    /// on some runs: its source flows into the neighbouring cave cell once block updates run, timing and not shape).</summary>
    private static void AssertTerrainGolden(IReadOnlyList<TerrainDigest> actual, string what)
    {
        List<string> mismatches = WorldgenGoldens.TerrainMismatches(actual);
        AssertNoSetupError(mismatches, what);

        var expectedMoved = new List<(int Cx, int Cz)>();
        if (CaveCarve.Applies)
        {
            expectedMoved.AddRange(CaveColumns);
        }

        if (RivuletBound.Applies)
        {
            expectedMoved.AddRange(RivuletColumns);
        }

        if (expectedMoved.Count == 0)
        {
            Assert.True(mismatches.Count == 0,
                $"{what} differ from the vanilla golden on {Describe()}: {mismatches.Count} mismatches: {string.Join("; ", mismatches)}");
            return;
        }

        var moved = new List<(int Cx, int Cz)>();
        var otherLayers = new List<string>();
        for (int i = 0; i < actual.Count; i++)
        {
            TerrainDigest golden = WorldgenGoldens.TerrainLayers[i];
            TerrainDigest got = actual[i];
            if (got.Rock != golden.Rock)
            {
                moved.Add((got.ChunkX, got.ChunkZ));
            }

            bool airMayFlow = RivuletBound.Applies && RivuletColumns.Contains((got.ChunkX, got.ChunkZ));
            if (got.HeightMap != golden.HeightMap || got.DepositRocks != golden.DepositRocks || (got.CaveAir != golden.CaveAir && !airMayFlow))
            {
                otherLayers.Add($"({got.ChunkX},{got.ChunkZ})");
            }
        }

        string tags = string.Join("; ", new[] { CaveCarve, RivuletBound }.Where(d => d.Applies).Select(d => d.Tag));
        Assert.True(otherLayers.Count == 0 && moved.OrderBy(c => c).SequenceEqual(expectedMoved.OrderBy(c => c)),
            $"{tags}: bug shape changed on {Describe()}: {what} moved in the rock layer of [{Format(moved)}] where [{Format(expectedMoved)}] is known, " +
            $"and height map, cave air or deposit rocks differ in [{string.Join(", ", otherLayers)}]; narrow or drop the exemption");
    }

    /// <summary>Capture mode writes the rows (vanilla only) and fails by design, compare mode hands the golden text to the check.</summary>
    private static void CaptureOrCompare(string fileName, string rows, Action<string> compare)
    {
        string? captureDirectory = Environment.GetEnvironmentVariable(CaptureVariable);
        if (!string.IsNullOrEmpty(captureDirectory))
        {
            Assert.False(ServerFlavor.IsStratum, "the worldgen goldens are captured from the vanilla leg only, never from Stratum; setup is invalid");
            string full = Path.Combine(Path.GetFullPath(captureDirectory), fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, rows);
            Assert.Fail($"{fileName} captured to {full}: commit it as fixtures/worldgenterrain-goldens/{fileName} " +
                $"and rerun without {CaptureVariable} (a capture run compares nothing, so it is never green)");
        }

        compare(ReadGolden(fileName));
    }

    /// <summary>The text of a committed golden file, failing as "setup is invalid" when it is missing.</summary>
    private static string ReadGolden(string fileName)
    {
        // Next to the scenario assembly: under Atlas AppContext.BaseDirectory is the game install, not the test output.
        string path = Path.Combine(Path.GetDirectoryName(typeof(WorldgenTerrainScenarios).Assembly.Location)!,
            "fixtures", "worldgenterrain-goldens", fileName);
        Assert.True(File.Exists(path),
            $"no golden at {path}: capture it from a vanilla run with {CaptureVariable}=<directory> and commit it as " +
            $"fixtures/worldgenterrain-goldens/{fileName}; setup is invalid");
        return File.ReadAllText(path);
    }

    private static bool HasAnyChunk(IWorldSession world, int cx, int cz)
    {
        int count = world.Api.WorldManager.MapSizeY / ChunkSize;
        for (int cy = 0; cy < count; cy++)
        {
            if (world.Api.World.BlockAccessor.GetChunk(cx, cy, cz) != null)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasEveryChunk(IWorldSession world, int cx, int cz)
    {
        int count = world.Api.WorldManager.MapSizeY / ChunkSize;
        for (int cy = 0; cy < count; cy++)
        {
            if (world.Api.World.BlockAccessor.GetChunk(cx, cy, cz) == null)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Polls once per tick and answers whether the condition held within the bound, so a failure can say more than a timeout.</summary>
    private static async Task<bool> WaitFor(IWorldSession world, Func<bool> condition, int timeoutTicks)
    {
        for (int tick = 0; tick < timeoutTicks; tick++)
        {
            if (condition())
            {
                return true;
            }

            await world.Ticks(1);
        }

        return condition();
    }

    /// <summary>Peeks every column up to the pass in one go and waits for all of them (a peek never loads its column).</summary>
    private static async Task<Dictionary<(int, int), IServerChunk[]>> PeekAll(IWorldSession world, IReadOnlyList<(int Cx, int Cz)> columns, EnumWorldGenPass pass, int timeoutTicks)
    {
        var peeked = new Dictionary<(int, int), IServerChunk[]>();
        foreach ((int cx, int cz) in columns)
        {
            world.Api.WorldManager.PeekChunkColumn(cx, cz, new ChunkPeekOptions
            {
                UntilPass = pass,
                OnGenerated = generated =>
                {
                    foreach (var column in generated)
                    {
                        if (column.Key.X == cx && column.Key.Y == cz)
                        {
                            peeked[(cx, cz)] = column.Value;
                        }
                    }
                },
            });
        }

        bool done = await WaitFor(world, () => peeked.Count == columns.Count, timeoutTicks);
        Assert.True(done, $"only {peeked.Count} of {columns.Count} peeks up to {pass} came back within {timeoutTicks} ticks on {Describe()}");
        return peeked;
    }

    private static string Show(IEnumerable<RivuletCell> cells) => string.Join(" ", cells.Take(8));

    private static object Pregen()
    {
        Type? runtime = Type.GetType("Vintagestory.Server.StratumRuntime, VintagestoryLib");
        const BindingFlags any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        object? pregen = runtime?.GetProperty("Pregen", any)?.GetValue(null) ?? runtime?.GetField("Pregen", any)?.GetValue(null);
        Assert.True(pregen != null, $"StratumRuntime.Pregen was not found by reflection on {Describe()}; setup is invalid");
        return pregen!;
    }

    private static string PregenStatus(object pregen)
    {
        PropertyInfo? status = pregen.GetType().GetProperty("ShortStatus", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.True(status != null, $"StratumPregenManager.ShortStatus was not found by reflection on {Describe()}; setup is invalid");
        return (string)status!.GetValue(pregen)!;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The golden rectangle, generated to Done and kept loaded until disposed (released through UnloadChunkColumn).</summary>
    private sealed class GoldenRect : IAsyncDisposable
    {
        private IWorldSession? world;

        private GoldenRect(IWorldSession world) => this.world = world;

        public IEnumerable<(int Cx, int Cz)> Inner => WorldgenArea.Inner(RectX, RectZ, RectEdge, RectEdge);

        public static async Task<GoldenRect> Open(IWorldSession world)
        {
            Prepare(world);
            await WorldgenArea.LoadDone(world, RectX, RectZ, RectEdge, RectEdge);
            var rect = new GoldenRect(world);
            try
            {
                // Structures are placed by order dependent passes and rivulets avoid them: the rectangle must have none.
                List<string> structures = WorldgenArea.Structures(world, RectX, RectZ, RectEdge, RectEdge);
                Assert.True(structures.Count == 0,
                    $"the golden rectangle has generated structures on {ServerFlavor.Name} ({string.Join(", ", structures)}); setup is invalid");
            }
            catch
            {
                await rect.DisposeAsync();
                throw;
            }

            return rect;
        }

        public ValueTask DisposeAsync()
        {
            if (world != null)
            {
                WorldgenArea.Unload(world, RectX, RectZ, RectEdge, RectEdge);
                world = null;
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A stone block as the rock it stands for: ore and cracked rock fold back into their host rock
    /// (their last code part), as in <see cref="WorldgenDigest"/>, everything else is not stone.</summary>
    private sealed class StoneFold
    {
        private readonly IList<Block> blocks;
        private readonly string?[] byId;
        private readonly bool[] known;

        public StoneFold(IWorldSession world)
        {
            blocks = world.Api.World.Blocks;
            byId = new string?[blocks.Count];
            known = new bool[blocks.Count];
        }

        public string? Of(int id)
        {
            if (known[id])
            {
                return byId[id];
            }

            string path = blocks[id].Code.Path;
            string? rock = null;
            if (path.StartsWith("rock-", StringComparison.Ordinal))
            {
                rock = path;
            }
            else if (path.StartsWith("ore-", StringComparison.Ordinal) || path.StartsWith("crackedrock-", StringComparison.Ordinal))
            {
                rock = "rock-" + path.Substring(path.LastIndexOf('-') + 1);
            }

            known[id] = true;
            return byId[id] = rock;
        }
    }

    private readonly record struct QueueCounts(int Requested, int Flagged, int Generating)
    {
        public bool AtMost(QueueCounts limit) =>
            Requested <= limit.Requested && Flagged <= limit.Flagged && Generating <= limit.Generating;

        public override string ToString() => $"requestedChunkColumns={Requested} ChunkColumnRequested={Flagged} chunkThread.requestedChunkColumns={Generating}";
    }

    /// <summary>The three request queues of the server, by reflection: the engine's own list of requested columns, the table of
    /// columns flagged as requested, and the chunk thread's queue of columns being generated.</summary>
    private sealed class ServerQueues
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly object requestedLock;
        private readonly UniqueQueue<long> requested;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<long, int> flagged;
        private readonly object generating;
        private readonly PropertyInfo generatingCount;
        private readonly MethodInfo generatingByIndex;

        private ServerQueues(object requestedLock, UniqueQueue<long> requested, System.Collections.Concurrent.ConcurrentDictionary<long, int> flagged,
            object generating, PropertyInfo generatingCount, MethodInfo generatingByIndex)
        {
            this.requestedLock = requestedLock;
            this.requested = requested;
            this.flagged = flagged;
            this.generating = generating;
            this.generatingCount = generatingCount;
            this.generatingByIndex = generatingByIndex;
        }

        public static ServerQueues Read(IWorldSession world)
        {
            object server = world.Api.World;
            Type type = server.GetType();
            object? requestedLock = type.GetField("requestedChunkColumnsLock", Any)?.GetValue(server);
            object? requested = type.GetField("requestedChunkColumns", Any)?.GetValue(server);
            object? flagged = type.GetField("ChunkColumnRequested", Any)?.GetValue(server);
            object? thread = type.GetField("chunkThread", Any)?.GetValue(server);
            Assert.True(requestedLock != null && requested is UniqueQueue<long> && flagged is System.Collections.Concurrent.ConcurrentDictionary<long, int> && thread != null,
                $"ServerMain.requestedChunkColumnsLock, requestedChunkColumns, ChunkColumnRequested or chunkThread was not found by reflection on {Describe()}; setup is invalid");

            object? generating = thread!.GetType().GetField("requestedChunkColumns", Any)?.GetValue(thread);
            PropertyInfo? count = generating?.GetType().GetProperty("Count", Any);
            MethodInfo? byIndex = generating?.GetType().GetMethod("GetByIndex", Any, null, new[] { typeof(long) }, null);
            Assert.True(generating != null && count != null && byIndex != null,
                $"ChunkServerThread.requestedChunkColumns (Count, GetByIndex) was not found by reflection on {Describe()}; setup is invalid");

            return new ServerQueues(requestedLock!, (UniqueQueue<long>)requested!, (System.Collections.Concurrent.ConcurrentDictionary<long, int>)flagged!,
                generating!, count!, byIndex!);
        }

        public QueueCounts Snapshot()
        {
            int pending;
            lock (requestedLock)
            {
                pending = requested.Count;
            }

            return new QueueCounts(pending, flagged.Count, (int)generatingCount.GetValue(generating)!);
        }

        /// <summary>The requested columns (as map chunk indices) that are still in one of the three queues, described for a message.</summary>
        public List<string> QueuedAmong(HashSet<long> indices)
        {
            var found = new List<string>();
            lock (requestedLock)
            {
                found.AddRange(indices.Where(requested.Contains).Select(i => $"{i} in requestedChunkColumns"));
            }

            found.AddRange(indices.Where(flagged.ContainsKey).Select(i => $"{i} in ChunkColumnRequested"));
            found.AddRange(indices.Where(i => generatingByIndex.Invoke(generating, new object[] { i }) != null).Select(i => $"{i} in chunkThread.requestedChunkColumns"));
            return found;
        }
    }
}

/// <summary>
/// The golden files of <see cref="WorldgenTerrainScenarios"/>, fields separated by a bar. Ores, one row per
/// column: <c>cx|cz|count|hash|code=count;code=count</c>. Rivulets, one row per water source: <c>x|y|z</c>. World free,
/// so the format and the mismatch texts are unit tested (<see cref="WorldgenTerrainGoldenFileTests"/>).
/// </summary>
internal static class WorldgenTerrainGoldenFiles
{
    internal static string FormatOres(IEnumerable<OreDigest> digests)
    {
        var text = new StringBuilder();
        foreach (OreDigest d in digests)
        {
            text.Append(CultureInfo.InvariantCulture, $"{d.ChunkX}|{d.ChunkZ}|{d.Count}|").Append(d.Hash).Append('|').Append(d.PerOre).Append('\n');
        }

        return text.ToString();
    }

    internal static List<OreDigest> ParseOres(string text) =>
        Rows(text, 5, "ore").Select(f => new OreDigest(Int(f[0]), Int(f[1]), f[3], Int(f[2]), f[4])).ToList();

    internal static string FormatRivulets(IEnumerable<RivuletCell> cells)
    {
        var text = new StringBuilder();
        foreach (RivuletCell c in cells.OrderBy(c => c.X).ThenBy(c => c.Y).ThenBy(c => c.Z))
        {
            text.Append(CultureInfo.InvariantCulture, $"{c.X}|{c.Y}|{c.Z}\n");
        }

        return text.ToString();
    }

    internal static List<RivuletCell> ParseRivulets(string text) =>
        Rows(text, 3, "rivulet").Select(f => new RivuletCell(Int(f[0]), Int(f[1]), Int(f[2]))).ToList();

    /// <summary>One message per column whose ore blocks differ from the golden, empty when they agree.</summary>
    internal static List<string> CompareOres(IReadOnlyList<OreDigest> golden, IReadOnlyList<OreDigest> actual)
    {
        var found = new List<string>();
        if (golden.Count != actual.Count)
        {
            found.Add($"measured {actual.Count} columns, the golden has {golden.Count}; setup is invalid");
            return found;
        }

        for (int i = 0; i < actual.Count; i++)
        {
            OreDigest want = golden[i];
            OreDigest got = actual[i];
            if (got.ChunkX != want.ChunkX || got.ChunkZ != want.ChunkZ)
            {
                found.Add($"column {i} is ({got.ChunkX},{got.ChunkZ}), the golden expects ({want.ChunkX},{want.ChunkZ}); setup is invalid");
            }
            else if (got.Hash != want.Hash || got.Count != want.Count)
            {
                found.Add($"column ({got.ChunkX},{got.ChunkZ}): {got.Count} ore blocks hash {got.Hash[..12]}, golden {want.Count} hash {want.Hash[..12]}; " +
                    $"per ore [{got.PerOre}] golden [{want.PerOre}]");
            }
        }

        return found;
    }

    /// <summary>Percent of the golden's ore blocks in the whole rectangle that Stratum's count may differ by (measured +1.8%: 17431
    /// against 17115 on 1.22.7-stratum.2 and its prerelease, so about five times the gap). A lost or doubled deposit moves it far more.</summary>
    internal const int OreTotalBandPercent = 10;

    /// <summary>Percent of the golden's ore blocks in one column that Stratum's count may differ by (measured between -13.5% and +19.8% over
    /// the 16 columns of the rectangle, +4.3% in the peeked one: every disc has its edge redrawn, so no column keeps the exact count). One and a half
    /// times the widest gap measured; a missing or doubled disc deposit is outside it.</summary>
    internal const int OreColumnBandPercent = 30;

    /// <summary>The ways the ore counts of a measurement leave the bands around the vanilla golden, empty when they hold: the total of the
    /// rectangle, each column's count, and the most plentiful ore of each column. No block or hash is compared, which is the documented
    /// difference of Stratum's DiscGenerator. Both lists must be of the same columns, in the same order (<see cref="CompareOres"/> checks that).</summary>
    internal static List<string> OreCountsOutsideBand(IReadOnlyList<OreDigest> golden, IReadOnlyList<OreDigest> actual)
    {
        var found = new List<string>();
        int wantTotal = golden.Sum(d => d.Count);
        int gotTotal = actual.Sum(d => d.Count);
        if (!Within(gotTotal, wantTotal, OreTotalBandPercent))
        {
            found.Add($"{gotTotal} ore blocks in all, the golden has {wantTotal} (band {OreTotalBandPercent}%)");
        }

        for (int i = 0; i < actual.Count; i++)
        {
            found.AddRange(OreColumnOutsideBand(golden[i], actual[i]));
        }

        return found;
    }

    /// <summary>The ways one column's ore count leaves the band around the vanilla golden's, empty when it holds.</summary>
    internal static List<string> OreColumnOutsideBand(OreDigest golden, OreDigest actual)
    {
        var found = new List<string>();
        if (!Within(actual.Count, golden.Count, OreColumnBandPercent))
        {
            found.Add($"column ({actual.ChunkX},{actual.ChunkZ}): {actual.Count} ore blocks, the golden has {golden.Count} (band {OreColumnBandPercent}%)");
        }

        string wantTop = MostPlentiful(golden);
        string gotTop = MostPlentiful(actual);
        if (gotTop != wantTop)
        {
            found.Add($"column ({actual.ChunkX},{actual.ChunkZ}): the most plentiful ore is {gotTop}, the golden's is {wantTop}");
        }

        return found;
    }

    private static bool Within(int got, int want, int percent) => Math.Abs((long)got - want) * 100 <= (long)want * percent;

    private static string MostPlentiful(OreDigest digest) =>
        digest.PerOre.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('='))
            .OrderByDescending(p => Int(p[1]))
            .ThenBy(p => p[0], StringComparer.Ordinal)
            .Select(p => p[0])
            .FirstOrDefault() ?? "none";

    /// <summary>The golden sources that the measurement lacks and the measured ones that the golden lacks; both empty when they agree.</summary>
    internal static (List<RivuletCell> Missing, List<RivuletCell> Extra) CompareRivulets(IEnumerable<RivuletCell> golden, IEnumerable<RivuletCell> actual)
    {
        var want = golden.ToHashSet();
        var got = actual.ToHashSet();
        return (want.Where(c => !got.Contains(c)).OrderBy(c => c.X).ThenBy(c => c.Y).ThenBy(c => c.Z).ToList(),
                got.Where(c => !want.Contains(c)).OrderBy(c => c.X).ThenBy(c => c.Y).ThenBy(c => c.Z).ToList());
    }

    private static IEnumerable<string[]> Rows(string text, int fields, string what)
    {
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] row = line.Split('|');
            if (row.Length != fields)
            {
                throw new FormatException($"{what} golden row has {row.Length} fields, expected {fields}: {line}");
            }

            yield return row;
        }
    }

    private static int Int(string value) => int.Parse(value, CultureInfo.InvariantCulture);
}

/// <summary>World-free checks of the golden file format and of the mismatch texts, so a mistyped row cannot turn
/// into a comparison against the wrong column.</summary>
public class WorldgenTerrainGoldenFileTests
{
    private static readonly string Hash = new string('a', 64);

    [Fact]
    public void Ores_Should_RoundTripThroughTheFileFormat_When_Formatted()
    {
        var digests = new[]
        {
            new OreDigest(16047, 16047, Hash, 12, "ore-quartz-granite=7;ore-copper-basalt=5"),
            new OreDigest(16047, 16048, new string('b', 64), 0, string.Empty),
        };

        List<OreDigest> parsed = WorldgenTerrainGoldenFiles.ParseOres(WorldgenTerrainGoldenFiles.FormatOres(digests));

        Assert.Equal(digests, parsed);
    }

    [Fact]
    public void Rivulets_Should_RoundTripThroughTheFileFormat_When_Formatted()
    {
        var cells = new[] { new RivuletCell(515241, 64, 516532), new RivuletCell(515241, 80, 516532), new RivuletCell(515250, 12, 516617) };

        List<RivuletCell> parsed = WorldgenTerrainGoldenFiles.ParseRivulets(WorldgenTerrainGoldenFiles.FormatRivulets(cells.Reverse()));

        Assert.Equal(cells, parsed);
    }

    [Fact]
    public void Format_Should_UseTheInvariantCulture_When_TheCurrentCultureWritesAnotherMinusSign()
    {
        CultureInfo saved = CultureInfo.CurrentCulture;
        var other = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        other.NumberFormat.NegativeSign = "\u2212";
        try
        {
            CultureInfo.CurrentCulture = other;
            Assert.Equal("-5|64|-7\n", WorldgenTerrainGoldenFiles.FormatRivulets(new[] { new RivuletCell(-5, 64, -7) }));
            Assert.StartsWith("-1|-2|3|", WorldgenTerrainGoldenFiles.FormatOres(new[] { new OreDigest(-1, -2, Hash, 3, string.Empty) }));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void CompareOres_Should_NameTheColumn_When_OneHashDiffers()
    {
        var golden = new[] { new OreDigest(1, 2, Hash, 4, "ore-a-b=4"), new OreDigest(1, 3, Hash, 5, "ore-a-b=5") };
        var actual = new[] { golden[0], golden[1] with { Hash = new string('d', 64), Count = 6 } };

        List<string> found = WorldgenTerrainGoldenFiles.CompareOres(golden, actual);

        string message = Assert.Single(found);
        Assert.Contains("column (1,3)", message);
        Assert.Contains("6 ore blocks", message);
        Assert.Empty(WorldgenTerrainGoldenFiles.CompareOres(golden, golden));
    }

    [Fact]
    public void OreCountsOutsideBand_Should_AcceptOtherBlocksAtComparableCounts_When_TheDepositsAreTheSame()
    {
        // Other hashes and counts moved by -13.5% and +19.8%, rare ores swapped, the total up by 2%: what Stratum's DiscGenerator does.
        var golden = new[]
        {
            new OreDigest(1, 2, Hash, 1447, "ore-quartz-granite=1447"),
            new OreDigest(1, 3, Hash, 354, "ore-olivine-peridotite=259;ore-rich-cassiterite-granite=60;ore-flint-sandstone=35"),
        };
        var actual = new[]
        {
            new OreDigest(1, 2, new string('d', 64), 1252, "ore-quartz-granite=1252"),
            new OreDigest(1, 3, new string('e', 64), 424, "ore-olivine-peridotite=326;ore-rich-cassiterite-granite=63;ore-poor-quartz_nativegold-granite=7"),
        };

        Assert.Empty(WorldgenTerrainGoldenFiles.OreCountsOutsideBand(golden, actual));
    }

    [Fact]
    public void OreCountsOutsideBand_Should_NameTheColumn_When_TheCountOrTheMostPlentifulOreMoves()
    {
        var golden = new[]
        {
            new OreDigest(1, 2, Hash, 1000, "ore-quartz-granite=900;ore-lignite-sandstone=100"),
            new OreDigest(1, 3, Hash, 1000, "ore-quartz-granite=900;ore-lignite-sandstone=100"),
            new OreDigest(1, 4, Hash, 1000, "ore-quartz-granite=900;ore-lignite-sandstone=100"),
        };
        var actual = new[]
        {
            golden[0] with { Hash = new string('d', 64), Count = 600, PerOre = "ore-quartz-granite=500;ore-lignite-sandstone=100" },
            golden[1] with { Hash = new string('e', 64), PerOre = "ore-lignite-sandstone=900;ore-quartz-granite=100" },
            golden[2],
        };

        List<string> found = WorldgenTerrainGoldenFiles.OreCountsOutsideBand(golden, actual);

        Assert.Equal(3, found.Count);
        Assert.Contains(found, m => m.StartsWith("column (1,2): 600 ore blocks", StringComparison.Ordinal));
        Assert.Contains(found, m => m.StartsWith("column (1,3): the most plentiful ore is ore-lignite-sandstone", StringComparison.Ordinal));
        Assert.Contains(found, m => m.StartsWith("2600 ore blocks in all, the golden has 3000", StringComparison.Ordinal));
    }

    [Fact]
    public void CompareRivulets_Should_NameMissingAndExtraCells_When_SetsDiffer()
    {
        var golden = new[] { new RivuletCell(1, 2, 3), new RivuletCell(4, 5, 6) };
        var actual = new[] { new RivuletCell(4, 5, 6), new RivuletCell(7, 8, 9) };

        (List<RivuletCell> missing, List<RivuletCell> extra) = WorldgenTerrainGoldenFiles.CompareRivulets(golden, actual);

        Assert.Equal(new[] { new RivuletCell(1, 2, 3) }, missing);
        Assert.Equal(new[] { new RivuletCell(7, 8, 9) }, extra);
        (missing, extra) = WorldgenTerrainGoldenFiles.CompareRivulets(golden, golden);
        Assert.Empty(missing);
        Assert.Empty(extra);
    }

    [Fact]
    public void ParseOres_Should_Reject_When_RowHasTheWrongFieldCount()
    {
        Assert.Throws<FormatException>(() => WorldgenTerrainGoldenFiles.ParseOres("1|2|3\n"));
        Assert.Throws<FormatException>(() => WorldgenTerrainGoldenFiles.ParseRivulets("1|2\n"));
    }
}
