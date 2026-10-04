using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Xunit;
using Xunit.Abstractions;

namespace StratumParity.Scenarios;

/// <summary>
/// The engine registers an entity's physics behavior in two steps: Initialize enqueues it on the
/// physics manager's <c>toAdd</c> queue, OnEntityDespawn enqueues it on <c>toRemove</c>, and the
/// manager's 1 ms tick listener drains both into its private <c>tickables</c> list, adds first
/// and removals second. Vanilla drains the whole add queue every tick, so an entity spawned and
/// despawned between two ticks is added and then removed again. Stratum's activation cap
/// (Performance.Physics.MaxActivationsPerTick, 50 by default) stops the add loop after the budget
/// but still drains every removal: a removal that runs while its add is still deferred finds
/// nothing in <c>tickables</c>, and the deferred add lands on a later tick, leaving a tickable
/// for an entity that no longer exists. The leak is permanent, and because the manager derives
/// each client's tracked-entity list from <c>tickables</c>, the despawned entity is also flipped
/// back to Active and kept in the tracked set of every client in range. Measured on both Stratum
/// builds: the client itself usually never ends up knowing the entity (the spawn packet and the
/// despawn packet both go out and cancel), the stale part is the server's own bookkeeping. That
/// cancelling is a race, though: in two of four full-suite legs on the Stratum builds (stable.2
/// and indev.1 once each) 20 of the 70 despawned dummies, the ones whose deferred add lands last,
/// were still known to the observer 90 passes later, while 36 runs of this class alone saw none.
///
/// Every scenario builds the same shape in one synchronous step (the scenario body runs on the
/// game thread, so no server tick can interleave): 120 straw dummies are spawned, then the last
/// 70 of them are despawned. The first 50 therefore sit ahead of the cap in the add queue and
/// the 70 despawned ones are all deferred. Guards read the manager's own queues by reflection and
/// fail with "setup is invalid" when that premise does not hold, so a green run can never be a
/// vacuous one. Counting is by entity id, so tickables leaked by an earlier scenario of this
/// class (the world is shared) never leak into a later scenario's numbers.
///
/// The two leak assertions (the tickable count and the observer's tracked set) carry the
/// <see cref="ActivationLeak"/> exemption on the builds where the bug is confirmed: there they pin
/// the observed shape, everywhere else they are strict parity. What the client knows about the
/// despawned dummies is the outcome of the race above, so on those builds it is logged, not
/// asserted; everywhere else it is strict. The survivors and the setup guards stay exact on every
/// build, and the cap-disabled scenario stays strict on every build (it is the proof that the cap
/// is the cause).
/// </summary>
public class PhysicsActivationQueueScenarios : AtlasScenarioBase
{
    private const string DummyCode = "game:strawdummy";
    private const string CapKey = "Performance.Physics.MaxActivationsPerTick";

    private const int SpawnCount = 120;
    private const int DespawnCount = 70;
    private const int SurvivorCount = SpawnCount - DespawnCount;
    private const int GridColumns = 12;

    // The deferred adds of a 120-entity batch drain in three ticks at the default cap of 50. The
    // wait ends on the queues being empty, the extra window checks that nothing prunes later.
    private const int SettleTimeoutTicks = 60;
    private const int PostDrainTicks = 10;
    private const int ChunkRadiusTimeoutTicks = 600;
    private const int ArrivalTimeoutTicks = 300;
    private const int DepartureWindowTicks = 90;

    // PhysicsManager.ServerTick stops the add loop at the activation cap but drains every removal,
    // so a removal that precedes its deferred add is lost. The hunk is identical on
    // v1.22.7-stratum.2, v1.22.7-stratum.2-indev.1 and upstream/indev.
    private static readonly KnownDivergence ActivationLeak =
        new("StratumServer/Stratum#358", StratumBuild.Stable2, StratumBuild.Indev1);

    private readonly ITestOutputHelper output;

