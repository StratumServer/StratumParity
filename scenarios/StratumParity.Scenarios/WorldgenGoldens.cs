using System.Globalization;
using System.Text;
using Atlas.Api;
using Vintagestory.API.Server;
using Xunit;

namespace StratumParity.Scenarios;

/// <summary>The worldgen state of one column at the moment the Terrain pass handler of the probe mod ran:
/// rock and granite blocks and empty solid positions of the whole column (the numbers of
/// <see cref="PassRecord"/>).</summary>
public sealed record PassGolden(int ChunkX, int ChunkZ, int Rock, int Granite, int Air);

/// <summary>
/// The fixed world of the worldgen classes and the goldens shared by more than one of them, taken
/// from the vanilla leg (Vintage Story 1.22.7). Classes declare
/// <c>[AtlasWorld(WorldType = "standard", Seed = WorldgenGoldens.Seed)]</c> so that every boot of every
/// class generates the same terrain.
///
/// The goldens are the terrain layers of a 6x6 rectangle of columns (<see cref="RectChunkX"/>,
/// <see cref="RectChunkZ"/>, about 2000 blocks from spawn, land with caves and strata; the Atlas standard
/// world generates no structure anywhere, 0 in 31 map regions measured) hashed over its inner 4x4 after the engine took them to Done, and the Terrain pass records of the
/// inner 3x3 of a 5x5 rectangle at the same corner. Both were measured on eight vanilla world boots in two
/// processes (every digest identical on all of them, which is why this rectangle was picked: most other areas
/// of the same seed differ between boots), and on the pinned and the prerelease Stratum builds. They have to
/// be re-pinned on a Vintage Story bump: run the capture helper of the owning class and paste the output of
/// <see cref="Format"/> / <see cref="FormatPass"/>.
///
/// What the hashes do not cover is deliberate, see <see cref="WorldgenDigest"/>: rain height map, deposits,
/// vegetation, cave decoration.
/// </summary>
public static class WorldgenGoldens
{
    /// <summary>World seed of every worldgen class.</summary>
    public const int Seed = 7351;

    /// <summary>Minimum corner of the golden rectangle, in chunk coordinates (the world centre is 16000, 16000).</summary>
    public const int RectChunkX = 16046;

    public const int RectChunkZ = 16046;

    /// <summary>Edge of the terrain rectangle in columns; the hashed inner part is Edge - 2.</summary>
    public const int RectEdge = 6;

    /// <summary>Edge of the pass contract rectangle in columns; the recorded inner part is Edge - 2.</summary>
    public const int PassRectEdge = 5;

