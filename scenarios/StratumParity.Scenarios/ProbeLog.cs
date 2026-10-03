using System.Collections.Concurrent;
using System.Diagnostics;
using Atlas.Api;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Xunit;

namespace StratumParity.Scenarios;

/// <summary>One OnNeighbourBlockChange(pos, neibpos) call received by a neighbourcounter block.
/// <see cref="Tick"/> counts game-tick passes (same unit as Atlas's CurrentTick, offset by an
/// unknown constant: compare records with each other only), <see cref="Ms"/> is a monotonic
/// millisecond stamp.</summary>
public sealed record NeighbourCall(BlockPos Pos, BlockPos NeibPos, long Tick, double Ms);

/// <summary>One random tick sampled onto a randomtickcounter block (its ShouldReceiveServerGameTicks call).</summary>
public sealed record RandomTickCall(BlockPos Pos, double Ms);

/// <summary>
/// Reads the logs that the staged stratumparityprobe mod (mods/randomtickprobe) publishes as public
/// queue fields of its mod system, reached by name through api.ModLoader and reflection. Drain returns everything logged since the previous drain and empties
/// the queue, so call it once before the action under test to discard noise (placing a counter
/// block notifies it too), then again after the quiet period.
/// <code>
/// ProbeLog.DrainNeighbourCalls(World);
/// ... change the world ...
/// List&lt;NeighbourCall&gt; calls = ProbeLog.DrainNeighbourCalls(World);
/// </code>
/// The class must stage the mod with [AtlasWorld(Mods = new[] { "mods/randomtickprobe" })].
/// </summary>
public static class ProbeLog
{
    // Names and entry layouts mirror RandomTickProbeModSystem in the mod source.
    private const string ModSystemName = "StratumParityProbe.RandomTickProbeModSystem";
    private const string NeighbourCallsField = "NeighbourCalls";
    private const string RandomTickCallsField = "RandomTickCalls";

    public static List<NeighbourCall> DrainNeighbourCalls(IWorldSession world)
    {
        var calls = new List<NeighbourCall>();
        foreach (long[] e in Drain(world, NeighbourCallsField))
        {
            calls.Add(new NeighbourCall(
                new BlockPos((int)e[0], (int)e[1], (int)e[2], 0),
                new BlockPos((int)e[3], (int)e[4], (int)e[5], 0),
                e[6],
                ToMs(e[7])));
        }

        return calls;
    }

    public static List<RandomTickCall> DrainRandomTickCalls(IWorldSession world)
    {
        var calls = new List<RandomTickCall>();
        foreach (long[] e in Drain(world, RandomTickCallsField))
        {
            calls.Add(new RandomTickCall(new BlockPos((int)e[0], (int)e[1], (int)e[2], 0), ToMs(e[3])));
        }

        return calls;
    }

    private static double ToMs(long stamp) => stamp * 1000.0 / Stopwatch.Frequency;

    private static List<long[]> Drain(IWorldSession world, string field)
    {
        ModSystem? system = world.Api.ModLoader.GetModSystem(ModSystemName);
        Assert.True(system != null,
            $"mod system {ModSystemName} is not loaded on {ServerFlavor.Name}: the class must stage mods/randomtickprobe; setup is invalid");

        var queue = system!.GetType().GetField(field)?.GetValue(system) as ConcurrentQueue<long[]>;
        Assert.True(queue != null, $"{ModSystemName}.{field} is missing or not a ConcurrentQueue<long[]>; setup is invalid");

        var entries = new List<long[]>();
        while (queue!.TryDequeue(out long[]? entry))
        {
            entries.Add(entry);
        }

        return entries;
    }
}
