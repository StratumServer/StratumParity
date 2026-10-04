using System.Globalization;
using System.Reflection;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Xunit;
using Xunit.Abstractions;

namespace StratumParity.Scenarios;

/// <summary>
/// Contract checks on the climate and map region queries Stratum optimised with caches and
/// shared scratch objects (ServerWorldMap.cs.patch, WorldAPI.cs.patch). Vanilla returns a
/// fresh object computed from scratch on every call; the Stratum build keeps a
/// [ThreadStatic] scratch ClimateCondition for the full modes and for the int overload, a
/// permanent per-thread cache for the two ForSuppliedDate_Temperature* modes (keyed by chunk
/// column and Y, so the key ignores the block column and wraps every 1024 chunks), and one
/// AllLoadedMapRegions dictionary per server tick. Four scenarios assert the vanilla contract
/// on both flavors, so a divergence reads as a Stratum regression; the fifth is a probe of a
/// deliberate trade-off (see the map region scenario below).
///
/// All queries run synchronously on the game thread, the thread whose [ThreadStatic] state the
/// Stratum caches use, so two reads without an await in between share one tick and one
/// thread. Nothing here needs a player or a tick wait except the far column load of the
/// cache key wrap check.
///
/// The cache is permanent per thread and the scenarios of a class share one world in no fixed
/// order, so each cache-reading scenario owns a Y value that no other scenario and no engine
/// system reads (the entity spawner normalises to about sea level, so 240 to 250 is untouched):
/// 250 for the three-queries scenario, 240 for the sampled chunks and 241 for the far pair.
/// On Stratum a reflected guard also checks the key is cold before the first query, because a
/// warm key makes later calls agree with each other and would hide the bug.
///
/// The first four scenarios are bug-first: each Stratum behaviour was confirmed on
/// 1.22.7-stratum.2 and 1.22.7-stratum.2-indev.1 (three and one runs, identical values), and the
/// two patches are byte-identical on upstream/indev. On those two builds the Stratum branch
/// asserts the observed bug shape (a silent fix turns it red); everywhere else it is strict
/// parity with the vanilla contract. Delete an entry when the pinned tag moves past its fix.
/// </summary>
public class ClimateQueryScenarios : AtlasScenarioBase
{
    private const int ChunkEdge = 32;
    private const int FullModeYLow = 100;
    private const int FullModeYHigh = 200;
    private const int ThreeQueriesY = 250;
    private const int SampledChunksY = 240;
    private const int FarPairY = 241;
    private const int SampledChunks = 32;
    private const int CacheWrapChunks = 1024;
    private const float MinWorldgenRainfall = 0.3f;
    private const float MinRainfallRewrite = 0.05f;

    // The Stratum cache key packs chunkX (10 bits), chunkZ (10 bits) and Y (9 bits); the
    // field is private and thread static, so a missing member means the build has no cache
    // and nothing can be warm.
    private static readonly FieldInfo? StratumClimateCache = ServerFlavor.IsStratum
        ? Type.GetType("Vintagestory.Server.ServerWorldMap, VintagestoryLib")?
            .GetField("stratumClimateCache", BindingFlags.NonPublic | BindingFlags.Static)
        : null;

    // Where each one comes from in the Stratum history: the shared scratch is PR #110 and the
    // permanent cache (miss path and key) is the rev 10 patch set. The issues are filed on
    // StratumServer/Stratum.
    private static readonly KnownDivergence FullModeScratch = new("StratumServer/Stratum#360", StratumBuild.Stable2, StratumBuild.Indev1);
    private static readonly KnownDivergence IntOverloadScratch = new("StratumServer/Stratum#360", StratumBuild.Stable2, StratumBuild.Indev1);
    private static readonly KnownDivergence CacheMissAliasing = new("StratumServer/Stratum#361", StratumBuild.Stable2, StratumBuild.Indev1);
    private static readonly KnownDivergence CacheChunkKey = new("StratumServer/Stratum#362", StratumBuild.Stable2, StratumBuild.Indev1);

    private readonly ITestOutputHelper output;

    public ClimateQueryScenarios(ITestOutputHelper output) => this.output = output;

