using System.Collections;
using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Xunit;
using Xunit.Abstractions;

namespace StratumParity.Scenarios;

/// <summary>
/// Pathfinding parity against walled mazes built in the superflat world. The pathfinder lives in
/// VSEssentials (Vintagestory.Essentials), which this project does not reference, so it is reached
/// by name and reflection: the PathfindSystem mod system for synchronous searches, and a raccoon's
/// EntityBehaviorTaskAI.PathTraverser for the asynchronous worker path.
///
/// Stratum pools the PathNode objects of every A* search (AStar.StratumRentNode, reset at the start
/// of each search, "nodes in the returned path are valid until the next search"). Vanilla allocates
/// fresh nodes per search, so a List of PathNode handed to a caller stays untouched there. The first
/// scenario is a probe of that deliberate difference (vanilla keeps the nodes, Stratum rewrites
/// them); the other two are parity scenarios that pin the route itself and the end-to-end walk,
/// which the pool must not change.
///
/// Mazes use one-block corridors between three-block walls, so the only route is derivable from the
/// layout: a diagonal step between two corridor cells always has a wall on one side, and the squeeze
/// test of AStar.traversable (the box is centred between the two cells and must not collide) refuses
/// it at every random cell offset because the probe box is wider than the corridor margin.
/// </summary>
public class PathfindingScenarios : AtlasScenarioBase
{
    private readonly ITestOutputHelper output;

    public PathfindingScenarios(ITestOutputHelper output) => this.output = output;

    private const string PathfindSystemType = "Vintagestory.Essentials.PathfindSystem";
    private const string WallCode = "game:rock-granite";
    private const string AirCode = "game:air";
    // A raccoon pup (hitbox 0.4 wide) rather than the adult (0.75): the traversal test only needs the
    // box not to intersect a wall, so with the adult the shortest route may graze a wall tip at
    // some random cell offsets, and the traverser counts every horizontal collision as stuck time.
    private const string CreatureCode = "game:raccoon-common-baby-male";

    // Floor ring around the layout so nothing outside the outer wall is reachable and an
    // unreachable target can sit more than one cell away from every corridor cell.
    private const int Ring = 3;
    // Walls must be at least two blocks high: AStar tries to step onto a blocking cell with
    // stepHeight 1.01 and a one-block wall would be climbable.
    private const int WallHeight = 3;
    private const int FreeHeight = 5;

    // The arguments PathfindSystem.FindPath is called with by AI code: fall of at most 3 blocks and
    // a step height just above one block. The box is 0.5 wide so that it fits a one-block corridor
    // at every random cell offset (0.3 to 0.7), yet always reaches the neighbouring cells at the
    // diagonal squeeze position.
    private const int MaxFallHeight = 3;
    private const float StepHeight = 1.01f;
    private static readonly Cuboidf ProbeBox = new(-0.25f, 0f, -0.25f, 0.25f, 0.6f, 0.25f);

    private const int NavigationTimeoutTicks = 600;
    private const float CreatureMoveSpeed = 0.03f;
    private const float CreatureTargetDistance = 0.5f;
    private const double CreatureBandBlocks = 28;

    // Long single-route snake (45 cells), start S top left, end E bottom left.
    private static readonly string[] LongMazeRows =
    {
        "#############",
        "#S..........#",
        "###########.#",
        "#...........#",
        "#.###########",
        "#...........#",
        "###########.#",
        "###E........#",
        "#############",
    };

    // Short single-route corridor (9 cells). A search on it rents far fewer pooled nodes than the
    // long maze, so the long search is sure to recycle every node of the short path.
    private static readonly string[] ShortMazeRows =
    {
        "#######",
        "#S...##",
        "####.##",
        "#E...##",
        "#######",
    };

