using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace StratumParityProbe
{
    public class RandomTickProbeModSystem : ModSystem
    {
        // The ModLoader compiles this source mod into its own assembly, so scenarios cannot
        // reference its types: the logging blocks below publish BCL-typed queues as public
        // fields of this mod system, which the scenario side reaches through
        // api.ModLoader.GetModSystem("StratumParityProbe.RandomTickProbeModSystem") and
        // reflection (StratumParity.Scenarios.ProbeLog owns the readers and the entry layouts).
        // api.ObjectCache is not an option: the ModLoader compiles mods against a reference set
        // without System.Collections, so any member typed Dictionary<,> fails with CS0012.

        /// <summary>Game-tick passes counted by a 1 ms game tick listener, the same unit as Atlas's tick.</summary>
        public readonly long[] Ticks = new long[1];

        /// <summary>One long[8] per call: x, y, z, neibX, neibY, neibZ, tick, Stopwatch timestamp.</summary>
        public readonly ConcurrentQueue<long[]> NeighbourCalls = new ConcurrentQueue<long[]>();

        /// <summary>One long[4] per call: x, y, z, Stopwatch timestamp.</summary>
        public readonly ConcurrentQueue<long[]> RandomTickCalls = new ConcurrentQueue<long[]>();

        public override void Start(ICoreAPI api)
        {
            base.Start(api);
            api.RegisterBlockClass("RandomTickProbeBlock", typeof(RandomTickProbeBlock));
            api.RegisterBlockClass("RandomTickPositionProbeBlock", typeof(RandomTickPositionProbeBlock));
            api.RegisterBlockClass("NeighbourCounterBlock", typeof(NeighbourCounterBlock));
            api.RegisterBlockClass("RandomTickCounterBlock", typeof(RandomTickCounterBlock));
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            api.Event.RegisterGameTickListener(dt => Interlocked.Increment(ref Ticks[0]), 1);
        }
    }

    /// <summary>
    /// Opts into every server random tick and converts itself to granite when one lands.
    /// The world itself is the counter: scenarios place a platform of these and count how
    /// many turned to granite, no cross-assembly state needed (the ModLoader compiles
    /// this source mod into its own assembly).
    /// </summary>
    public class RandomTickProbeBlock : Block
    {
        public override bool ShouldReceiveServerGameTicks(IWorldAccessor world, BlockPos pos, Random offThreadRandom, out object extra)
        {
            extra = null;
            return true;
        }

        public override void OnServerGameTick(IWorldAccessor world, BlockPos pos, object extra = null)
        {
            Block granite = world.GetBlock(new AssetLocation("game:rock-granite"));
            world.BlockAccessor.SetBlock(granite.BlockId, pos);
        }
    }

    /// <summary>
    /// Keeps the position object every random tick hands to it, the way a mod may when it
    /// schedules a delayed callback or stores the position in a list: the engine gives each
    /// handler its own copy (vanilla allocates one per queued tick), so nothing else should
    /// ever touch it again. The world is the counter, as above. A tick turns its block into
    /// andesite, which counts how many handlers ran. On later ticks the block audits the
    /// positions it kept: one whose coordinates changed since the handler returned means the
    /// engine recycled the object, and the block at the position it was originally handed out
    /// for turns to granite. Ported from Stratum's tests/StratumScenarios (StratumServer/Stratum#353).
    /// </summary>
    public class RandomTickPositionProbeBlock : Block
    {
        // Bounded so the audit stays cheap when nothing ever moves.
        private const int MaxKept = 256;

        private readonly List<(BlockPos Pos, int X, int Y, int Z)> kept = new List<(BlockPos, int, int, int)>();

        public override bool ShouldReceiveServerGameTicks(IWorldAccessor world, BlockPos pos, Random offThreadRandom, out object extra)
        {
            extra = null;
            return true;
        }

        public override void OnServerGameTick(IWorldAccessor world, BlockPos pos, object extra = null)
        {
            Block granite = world.GetBlock(new AssetLocation("game:rock-granite"));
            for (int i = kept.Count - 1; i >= 0; i--)
            {
                var entry = kept[i];
                if (entry.Pos.X != entry.X || entry.Pos.Y != entry.Y || entry.Pos.Z != entry.Z)
                {
                    world.BlockAccessor.SetBlock(granite.BlockId, new BlockPos(entry.X, entry.Y, entry.Z, 0));
                    kept.RemoveAt(i);
                }
            }

            if (kept.Count < MaxKept)
            {
                kept.Add((pos, pos.X, pos.Y, pos.Z));
            }

            Block andesite = world.GetBlock(new AssetLocation("game:rock-andesite"));
            world.BlockAccessor.SetBlock(andesite.BlockId, pos);
        }
    }

    /// <summary>
    /// Logs every OnNeighbourBlockChange it receives, as (pos, neibpos, tick, timestamp), into
    /// the NeighbourCalls queue of the mod system. The coordinates are copied on the spot: the
    /// engine may reuse the BlockPos objects it passes in. Self notifications from a bulk
    /// accessor commit arrive with pos equal to neibpos.
    /// </summary>
    public class NeighbourCounterBlock : Block
    {
        private RandomTickProbeModSystem probe;

        public override void OnNeighbourBlockChange(IWorldAccessor world, BlockPos pos, BlockPos neibpos)
        {
            base.OnNeighbourBlockChange(world, pos, neibpos);

            probe ??= world.Api.ModLoader.GetModSystem<RandomTickProbeModSystem>();
            probe.NeighbourCalls.Enqueue(new long[]
            {
                pos.X, pos.Y, pos.Z, neibpos.X, neibpos.Y, neibpos.Z,
                Interlocked.Read(ref probe.Ticks[0]),
                Stopwatch.GetTimestamp()
            });
        }
    }

    /// <summary>
    /// Counts the random ticks the engine samples onto it without ever accepting one: logs
    /// (x, y, z, timestamp) into the RandomTickCalls queue of the mod system from
    /// ShouldReceiveServerGameTicks, which runs off the main thread, and returns false so
    /// OnServerGameTick never fires and the world stays untouched. A chunk filled with these
    /// shows the per-chunk random tick budget as bursts of closely spaced timestamps.
    /// </summary>
    public class RandomTickCounterBlock : Block
    {
        private RandomTickProbeModSystem probe;

        public override bool ShouldReceiveServerGameTicks(IWorldAccessor world, BlockPos pos, Random offThreadRandom, out object extra)
        {
            extra = null;
            probe ??= world.Api.ModLoader.GetModSystem<RandomTickProbeModSystem>();
            probe.RandomTickCalls.Enqueue(new long[] { pos.X, pos.Y, pos.Z, Stopwatch.GetTimestamp() });
            return false;
        }
    }
}
