using System.Collections;
using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.MathTools;
using Xunit;
using Xunit.Abstractions;

namespace StratumParity.Scenarios;

/// <summary>
/// Chunk read pool parity. Stratum can fan the chunk reads of a column out to a pool of read-only
/// SQLite connections (Performance.ChunkIo, off by default). The seeded fixture turns it on with
/// four workers before boot, so everything below runs on the pooled path; vanilla ignores the
/// files and reads one chunk at a time, which makes the persistence assertions the shared contract:
/// a column that was modified, saved, unloaded and loaded again carries exactly what was written,
/// in every Y level.
///
/// The risk the pool adds is a read that goes wrong silently. TryLoadChunkColumn treats a missing
/// row as an absent column and regenerates it, so a swallowed read error would put fresh terrain
/// over saved data. Each column therefore gets more than one signal: an eight block pattern,
/// chunk moddata that only the database can supply (the column's own coordinates, so rows mixed
/// up between columns show), and one marker block per chunk level (so a row read into the wrong
/// level shows). After the reload every signal is checked and the failure lists what is gone
/// instead of waiting for a timeout.
///
/// On Stratum the scenarios also prove that the pool did the reading, because a pass through the
/// sequential path would look the same. The internal StratumChunkReadPool keeps one prepared
/// statement per worker; sqlite3_stmt_status with SQLITE_STMTSTATUS_RUN counts how often each one
/// ran, which is one run per chunk row read, so the sum before and after a reload is an exact
/// counter of pool reads (reflection, every missing member fails as "setup is invalid"). The
/// assertion is exact: one read per chunk of every reloaded column, so a reload that skipped the
/// pool counts zero and one that read twice counts too many. Engine errors of the
/// read path (a failed pool read, a half loaded column, a chunk that does not deserialize) are
/// logged at Error level, so the diagnostics the host records during a scenario must hold none of them.
///
/// Scope of the 25 column scenario: it is a round trip of 200 pooled reads through four recycled
/// connection slots, not a contention test. Stage 0 loads run one column after another on the
/// chunk thread, so reads of different columns never overlap and a lease never waits.
/// </summary>
[AtlasDataFiles("fixtures/stratum-chunkio-on", TargetPath = "")]
public class ChunkReadPoolScenarios : AtlasScenarioBase
{
    private const int ChunkSize = ChunkPersistence.ChunkSize;
    private const int RectEdge = 5;

    // WorkerThreads of the fixture.
    private const int FixtureWorkers = 4;

    // Inside a column, clear of the 8 block pattern (x 0 to 7, z 0 to 15 above the anchor).
    private const int MarkerOffset = 20;
    private const int MarkerYInLevel = 28;

    // Rock types that exist on every VS 1.2x install; one per chunk level of the column.
    private static readonly string[] MarkerPalette =
    {
        "game:rock-granite", "game:rock-andesite", "game:rock-basalt", "game:rock-peridotite",
        "game:rock-chalk", "game:rock-limestone", "game:rock-shale", "game:rock-claystone",
    };

    // The three messages the read path logs at Error level when a row is lost or unreadable
    // (the first is the pool's own, the other two are the engine's and exist on vanilla too).
    private static readonly string[] ReadErrorFragments =
    {
        "chunk read pool",
        "Loaded some but not all chunks",
        "Failed deserializing a chunk",
    };

    private readonly ITestOutputHelper output;

    public ChunkReadPoolScenarios(ITestOutputHelper output) => this.output = output;

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task ModifiedColumn_Should_SurviveUnloadReload_When_ReadPoolEnabled()
    {
        ReadPool? pool = await AttachPool();
        int diagMark = World.BootDiagnostics.Count;
        int levels = Levels();
        int cx = World.Spawn.X / ChunkSize + 6;
        int cz = World.Spawn.Z / ChunkSize;
        BlockPos anchor = Anchor(cx, cz);

        await ChunkPersistence.LoadColumn(World, anchor, keepLoaded: true);
        try
        {
            ColumnState written = await WriteColumn(cx, cz, levels);

            // The gate is only that the column is present again: whether the stored copy was
            // applied is what the assertions below decide, and they name what is missing.
            await ChunkPersistence.SaveNow(World);
            await ChunkPersistence.UnloadColumn(World, anchor);
            int readsBefore = pool?.Reads() ?? 0;
            await ChunkPersistence.ReloadColumn(World, anchor, expectModdata: false, keepLoaded: true);

            List<string> problems = Problems(written);
            Assert.True(problems.Count == 0,
                $"column ({cx},{cz}) lost data across the unload and reload on {ServerFlavor.Name}: {string.Join("; ", problems)}");
            if (pool != null)
            {
                AssertPoolServed(pool, readsBefore, expected: levels, label: "one column");
            }
            AssertNoReadErrors(diagMark);
        }
        finally
        {
            WorldgenArea.Unload(World, cx, cz, 1, 1);
        }
    }