    /// <summary>Heights, rock and cave air of the 16 inner columns of the terrain rectangle, x outer and z inner.</summary>
    public static readonly IReadOnlyList<TerrainDigest> TerrainLayers = new TerrainDigest[]
    {
        new(16047, 16047,
            "bb6421c53bc917b2461cc66ab6400ea55b7125744b5e7fd03fc6d2cce4b840d9",
            "87b39868a6a84eb2dd9860a914483f0128d317cca6049082d40c6081fc534d72",
            137, 0),
        new(16047, 16048,
            "957c3835f19cce692a21b377d208e252aa54ca257d77163b405b0176cdb38513",
            "97bbf99bd42103458d0926bb0377d778d8bd86a472e969ad916a0d7f71c5dbdd",
            752, 0),
        new(16047, 16049,
            "3cc96b31cda5a1721b9c9bde0ea4008f797f2527d0c4a504598ce688a473d08c",
            "e9fa2a35a07e0f352a8b875524c4d2564b35a6d1bf39788e464ea01e7f01cf6e",
            366, 0),
        new(16047, 16050,
            "4b3da8720df152c5099b78d28ce80199931c4bb87ea35d00ca624265d2d82e41",
            "ccc133217fbcea5b90f7f58680bba27cd82cb20a4da4b87b4d1b34fdab4d3ddf",
            0, 0),
        new(16048, 16047,
            "50692ae5ecfc0582d5ae212100369aba7a446868e8c31a7c1ed7dea8ac2d9709",
            "93f345128f6b902f3c28a38eaf7c5c98f300eef44f393a57dfad8e4c4f07a199",
            4, 0),
        new(16048, 16048,
            "7363e77c71c8a6879ac6dc2daa0a04f8914aa125b570d51d047fac4cb641be76",
            "c511cd27ec8521d0680feef8f561406e8cfbf5f1a9672575bb4b52660bc52203",
            975, 0),
        new(16048, 16049,
            "4ec05bc5e93ec2c5117e6f0bebb97549281e03e33cd68b871a672e5a63f1244e",
            "6c06205ebbadc2ba914dac0317b57e690cb71ef31e529ac561bf608c6c968a33",
            683, 0),
        new(16048, 16050,
            "69ddb7b5077f4850ba25169d21d99f8be6b424809c0ff8a73abe8095a901134c",
            "c064de51c26aad01b1e7c7973b8347c6ee5ec4c45181add3c673c4c11b204655",
            71, 0),
        new(16049, 16047,
            "f8accf4a27bfd2e8dcf29986e0683120b8fc0a68c6850962fce056f120916ad2",
            "724bdcbe1c6be2abd2ffd54dd0d0a17d4e1333ae7e594810062de1cb5611c502",
            188, 0),
        new(16049, 16048,
            "a3c3f159b249cd9efb9b304c53fdc9bdc0d4ab91fab33ac32523f9ff194fffc2",
            "4b354c17be614361c0b4556d5eae9cc2c615235fe0e2fc2d187f45645db422c1",
            97, 0),
        new(16049, 16049,
            "cad4827fcbf835cd8c9996de82fdcc159f45a82449a02c93c6ce392b47de5332",
            "976b625086d21852eab674daa41967255979cc9bb19debe95a112dd5855a22e1",
            1300, 0),
        new(16049, 16050,
            "a007c4afd427c65f6dcefa64f9a6dccb4b3846acf94b4d5e9911b5cd22be64b2",
            "a9055df9cbdb5bee7dff4c7e12090c654110745d83f3b2ef7e4280762dec479d",
            3427, 0),
        new(16050, 16047,
            "5542083979a3dd6aa8f501aae4e35bc3f4c73aaffe55a0921239ca031c32f938",
            "73b1efe15f5d4b567bba333fafab961a4a5bd18beb64cdf47a17fa274a4890d8",
            2788, 0),
        new(16050, 16048,
            "f7822cad0de8c5b1a0d67d9ea301b531800230a1792c9b4d57e47e3e0290356d",
            "a6316e3ffb67caa1c50befc5d36ceda07c7fab03f06ddd39c8211c23f4e4614f",
            600, 0),
        new(16050, 16049,
            "e1725455e12a334462fe1324d83182c6bba409b2b6581313534a11c03008bb48",
            "f6031f3cf061f722532448fd889889f2926e8a604e638ac0fee2ab1354299654",
            217, 0),
        new(16050, 16050,
            "1c65fce8467f8018b16bea4677055e45fab8592cd1df98b51765e006edaa4784",
            "6a21987b95c78f4cfb97ae3cbb6aeb4c8828173cb167c4840a2d6c85cdc97ed4",
            844, 0),
    };

    /// <summary>Terrain pass records of the 9 inner columns of the pass rectangle, x outer and z inner.</summary>
    public static readonly IReadOnlyList<PassGolden> TerrainPass = new PassGolden[]
    {
        new(16047, 16047, 115515, 94535, 141321),
        new(16047, 16048, 115556, 94948, 141277),
        new(16047, 16049, 116451, 95374, 140338),
        new(16048, 16047, 116327, 98421, 140409),
        new(16048, 16048, 116802, 101789, 139943),
        new(16048, 16049, 119521, 107927, 137194),
        new(16049, 16047, 118459, 106794, 138007),
        new(16049, 16048, 119616, 110707, 136791),
        new(16049, 16049, 119895, 114328, 136473),
    };

