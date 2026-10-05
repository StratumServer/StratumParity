using System;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace StratumParityPersist
{
    /// <summary>
    /// Leaves one record per server boot in everything the engine persists across a restart:
    /// the savegame store, and the chunk, the map chunk and the map region of the world centre column
    /// (Atlas spawns there).
    /// A record is the text "boot1", "boot2", ... appended to a comma separated list, so a
    /// world that went through two boots reads "boot1,boot2" in all four places. The centre column
    /// also gets one marker block per boot (granite at local x = boot number, y = 100, z = 1).
    ///
    /// The savegame record is written at SaveGameLoaded. The column records are written from the
    /// ChunkColumnLoaded event of the centre column, which the engine loads (or generates, on the
    /// first boot) during startup, before the world is handed out: nothing has to request it. Both
    /// writes are idempotent per boot, so a column that unloads and loads again inside one boot is
    /// not recorded twice. The scenario side (StratumParity.Scenarios.PersistProbe) knows the keys
    /// and the layout; the ModLoader compiles this source into its own assembly, so nothing is
    /// shared in code.
    /// </summary>
    public class PersistProbeModSystem : ModSystem
    {
        public const string BootsKey = "stratumparitypersist:boots";
        public const string ChunkKey = "stratumparitypersist:chunk";
        public const string MapChunkKey = "stratumparitypersist:mapchunk";
        public const string MapRegionKey = "stratumparitypersist:mapregion";
        public const int MarkerY = 100;
        public const int ChunkSize = 32;

        private ICoreServerAPI sapi;
        private int bootNumber;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            api.Event.SaveGameLoaded += OnSaveGameLoaded;
            api.Event.ChunkColumnLoaded += OnChunkColumnLoaded;
        }

        private string Boot => "boot" + bootNumber;

        private void OnSaveGameLoaded()
        {
            string previous = Decode(sapi.WorldManager.SaveGame.GetData(BootsKey));
            bootNumber = previous.Length == 0 ? 1 : previous.Split(',').Length + 1;
            sapi.WorldManager.SaveGame.StoreData(BootsKey, Encode(Append(previous, Boot)));
        }

        private void OnChunkColumnLoaded(Vec2i chunkCoord, IWorldChunk[] chunks)
        {
            if (bootNumber == 0)
            {
                return;
            }

            // The world centre, which is where Atlas puts the spawn. The default spawn itself is not
            // known yet while the startup columns load, so it cannot be the key.
            int cx = sapi.WorldManager.MapSizeX / 2 / ChunkSize;
            int cz = sapi.WorldManager.MapSizeZ / 2 / ChunkSize;
            if (chunkCoord.X != cx || chunkCoord.Y != cz)
            {
                return;
            }

            chunks[0].SetModdata(ChunkKey, Encode(Append(Decode(chunks[0].GetModdata(ChunkKey)), Boot)));
            chunks[0].MarkModified();

            IMapChunk mapChunk = sapi.WorldManager.GetMapChunk(cx, cz);
            mapChunk.SetModdata(MapChunkKey, Encode(Append(Decode(mapChunk.GetModdata(MapChunkKey)), Boot)));
            mapChunk.MarkDirty();

            int regionSize = sapi.WorldManager.RegionSize;
            IMapRegion region = sapi.WorldManager.GetMapRegion(cx * ChunkSize / regionSize, cz * ChunkSize / regionSize);
            region.SetModdata(MapRegionKey, Encode(Append(Decode(region.GetModdata(MapRegionKey)), Boot)));
            region.DirtyForSaving = true;

            // The marker is written two ways. Straight into the chunk it survives the first boot, where
            // the column was generated a moment ago and nothing may stop before the world is ready; a
            // column read back from the database has no block data to write into yet (Data is null), so
            // a callback also sets the block through the accessor once the column is part of the world.
            Block granite = sapi.World.GetBlock(new AssetLocation("game:rock-granite"));
            var markerPos = new BlockPos(cx * ChunkSize + bootNumber, MarkerY, cz * ChunkSize + 1, 0);
            sapi.Event.RegisterCallback(dt => sapi.World.BlockAccessor.SetBlock(granite.BlockId, markerPos), 100);

            IWorldChunk markerChunk = chunks[MarkerY / ChunkSize];
            if (markerChunk.Data != null)
            {
                markerChunk.Data[((MarkerY % ChunkSize) * ChunkSize + 1) * ChunkSize + bootNumber] = granite.BlockId;
                markerChunk.MarkModified();
            }
        }

        private string Append(string list, string boot)
        {
            if (list.Length == 0)
            {
                return boot;
            }

            return list.EndsWith(boot, StringComparison.Ordinal) ? list : list + "," + boot;
        }

        private static string Decode(byte[] data) => data == null ? string.Empty : Encoding.UTF8.GetString(data);

        private static byte[] Encode(string text) => Encoding.UTF8.GetBytes(text);
    }
}