    [AtlasScenario(TimeoutMs = 240_000)]
    public async Task TwentyFiveColumns_Should_RoundTrip_When_ReloadedInOneRequestThroughReadPool()
    {
        // One request reloads 25 columns through the pool, one column after another on the chunk
        // thread: 200 leases (25 columns x 8 levels) that recycle the four connection slots, every
        // row mapped to its own level and every column carrying its own moddata. Overlap between
        // columns and waits on the pool semaphore are not covered, and cannot be reached: stage 0
        // (loading from the database) only runs serially on the single chunk thread (TryClaimStage
        // rejects stage 0 for the dispatcher and the workers), and inside one column the fan out
        // is min(WorkerCount, levels) = 4 reads against 4 slots, so a lease never waits.
        ReadPool? pool = await AttachPool();
        int diagMark = World.BootDiagnostics.Count;
        int levels = Levels();
        int cx0 = World.Spawn.X / ChunkSize - 14;
        int cz0 = World.Spawn.Z / ChunkSize - 14;

        try
        {
            await WorldgenArea.LoadDone(World, cx0, cz0, RectEdge, RectEdge);
            var written = new List<ColumnState>();
            foreach ((int cx, int cz) in WorldgenArea.Columns(cx0, cz0, RectEdge, RectEdge))
            {
                written.Add(await WriteColumn(cx, cz, levels));
            }
            Assert.True(written.Count == RectEdge * RectEdge,
                $"wrote {written.Count} columns instead of {RectEdge * RectEdge} on {ServerFlavor.Name}; setup is invalid");

            await ChunkPersistence.SaveNow(World);
            WorldgenArea.Unload(World, cx0, cz0, RectEdge, RectEdge);
            await World.Until(
                () => written.All(s => !ChunkPersistence.IsLoaded(World, Anchor(s.Cx, s.Cz))),
                timeoutTicks: ChunkPersistence.DefaultTimeoutTicks);

            int readsBefore = pool?.Reads() ?? 0;
            await WorldgenArea.LoadDone(World, cx0, cz0, RectEdge, RectEdge);

            List<string> broken = written
                .Select(s => (State: s, Problems: Problems(s)))
                .Where(x => x.Problems.Count > 0)
                .Select(x => $"({x.State.Cx},{x.State.Cz}): {string.Join(", ", x.Problems)}")
                .ToList();
            Assert.True(broken.Count == 0,
                $"{broken.Count} of {written.Count} columns lost data across the reload on {ServerFlavor.Name}: {string.Join(" | ", broken)}");
            if (pool != null)
            {
                AssertPoolServed(pool, readsBefore, expected: written.Count * levels, label: $"{written.Count} columns");
            }
            AssertNoReadErrors(diagMark);
        }
        finally
        {
            WorldgenArea.Unload(World, cx0, cz0, RectEdge, RectEdge);
        }
    }

    // Column helpers

    private sealed record Marker(BlockPos Pos, string Code);

    private sealed record ColumnState(int Cx, int Cz, byte[] Payload, List<BlockPos> Pattern, List<Marker> Markers);

    /// <summary>Block position at the minimum corner of the column, one above the spawn feet like
    /// the other persistence classes, so the shared pattern lands in the air above the surface.</summary>
    private BlockPos Anchor(int cx, int cz) => new BlockPos(cx * ChunkSize, World.Spawn.Y + 1, cz * ChunkSize, 0);

    private int Levels()
    {
        int levels = World.Api.WorldManager.MapSizeY / ChunkSize;
        // The pooled path needs at least MinChunkYLevelsForParallel (2) levels, and each level
        // gets its own marker rock.
        Assert.True(levels >= 2 && levels <= MarkerPalette.Length,
            $"the world has {levels} chunk levels, outside 2 to {MarkerPalette.Length} on {ServerFlavor.Name}; setup is invalid");
        return levels;
    }