    // Three rooms three blocks wide, joined by gaps at alternating ends: the creature has to
    // detour around two wall tips (about 27 blocks of walking).
    private static readonly string[] CourseRows =
    {
        "#############",
        "#...........#",
        "#.S.........#",
        "#...........#",
        "#########...#",
        "#...........#",
        "#...........#",
        "#...........#",
        "#...#########",
        "#...........#",
        "#.........E.#",
        "#...........#",
        "#############",
    };

    // A deliberate Stratum trade-off, not a bug: Stratum commit ad381da ("Pool PathNode allocations
    // in AStar search", patches/VSEssentials/Entity/Pathfinding/Astar/AStar.cs.patch) rents the nodes
    // of every search from a per-AStar pool of 4096 slots that is reset at the start of the next
    // search, and documents that the returned path nodes stay valid only until then. Callers are
    // expected to convert a found path to waypoints at once; a caller that holds on to the List of
    // PathNode across a later search sees its nodes rewritten in place. Vanilla allocates fresh
    // nodes, so the list stays as it was. The scenario asserts both sides.
    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task FoundPath_Should_KeepItsNodesOnVanillaAndBeRewrittenOnStratum_When_AnotherSearchRuns()
    {
        MazePair mazes = await BuildMazePair(ChunkBase(World.Spawn.X + 200), ChunkBase(World.Spawn.Z));
        PathfinderUnderTest finder = PathfinderUnderTest.Resolve(World);

        IList first = Require(finder.FindPath(mazes.Short.StartCell, mazes.Short.EndCell), "short maze search");
        List<Node> firstBefore = Snapshot(first);
        List<Node> expectedFirst = ExpectedRoute(mazes.Short);
        Assert.True(expectedFirst.Count >= 5,
            $"short route has only {expectedFirst.Count} nodes, too few to observe recycling; setup is invalid");
        Assert.True(firstBefore.SequenceEqual(expectedFirst),
            $"short maze route on {ServerFlavor.Name} is not the corridor ({Describe(firstBefore)} vs {Describe(expectedFirst)}); setup is invalid");

        // A second search on another route, with no await in between: a recycled node would be
        // rewritten here, by the search that rents the same pool slot.
        IList second = Require(finder.FindPath(mazes.Long.StartCell, mazes.Long.EndCell), "long maze search");
        List<Node> secondNodes = Snapshot(second);
        Assert.True(secondNodes.SequenceEqual(ExpectedRoute(mazes.Long)),
            $"long maze route on {ServerFlavor.Name} is not the corridor ({Describe(secondNodes)}); setup is invalid");
        Assert.True(second.Count > first.Count,
            $"the second search ({second.Count} nodes) is not longer than the first ({first.Count}); setup is invalid");

        List<Node> firstAfter = Snapshot(first);
        int rewritten = 0;
        for (int i = 0; i < Math.Min(firstBefore.Count, firstAfter.Count); i++)
        {
            if (firstAfter[i] != firstBefore[i])
            {
                rewritten++;
            }
        }

        int shared = SharedReferences(first, second);
        output.WriteLine(
            $"{ServerFlavor.Name} {ServerFlavor.Version}: first path {firstBefore.Count} nodes, second path {second.Count} nodes, " +
            $"{rewritten} of {firstBefore.Count} first-path nodes rewritten, {shared} node objects shared");

        string detail =
            $"{rewritten} of {firstBefore.Count} nodes rewritten, {shared} node objects shared with the second path; " +
            $"before {Describe(firstBefore)}, after {Describe(firstAfter)}";
        if (ServerFlavor.IsStratum)
        {
            // The documented pooling: the second search rents the slots of the first path's nodes.
            Assert.True(rewritten > 0,
                $"no node of the first path was rewritten by the second search on {ServerFlavor.Name}: the pooling that " +
                $"Stratum documents (nodes valid until the next search) is gone, or the pool no longer recycles them ({detail})");
        }
        else
        {
            Assert.True(first.Count == firstBefore.Count && rewritten == 0 && shared == 0,
                $"the node list of the first search changed under the second search on {ServerFlavor.Name}: {detail}");
        }
    }

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task MazeRoute_Should_FollowTheOnlyCorridor_When_SearchedSynchronously()
    {
        MazePair mazes = await BuildMazePair(ChunkBase(World.Spawn.X), ChunkBase(World.Spawn.Z + 200));
        PathfinderUnderTest finder = PathfinderUnderTest.Resolve(World);

        // Each path is snapshotted right after its own search: pooled nodes are only valid until
        // the next one, which the first scenario judges on its own.
        foreach ((string label, PlacedMaze maze) in new[] { ("long", mazes.Long), ("short", mazes.Short) })
        {
            IList? path = finder.FindPath(maze.StartCell, maze.EndCell);
            Assert.True(path != null, $"{label} maze: no path from the start to the end cell on {ServerFlavor.Name}");
            List<Node> actual = Snapshot(path!);
            List<Node> expected = ExpectedRoute(maze);
            Assert.True(actual.SequenceEqual(expected),
                $"{label} maze: route is not the corridor on {ServerFlavor.Name}; expected {Describe(expected)}, got {Describe(actual)}");
        }

        // A target outside the outer wall cannot be reached: the search must exhaust the corridor
        // and answer null, not an empty or partial path. It sits three cells west of the long
        // maze, more than the tolerance of one cell away from every corridor cell.
        BlockPos outside = mazes.Long.Cell(-2, mazes.Long.Layout.Start.Z);
        IList? none = finder.FindPath(mazes.Long.StartCell, outside);
        Assert.True(none == null,
            $"search to an unreachable target returned {none?.Count} nodes instead of no path on {ServerFlavor.Name}");
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Creature_Should_ReachGoal_When_NavigatingTheMazeAsync()
    {
        // The entity throttle and every activation range are measured from the nearest Playing
        // client, so an anchor keeps the whole course in the full-rate band on both flavors.
        ITestPlayer anchor = await World.JoinPlayer("pf-async-anchor");
        await World.Ticks(2);
        await anchor.TeleportTo(World.Spawn);
        await World.Ticks(2);
        BlockPos anchorPos = anchor.Position;

        Maze course = new(CourseRows);
        int originX = anchorPos.X + Ring + 4;
        int originZ = anchorPos.Z - course.Height / 2;
        PlacedMaze placed = await BuildMaze(course, originX, originZ);

        BlockPos center = placed.Cell(course.Width / 2, course.Height / 2);
        Assert.True(HorizontalDistance(anchorPos, center) + course.Width / 2.0 < CreatureBandBlocks,
            $"the course is too far from the anchor for the full-rate entity band on {ServerFlavor.Name}; setup is invalid");

        Entity creature = World.SpawnEntity(CreatureCode, placed.StartCell.AddCopy(0, 1, 0));
        try
        {
            object taskAi = RequireMember(creature.GetBehavior("taskai"), "taskai behavior of " + CreatureCode);
            object traverser = RequireMember(Member(taskAi, "PathTraverser"), "PathTraverser of " + CreatureCode);
            StopOwnAi(taskAi);

            await WaitSetup(
                () => creature.OnGround && Math.Abs(World.PositionOf(creature).Y - placed.CellY) < 0.05,
                200, "the creature never settled on the course floor");
            await World.Ticks(5);

            BlockPos startBlock = World.PositionOf(creature).AsBlockPos;
            Assert.True(startBlock.X == placed.StartCell.X && startBlock.Y == placed.StartCell.Y && startBlock.Z == placed.StartCell.Z,
                $"the creature rests in block {startBlock.X},{startBlock.Y},{startBlock.Z} instead of the start cell " +
                $"{placed.StartCell.X},{placed.StartCell.Y},{placed.StartCell.Z} on {ServerFlavor.Name}; setup is invalid");

            var outcome = new NavigationOutcome();
            Vec3d goal = new(placed.EndCell.X + 0.5, placed.CellY, placed.EndCell.Z + 0.5);
            MethodInfo? navigate = traverser.GetType().GetMethod("NavigateTo_Async", BindingFlags.Public | BindingFlags.Instance);
            Assert.True(navigate != null && navigate.GetParameters().Length == 9,
                $"WaypointsTraverser.NavigateTo_Async has an unexpected signature on {ServerFlavor.Name}; setup is invalid");

            // (target, movingSpeed, targetDistance, OnGoalReached, OnStuck, onNoPath, searchDepth,
            // mhdistanceTolerance, creatureType): the creature type stays at the traverser's own.
            object? started = navigate!.Invoke(traverser, new object?[]
            {
                goal, CreatureMoveSpeed, CreatureTargetDistance,
                (Action)(() => Interlocked.Increment(ref outcome.GoalReached)),
                (Action)(() => Interlocked.Increment(ref outcome.Stuck)),
                (Action)(() => Interlocked.Increment(ref outcome.NoPath)),
                10000, 0, null,
            });
            Assert.True(started is true,
                $"NavigateTo_Async refused the search on {ServerFlavor.Name}; setup is invalid");
            Assert.True(outcome.NoPath == 0, $"NavigateTo_Async reported no path at once on {ServerFlavor.Name}; setup is invalid");

            long startTick = World.CurrentTick;
            try
            {
                await World.Until(() => outcome.Finished, timeoutTicks: NavigationTimeoutTicks);
            }
            catch (ScenarioTimeoutException)
            {
                Assert.Fail($"the creature neither reached the exit nor got stuck within {NavigationTimeoutTicks} ticks on {ServerFlavor.Name}: " +
                            DescribeNavigation(World.PositionOf(creature), traverser, outcome, placed, World.CurrentTick - startTick));
            }

            string state = DescribeNavigation(World.PositionOf(creature), traverser, outcome, placed, World.CurrentTick - startTick);
            output.WriteLine($"{ServerFlavor.Name} {ServerFlavor.Version}: walk finished after {World.CurrentTick - startTick} of {NavigationTimeoutTicks} ticks ({state})");
            Assert.True(outcome.NoPath == 0, $"the asynchronous search found no path through the maze on {ServerFlavor.Name}: {state}");
            Assert.True(outcome.Stuck == 0, $"the creature reported itself stuck on {ServerFlavor.Name}: {state}");
            Assert.True(outcome.GoalReached == 1, $"the goal callback fired {outcome.GoalReached} times on {ServerFlavor.Name}: {state}");

            // The callback only says the traverser ran out of waypoints: the creature has to be
            // standing at the exit as well.
            EntityPos end = World.PositionOf(creature);
            double toExit = Math.Sqrt(Math.Pow(end.X - goal.X, 2) + Math.Pow(end.Z - goal.Z, 2));
            Assert.True(toExit < 1.0, $"the goal callback fired but the creature is {toExit:F2} blocks from the exit on {ServerFlavor.Name}: {state}");
            Assert.True(HorizontalDistance(anchor.Position, end.AsBlockPos) < CreatureBandBlocks,
                $"the anchor drifted out of the full-rate band during the walk on {ServerFlavor.Name}; setup is invalid");
        }
        finally
        {
            World.Api.World.DespawnEntity(creature, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
        }
    }

    private static string DescribeNavigation(EntityPos pos, object traverser, NavigationOutcome outcome, PlacedMaze maze, long ticks)
    {
        return $"goal={outcome.GoalReached}, stuck={outcome.Stuck}, noPath={outcome.NoPath}, after {ticks} ticks, " +
               $"creature at {pos.X:F2},{pos.Y:F2},{pos.Z:F2} (maze origin {maze.OriginX},{maze.OriginZ}, floor cell y {maze.CellY}), " +
               $"traverser Active={Member(traverser, "Active")}, Ready={Member(traverser, "Ready")}";
    }

    private static double HorizontalDistance(BlockPos a, BlockPos b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Z - b.Z, 2));

    // ---- reflection helpers ----

    /// <summary>Public or non-public field or property of an object, null when absent.</summary>
    private static object? Member(object owner, string name)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        Type type = owner.GetType();
        FieldInfo? field = type.GetField(name, Flags);
        if (field != null)
        {
            return field.GetValue(owner);
        }

        return type.GetProperty(name, Flags)?.GetValue(owner);
    }

