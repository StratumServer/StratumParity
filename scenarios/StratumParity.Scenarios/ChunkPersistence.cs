using Atlas.Api;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Xunit;

namespace StratumParity.Scenarios;

/// <summary>
/// Shared steps of the chunk persistence classes (ChunkPersistenceScenarios and the batch 4
/// classes that reuse them): load a column, write a confirmed pattern and moddata, save,
/// unload, reload. Everything takes the world session and an anchor block position (the
/// column is anchor.X / 32, anchor.Z / 32), so every class keeps its own anchors 200 blocks or
/// more apart.
///
/// Two synchronization lessons are baked in. Writes are confirmed by reading them back (a
/// column still loading swallows writes silently), and a reload is by default only considered
/// complete once the probe moddata is visible again: moddata can only come from the database,
/// so its presence proves the stored column was applied rather than a fresh or regenerated
/// one. A scenario that expects the data to be LOST passes expectModdata: false and gates on
/// the column being present again instead.
/// </summary>
internal static class ChunkPersistence
{
    /// <summary>Key of the chunk moddata that <see cref="SetColumnModdata"/> writes (chunk Y 0 of the anchor).</summary>
    internal const string ModdataKey = "stratumparity:probe";

    /// <summary>Key of the map chunk moddata that <see cref="SetMapChunkModdata"/> writes.</summary>
    internal const string MapChunkModdataKey = "stratumparity:mapprobe";

    internal const int ChunkSize = 32;
    internal const int DefaultTimeoutTicks = 600;

    internal static bool IsLoaded(IWorldSession world, BlockPos anchor) =>
        world.Api.World.BlockAccessor.GetChunkAtBlockPos(anchor) != null;

    /// <summary>Requests the anchor's column with LoadChunkColumnPriority and waits for its
    /// chunk. With keepLoaded the column stays until UnloadColumn is called, otherwise it ages
    /// out by itself (about 12 to 15 s after the last use).</summary>
    internal static async Task LoadColumn(IWorldSession world, BlockPos anchor, bool keepLoaded = false)
    {
        Request(world, anchor, keepLoaded);
        await world.Until(() => IsLoaded(world, anchor), timeoutTicks: DefaultTimeoutTicks);
    }

    /// <summary>Saves the world the way an autosave does and completes when it is in the database
    /// (Atlas SaveNow). Reload steps that must not lose anything start from here.</summary>
    internal static Task SaveNow(IWorldSession world) => world.SaveNow();

    /// <summary>Unloads the anchor's column through the API and waits until it is gone.
    /// UnloadChunkColumn never persists: save first when the changes must survive.</summary>
    internal static async Task UnloadColumn(IWorldSession world, BlockPos anchor)
    {
        world.Api.WorldManager.UnloadChunkColumn(anchor.X / ChunkSize, anchor.Z / ChunkSize);
        await world.Until(() => !IsLoaded(world, anchor), timeoutTicks: DefaultTimeoutTicks);
    }

    /// <summary>Waits for the engine to unload a column nobody keeps (no player near, loaded
    /// without keepLoaded). Natural unloads take about 12 to 15 s of ticks on both flavors.</summary>
    internal static Task WaitNaturalUnload(IWorldSession world, BlockPos anchor, int timeoutTicks = 1200) =>
        world.Until(() => !IsLoaded(world, anchor), timeoutTicks: timeoutTicks);

    /// <summary>Requests the column again after an unload and waits until it is usable. With
    /// expectModdata (the default) usable means the chunk moddata is readable again, which
    /// proves the stored copy was applied; without it, the column merely has to be present
    /// again (the gate for scenarios whose data is expected to be lost).</summary>
    internal static async Task ReloadColumn(IWorldSession world, BlockPos anchor, bool expectModdata = true, bool keepLoaded = false)
    {
        Request(world, anchor, keepLoaded);
        await world.Until(
            () => IsLoaded(world, anchor) && (!expectModdata || ReadColumnModdata(world, anchor) != null),
            timeoutTicks: DefaultTimeoutTicks);
    }