    [AtlasScenario]
    public Task FullModeClimate_Should_ReturnIndependentObjects_When_QueriedTwice()
    {
        SpawnRegion region = GetSpawnRegion();
        BlockPos first = region.At(100, FullModeYLow, 100);
        BlockPos second = region.At(400, FullModeYHigh, 380);
        double days = SuppliedDays();
        var problems = new List<string>();
        int sharedModes = 0;

        foreach (EnumGetClimateMode mode in new[]
        {
            EnumGetClimateMode.NowValues,
            EnumGetClimateMode.WorldGenValues,
            EnumGetClimateMode.ForSuppliedDateValues,
        })
        {
            // No await between the two reads: same tick, same thread.
            ClimateCondition a = Query(first, mode, days);
            ClimateSnapshot firstValues = ClimateSnapshot.Of(a);
            ClimateCondition b = Query(second, mode, days);
            ClimateSnapshot secondValues = ClimateSnapshot.Of(b);

            // The positions differ in column and altitude, so a result that was overwritten
            // by the second query cannot pass as unchanged by coincidence.
            Assert.True(firstValues != secondValues,
                $"both positions returned identical {mode} climate on {ServerFlavor.Name}; setup is invalid");

            bool same = ReferenceEquals(a, b);
            string drift = firstValues.Diff(ClimateSnapshot.Of(a));
            output.WriteLine($"{mode}: same object {same}; temperature of the first result {Format(firstValues.Temperature)} " +
                $"then {Format(a.Temperature)} after the second query (second position {Format(secondValues.Temperature)})");
            if (same)
            {
                problems.Add($"{mode}: the second query returned the very object of the first");
            }

            if (drift.Length > 0)
            {
                problems.Add($"{mode}: the first result changed after the second query ({drift})");
            }

            if (same && drift.Length > 0)
            {
                sharedModes++;
            }
        }

        if (FullModeScratch.Applies)
        {
            Assert.True(sharedModes == 3,
                $"{FullModeScratch.Tag}: bug shape changed, {sharedModes} of 3 modes still hand out one shared object " +
                $"that the next query overwrites; narrow or drop the exemption ({string.Join("; ", problems)})");
            return Task.CompletedTask;
        }

        Assert.True(problems.Count == 0,
            $"full-mode climate objects are not independent on {ServerFlavor.Name}: {string.Join("; ", problems)}");
        return Task.CompletedTask;
    }

    [AtlasScenario]
    public Task ClimateIntOverload_Should_IgnorePreviousQueries_When_CalledAfterAnotherPosition()
    {
        SpawnRegion region = GetSpawnRegion();
        BlockPos target = region.At(200, 120, 200);
        BlockPos unrelatedA = region.At(60, FullModeYLow, 60);
        BlockPos unrelatedB = region.At(420, FullModeYHigh, 380);
        // Derived the way the engine derives it for a full query (ServerWorldMap
        // getWorldGenClimateAt): the bilinear colour of the region climate map at the position.
        int climateInt = region.ClimateIntAt(target);

        // Synchronous block: the calendar only moves on a tick, so NowValues is the same
        // instant for every call below.
        ClimateSnapshot alone = ClimateSnapshot.Of(QueryInt(target, climateInt));

        ClimateSnapshot unrelatedAValues = ClimateSnapshot.Of(Query(unrelatedA, EnumGetClimateMode.NowValues, 0));
        ClimateSnapshot afterA = ClimateSnapshot.Of(QueryInt(target, climateInt));

        ClimateSnapshot unrelatedBValues = ClimateSnapshot.Of(Query(unrelatedB, EnumGetClimateMode.NowValues, 0));
        ClimateSnapshot afterB = ClimateSnapshot.Of(QueryInt(target, climateInt));

        // A full-mode query that leaves nothing different behind would let a leak pass.
        Assert.True(unrelatedAValues.WorldGenTemperature != unrelatedBValues.WorldGenTemperature,
            $"the two unrelated queries returned the same world gen temperature on {ServerFlavor.Name}; setup is invalid");

        string aloneVsA = alone.Diff(afterA);
        string aVsB = afterA.Diff(afterB);
        output.WriteLine($"int overload at {target}: alone T={Format(alone.Temperature)} WGR={Format(alone.WorldgenRainfall)} WGT={Format(alone.WorldGenTemperature)} | " +
            $"after A T={Format(afterA.Temperature)} WGR={Format(afterA.WorldgenRainfall)} WGT={Format(afterA.WorldGenTemperature)} | " +
            $"after B T={Format(afterB.Temperature)} WGR={Format(afterB.WorldgenRainfall)} WGT={Format(afterB.WorldGenTemperature)}");

        if (IntOverloadScratch.Applies)
        {
            // The alone value depends on what ran before in the shared world, but A and B were
            // guaranteed to differ, so the leak always shows between the two later calls.
            Assert.True(aVsB.Length > 0,
                $"{IntOverloadScratch.Tag}: bug shape gone, the int overload no longer carries values of the previous query; drop the exemption");
            return Task.CompletedTask;
        }

        Assert.True(aloneVsA.Length == 0 && aVsB.Length == 0,
            $"the int overload depends on previous queries on {ServerFlavor.Name}: " +
            $"alone vs after query A ({Describe(aloneVsA)}); after query A vs after query B ({Describe(aVsB)})");
        return Task.CompletedTask;
    }