    private static object RequireMember(object? value, string what)
    {
        Assert.True(value != null, $"{what} is missing on {ServerFlavor.Name}; setup is invalid");
        return value!;
    }

    /// <summary>Stops the creature's own AI tasks and empties the task list so that wander or a
    /// scan cannot redirect it while the scenario drives the traverser.</summary>
    private static void StopOwnAi(object taskAi)
    {
        object taskManager = RequireMember(Member(taskAi, "TaskManager"), "TaskManager of the taskai behavior");
        MethodInfo? stop = taskManager.GetType().GetMethod("StopTasks", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
        Assert.True(stop != null, $"AiTaskManager.StopTasks is missing on {ServerFlavor.Name}; setup is invalid");
        stop!.Invoke(taskManager, null);

        IList? tasks = Member(taskManager, "AllTasks") as IList;
        Assert.True(tasks != null, $"AiTaskManager.AllTasks is missing on {ServerFlavor.Name}; setup is invalid");
        tasks!.Clear();
        Assert.True(tasks.Count == 0, $"the creature's AI task list could not be emptied on {ServerFlavor.Name}; setup is invalid");
    }

    /// <summary>The PathfindSystem mod system and its public FindPath(start, end, maxFallHeight,
    /// stepHeight, box, creatureType), the entry point that AI code and mods use.</summary>
    private sealed class PathfinderUnderTest
    {
        private readonly object system;
        private readonly MethodInfo findPath;

        private PathfinderUnderTest(object system, MethodInfo findPath)
        {
            this.system = system;
            this.findPath = findPath;
        }

        public static PathfinderUnderTest Resolve(IWorldSession world)
        {
            object? system = world.Api.ModLoader.GetModSystem(PathfindSystemType);
            Assert.True(system != null, $"{PathfindSystemType} is not loaded on {ServerFlavor.Name}; setup is invalid");

            MethodInfo? find = system!.GetType().GetMethod(
                "FindPath",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(BlockPos), typeof(BlockPos), typeof(int), typeof(float), typeof(Cuboidf), typeof(EnumAICreatureType) },
                null);
            Assert.True(find != null, $"{PathfindSystemType}.FindPath(BlockPos, BlockPos, int, float, Cuboidf, EnumAICreatureType) is missing on {ServerFlavor.Name}; setup is invalid");
            return new PathfinderUnderTest(system, find!);
        }

        /// <summary>The List of PathNode the engine returned, as it came back (live objects), or null.</summary>
        public IList? FindPath(BlockPos start, BlockPos end)
        {
            object? result = findPath.Invoke(system, new object[]
            {
                start.Copy(), end.Copy(), MaxFallHeight, StepHeight, ProbeBox.Clone(), EnumAICreatureType.Default,
            });
            return result as IList;
        }
    }

