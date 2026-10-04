using System.Text;
using Atlas.Api;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Xunit;

namespace StratumParity.Scenarios;

/// <summary>The boot records read back from everything the persist probe writes. Each list is
/// the comma separated record split into its entries: after two boots all four read
/// ["boot1", "boot2"]. A place the mod never reached is an empty list.</summary>
public sealed record PersistState(string[] SaveGame, string[] Chunk, string[] MapChunk, string[] MapRegion, bool[] Markers)
{
    /// <summary>One line per place, for assertion messages.</summary>
    public override string ToString() =>
        $"savegame=[{string.Join(",", SaveGame)}] chunk=[{string.Join(",", Chunk)}] mapchunk=[{string.Join(",", MapChunk)}] " +
        $"mapregion=[{string.Join(",", MapRegion)}] markers=[{string.Join(",", Markers)}]";
}

/// <summary>
/// Reader for the staged stratumparitypersist mod (mods/persistprobe). The mod needs nothing from
/// the scenario: at every boot it appends "boot1", "boot2", ... to the savegame store
/// (SaveGameLoaded) and to the chunk, map chunk and map region moddata of the world centre column
/// (ChunkColumnLoaded of the centre column, which the engine loads at startup), and places one granite marker
/// block per boot. This class mirrors the keys and the marker layout and reads them back, so a
/// scenario with [AtlasScenario(RestartWorld = true)] only has to ask <see cref="Read"/>:
/// <code>
/// [AtlasWorld(Mods = new[] { "mods/persistprobe" })]
/// ...
/// PersistState state = await PersistProbe.Read(World, boots: 2);
/// </code>
/// The first boot happens before the scenario body (a restart scenario that is the first of its
/// class boots once, restarts, and runs on the second boot), so a world that was restarted once
/// must read two boots everywhere. Joined players do not survive a restart: join none before it.
/// </summary>
public static class PersistProbe
{
    // Mirrors PersistProbeModSystem in the mod source.
    private const string BootsKey = "stratumparitypersist:boots";
    private const string ChunkKey = "stratumparitypersist:chunk";
    private const string MapChunkKey = "stratumparitypersist:mapchunk";
    private const string MapRegionKey = "stratumparitypersist:mapregion";
    private const int MarkerY = 100;
    private const int ChunkSize = 32;

    /// <summary>Block position at the minimum corner of the world centre column (where Atlas spawns),
    /// the one the mod records into.</summary>
    public static BlockPos Anchor(IWorldSession world) =>
        new BlockPos(world.Api.WorldManager.MapSizeX / 2 / ChunkSize * ChunkSize, 0, world.Api.WorldManager.MapSizeZ / 2 / ChunkSize * ChunkSize, 0);

    /// <summary>Where the marker block of boot number <paramref name="boot"/> (1-based) sits.</summary>
    public static BlockPos MarkerPos(IWorldSession world, int boot)
    {
        BlockPos anchor = Anchor(world);
        return new BlockPos(anchor.X + boot, MarkerY, anchor.Z + 1, 0);
    }

    /// <summary>Makes sure the world centre column is loaded (kept loaded) and reads every record,
    /// with a marker flag per boot from 1 to <paramref name="boots"/>.</summary>
    public static async Task<PersistState> Read(IWorldSession world, int boots)
    {
        BlockPos anchor = Anchor(world);
        await ChunkPersistence.LoadColumn(world, anchor, keepLoaded: true);

        IMapChunk map = world.Api.WorldManager.GetMapChunk(anchor.X / ChunkSize, anchor.Z / ChunkSize);
        Assert.True(map != null, $"map chunk of the world centre column is not loaded on {ServerFlavor.Name}; setup is invalid");
        IMapRegion region = world.Api.WorldManager.GetMapRegion(
            anchor.X / world.Api.WorldManager.RegionSize, anchor.Z / world.Api.WorldManager.RegionSize);
        Assert.True(region != null, $"map region of the world centre column is not loaded on {ServerFlavor.Name}; setup is invalid");
        IWorldChunk chunk = world.Api.World.BlockAccessor.GetChunkAtBlockPos(anchor); // chunk Y 0 holds the moddata

        // The mod sets the marker of a boot from a callback shortly after its column loads, so give the
        // newest one a moment to appear before reporting it missing.
        var markers = new bool[boots];
        for (int attempt = 0; attempt < 300; attempt++)
        {
            for (int boot = 1; boot <= boots; boot++)
            {
                markers[boot - 1] = world.BlockAt(MarkerPos(world, boot)).Code.ToString() == "game:rock-granite";
            }

            if (markers.All(m => m))
            {
                break;
            }

            await world.Ticks(1);
        }

        return new PersistState(
            Split(world.Api.WorldManager.SaveGame.GetData(BootsKey)),
            Split(chunk.GetModdata(ChunkKey)),
            Split(map.GetModdata(MapChunkKey)),
            Split(region.GetModdata(MapRegionKey)),
            markers);
    }

    private static string[] Split(byte[]? data) =>
        data == null ? Array.Empty<string>() : Encoding.UTF8.GetString(data).Split(',');
}