    [AtlasScenario]
    public Task TemperatureRainfallOnly_Should_ReturnStableValues_When_QueriedThreeTimes()
    {
        SpawnRegion region = GetSpawnRegion();
        // At 16:00 the diurnal term of the temperature handler is at its maximum, so any
        // change of the rainfall that handler reads moves the temperature too.
        double days = SuppliedDays();
        IBlockAccessor accessor = World.Api.World.BlockAccessor;

        // A column where the rewrite of the rainfall is observable: worldgen rainfall above 0.3
        // and a handler result that moves it by more than 0.05 (computed through the baseClimate
        // overload, which never touches the cache). The world gen mode and that overload both
        // bypass the cache, so the search leaves every key cold. OverridePrecipitation is null in
        // a fresh world and nothing here sets it.
        int chunksPerRegion = region.ChunksPerRegion;
        BlockPos? chosen = null;
        for (int cz = 0; cz < chunksPerRegion && chosen == null; cz++)
        {
            for (int cx = 0; cx < chunksPerRegion; cx++)
            {
                BlockPos candidate = region.At(cx * ChunkEdge + ChunkEdge / 2, ThreeQueriesY, cz * ChunkEdge + ChunkEdge / 2);
                ClimateCondition worldGen = CopyOf(Query(candidate, EnumGetClimateMode.WorldGenValues, days));
                if (worldGen.WorldgenRainfall <= MinWorldgenRainfall)
                {
                    continue;
                }

                ClimateCondition expected = accessor.GetClimateAt(
                    candidate, CopyOf(worldGen), EnumGetClimateMode.ForSuppliedDate_TemperatureRainfallOnly, days);
                if (Math.Abs(expected.Rainfall - worldGen.WorldgenRainfall) < MinRainfallRewrite)
                {
                    continue;
                }

                chosen = candidate;
                break;
            }
        }

        Assert.True(chosen != null,
            $"no column of the spawn region has worldgen rainfall above {MinWorldgenRainfall} at Y={ThreeQueriesY} " +
            $"with an observable rainfall rewrite on {ServerFlavor.Name}; setup is invalid " +
            "(move this class to WorldType standard if superflat never offers one)");
        BlockPos pos = chosen!;
        AssertCacheKeyCold(pos);

        const EnumGetClimateMode mode = EnumGetClimateMode.ForSuppliedDate_TemperatureRainfallOnly;
        ClimateSnapshot[] calls =
        {
            ClimateSnapshot.Of(Query(pos, mode, days)),
            ClimateSnapshot.Of(Query(pos, mode, days)),
            ClimateSnapshot.Of(Query(pos, mode, days)),
        };
        output.WriteLine($"column {pos}: " + string.Join(" | ", calls.Select((c, i) =>
            $"call {i + 1}: T={Format(c.Temperature)} R={Format(c.Rainfall)}")));

        string second = calls[0].Diff(calls[1], nameof(ClimateSnapshot.Temperature), nameof(ClimateSnapshot.Rainfall));
        string third = calls[0].Diff(calls[2], nameof(ClimateSnapshot.Temperature), nameof(ClimateSnapshot.Rainfall));
        if (CacheMissAliasing.Applies)
        {
            // Call 1 is the miss that hands out the cache entry the handlers then rewrite.
            Assert.True(second.Length > 0,
                $"{CacheMissAliasing.Tag}: bug shape gone, the first and second query at {pos} agree; drop the exemption");
            return Task.CompletedTask;
        }

        Assert.True(second.Length == 0 && third.Length == 0,
            $"temperature and rainfall changed between identical queries at {pos} on {ServerFlavor.Name}: " +
            $"call 1 vs call 2 ({Describe(second)}); call 1 vs call 3 ({Describe(third)})");
        return Task.CompletedTask;
    }

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task TemperatureOnly_Should_MatchFreshComputation_When_ColumnsShareAChunk()
    {
        SpawnRegion region = GetSpawnRegion();
        double days = SuppliedDays();
        const EnumGetClimateMode mode = EnumGetClimateMode.ForSuppliedDate_TemperatureOnly;
        int chunksPerRegion = region.ChunksPerRegion;
        var sameChunkMismatches = new List<string>();
        var wrapMismatches = new List<string>();

        // The two corner columns of a chunk are as far apart as two columns of one chunk get,
        // which makes the world gen values differ more often than two columns on one row would.
        var sampled = new HashSet<(int X, int Z)>();
        int differingSamples = 0;
        for (int i = 0; i < SampledChunks; i++)
        {
            int chunkDx = i * 5 % chunksPerRegion;
            int chunkDz = (i * 3 + i / chunksPerRegion * (chunksPerRegion / 2) + 1) % chunksPerRegion;
            Assert.True(sampled.Add((chunkDx, chunkDz)),
                $"sample {i} repeats chunk {chunkDx}/{chunkDz} of the spawn region; setup is invalid");

            BlockPos west = region.At(chunkDx * ChunkEdge, SampledChunksY, chunkDz * ChunkEdge);
            BlockPos east = region.At(chunkDx * ChunkEdge + ChunkEdge - 1, SampledChunksY, chunkDz * ChunkEdge + ChunkEdge - 1);
            AssertCacheKeyCold(west);

            ClimateSnapshot westWorldGen = ClimateSnapshot.Of(Query(west, EnumGetClimateMode.WorldGenValues, days));
            ClimateSnapshot eastWorldGen = ClimateSnapshot.Of(Query(east, EnumGetClimateMode.WorldGenValues, days));
            if (westWorldGen.WorldGenTemperature != eastWorldGen.WorldGenTemperature
                || westWorldGen.WorldgenRainfall != eastWorldGen.WorldgenRainfall)
            {
                differingSamples++;
            }

            // The first query of a chunk fills the Stratum cache entry, the second one reads it.
            CheckAgainstOracle(west, mode, days, $"chunk {chunkDx}/{chunkDz} west column", sameChunkMismatches);
            CheckAgainstOracle(east, mode, days, $"chunk {chunkDx}/{chunkDz} east column", sameChunkMismatches);
        }

        // Without a difference between the columns of at least one chunk the cache could not
        // be told from a fresh computation.
        Assert.True(differingSamples >= 1,
            $"the world gen climate is identical across the columns of all {SampledChunks} sampled chunks " +
            $"on {ServerFlavor.Name}; setup is invalid");
        output.WriteLine($"{differingSamples}/{SampledChunks} sampled chunks have differing world gen values between their columns");

        // Two columns exactly CacheWrapChunks chunks apart share one Stratum cache key.
        int nearChunkX = region.OriginX / ChunkEdge + chunksPerRegion / 2;
        int chunkZ = region.OriginZ / ChunkEdge + chunksPerRegion / 2;
        int farChunkX = (nearChunkX + CacheWrapChunks + 1) * ChunkEdge <= World.Api.WorldManager.MapSizeX
            ? nearChunkX + CacheWrapChunks
            : nearChunkX - CacheWrapChunks;
        BlockPos near = new(nearChunkX * ChunkEdge, FarPairY, chunkZ * ChunkEdge, 0);
        BlockPos far = new(farChunkX * ChunkEdge, FarPairY, chunkZ * ChunkEdge, 0);

        // Without the far map region, vanilla answers with the 4 degrees default while the
        // Stratum cache answers with the near column's values: only a loaded region makes the
        // comparison about the cache.
        World.Api.WorldManager.LoadChunkColumnPriority(farChunkX, chunkZ);
        try
        {
            await World.Until(
                () => World.Api.WorldManager.GetMapRegion(far.X / region.RegionSize, far.Z / region.RegionSize)?.ClimateMap?.Data != null,
                timeoutTicks: 600);
        }
        catch (Atlas.Api.ScenarioTimeoutException)
        {
            Assert.Fail($"the map region of the far column {far} never loaded on {ServerFlavor.Name}; setup is invalid");
        }

        ClimateSnapshot nearWorldGen = ClimateSnapshot.Of(Query(near, EnumGetClimateMode.WorldGenValues, days));
        ClimateSnapshot farWorldGen = ClimateSnapshot.Of(Query(far, EnumGetClimateMode.WorldGenValues, days));
        Assert.True(nearWorldGen.WorldGenTemperature != farWorldGen.WorldGenTemperature
                || nearWorldGen.WorldgenRainfall != farWorldGen.WorldgenRainfall,
            $"the columns {CacheWrapChunks} chunks apart have identical world gen climate on {ServerFlavor.Name}; setup is invalid");
        AssertCacheKeyCold(near);

        CheckAgainstOracle(near, mode, days, "near column of the wrap pair", wrapMismatches);
        CheckAgainstOracle(far, mode, days, $"far column {CacheWrapChunks} chunks away", wrapMismatches);
        output.WriteLine($"wrap pair at Y={FarPairY}, world gen temperature: near {Format(nearWorldGen.WorldGenTemperature)}, " +
            $"far {Format(farWorldGen.WorldGenTemperature)}, temperature-only query of the far column {Format(Query(far, mode, days).WorldGenTemperature)}");

        output.WriteLine($"{sameChunkMismatches.Count} of {SampledChunks * 2} sampled columns and {wrapMismatches.Count} of 2 wrap pair columns " +
            "differ from a fresh computation" + (sameChunkMismatches.Count > 0 ? $"; first: {sameChunkMismatches[0]}" : string.Empty) +
            (wrapMismatches.Count > 0 ? $"; wrap: {wrapMismatches[0]}" : string.Empty));

        if (CacheChunkKey.Applies)
        {
            // Both halves of the shape: another column of a cached chunk, and the column 1024
            // chunks away, get the first column's values.
            Assert.True(sameChunkMismatches.Count >= 1 && wrapMismatches.Count >= 1,
                $"{CacheChunkKey.Tag}: bug shape changed, {sameChunkMismatches.Count} same chunk and {wrapMismatches.Count} wrap " +
                "mismatches left (both are expected); narrow or drop the exemption");
            return;
        }

        int total = sameChunkMismatches.Count + wrapMismatches.Count;
        Assert.True(total == 0,
            $"{total} temperature-only query results differ from a fresh computation on {ServerFlavor.Name}; " +
            string.Join("; ", sameChunkMismatches.Concat(wrapMismatches).Take(3)));
    }