    // ---- path snapshots and the expected route ----

    private readonly record struct Node(int X, int Y, int Z);

    private static IList Require(IList? path, string what)
    {
        Assert.True(path != null, $"{what} returned no path on {ServerFlavor.Name}; setup is invalid");
        return path!;
    }

    private static List<Node> Snapshot(IList nodes)
    {
        var result = new List<Node>(nodes.Count);
        for (int i = 0; i < nodes.Count; i++)
        {
            BlockPos? pos = nodes[i] as BlockPos;
            Assert.True(pos != null, $"path node {i} is {(nodes[i] == null ? "null" : nodes[i]!.GetType().Name)}, not a BlockPos, on {ServerFlavor.Name}; setup is invalid");
            result.Add(new Node(pos!.X, pos.Y, pos.Z));
        }

        return result;
    }

    private static int SharedReferences(IList a, IList b)
    {
        var inB = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (object? node in b)
        {
            if (node != null)
            {
                inB.Add(node);
            }
        }

        int shared = 0;
        foreach (object? node in a)
        {
            if (node != null && inB.Contains(node))
            {
                shared++;
            }
        }

        return shared;
    }

    private static string Describe(IReadOnlyList<Node> nodes)
    {
        if (nodes.Count == 0)
        {
            return "[]";
        }

        const int Shown = 6;
        string head = string.Join(" ", nodes.Take(Shown).Select(n => $"{n.X},{n.Y},{n.Z}"));
        return $"[{nodes.Count} nodes: {head}{(nodes.Count > Shown ? " ..." : string.Empty)}]";
    }

