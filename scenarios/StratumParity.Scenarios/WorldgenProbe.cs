using System.Globalization;
using System.Text;
using Atlas.Api;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Xunit;

namespace StratumParity.Scenarios;

/// <summary>One call of the probe handler registered at <see cref="Pass"/>: what the column looked like at that moment.</summary>
/// <param name="Pass">The pass the handler is registered for.</param>
/// <param name="CurrentPass">The CurrentPass of the column's own map chunk during the call; the engine contract says it equals Pass.</param>
/// <param name="Rock">Solid blocks whose code starts with rock- in the whole column.</param>
/// <param name="Granite">Of those, rock-granite (the terrain noise only places granite, the strata swap it later).</param>
/// <param name="Air">Positions with an empty solid layer in the whole column (caves carve them).</param>
/// <param name="NeighboursReady">Neighbouring map chunks (of eight) whose CurrentPass was at or above Pass; eight for every pass after Terrain.</param>
/// <param name="Error">Set instead of the numbers when the handler itself threw.</param>
public sealed record PassRecord(EnumWorldGenPass Pass, EnumWorldGenPass CurrentPass, int Rock, int Granite, int Air, int NeighboursReady, string? Error = null)
{
    public override string ToString() => Error != null
        ? $"error({Error})"
        : $"{Pass}[current={CurrentPass} rock={Rock} granite={Granite} air={Air} ready={NeighboursReady}]";
}

/// <summary>
/// Reader for the staged stratumparityworldgen mod (mods/worldgenprobe), which registers a handler at
/// each of the five passes of the standard world and appends one record per call to the moddata of
/// the column's first chunk. A class that uses it declares
/// <c>[AtlasWorld(WorldType = "standard", Seed = WorldgenGoldens.Seed, Mods = new[] { "mods/worldgenprobe" })]</c>,
/// loads an area (WorldgenArea.LoadDone) and reads the inner columns:
/// <code>
/// List&lt;PassRecord&gt; records = WorldgenProbe.Read(World, cx, cz);
/// Assert.Equal(WorldgenProbe.ExpectedPasses, records.Select(r => r.Pass));
/// </code>
/// The records live in chunk moddata, so they survive a column unload and describe the generation
/// that produced the column, not a later load.
/// </summary>
public static class WorldgenProbe
{
    // Mirrors WorldgenProbeModSystem in the mod source.
    private const string ModSystemName = "StratumParityWorldgen.WorldgenProbeModSystem";
    private const string RecordKey = "stratumparityworldgen:records";

    /// <summary>The five passes a vanilla worldgen calls handlers for, in order (Done is not an event).</summary>
    public static readonly EnumWorldGenPass[] ExpectedPasses =
    {
        EnumWorldGenPass.Terrain,
        EnumWorldGenPass.TerrainFeatures,
        EnumWorldGenPass.Vegetation,
        EnumWorldGenPass.NeighbourSunLightFlood,
        EnumWorldGenPass.PreDone,
    };

    /// <summary>The records of one generated column, in call order. Empty when the handlers never ran for it.</summary>
    public static List<PassRecord> Read(IWorldSession world, int cx, int cz)
    {
        ModSystem? system = world.Api.ModLoader.GetModSystem(ModSystemName);
        Assert.True(system != null,
            $"mod system {ModSystemName} is not loaded on {ServerFlavor.Name}: the class must stage mods/worldgenprobe; setup is invalid");

        IWorldChunk? chunk = world.Api.World.BlockAccessor.GetChunk(cx, 0, cz);
        Assert.True(chunk != null, $"chunk ({cx},0,{cz}) is not loaded on {ServerFlavor.Name}; setup is invalid");

        byte[]? raw = chunk!.GetModdata(RecordKey);
        var records = new List<PassRecord>();
        if (raw == null)
        {
            return records;
        }

        foreach (string line in Encoding.UTF8.GetString(raw).Split('\n'))
        {
            string[] f = line.Split(';');
            if (f[0] == "error")
            {
                records.Add(new PassRecord(EnumWorldGenPass.None, EnumWorldGenPass.None, 0, 0, 0, 0, f.Length > 1 ? f[1] : "unknown"));
                continue;
            }

            records.Add(new PassRecord(
                Enum.Parse<EnumWorldGenPass>(f[0]), Enum.Parse<EnumWorldGenPass>(f[1]),
                int.Parse(f[2], CultureInfo.InvariantCulture), int.Parse(f[3], CultureInfo.InvariantCulture),
                int.Parse(f[4], CultureInfo.InvariantCulture), int.Parse(f[5], CultureInfo.InvariantCulture)));
        }

        return records;
    }

    /// <summary>One line for assertion messages.</summary>
    public static string Describe(IEnumerable<PassRecord> records) => string.Join(", ", records);
}