    // A contract difference, judged not worth an issue: the vanilla API documents the property as
    // "a (cloned) list of all currently loaded map regions" and builds a fresh dictionary on every
    // read. Stratum commit 035a42d ("Cache AllLoadedMapRegions per tick",
    // patches/VintagestoryLib/Vintagestory.Server/WorldAPI.cs.patch) builds it once per server tick
    // and hands the same instance to every reader of that tick, because the weather system reads
    // the property several times per tick and each read used to copy every loaded region. A caller
    // that mutates the returned dictionary changes what the other readers of that tick see, and
    // callers read it without mutating it in practice. The scenario asserts both sides: two reads
    // in one tick are independent copies on vanilla and the very same instance on Stratum.
    [AtlasScenario]
    public Task AllLoadedMapRegions_Should_ReturnFreshCopyOnVanillaAndTheSharedSnapshotOnStratum_When_ReadTwiceInOneTick()
    {
        // No await between the reads: one tick on both flavors.
        Dictionary<long, IMapRegion> first = World.Api.WorldManager.AllLoadedMapRegions;
        Dictionary<long, IMapRegion> second = World.Api.WorldManager.AllLoadedMapRegions;
        Assert.True(first.Count >= 1 && first.Count == second.Count,
            $"AllLoadedMapRegions returned {first.Count} and {second.Count} entries on {ServerFlavor.Name}; setup is invalid");

        long removedKey = first.Keys.First();
        IMapRegion removed = first[removedKey];
        int countBefore = second.Count;
        bool sameObject = ReferenceEquals(first, second);

        first.Remove(removedKey);
        int countAfter = second.Count;
        // Put it back: a Stratum snapshot shared per tick is also what every other reader of
        // this tick (the weather system among them) would see.
        first[removedKey] = removed;

        output.WriteLine($"AllLoadedMapRegions: same object {sameObject}; second read held {countBefore} entries, {countAfter} after removing one from the first");

        if (ServerFlavor.IsStratum)
        {
            Assert.True(sameObject && countAfter == countBefore - 1,
                $"the per-tick snapshot that Stratum documents (Stratum commit 035a42d) is gone on {ServerFlavor.Name}: " +
                $"same object {sameObject}, second read held {countBefore} entries before removing one from the first and {countAfter} after");
            return Task.CompletedTask;
        }

        Assert.True(!sameObject && countAfter == countBefore,
            $"AllLoadedMapRegions is not a fresh copy on {ServerFlavor.Name}: same object {sameObject}, " +
            $"second read held {countBefore} entries before removing one from the first and {countAfter} after");
        return Task.CompletedTask;
    }