    /// <summary>
    /// What the search must return, derived from the layout alone: PathfindSystem.FindPath accepts
    /// the first popped node within one cell of the target on every axis (mhdistanceTolerance 1),
    /// and a one-block corridor pops its cells in order, so the route is the corridor from its
    /// second cell up to the first cell near the end. The start cell itself is never part of it.
    /// </summary>
    private static List<Node> ExpectedRoute(PlacedMaze maze)
    {
        IReadOnlyList<(int X, int Z)> corridor = maze.Layout.Corridor;
        (int X, int Z) end = maze.Layout.End;
        Assert.True(!Near(corridor[0], end),
            $"the start cell is already within the search tolerance of the end cell; setup is invalid");

        var route = new List<Node>();
        for (int i = 1; i < corridor.Count; i++)
        {
            BlockPos cell = maze.Cell(corridor[i].X, corridor[i].Z);
            route.Add(new Node(cell.X, cell.Y, cell.Z));
            if (Near(corridor[i], end))
            {
                break;
            }
        }

        return route;
    }

    private static bool Near((int X, int Z) a, (int X, int Z) b) => Math.Abs(a.X - b.X) <= 1 && Math.Abs(a.Z - b.Z) <= 1;

    // ---- the maze and its construction ----

    /// <summary>A rectangular layout: '#' wall, '.' free, 'S' start, 'E' end.</summary>
    private sealed class Maze
    {
        private IReadOnlyList<(int X, int Z)>? corridor;