    /// <summary>SaveNow, UnloadColumn, ReloadColumn: the explicit round trip every persistence
    /// scenario starts from.</summary>
    internal static async Task SaveUnloadReload(IWorldSession world, BlockPos anchor)
    {
        await SaveNow(world);
        await UnloadColumn(world, anchor);
        await ReloadColumn(world, anchor);
    }

    /// <summary>
    /// Writes a deterministic pattern of eight granite blocks inside the anchor's chunk column,
    /// salted per cycle so repeated cycles add distinct positions, then waits until every write
    /// reads back.
    /// </summary>
    internal static async Task<List<BlockPos>> WritePatternConfirmed(IWorldSession world, BlockPos anchor, int saltForCycle)
    {
        var positions = new List<BlockPos>();
        for (int i = 0; i < 8; i++)
        {
            positions.Add(anchor.AddCopy(i, 2 + saltForCycle, (i * 3) % 16));
        }

        foreach (BlockPos pos in positions)
        {
            world.SetBlock("game:rock-granite", pos);
        }
        await world.Until(
            () => positions.TrueForAll(p => world.BlockAt(p).Code.Path == "rock-granite"),
            timeoutTicks: 300);
        return positions;
    }

    /// <summary>True when every pattern position holds granite.</summary>
    internal static bool PatternPresent(IWorldSession world, IEnumerable<BlockPos> pattern) =>
        pattern.All(p => world.BlockAt(p).Code.ToString() == "game:rock-granite");

    internal static void SetColumnModdata(IWorldSession world, BlockPos anchor, byte[] payload)
    {
        IWorldChunk chunk = world.Api.World.BlockAccessor.GetChunkAtBlockPos(anchor);
        Assert.NotNull(chunk);
        chunk.SetModdata(ModdataKey, payload);
        chunk.MarkModified();
    }

    internal static byte[]? ReadColumnModdata(IWorldSession world, BlockPos anchor)
    {
        IWorldChunk? chunk = world.Api.World.BlockAccessor.GetChunkAtBlockPos(anchor);
        return chunk?.GetModdata(ModdataKey);
    }

    /// <summary>Map chunk moddata (one map chunk per column) is stored apart from the chunks and
    /// saved only when the map chunk is marked dirty, which this does.</summary>
    internal static void SetMapChunkModdata(IWorldSession world, BlockPos anchor, byte[] payload)
    {
        IMapChunk? map = world.Api.WorldManager.GetMapChunk(anchor.X / ChunkSize, anchor.Z / ChunkSize);
        Assert.True(map != null, $"map chunk of the column at {anchor} is not loaded on {ServerFlavor.Name}; setup is invalid");
        map!.SetModdata(MapChunkModdataKey, payload);
        map.MarkDirty();
    }

    internal static byte[]? ReadMapChunkModdata(IWorldSession world, BlockPos anchor) =>
        world.Api.WorldManager.GetMapChunk(anchor.X / ChunkSize, anchor.Z / ChunkSize)?.GetModdata(MapChunkModdataKey);

    internal static (int Sun, int Block)[] ReadLightLevels(IWorldSession world, BlockPos[] positions)
    {
        var readings = new (int Sun, int Block)[positions.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            readings[i] = (
                world.Api.World.BlockAccessor.GetLightLevel(positions[i], EnumLightLevelType.OnlySunLight),
                world.Api.World.BlockAccessor.GetLightLevel(positions[i], EnumLightLevelType.OnlyBlockLight));
        }
        return readings;
    }

    private static void Request(IWorldSession world, BlockPos anchor, bool keepLoaded) =>
        world.Api.WorldManager.LoadChunkColumnPriority(
            anchor.X / ChunkSize,
            anchor.Z / ChunkSize,
            keepLoaded ? new ChunkLoadOptions { KeepLoaded = true } : null);
}