    public PhysicsActivationQueueScenarios(ITestOutputHelper output) => this.output = output;

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task EarlyDespawnedEntities_Should_LeaveNoPhysicsTickable_When_SpawnedAndDespawnedInOneStep()
    {
        string capNote = await AssertCapDefersTheBatch();
        BlockPos anchor = await LoadKeptColumn(200, 0);

        var batch = new Batch();
        try
        {
            Census census = await SpawnDespawnAndSettle(batch, anchor);

            AssertSurvivorsRegistered(census, capNote);
            if (ActivationLeak.Applies)
            {
                Assert.True(census.Leaked >= 1,
                    $"{ActivationLeak.Tag}: no physics tickable is left for the {DespawnCount} dummies despawned in the same step " +
                    $"as their spawn on {ServerFlavor.Name}{capNote}, so the leak is gone on this build: drop the exemption; " +
                    $"stale tickables overall {census.StaleOverall} of {census.Total}");
            }
            else
            {
                Assert.True(census.Leaked == 0, LeakMessage(census, capNote));
            }
        }
        finally
        {
            Cleanup(batch);
        }
    }

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task EarlyDespawnedEntities_Should_DepartFromClients_When_DespawnedBeforeActivation()
    {
        string capNote = await AssertCapDefersTheBatch();

        ITestPlayer observer = await World.JoinPlayer("pq-observer");
        await World.Ticks(2);
        // The join scatters players around spawn: pin the observer, then read its position.
        await observer.TeleportTo(World.Spawn);
        await World.Ticks(2);
        await WaitForChunkRadius(observer, 2);

        BlockPos anchor = observer.Position.AddCopy(4, 1, 4);
        // Arrivals are only meaningful from here on; KnowsEntity is unaffected by Clear.
        observer.Client.Clear();

        var batch = new Batch();
        try
        {
            PhysicsManagerView physics = PhysicsManagerView.Open(World);
            SpawnAndDespawnInOneStep(batch, anchor, physics);

            // Positive control first: an absence assertion proves nothing unless the observer
            // is known to be listening, so every surviving dummy must reach it.
            await WaitUntilSurvivorsKnown(observer, batch, capNote);
            await World.Ticks(DepartureWindowTicks);

            int knownSurvivors = batch.Survivors.Count(e => observer.Client.KnowsEntity(e.EntityId));
            Assert.True(knownSurvivors == SurvivorCount,
                $"only {knownSurvivors} of {SurvivorCount} surviving dummies are still known to the observer " +
                $"on {ServerFlavor.Name} after the departure window; setup is invalid");

            // The server's own record of what this client tracks. Positive control first: the
            // survivors must be in it, otherwise the read is not looking at the tracked set.
            ClientTrackingView tracking = ClientTrackingView.Open(World, observer);
            int trackedSurvivors = tracking.Count(batch.SurvivorIds);
            Assert.True(trackedSurvivors == SurvivorCount,
                $"only {trackedSurvivors} of {SurvivorCount} surviving dummies are in the observer's server-side tracked set " +
                $"on {ServerFlavor.Name}; setup is invalid");

            // What the client is told ends up the same on a build without the leak. With the leak
            // it is a race between the spawn and the despawn packet of each leaked entity (0 of 70
            // in a class-only run, 20 of 70 in two of four full-suite legs), so the count is
            // logged and not asserted there.
            int ghosts = batch.Despawned.Count(e => observer.Client.KnowsEntity(e.EntityId));
            output.WriteLine($"{ghosts} of {DespawnCount} despawned dummies are known to the observer on {ServerFlavor.Name}{capNote}");
            if (!ActivationLeak.Applies)
            {
                Assert.True(ghosts == 0,
                    $"{ghosts} of {DespawnCount} dummies despawned before their physics activation are still known " +
                    $"to the observer on {ServerFlavor.Name}, {DepartureWindowTicks} passes after every survivor arrived{capNote}");
            }

            int staleTracked = tracking.Count(batch.DespawnedIds);
            if (ActivationLeak.Applies)
            {
                Assert.True(staleTracked >= 1,
                    $"{ActivationLeak.Tag}: none of the {DespawnCount} dummies despawned before their physics activation is left " +
                    $"in the observer's server-side tracked set on {ServerFlavor.Name}{capNote}, so the leak is gone on this build: " +
                    "drop the exemption");
            }
            else
            {
                Assert.True(staleTracked == 0,
                    $"{staleTracked} of {DespawnCount} dummies despawned before their physics activation are still in the " +
                    $"observer's server-side tracked set on {ServerFlavor.Name}, {DepartureWindowTicks} passes after every survivor arrived{capNote}");
            }
        }
        finally
        {
            Cleanup(batch);
        }
    }

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task EarlyDespawnedEntities_Should_LeaveNoTickable_When_ActivationCapDisabled()
    {
        BlockPos anchor = await LoadKeptColumn(0, 200);

        // Vanilla has no cap and no /stratum: Set returns a no-op handle and the same body proves
        // the default behavior. On Stratum 0 means "no limit", and the key is read on every tick.
        await using IAsyncDisposable toggle = await StratumSetting.Set(World, CapKey, "0");
        string? applied = await StratumSetting.Get(World, CapKey);
        Assert.True(!ServerFlavor.IsStratum || applied == "0",
            $"{CapKey} reads '{applied}' after /stratum set {CapKey} 0 on stratum; setup is invalid");
        string capNote = $" ({CapKey}={applied ?? "none"}, cap disabled)";

        var batch = new Batch();
        try
        {
            Census census = await SpawnDespawnAndSettle(batch, anchor);

            AssertSurvivorsRegistered(census, capNote);
            Assert.True(census.Leaked == 0, LeakMessage(census, capNote));
        }
        finally
        {
            Cleanup(batch);
        }
    }