        public Maze(string[] rows)
        {
            Rows = rows;
            Height = rows.Length;
            Width = rows[0].Length;
            (int X, int Z)? start = null;
            (int X, int Z)? end = null;
            for (int z = 0; z < Height; z++)
            {
                if (rows[z].Length != Width)
                {
                    throw new InvalidOperationException($"maze row {z} has {rows[z].Length} cells instead of {Width}; setup is invalid");
                }

                for (int x = 0; x < Width; x++)
                {
                    if (rows[z][x] == 'S')
                    {
                        start = (x, z);
                    }
                    else if (rows[z][x] == 'E')
                    {
                        end = (x, z);
                    }
                }
            }

            Start = start ?? throw new InvalidOperationException("maze layout has no start cell; setup is invalid");
            End = end ?? throw new InvalidOperationException("maze layout has no end cell; setup is invalid");
        }

        public string[] Rows { get; }

        public int Width { get; }

        public int Height { get; }

        public (int X, int Z) Start { get; }

        public (int X, int Z) End { get; }

        public bool IsWall(int x, int z) => Rows[z][x] == '#';

        private bool IsFree(int x, int z) => x >= 0 && z >= 0 && x < Width && z < Height && !IsWall(x, z);

        /// <summary>The cells from the start to the end, followed through the layout. Throws unless
        /// the layout is one single route: every inner cell has exactly two free neighbours, the
        /// ends exactly one, no loose cells, and no 2x2 free block that would allow a diagonal.</summary>
        public IReadOnlyList<(int X, int Z)> Corridor => corridor ??= WalkCorridor();