    private ClimateCondition Query(BlockPos pos, EnumGetClimateMode mode, double days)
    {
        ClimateCondition? climate = World.Api.World.BlockAccessor.GetClimateAt(pos, mode, days);
        Assert.True(climate != null, $"no {mode} climate at {pos} on {ServerFlavor.Name}; setup is invalid");
        return climate!;
    }

    private ClimateCondition QueryInt(BlockPos pos, int climateInt)
    {
        ClimateCondition? climate = World.Api.World.BlockAccessor.GetClimateAt(pos, climateInt);
        Assert.True(climate != null, $"no int overload climate at {pos} on {ServerFlavor.Name}; setup is invalid");
        return climate!;
    }

    /// <summary>Compares the direct query with the oracle (the baseClimate overload fed with a
    /// fresh copy of the world gen result of the same column, which never touches any cache) on
    /// the four fields a temperature-only query fills in.</summary>
    private void CheckAgainstOracle(BlockPos pos, EnumGetClimateMode mode, double days, string label, List<string> mismatches)
    {
        ClimateCondition worldGen = CopyOf(Query(pos, EnumGetClimateMode.WorldGenValues, days));
        ClimateSnapshot direct = ClimateSnapshot.Of(Query(pos, mode, days));
        ClimateSnapshot oracle = ClimateSnapshot.Of(
            World.Api.World.BlockAccessor.GetClimateAt(pos, CopyOf(worldGen), mode, days));

        string diff = oracle.Diff(direct,
            nameof(ClimateSnapshot.Temperature),
            nameof(ClimateSnapshot.Rainfall),
            nameof(ClimateSnapshot.WorldgenRainfall),
            nameof(ClimateSnapshot.WorldGenTemperature));
        if (diff.Length > 0)
        {
            mismatches.Add($"{label} at {pos}: fresh computation vs query ({diff})");
        }
    }