    /// <summary>On Stratum the batch must exceed the cap, otherwise nothing is deferred and the
    /// leak cannot exist: that would be a different test, so it fails as an invalid setup. Returns
    /// a message fragment naming the cap (empty on vanilla, which has none).</summary>
    private async Task<string> AssertCapDefersTheBatch()
    {
        string? raw = await StratumSetting.Get(World, CapKey);
        if (raw == null)
        {
            return string.Empty;
        }

        Assert.True(int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cap),
            $"{CapKey} answered '{raw}' on stratum, which is not a number; setup is invalid");
        Assert.True(cap > 0 && cap < SpawnCount,
            $"{CapKey} is {cap} on stratum: a batch of {SpawnCount} spawns is not deferred by it; setup is invalid");
        return $" ({CapKey}={cap})";
    }

    /// <summary>A chunk column kept loaded at a fixed offset from the world spawn, anchored inside
    /// the column so the whole 12 by 10 grid of dummies stays in one chunk. The offsets used by
    /// the scenarios are at least 200 blocks apart (shared world, no order).</summary>
    private async Task<BlockPos> LoadKeptColumn(int dx, int dz)
    {
        BlockPos spawn = World.Spawn;
        int columnX = (spawn.X + dx) >> 5;
        int columnZ = (spawn.Z + dz) >> 5;
        BlockPos anchor = spawn.AddCopy(columnX * 32 + 8 - spawn.X, 1, columnZ * 32 + 8 - spawn.Z);

        World.Api.WorldManager.LoadChunkColumnPriority(columnX, columnZ,
            new Vintagestory.API.Server.ChunkLoadOptions { KeepLoaded = true });
        await World.Until(
            () => World.Api.World.BlockAccessor.GetChunkAtBlockPos(anchor) != null,
            timeoutTicks: 600);
        return anchor;
    }

    /// <summary>The engine tells a client about an entity only through chunks it already sent
    /// (the tracked-entity pass checks DidSendChunk), so the observer must have its surroundings
    /// before the batch exists.</summary>
    private async Task WaitForChunkRadius(ITestPlayer observer, int radius)
    {
        try
        {
            await World.Until(() => observer.Player.CurrentChunkSentRadius >= radius, timeoutTicks: ChunkRadiusTimeoutTicks);
        }
        catch (Exception)
        {
            Assert.Fail(
                $"the observer's chunk send radius stayed at {observer.Player.CurrentChunkSentRadius} (wanted {radius}) " +
                $"on {ServerFlavor.Name}; setup is invalid");
        }
    }

    private async Task WaitUntilSurvivorsKnown(ITestPlayer observer, Batch batch, string capNote)
    {
        try
        {
            await World.Until(
                () => batch.Survivors.All(e => observer.Client.KnowsEntity(e.EntityId)),
                timeoutTicks: ArrivalTimeoutTicks);
        }
        catch (Exception)
        {
            int known = batch.Survivors.Count(e => observer.Client.KnowsEntity(e.EntityId));
            Assert.Fail(
                $"the observer learned of only {known} of {SurvivorCount} surviving dummies within " +
                $"{ArrivalTimeoutTicks} passes on {ServerFlavor.Name}{capNote}; setup is invalid");
        }
    }

    private async Task<Census> SpawnDespawnAndSettle(Batch batch, BlockPos anchor)
    {
        PhysicsManagerView physics = PhysicsManagerView.Open(World);
        SpawnAndDespawnInOneStep(batch, anchor, physics);

        HashSet<long> ids = batch.AllIds;
        try
        {
            await World.Until(
                () => physics.PendingAdds(ids) == 0 && physics.PendingRemoves(ids) == 0,
                timeoutTicks: SettleTimeoutTicks);
        }
        catch (Exception)
        {
            Assert.Fail(
                $"{physics.PendingAdds(ids)} adds and {physics.PendingRemoves(ids)} removals of the batch are still queued " +
                $"after {SettleTimeoutTicks} passes on {ServerFlavor.Name}; setup is invalid");
        }

        await World.Ticks(PostDrainTicks);
        return TakeCensus(physics, batch);
    }

    /// <summary>Spawns the whole batch, then despawns its tail, with no server tick in between,
    /// and proves through the manager's own queues that both halves are pending together.</summary>
    private void SpawnAndDespawnInOneStep(Batch batch, BlockPos anchor, PhysicsManagerView physics)
    {
        for (int i = 0; i < SpawnCount; i++)
        {
            batch.All.Add(World.SpawnEntity(DummyCode, anchor.AddCopy(i % GridColumns, 0, i / GridColumns)));
        }

        HashSet<long> ids = batch.AllIds;
        Assert.True(ids.Count == SpawnCount,
            $"{ids.Count} distinct entity ids for {SpawnCount} spawns on {ServerFlavor.Name}; setup is invalid");
        Assert.True(CountLoaded(batch.All) == SpawnCount,
            $"{CountLoaded(batch.All)} of {SpawnCount} spawned dummies are loaded on {ServerFlavor.Name}; setup is invalid");
        Assert.True(physics.PendingAdds(ids) == SpawnCount,
            $"{physics.PendingAdds(ids)} of {SpawnCount} spawned dummies reached the physics add queue on {ServerFlavor.Name}; setup is invalid");
        Assert.True(physics.Registered(ids) == 0,
            $"{physics.Registered(ids)} dummies were registered before the first physics tick on {ServerFlavor.Name}; setup is invalid");

        foreach (Entity entity in batch.Despawned)
        {
            World.Api.World.DespawnEntity(entity, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
        }

        Assert.True(CountLoaded(batch.Despawned) == 0,
            $"{CountLoaded(batch.Despawned)} of {DespawnCount} despawned dummies are still loaded on {ServerFlavor.Name}; setup is invalid");
        Assert.True(CountLoaded(batch.Survivors) == SurvivorCount,
            $"{CountLoaded(batch.Survivors)} of {SurvivorCount} surviving dummies are loaded on {ServerFlavor.Name}; setup is invalid");
        Assert.True(physics.PendingRemoves(batch.DespawnedIds) == DespawnCount,
            $"{physics.PendingRemoves(batch.DespawnedIds)} of {DespawnCount} despawns reached the physics remove queue on {ServerFlavor.Name}; setup is invalid");
        Assert.True(physics.Registered(ids) == 0,
            $"{physics.Registered(ids)} dummies became registered inside the one-step batch on {ServerFlavor.Name}; setup is invalid");
    }

    private int CountLoaded(IEnumerable<Entity> entities) =>
        entities.Count(e => World.Api.World.GetEntityById(e.EntityId) != null);

    /// <summary>What the physics manager holds for the batch once its queues are empty. Entities are
    /// matched by id, so duplicates count and entities of other scenarios do not.</summary>
    private Census TakeCensus(PhysicsManagerView physics, Batch batch)
    {
        List<IPhysicsTickable> tickables = physics.Tickables();
        var perEntity = new Dictionary<long, int>();
        int stale = 0;
        foreach (IPhysicsTickable tickable in tickables)
        {
            Entity? entity = tickable.Entity;
            if (entity == null)
            {
                continue;
            }

            perEntity[entity.EntityId] = perEntity.GetValueOrDefault(entity.EntityId) + 1;
            if (World.Api.World.GetEntityById(entity.EntityId) == null)
            {
                stale++;
            }
        }

        return new Census(
            SurvivorsRegisteredOnce: batch.Survivors.Count(e => perEntity.GetValueOrDefault(e.EntityId) == 1),
            Leaked: batch.Despawned.Sum(e => perEntity.GetValueOrDefault(e.EntityId)),
            StaleOverall: stale,
            Total: tickables.Count);
    }

    /// <summary>Positive control of the count scenarios: the dummies that stayed alive must each be
    /// registered exactly once, which proves the manager drained the batch at all.</summary>
    private static void AssertSurvivorsRegistered(Census census, string capNote)
    {
        Assert.True(census.SurvivorsRegisteredOnce == SurvivorCount,
            $"{census.SurvivorsRegisteredOnce} of {SurvivorCount} surviving dummies are registered exactly once " +
            $"after the queues drained on {ServerFlavor.Name}{capNote}; setup is invalid");
    }

    private static string LeakMessage(Census census, string capNote) =>
        $"{census.Leaked} physics tickables remain for the {DespawnCount} dummies despawned in the same step as their spawn " +
        $"on {ServerFlavor.Name}{capNote}; stale tickables overall {census.StaleOverall} of {census.Total}";

    private void Cleanup(Batch batch)
    {
        foreach (Entity entity in batch.All)
        {
            if (World.Api.World.GetEntityById(entity.EntityId) != null)
            {
                World.Api.World.DespawnEntity(entity, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
            }
        }
    }

    private sealed record Census(int SurvivorsRegisteredOnce, int Leaked, int StaleOverall, int Total);

    private sealed class Batch
    {
        public List<Entity> All { get; } = new();

        public IEnumerable<Entity> Survivors => All.Take(SurvivorCount);

        public IEnumerable<Entity> Despawned => All.Skip(SurvivorCount);

        public HashSet<long> AllIds => All.Select(e => e.EntityId).ToHashSet();

        public HashSet<long> SurvivorIds => Survivors.Select(e => e.EntityId).ToHashSet();

        public HashSet<long> DespawnedIds => Despawned.Select(e => e.EntityId).ToHashSet();
    }

    /// <summary>Reflection onto ServerMain.ServerUdpNetwork.physicsManager: the private tickables
    /// list and the public toAdd and toRemove queues. Every lookup that misses fails as an invalid
    /// setup naming the member. Reads run on the game thread between passes, where the manager's
    /// own tick (the only writer) cannot be running.</summary>
    private sealed class PhysicsManagerView
    {
        private readonly IEnumerable<IPhysicsTickable> tickables;
        private readonly ConcurrentQueue<IPhysicsTickable> toAdd;
        private readonly ConcurrentQueue<IPhysicsTickable> toRemove;

        private PhysicsManagerView(
            IEnumerable<IPhysicsTickable> tickables,
            ConcurrentQueue<IPhysicsTickable> toAdd,
            ConcurrentQueue<IPhysicsTickable> toRemove)
        {
            this.tickables = tickables;
            this.toAdd = toAdd;
            this.toRemove = toRemove;
        }

        public static PhysicsManagerView Open(IWorldSession world)
        {
            object server = world.Api.World;
            object network = Members.Read(server, "ServerUdpNetwork");
            object manager = Members.Read(network, "physicsManager");
            return new PhysicsManagerView(
                Members.Typed<IEnumerable<IPhysicsTickable>>(manager, "tickables"),
                Members.Typed<ConcurrentQueue<IPhysicsTickable>>(manager, "toAdd"),
                Members.Typed<ConcurrentQueue<IPhysicsTickable>>(manager, "toRemove"));
        }

        /// <summary>A copy of the registered list.</summary>
        public List<IPhysicsTickable> Tickables() => new(tickables);

        public int Registered(HashSet<long> ids) => Count(tickables, ids);

        public int PendingAdds(HashSet<long> ids) => Count(toAdd, ids);

        public int PendingRemoves(HashSet<long> ids) => Count(toRemove, ids);

        private static int Count(IEnumerable<IPhysicsTickable> source, HashSet<long> ids) =>
            source.ToArray().Count(t => t?.Entity != null && ids.Contains(t.Entity.EntityId));
    }

    /// <summary>Reflection onto the server's ConnectedClient of one test player and its
    /// <c>TrackedEntities</c> set: the ids the server believes the client holds, which the physics
    /// manager rebuilds from its tickables on every state update. Same game-thread guarantee as
    /// <see cref="PhysicsManagerView"/>.</summary>
    private sealed class ClientTrackingView
    {
        private readonly HashSet<long> tracked;

        private ClientTrackingView(HashSet<long> tracked) => this.tracked = tracked;

        public static ClientTrackingView Open(IWorldSession world, ITestPlayer player)
        {
            object clients = Members.Read(world.Api.World, "Clients");
            object? values = clients.GetType().GetProperty("Values")?.GetValue(clients);
            Assert.True(values is System.Collections.IEnumerable,
                $"{clients.GetType().FullName}.Values was not found on {ServerFlavor.Name}; setup is invalid");

            foreach (object client in (System.Collections.IEnumerable)values!)
            {
                if (Members.Read(client, "Player") is IPlayer { } owner && owner.PlayerUID == player.Player.PlayerUID)
                {
                    return new ClientTrackingView(Members.Typed<HashSet<long>>(client, "TrackedEntities"));
                }
            }

            Assert.Fail($"no server-side client was found for {player.Player.PlayerName} on {ServerFlavor.Name}; setup is invalid");
            return null!;
        }

        public int Count(HashSet<long> ids) => ids.Count(tracked.Contains);
    }

    private static class Members
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static object Read(object target, string member)
        {
            Type type = target.GetType();
            object? value = type.GetField(member, AnyInstance)?.GetValue(target)
                ?? type.GetProperty(member, AnyInstance)?.GetValue(target);
            Assert.True(value != null,
                $"{type.FullName}.{member} was not found or is null on {ServerFlavor.Name}; setup is invalid");
            return value!;
        }

        public static T Typed<T>(object target, string member) where T : class
        {
            object value = Read(target, member);
            Assert.True(value is T,
                $"{target.GetType().FullName}.{member} is a {value.GetType().FullName}, not a {typeof(T).Name}, " +
                $"on {ServerFlavor.Name}; setup is invalid");
            return (T)value;
        }
    }
}