        private List<(int X, int Z)> FreeNeighbours((int X, int Z) cell) =>
            new[] { (cell.X + 1, cell.Z), (cell.X - 1, cell.Z), (cell.X, cell.Z + 1), (cell.X, cell.Z - 1) }
                .Where(n => IsFree(n.Item1, n.Item2))
                .ToList();

        private List<(int X, int Z)> WalkCorridor()
        {
            int free = 0;
            for (int z = 0; z < Height; z++)
            {
                for (int x = 0; x < Width; x++)
                {
                    free += IsWall(x, z) ? 0 : 1;
                    if (IsFree(x, z) && IsFree(x + 1, z) && IsFree(x, z + 1) && IsFree(x + 1, z + 1))
                    {
                        throw new InvalidOperationException($"maze has a free 2x2 block at {x},{z}, a diagonal shortcut; setup is invalid");
                    }
                }
            }

            var route = new List<(int X, int Z)> { Start };
            var seen = new HashSet<(int X, int Z)> { Start };
            (int X, int Z) current = Start;
            while (current != End)
            {
                int expectedNeighbours = route.Count == 1 ? 1 : 2;
                List<(int X, int Z)> neighbours = FreeNeighbours(current);
                List<(int X, int Z)> unseen = neighbours.Where(n => !seen.Contains(n)).ToList();
                if (neighbours.Count != expectedNeighbours || unseen.Count != 1)
                {
                    throw new InvalidOperationException(
                        $"maze cell {current.X},{current.Z} has {neighbours.Count} free neighbours ({unseen.Count} unseen): not a single route; setup is invalid");
                }

                current = unseen[0];
                seen.Add(current);
                route.Add(current);
            }

            if (FreeNeighbours(End).Count != 1 || route.Count != free)
            {
                throw new InvalidOperationException(
                    $"maze route covers {route.Count} of {free} free cells and its end has {FreeNeighbours(End).Count} neighbours: not a single route; setup is invalid");
            }

            return route;
        }
    }

    /// <summary>A layout built in the world: where its cell (0,0) is and at which height cells stand.</summary>
    private sealed record PlacedMaze(Maze Layout, BlockPos Spawn, int OriginX, int OriginZ, int CellY)
    {
        /// <summary>The block (air) a creature stands in for a layout cell; its floor is the block below.</summary>
        public BlockPos Cell(int x, int z) => Spawn.AddCopy(OriginX + x - Spawn.X, CellY - Spawn.Y, OriginZ + z - Spawn.Z);

        public BlockPos StartCell => Cell(Layout.Start.X, Layout.Start.Z);

        public BlockPos EndCell => Cell(Layout.End.X, Layout.End.Z);
    }

    private sealed record MazePair(PlacedMaze Long, PlacedMaze Short);

    private sealed class NavigationOutcome
    {
        public int GoalReached;
        public int Stuck;
        public int NoPath;

        public bool Finished => Volatile.Read(ref GoalReached) + Volatile.Read(ref Stuck) + Volatile.Read(ref NoPath) > 0;
    }

    private static int ChunkBase(int blockCoordinate) => (int)Math.Floor(blockCoordinate / 32.0) * 32;

    private BlockPos At(int x, int y, int z)
    {
        BlockPos spawn = World.Spawn;
        return spawn.AddCopy(x - spawn.X, y - spawn.Y, z - spawn.Z);
    }