    /// <summary>Writes every signal into a loaded column and asserts that they read back before
    /// anything is saved, so a later loss cannot be a write that never happened.</summary>
    private async Task<ColumnState> WriteColumn(int cx, int cz, int levels)
    {
        BlockPos anchor = Anchor(cx, cz);
        List<BlockPos> pattern = await ChunkPersistence.WritePatternConfirmed(World, anchor, saltForCycle: 0);
        byte[] payload = BitConverter.GetBytes(cx).Concat(BitConverter.GetBytes(cz)).ToArray();
        ChunkPersistence.SetColumnModdata(World, anchor, payload);
        List<Marker> markers = await WriteLevelMarkers(cx, cz, levels);

        var state = new ColumnState(cx, cz, payload, pattern, markers);
        List<string> problems = Problems(state);
        Assert.True(problems.Count == 0,
            $"column ({cx},{cz}) does not read back what was just written on {ServerFlavor.Name}: {string.Join("; ", problems)}; setup is invalid");
        return state;
    }

    /// <summary>One marker block per chunk level. The rock is the level's palette entry, or the next
    /// one when that is what the world already holds there (the surface may be above or below the
    /// marker height), so a regenerated column never matches by chance.</summary>
    private async Task<List<Marker>> WriteLevelMarkers(int cx, int cz, int levels)
    {
        var markers = new List<Marker>();
        for (int level = 0; level < levels; level++)
        {
            var pos = new BlockPos(cx * ChunkSize + MarkerOffset, level * ChunkSize + MarkerYInLevel, cz * ChunkSize + MarkerOffset, 0);
            string current = World.BlockAt(pos).Code.ToString();
            string code = Enumerable.Range(0, MarkerPalette.Length)
                .Select(k => MarkerPalette[(level + k) % MarkerPalette.Length])
                .First(c => c != current);
            markers.Add(new Marker(pos, code));
        }
        Assert.True(markers.Select(m => m.Code).Distinct().Count() == levels,
            $"the level marker codes are not distinct ({string.Join(", ", markers.Select(m => m.Code))}) on {ServerFlavor.Name}: a row read into the wrong level could go unseen; setup is invalid");

        foreach (Marker marker in markers)
        {
            World.SetBlock(marker.Code, marker.Pos);
        }
        await World.Until(
            () => markers.TrueForAll(m => World.BlockAt(m.Pos).Code.ToString() == m.Code),
            timeoutTicks: 300);
        return markers;
    }

    /// <summary>What differs from the written state; empty when the column is intact.</summary>
    private List<string> Problems(ColumnState state)
    {
        var problems = new List<string>();

        byte[]? moddata = ChunkPersistence.ReadColumnModdata(World, Anchor(state.Cx, state.Cz));
        if (moddata == null || !moddata.SequenceEqual(state.Payload))
        {
            problems.Add($"chunk moddata {(moddata == null ? "missing" : Convert.ToHexString(moddata))}, expected {Convert.ToHexString(state.Payload)}");
        }

        int lost = state.Pattern.Count(p => World.BlockAt(p).Code.ToString() != "game:rock-granite");
        if (lost > 0)
        {
            problems.Add($"{lost} of {state.Pattern.Count} pattern blocks gone");
        }

        foreach (Marker marker in state.Markers)
        {
            string actual = World.BlockAt(marker.Pos).Code.ToString();
            if (actual != marker.Code)
            {
                problems.Add($"level {marker.Pos.Y / ChunkSize} marker is {actual}, expected {marker.Code}");
            }
        }

        return problems;
    }

    // Pool assertions

    private async Task<ReadPool?> AttachPool()
    {
        if (!ServerFlavor.IsStratum)
        {
            return null;
        }

        string? enabled = await StratumSetting.Get(World, "Performance.ChunkIo.Enabled");
        Assert.True(string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase),
            $"Performance.ChunkIo.Enabled reads {enabled} on {ServerFlavor.Version}: the fixture was not applied; setup is invalid");