    /// <summary>Loads the terrain rectangle, takes every column to Done, digests the inner 4x4 and releases it.
    /// The columns come back in the order of <see cref="TerrainLayers"/>.</summary>
    public static async Task<List<TerrainDigest>> MeasureTerrain(IWorldSession world)
    {
        await WorldgenArea.LoadDone(world, RectChunkX, RectChunkZ, RectEdge, RectEdge);
        var digests = WorldgenArea.Inner(RectChunkX, RectChunkZ, RectEdge, RectEdge)
            .Select(c => WorldgenDigest.Terrain(world, c.Cx, c.Cz))
            .ToList();
        WorldgenArea.Unload(world, RectChunkX, RectChunkZ, RectEdge, RectEdge);
        return digests;
    }

    /// <summary>One message per layer and column where the measurement differs from <see cref="TerrainLayers"/>;
    /// empty when they agree. Each message names the layer and the column, so an assertion can print them all.</summary>
    public static List<string> TerrainMismatches(IReadOnlyList<TerrainDigest> actual)
    {
        var found = new List<string>();
        if (actual.Count != TerrainLayers.Count)
        {
            found.Add($"measured {actual.Count} columns, the golden has {TerrainLayers.Count}; setup is invalid");
            return found;
        }

        for (int i = 0; i < actual.Count; i++)
        {
            TerrainDigest golden = TerrainLayers[i];
            TerrainDigest got = actual[i];
            if (got.ChunkX != golden.ChunkX || got.ChunkZ != golden.ChunkZ)
            {
                found.Add($"column {i} is ({got.ChunkX},{got.ChunkZ}), the golden expects ({golden.ChunkX},{golden.ChunkZ}); setup is invalid");
                continue;
            }

            string where = $"column ({got.ChunkX},{got.ChunkZ})";
            if (got.HeightMap != golden.HeightMap) found.Add($"{where} layer height map: {got.HeightMap[..12]} expected {golden.HeightMap[..12]}");
            if (got.Rock != golden.Rock) found.Add($"{where} layer rock: {got.Rock[..12]} expected {golden.Rock[..12]}");
            if (got.CaveAir != golden.CaveAir) found.Add($"{where} layer cave air: {got.CaveAir} expected {golden.CaveAir}");
            if (got.DepositRocks != golden.DepositRocks) found.Add($"{where} layer deposit rocks: {got.DepositRocks} expected {golden.DepositRocks}");
        }

        return found;
    }

    /// <summary>C# initializer rows for <see cref="TerrainLayers"/>, for re-pinning after a Vintage Story bump
    /// (print them through ITestOutputHelper from the vanilla leg).</summary>
    public static string Format(IEnumerable<TerrainDigest> digests)
    {
        var text = new StringBuilder();
        foreach (TerrainDigest d in digests)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"        new({d.ChunkX}, {d.ChunkZ},");
            text.AppendLine(CultureInfo.InvariantCulture, $"            \"{d.HeightMap}\",");
            text.AppendLine(CultureInfo.InvariantCulture, $"            \"{d.Rock}\",");
            text.AppendLine(CultureInfo.InvariantCulture, $"            {d.CaveAir}, {d.DepositRocks}),");
        }

        return text.ToString();
    }

    /// <summary>C# initializer rows for <see cref="TerrainPass"/>, from the Terrain records of the inner 3x3.</summary>
    public static string FormatPass(IWorldSession world)
    {
        var text = new StringBuilder();
        foreach ((int cx, int cz) in WorldgenArea.Inner(RectChunkX, RectChunkZ, PassRectEdge, PassRectEdge))
        {
            PassRecord record = WorldgenProbe.Read(world, cx, cz).First(r => r.Pass == EnumWorldGenPass.Terrain);
            text.AppendLine(CultureInfo.InvariantCulture, $"        new({cx}, {cz}, {record.Rock}, {record.Granite}, {record.Air}),");
        }

        return text.ToString();
    }
}