    /// <summary>
    /// Both synchronous mazes in one kept-loaded chunk column, stacked along Z with their rings
    /// touching, at least 200 blocks from the other scenarios. Nothing needs a player: the search
    /// only reads blocks.
    /// </summary>
    private async Task<MazePair> BuildMazePair(int chunkBaseX, int chunkBaseZ)
    {
        World.Api.WorldManager.LoadChunkColumnPriority(chunkBaseX / 32, chunkBaseZ / 32,
            new ChunkLoadOptions { KeepLoaded = true });
        await WaitSetup(
            () => World.Api.World.BlockAccessor.GetChunkAtBlockPos(At(chunkBaseX + 16, World.Spawn.Y, chunkBaseZ + 16)) != null,
            600, "the maze column never loaded");

        Maze longLayout = new(LongMazeRows);
        Maze shortLayout = new(ShortMazeRows);
        int platformX = chunkBaseX + 2;
        int platformZ = chunkBaseZ + 2;
        PlacedMaze longMaze = await BuildMaze(longLayout, platformX + Ring, platformZ + Ring);
        PlacedMaze shortMaze = await BuildMaze(shortLayout, platformX + Ring, platformZ + longLayout.Height + 2 * Ring + Ring);
        return new MazePair(longMaze, shortMaze);
    }

    private async Task<PlacedMaze> BuildMaze(Maze layout, int originX, int originZ)
    {
        // Terrain height under the whole platform: the world is superflat, so every sample must
        // agree, and the platform is laid on top of it rather than relying on the surface material.
        int minX = originX - Ring;
        int maxX = originX + layout.Width + Ring - 1;
        int minZ = originZ - Ring;
        int maxZ = originZ + layout.Height + Ring - 1;
        (int X, int Z)[] samples =
        {
            (minX, minZ), (maxX, minZ), (minX, maxZ), (maxX, maxZ), ((minX + maxX) / 2, (minZ + maxZ) / 2),
        };
        await WaitSetup(() => samples.All(s => FindGroundTop(s.X, s.Z) >= 0), 600, "the terrain under the maze never loaded");
        int[] heights = samples.Select(s => FindGroundTop(s.X, s.Z)).ToArray();
        Assert.True(heights.Distinct().Count() == 1,
            $"the terrain under the maze is not flat on {ServerFlavor.Name} (heights {string.Join(",", heights)}); setup is invalid");

        int floorY = heights[0] + 1;
        var expected = new List<(BlockPos Pos, string Code)>();
        for (int z = -Ring; z < layout.Height + Ring; z++)
        {
            for (int x = -Ring; x < layout.Width + Ring; x++)
            {
                bool wall = x >= 0 && z >= 0 && x < layout.Width && z < layout.Height && layout.IsWall(x, z);
                expected.Add((At(originX + x, floorY, originZ + z), WallCode));
                for (int dy = 1; dy <= FreeHeight; dy++)
                {
                    expected.Add((At(originX + x, floorY + dy, originZ + z), wall && dy <= WallHeight ? WallCode : AirCode));
                }
            }
        }

        foreach ((BlockPos pos, string code) in expected)
        {
            if (Matches(pos, code))
            {
                continue;
            }

            if (code == AirCode)
            {
                World.Api.World.BlockAccessor.SetBlock(0, pos);
            }
            else
            {
                World.SetBlock(code, pos);
            }
        }

        await WaitSetup(() => expected.TrueForAll(e => Matches(e.Pos, e.Code)), 300, "the maze blocks never read back as placed");
        return new PlacedMaze(layout, World.Spawn, originX, originZ, floorY + 1);
    }

    private bool Matches(BlockPos pos, string code)
    {
        Block block = World.BlockAt(pos);
        return code == AirCode ? block.Id == 0 : block.Code.ToString() == code;
    }

    /// <summary>Y of the highest block with a collision box in a column (grass and flowers have
    /// none), or -1 while the column is not loaded or has nothing solid.</summary>
    private int FindGroundTop(int x, int z)
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

    private async Task WaitSetup(Func<bool> condition, int ticks, string what)
    {
        try
        {
            await World.Until(condition, timeoutTicks: ticks);
        }
        catch (ScenarioTimeoutException)
        {
            Assert.Fail($"{what} within {ticks} ticks on {ServerFlavor.Name}; setup is invalid");
        }
    }
}
