using System.Collections;
using System.Reflection;
using Atlas.Api;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Xunit;

namespace StratumParity.Scenarios;

/// <summary>
/// Loading a rectangle of chunk columns through the worldgen and getting each one to Done, the
/// first step of every golden comparison. Coordinates are chunk coordinates; a rectangle is its
/// minimum corner plus a width (x) and a depth (z) in columns. The engine only completes a column
/// when its eight neighbours are one pass behind, so generating a rectangle to Done generates
/// about four more columns around it: hash the inner columns (<see cref="Inner"/>), never the edge.
/// </summary>
public static class WorldgenArea
{
    public const int ChunkSize = 32;

    /// <summary>Every column of the rectangle, x outer, z inner.</summary>
    public static IEnumerable<(int Cx, int Cz)> Columns(int cx0, int cz0, int width, int depth)
    {
        for (int x = 0; x < width; x++)
        {
            for (int z = 0; z < depth; z++)
            {
                yield return (cx0 + x, cz0 + z);
            }
        }
    }

    /// <summary>The columns of the rectangle that are not on its edge (margin 1 gives the inner 4x4 of a 6x6).</summary>
    public static IEnumerable<(int Cx, int Cz)> Inner(int cx0, int cz0, int width, int depth, int margin = 1) =>
        Columns(cx0 + margin, cz0 + margin, width - 2 * margin, depth - 2 * margin);

    /// <summary>True when the column's map chunk has reached Done and its top chunk is loaded.</summary>
    public static bool IsDone(IWorldSession world, int cx, int cz)
    {
        IMapChunk? map = world.Api.WorldManager.GetMapChunk(cx, cz);
        int top = world.Api.WorldManager.MapSizeY / ChunkSize - 1;
        return map != null
               && map.CurrentPass == EnumWorldGenPass.Done
               && world.Api.World.BlockAccessor.GetChunk(cx, top, cz) != null;
    }

    /// <summary>Requests the rectangle with LoadChunkColumnPriority, kept loaded, and waits until every
    /// column of it is Done. The columns stay loaded until <see cref="Unload"/>.</summary>
    public static async Task LoadDone(IWorldSession world, int cx0, int cz0, int width, int depth, int timeoutTicks = 9000)
    {
        world.Api.WorldManager.LoadChunkColumnPriority(
            cx0, cz0, cx0 + width - 1, cz0 + depth - 1, new ChunkLoadOptions { KeepLoaded = true });
        await world.Until(() => Columns(cx0, cz0, width, depth).All(c => IsDone(world, c.Cx, c.Cz)), timeoutTicks);
    }

    /// <summary>Releases the rectangle's columns (UnloadChunkColumn on each).</summary>
    public static void Unload(IWorldSession world, int cx0, int cz0, int width, int depth)
    {
        foreach ((int cx, int cz) in Columns(cx0, cz0, width, depth))
        {
            world.Api.WorldManager.UnloadChunkColumn(cx, cz);
        }
    }

    /// <summary>The codes of the generated structures whose bounding box touches the rectangle grown by
    /// <paramref name="marginBlocks"/>. Structures are placed by order dependent passes and rivulets
    /// avoid them, so a golden rectangle should return an empty list; assert that as "setup is invalid".</summary>
    public static List<string> Structures(IWorldSession world, int cx0, int cz0, int width, int depth, int marginBlocks = 32)
    {
        int minX = cx0 * ChunkSize - marginBlocks;
        int minZ = cz0 * ChunkSize - marginBlocks;
        int maxX = (cx0 + width) * ChunkSize + marginBlocks;
        int maxZ = (cz0 + depth) * ChunkSize + marginBlocks;
        int regionSize = world.Api.WorldManager.RegionSize;

        var found = new List<string>();
        for (int rx = minX / regionSize; rx <= maxX / regionSize; rx++)
        {
            for (int rz = minZ / regionSize; rz <= maxZ / regionSize; rz++)
            {
                IMapRegion? region = world.Api.WorldManager.GetMapRegion(rx, rz);
                if (region == null)
                {
                    continue;
                }

                foreach (GeneratedStructure structure in region.GeneratedStructures)
                {
                    var box = structure.Location;
                    if (box.X1 < maxX && box.X2 > minX && box.Z1 < maxZ && box.Z2 > minZ)
                    {
                        found.Add($"{structure.Code} at {box.X1},{box.Y1},{box.Z1}");
                    }
                }
            }
        }

        return found;
    }
}