        ReadPool pool = ReadPool.Attach(World);
        Assert.True(pool.IsOpen,
            $"the chunk read pool is not open on {ServerFlavor.Version} although Performance.ChunkIo.Enabled is true");
        Assert.True(pool.WorkerCount == FixtureWorkers,
            $"the chunk read pool has {pool.WorkerCount} workers on {ServerFlavor.Version}, the fixture asks for {FixtureWorkers}; setup is invalid");
        return pool;
    }

    private void AssertPoolServed(ReadPool pool, int readsBefore, int expected, string label)
    {
        int served = pool.Reads() - readsBefore;
        output.WriteLine($"read pool statements ran {served} times for the reload of {label} (expected {expected}) on {ServerFlavor.Version}");
        Assert.True(served == expected,
            $"the read pool ran {served} chunk reads for the reload of {label}, expected exactly {expected} (one per chunk) on {ServerFlavor.Version}: " +
            (served < expected ? "the reload did not go through the pooled path" : "rows were read more than once"));
    }

    /// <summary>Only entries logged since the scenario started count: the host is shared by the
    /// whole class, so the earlier scenario's errors are not this reload's.</summary>
    private void AssertNoReadErrors(int diagMark)
    {
        List<string> hits = World.BootDiagnostics
            .Skip(diagMark)
            .Where(e => ReadErrorFragments.Any(f => e.Message.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .Select(e => $"{e.Level}: {e.Message}")
            .ToList();
        Assert.True(hits.Count == 0,
            $"the engine logged chunk read errors on {ServerFlavor.Name}: {string.Join(" | ", hits)}");
    }

    /// <summary>Reflection onto ServerMain.chunkThread.stratumReadPool (an internal
    /// StratumChunkReadPool, built at boot when Performance.ChunkIo.Enabled is true).</summary>
    private sealed class ReadPool
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // SQLITE_STMTSTATUS_RUN: number of times the statement ran. The SQLitePCLRaw constants stop at VM_STEP.
        private const int StmtStatusRun = 6;

        private readonly object pool;

        private ReadPool(object pool) => this.pool = pool;

        public bool IsOpen => Member<bool>(pool, "IsOpen");

        public int WorkerCount => Member<int>(pool, "WorkerCount");

        public static ReadPool Attach(IWorldSession world)
        {
            object server = world.Api.World;
            object chunkThread = Member<object>(server, "chunkThread");

            FieldInfo? field = chunkThread.GetType().GetField("stratumReadPool", AnyInstance);
            Assert.True(field != null, $"ChunkServerThread.stratumReadPool was not found by reflection on {ServerFlavor.Version}; setup is invalid");
            object? pool = field!.GetValue(chunkThread);
            Assert.True(pool != null,
                $"chunkThread.stratumReadPool is null on {ServerFlavor.Version} although Performance.ChunkIo.Enabled is true: the pool never opened");
            return new ReadPool(pool!);
        }

        /// <summary>How many times the prepared chunk read statements of every worker have run, ever.</summary>
        public int Reads()
        {
            int total = 0;
            foreach (object? command in Member<Array>(pool, "getChunkCmds"))
            {
                Assert.True(command != null, $"a read pool worker has no command on {ServerFlavor.Version}; the pool was closed; setup is invalid");
                foreach (object? entry in Member<IEnumerable>(command!, "_preparedStatements"))
                {
                    // The list holds (sqlite3_stmt Statement, int ParamCount) tuples.
                    object? statement = entry?.GetType().GetField("Item1")?.GetValue(entry);
                    Assert.True(statement != null, $"a read pool command holds no prepared statement on {ServerFlavor.Version}; setup is invalid");
                    total += (int)StatementStatus().Invoke(null, new[] { statement, StmtStatusRun, 0 })!;
                }
            }

            return total;
        }

        private static MethodInfo StatementStatus()
        {
            MethodInfo? method = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("SQLitePCL.raw"))
                .FirstOrDefault(t => t != null)?
                .GetMethod("sqlite3_stmt_status", BindingFlags.Public | BindingFlags.Static);
            Assert.True(method != null, $"SQLitePCL.raw.sqlite3_stmt_status was not found by reflection on {ServerFlavor.Version}; setup is invalid");
            return method!;
        }

        private static T Member<T>(object target, string name)
        {
            object? value = target.GetType().GetField(name, AnyInstance)?.GetValue(target)
                            ?? target.GetType().GetProperty(name, AnyInstance)?.GetValue(target);
            Assert.True(value is T, $"{target.GetType().Name}.{name} was not found by reflection on {ServerFlavor.Version}; setup is invalid");
            return (T)value!;
        }
    }
}