    /// <summary>Fails the setup when the Stratum cache already holds the key of a position:
    /// a warm key makes every later query agree, which would hide the bug.</summary>
    private static void AssertCacheKeyCold(BlockPos pos)
    {
        if (StratumClimateCache == null)
        {
            return;
        }

        long key = ((long)(pos.X >> 5) & 0x3FF)
            | (((long)(pos.Z >> 5) & 0x3FF) << 10)
            | (((long)pos.Y & 0x1FF) << 20);
        bool warm = StratumClimateCache.GetValue(null) is System.Collections.IDictionary cache && cache.Contains(key);
        Assert.False(warm, $"the Stratum climate cache already holds the key of {pos}; setup is invalid");
    }

    /// <summary>16:00 on the current day: the diurnal term of the temperature handler is at
    /// its maximum there (04:00 is the minimum), so it reacts to every rainfall change.</summary>
    private double SuppliedDays() =>
        Math.Floor(World.Calendar.TotalDays) + 16.0 / World.Calendar.HoursPerDay;

    private SpawnRegion GetSpawnRegion()
    {
        int regionSize = World.Api.WorldManager.RegionSize;
        BlockPos spawn = World.Spawn;
        int regionX = spawn.X / regionSize;
        int regionZ = spawn.Z / regionSize;
        IMapRegion? region = World.Api.WorldManager.GetMapRegion(regionX, regionZ);
        Assert.True(region?.ClimateMap?.Data != null && region.ClimateMap.InnerSize > 0,
            $"the spawn map region {regionX}/{regionZ} has no climate map on {ServerFlavor.Name}; setup is invalid");
        return new SpawnRegion(region!, regionX * regionSize, regionZ * regionSize, regionSize);
    }