/// <summary>Which worldgen topology the running Stratum build booted with.</summary>
public static class WorldgenTopology
{
    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// How many generators Stratum moved into the second sub-stage of the Terrain pass
    /// (WorldGenHandler.OnChunkColumnGenTerrainLate). Positive means the split is active; zero means
    /// the stock single stage, which is what vanilla always has, what Stratum falls back to when a
    /// foreign handler is registered at the Terrain pass (Worldgen.AutoDisableSplitForMods) and what
    /// Worldgen.SplitTerrainPass false gives. Use it as a setup guard: a class that is about to
    /// assert on one topology fails as "setup is invalid" when the engine booted in the other.
    /// </summary>
    public static int TerrainLateHandlers(IWorldSession world, string worldType = "standard")
    {
        if (!ServerFlavor.IsStratum)
        {
            return 0;
        }

        object server = world.Api.World;
        object? events = server.GetType().GetField("ModEventManager", AnyInstance)?.GetValue(server)
                         ?? server.GetType().GetProperty("ModEventManager", AnyInstance)?.GetValue(server);
        Assert.True(events != null, "ServerMain.ModEventManager was not found by reflection; setup is invalid");

        MethodInfo? getHandler = events!.GetType().GetMethod("GetWorldGenHandler", AnyInstance);
        Assert.True(getHandler != null, "ServerEventManager.GetWorldGenHandler was not found by reflection; setup is invalid");
        object? handler = getHandler!.Invoke(events, new object[] { worldType });
        Assert.True(handler != null, $"no worldgen handler for world type {worldType}; setup is invalid");

        object? late = handler!.GetType().GetField("OnChunkColumnGenTerrainLate", AnyInstance)?.GetValue(handler);
        Assert.True(late is ICollection,
            $"WorldGenHandler.OnChunkColumnGenTerrainLate was not found on {ServerFlavor.Version}; setup is invalid");
        return ((ICollection)late!).Count;
    }
}

/// <summary>
/// One column generated in memory up to a pass with WorldManager.PeekChunkColumn, without loading it.
/// Measured with the terrain digest on seed 7351: vanilla peeks to Terrain are the same on every boot in
/// most columns, but 4 of 16 columns of the golden area differ between the first standard world of a
/// process and the later ones; Stratum peeks to Terrain stop before the rock strata, the caves and the
/// block layers (the split Terrain pass is cut at its first sub-stage: cave air 0 in most columns, rock
/// hashes unlike vanilla in every column), so a peek to Terrain is not comparable across flavors. Peek
/// to TerrainFeatures or later to compare, and expect deposits and vegetation to differ (see
/// <see cref="WorldgenDigest"/>).
/// </summary>
public static class WorldgenPeek
{
    /// <summary>The chunks of the column at (cx, cz) after worldgen ran up to <paramref name="untilPass"/>, bottom chunk first.</summary>
    public static async Task<IServerChunk[]> Column(IWorldSession world, int cx, int cz, EnumWorldGenPass untilPass = EnumWorldGenPass.Terrain, int timeoutTicks = 1200)
    {
        IServerChunk[]? result = null;
        world.Api.WorldManager.PeekChunkColumn(cx, cz, new ChunkPeekOptions
        {
            UntilPass = untilPass,
            OnGenerated = columns =>
            {
                foreach (var column in columns)
                {
                    if (column.Key.X == cx && column.Key.Y == cz)
                    {
                        result = column.Value;
                    }
                }
            },
        });
        await world.Until(() => result != null, timeoutTicks);
        return result!;
    }
}
