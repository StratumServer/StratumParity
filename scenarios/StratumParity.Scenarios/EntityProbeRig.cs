using Atlas.Api;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Xunit;

namespace StratumParity.Scenarios;

/// <summary>One counted entity of a probe rig: the entity, where it was spawned and its tick counter.</summary>
internal sealed record RigEntity(Entity Entity, BlockPos Pos, TickCounterBehavior Counter);

/// <summary>
/// The setup steps the entity tick and physics probes share, with the distances left to the
/// caller (EntityTickingProbes.SpawnProbePair keeps its fixed 8 and 200 block pair). A typical
/// scenario:
/// <code>
/// ITestPlayer anchor = await EntityProbeRig.JoinAnchor(World, "my-anchor");
/// BlockPos origin = anchor.Position;
/// RigEntity mid = await EntityProbeRig.SpawnCountedLoaded(World, origin.AddCopy(48, 1, 0), "game:strawdummy");
/// RigEntity far = await EntityProbeRig.SpawnCountedLoaded(World, origin.AddCopy(80, 1, 0), "game:strawdummy");
/// await World.Ticks(60);   // settle: Stratum throttles only entities at rest
/// ... measure with Counter.Ticks, Counter.DtSum and World.EntitySimulationTicks ...
/// EntityProbeRig.AssertAnchorStayed(anchor, origin);
/// </code>
/// Rules of the engine the rig respects: distance bands are measured from the nearest Playing
/// client, a join scatters players up to the spawn radius (hence the teleport to the world
/// spawn), and the entity throttle has no force-loaded exemption (so keeping a column loaded
/// cannot bias a count). Use a unique anchor name per scenario: test players persist across the
/// scenarios of a class.
/// </summary>
internal static class EntityProbeRig
{
    /// <summary>Joins a Playing player and pins it at the world spawn; returns it with its position
    /// read after the teleport settled (take <c>anchor.Position</c> as the origin of the geometry).</summary>
    public static async Task<ITestPlayer> JoinAnchor(IWorldSession world, string name)
    {
        ITestPlayer anchor = await world.JoinPlayer(name);
        await world.Ticks(2);
        await anchor.TeleportTo(world.Spawn);
        await world.Ticks(2);
        return anchor;
    }

    /// <summary>Loads the column holding <paramref name="pos"/> and keeps it loaded, waiting up to 600 ticks.</summary>
    public static async Task LoadColumnKept(IWorldSession world, BlockPos pos)
    {
        world.Api.WorldManager.LoadChunkColumnPriority(pos.X / 32, pos.Z / 32, new ChunkLoadOptions { KeepLoaded = true });
        await world.Until(() => world.Api.World.BlockAccessor.GetChunkAtBlockPos(pos) != null, timeoutTicks: 600);
    }

    /// <summary>Spawns an entity of the given code at the position and attaches a tick counter to it. The
    /// column must already be loaded (see <see cref="LoadColumnKept"/>).</summary>
    public static RigEntity SpawnCounted(IWorldSession world, BlockPos pos, string entityCode)
    {
        Entity entity = world.SpawnEntity(entityCode, pos);
        var counter = new TickCounterBehavior(entity);
        entity.SidedProperties.Behaviors.Add(counter);
        return new RigEntity(entity, pos, counter);
    }

    /// <summary>LoadColumnKept then SpawnCounted.</summary>
    public static async Task<RigEntity> SpawnCountedLoaded(IWorldSession world, BlockPos pos, string entityCode)
    {
        await LoadColumnKept(world, pos);
        return SpawnCounted(world, pos, entityCode);
    }

    /// <summary>End-of-window guard with setup-failure semantics: an anchor that drifted more than
    /// <paramref name="maxBlocks"/> (3D) from where it was pinned would have moved the entities across a
    /// band edge, so the run is invalid rather than silently miscounted. Keep every entity at least
    /// 16 blocks from each band edge (32, 64, 96) and the default of 8 is safe.</summary>
    public static void AssertAnchorStayed(ITestPlayer anchor, BlockPos origin, double maxBlocks = 8)
    {
        BlockPos p = anchor.Position;
        double dx = p.X - origin.X, dy = p.Y - origin.Y, dz = p.Z - origin.Z;
        double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        Assert.True(distance <= maxBlocks,
            $"anchor drifted {distance:F1} blocks from its origin on {ServerFlavor.Name} (limit {maxBlocks}); setup is invalid");
    }
}