    private static ClimateCondition CopyOf(ClimateCondition source) => new()
    {
        Temperature = source.Temperature,
        WorldgenRainfall = source.WorldgenRainfall,
        WorldGenTemperature = source.WorldGenTemperature,
        GeologicActivity = source.GeologicActivity,
        Rainfall = source.Rainfall,
        RainCloudOverlay = source.RainCloudOverlay,
        Fertility = source.Fertility,
        ForestDensity = source.ForestDensity,
        ShrubDensity = source.ShrubDensity,
        Biome = source.Biome,
    };

    private static string Describe(string diff) => diff.Length == 0 ? "identical" : diff;

    private static string Format(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The map region the world spawn sits in, which is loaded for the whole run.</summary>
    private sealed class SpawnRegion
    {
        public SpawnRegion(IMapRegion region, int originX, int originZ, int regionSize)
        {
            Region = region;
            OriginX = originX;
            OriginZ = originZ;
            RegionSize = regionSize;
        }

        public IMapRegion Region { get; }

        public int OriginX { get; }

        public int OriginZ { get; }

        public int RegionSize { get; }

        public int ChunksPerRegion => RegionSize / ChunkEdge;

        public BlockPos At(int dx, int y, int dz) => new(OriginX + dx, y, OriginZ + dz, 0);

        /// <summary>The packed climate colour at a position, derived as the engine's own full
        /// query does (the (float)((double)v % 1.0) form keeps the precision at large coordinates).</summary>
        public int ClimateIntAt(BlockPos pos)
        {
            float x = (float)((double)pos.X / RegionSize % 1.0);
            float z = (float)((double)pos.Z / RegionSize % 1.0);
            return Region.ClimateMap.GetUnpaddedColorLerpedForNormalizedPos(x, z);
        }
    }

    /// <summary>Every public value of a <see cref="ClimateCondition"/>, copied at the moment of
    /// the call so a later query cannot rewrite it through a shared object.</summary>
    private sealed record ClimateSnapshot(
        float Temperature,
        float WorldgenRainfall,
        float WorldGenTemperature,
        float GeologicActivity,
        float Rainfall,
        float RainCloudOverlay,
        float Fertility,
        float ForestDensity,
        float ShrubDensity,
        int Biome)
    {
        public static ClimateSnapshot Of(ClimateCondition c) => new(
            c.Temperature, c.WorldgenRainfall, c.WorldGenTemperature, c.GeologicActivity, c.Rainfall,
            c.RainCloudOverlay, c.Fertility, c.ForestDensity, c.ShrubDensity, c.Biome);

        /// <summary>The fields (all of them, or only the named ones) whose values differ, as
        /// "Name old -> new" entries; empty when they match.</summary>
        public string Diff(ClimateSnapshot other, params string[] only)
        {
            var parts = new List<string>();
            foreach (PropertyInfo property in typeof(ClimateSnapshot).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (only.Length > 0 && !only.Contains(property.Name))
                {
                    continue;
                }

                object? mine = property.GetValue(this);
                object? theirs = property.GetValue(other);
                if (!Equals(mine, theirs))
                {
                    parts.Add($"{property.Name} {Text(mine)} -> {Text(theirs)}");
                }
            }

            return string.Join(", ", parts);
        }

        // "R" is a floating point format: it throws for the int Biome.
        private static string Text(object? value) =>
            value is float number ? number.ToString("R", CultureInfo.InvariantCulture) : $"{value}";
    }
}
