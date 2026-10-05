using System;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace StratumParityWorldgen
{
    /// <summary>
    /// Registers one ChunkColumnGeneration handler at each of the five worldgen passes of the
    /// "standard" world type and appends one line per call to the moddata of the column's first
    /// chunk, under <see cref="RecordKey"/>. Each line is "pass;currentPass;rock;granite;air;ready":
    ///   pass         the pass the handler was registered for (Terrain, TerrainFeatures, ...);
    ///   currentPass  the CurrentPass of the column's own map chunk at the moment of the call, which
    ///                the engine contract says equals pass;
    ///   rock         blocks of the solid layer whose code starts with rock- in the whole column;
    ///   granite      of those, rock-granite (the terrain noise places granite, the strata pass
    ///                swaps it for the real rock);
    ///   air          positions of the whole column with an empty solid layer (caves carve them);
    ///   ready        how many of the eight neighbouring map chunks had CurrentPass at or above
    ///                pass (the engine contract says all eight for every pass after Terrain).
    /// A handler that throws appends "error;" plus the exception text instead. Lines are separated
    /// by a newline and read back by StratumParity.Scenarios.WorldgenProbe. The handler registers
    /// last (ExecuteOrder 100), so it sees what every vanilla generator of its pass has done.
    ///
    /// The ModLoader compiles this source into its own assembly, so the scenario side reaches the
    /// records through the chunk moddata and shares no type with it.
    /// </summary>
    public class WorldgenProbeModSystem : ModSystem
    {
        public const string RecordKey = "stratumparityworldgen:records";
        private const int ChunkVolume = 32 * 32 * 32;

        private static readonly EnumWorldGenPass[] Passes =
        {
            EnumWorldGenPass.Terrain,
            EnumWorldGenPass.TerrainFeatures,
            EnumWorldGenPass.Vegetation,
            EnumWorldGenPass.NeighbourSunLightFlood,
            EnumWorldGenPass.PreDone,
        };

        private ICoreServerAPI sapi;
        private readonly object flagLock = new object();
        private bool[] isRock;
        private bool[] isGranite;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override double ExecuteOrder() => 100;

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            foreach (EnumWorldGenPass pass in Passes)
            {
                EnumWorldGenPass forPass = pass;
                api.Event.ChunkColumnGeneration(request => Record(request, forPass), forPass, "standard");
            }
        }

        private void Record(IChunkColumnGenerateRequest request, EnumWorldGenPass pass)
        {
            IServerChunk[] chunks = request.Chunks;
            string line;
            try
            {
                EnsureFlags();

                int rock = 0;
                int granite = 0;
                int air = 0;
                for (int c = 0; c < chunks.Length; c++)
                {
                    IChunkBlocks data = chunks[c].Data;
                    for (int i = 0; i < ChunkVolume; i++)
                    {
                        int id = data.GetBlockId(i, BlockLayersAccess.Solid);
                        if (id == 0)
                        {
                            air++;
                        }
                        else if (isRock[id])
                        {
                            rock++;
                            if (isGranite[id])
                            {
                                granite++;
                            }
                        }
                    }
                }

                int ready = 0;
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dz == 0)
                        {
                            continue;
                        }

                        IMapChunk neighbour = sapi.WorldManager.GetMapChunk(request.ChunkX + dx, request.ChunkZ + dz);
                        if (neighbour != null && neighbour.CurrentPass >= pass)
                        {
                            ready++;
                        }
                    }
                }

                line = pass + ";" + chunks[0].MapChunk.CurrentPass + ";" + rock + ";" + granite + ";" + air + ";" + ready;
            }
            catch (Exception e)
            {
                line = "error;" + e.GetType().Name + ": " + e.Message.Replace('\n', ' ');
            }

            byte[] existing = chunks[0].GetModdata(RecordKey);
            string text = existing == null ? line : Encoding.UTF8.GetString(existing) + "\n" + line;
            chunks[0].SetModdata(RecordKey, Encoding.UTF8.GetBytes(text));
        }

        private void EnsureFlags()
        {
            if (isGranite != null)
            {
                return;
            }

            lock (flagLock)
            {
                if (isGranite != null)
                {
                    return;
                }

                var blocks = sapi.World.Blocks;
                var rock = new bool[blocks.Count];
                var granite = new bool[blocks.Count];
                for (int id = 0; id < blocks.Count; id++)
                {
                    Block block = blocks[id];
                    if (block == null || block.Code == null)
                    {
                        continue;
                    }

                    rock[id] = block.Code.Path.StartsWith("rock-", StringComparison.Ordinal);
                    granite[id] = block.Code.Path == "rock-granite";
                }

                isRock = rock;
                isGranite = granite;
            }
        }
    }
}
