using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Atlas.Api;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Xunit;

namespace StratumParity.Scenarios;

/// <summary>What the terrain passes decided for one chunk column, as values that a golden pins down.
/// <see cref="HeightMap"/> is SHA-256 over the 1024 values of WorldGenTerrainHeightMap (little endian
/// ushort, z major). <see cref="Rock"/> is SHA-256 over (local x, local z, y, code) of every stone block
/// from the bottom up to and including the terrain height, in x, z, y order, with deposit products folded
/// back into their host rock (see <see cref="WorldgenDigest"/>). <see cref="CaveAir"/> counts the
/// positions at or below the terrain height that are empty space: no solid block and no fluid, or a
/// block that is not a full cube (the saltpeter, stalagmites and loose stones that deposits and patches
/// put into caves, which differ between boots). <see cref="DepositRocks"/> counts the stone blocks that
/// only a deposit can have placed and whose host rock cannot be recovered (marbles, obsidian,
/// travertine, halite): a golden area must have none, or the rock hash depends on the deposit
/// generator. RainHeightMap is deliberately not part of it: trees and shrubs move it, and the
/// vegetation of one seed differs from one world boot to the next on vanilla.</summary>
public sealed record TerrainDigest(int ChunkX, int ChunkZ, string HeightMap, string Rock, int CaveAir, int DepositRocks)
{
    public override string ToString() =>
        $"({ChunkX},{ChunkZ}) height={HeightMap} rock={Rock} caveAir={CaveAir} depositRocks={DepositRocks}";
}

/// <summary>Every ore block of one chunk column: SHA-256 over (x, y, z, code) of each, the block
/// count and the count per ore code as "code=count;code=count" sorted by code.</summary>
public sealed record OreDigest(int ChunkX, int ChunkZ, string Hash, int Count, string PerOre)
{
    public override string ToString() => $"({ChunkX},{ChunkZ}) ores={Count} hash={Hash} perOre={PerOre}";
}

/// <summary>One water source that GenRivulets left in the rock, at block coordinates.</summary>
public readonly record struct RivuletCell(int X, int Y, int Z)
{
    public override string ToString() => $"({X},{Y},{Z})";
}

/// <summary>
/// Hashes of what the worldgen passes left in a loaded chunk column, for the golden comparisons of
/// the worldgen classes. Every method reads a column the caller has loaded and that has reached
/// Done (WorldgenArea.LoadDone); coordinates in the digests are chunk coordinates. Hashes are
/// lowercase hex SHA-256 and the input layout is fixed in the member summaries: a change to it
/// re-pins every golden.
///
/// The terrain digest must not move when only the deposit generator (DiscGenerator) differs, and
/// that generator replaces rock blocks in place: an ore or cracked rock block carries its host rock
/// as the last part of its code (ore-quartz-granite replaced rock-granite, crackedrock-peridotite
/// replaced rock-peridotite). <see cref="Terrain"/> therefore hashes those blocks as the rock they
/// replaced, so a position reads the same whether a deposit sits on it or not; soil, gravel, sand
/// and the clay, peat and compost deposits of the soil layer are not hashed at all. The six rocks
/// that only deposits place (marbles, obsidian, travertine, halite) have no recoverable host and
/// are counted in <see cref="TerrainDigest.DepositRocks"/> instead. <see cref="Ores"/> covers the
/// ore family on its own.
///
/// Measured while building this (seed 7351, vanilla 1.22.7): in the golden rectangle of
/// <see cref="WorldgenGoldens"/> every digest of <see cref="Terrain"/> is identical on eight vanilla boots
/// in two processes. That is not true of every area, nor of the other digests. Elsewhere the first
/// standard world that a test process generates gets different ore, cracked rock, saltpeter and
/// stalagmite positions from every later one (same seed, same code; a superflat world booted before
/// does not count), vegetation differs on every boot, and the fluid blocks of a loaded column move within
/// a few ticks of Done (the rivulet cells of a loaded rectangle differed from boot to boot, from 2 to 24 in
/// the same 20x20 columns, while the cells of a peeked column were identical on 3 boots: see
/// <see cref="RivuletSources"/>). An <see cref="Ores"/> golden taken from vanilla therefore
/// depends on how many standard worlds the process generated before the class: check a candidate on
/// several boots before pinning it, as the golden rectangle was.
/// </summary>
public static class WorldgenDigest
{
    public const int ChunkSize = 32;
    private const int ChunkVolume = ChunkSize * ChunkSize * ChunkSize;

    private const string RivuletWater = "water-still-7";

    // Blocks only a deposit can place and whose host rock is not in the code.
    internal static readonly string[] DepositRockPaths =
    {
        "rock-whitemarble", "rock-redmarble", "rock-greenmarble", "rock-obsidian", "rock-travertine", "rock-halite",
    };

    /// <summary>The chunks of a loaded column from the bottom up, failing as "setup is invalid" when one is missing.</summary>
    public static IWorldChunk[] Column(IWorldSession world, int cx, int cz)
    {
        int count = world.Api.WorldManager.MapSizeY / ChunkSize;
        var chunks = new IWorldChunk[count];
        for (int cy = 0; cy < count; cy++)
        {
            chunks[cy] = world.Api.World.BlockAccessor.GetChunk(cx, cy, cz);
            Assert.True(chunks[cy] != null,
                $"chunk ({cx},{cy},{cz}) is not loaded on {ServerFlavor.Name}: load the area with WorldgenArea.LoadDone first; setup is invalid");
        }

        return chunks;
    }

    /// <summary>The map chunk of a loaded column, failing as "setup is invalid" when it is missing.</summary>
    public static IMapChunk MapChunkOf(IWorldSession world, int cx, int cz)
    {
        IMapChunk? map = world.Api.WorldManager.GetMapChunk(cx, cz);
        Assert.True(map != null, $"map chunk ({cx},{cz}) is not loaded on {ServerFlavor.Name}; setup is invalid");
        return map!;
    }

    /// <summary>Height map, rock and cave air of a column that is loaded in the world, see <see cref="TerrainDigest"/>.</summary>
    public static TerrainDigest Terrain(IWorldSession world, int cx, int cz) => Terrain(world, Column(world, cx, cz), cx, cz);

    /// <summary>Height map, rock and cave air of the given chunks (bottom up), for example a column peeked
    /// to the Terrain pass with <see cref="WorldgenPeek.Column"/>, see <see cref="TerrainDigest"/>.</summary>
    public static TerrainDigest Terrain(IWorldSession world, IReadOnlyList<IWorldChunk> chunks, int cx, int cz)
    {
        IMapChunk map = chunks[0].MapChunk;
        BlockCodes codes = new(world);
        int maxY = chunks.Count * ChunkSize - 1;

        using var rock = new Digester();
        int caveAir = 0;
        int depositRocks = 0;
        for (int lz = 0; lz < ChunkSize; lz++)
        {
            for (int lx = 0; lx < ChunkSize; lx++)
            {
                int top = Math.Min(map.WorldGenTerrainHeightMap[lz * ChunkSize + lx], maxY);
                for (int y = 0; y <= top; y++)
                {
                    IChunkBlocks data = chunks[y / ChunkSize].Data;
                    int index = (y % ChunkSize * ChunkSize + lz) * ChunkSize + lx;
                    int id = data.GetBlockId(index, BlockLayersAccess.Solid);
                    if (id == 0)
                    {
                        if (data.GetFluid(index) == 0)
                        {
                            caveAir++;
                        }

                        continue;
                    }

                    byte[]? stone = codes.StoneCode(id);
                    if (stone == null)
                    {
                        if (!codes.IsFullCube(id))
                        {
                            caveAir++; // cave decor (saltpeter, stalagmites, loose stones) still is empty space
                        }

                        continue;
                    }

                    if (codes.IsDepositRock(id))
                    {
                        depositRocks++;
                    }

                    rock.AddByte((byte)lx);
                    rock.AddByte((byte)lz);
                    rock.AddUShort((ushort)y);
                    rock.AddCode(stone);
                }
            }
        }

        return new TerrainDigest(cx, cz, HashHeights(map.WorldGenTerrainHeightMap), rock.Finish(), caveAir, depositRocks);
    }

    /// <summary>Every block whose code starts with ore- in one column, scanned from the bottom chunk up.</summary>
    public static OreDigest Ores(IWorldSession world, int cx, int cz) => Ores(world, Column(world, cx, cz), cx, cz);

    /// <summary>Ore blocks of the given chunks (bottom up), for example a column peeked to a pass with
    /// <see cref="WorldgenPeek.Column"/>: deposits are placed at TerrainFeatures, so a Terrain peek has none.</summary>
    public static OreDigest Ores(IWorldSession world, IReadOnlyList<IWorldChunk> chunks, int cx, int cz)
    {
        BlockCodes codes = new(world);
        var perOre = new SortedDictionary<string, int>(StringComparer.Ordinal);

        using var hash = new Digester();
        int count = 0;
        for (int cy = 0; cy < chunks.Count; cy++)
        {
            IChunkBlocks data = chunks[cy].Data;
            for (int index = 0; index < ChunkVolume; index++)
            {
                int id = data.GetBlockId(index, BlockLayersAccess.Solid);
                if (id == 0 || !codes.IsOre(id))
                {
                    continue;
                }

                hash.AddInt(cx * ChunkSize + index % ChunkSize);
                hash.AddInt(cy * ChunkSize + index / (ChunkSize * ChunkSize));
                hash.AddInt(cz * ChunkSize + index / ChunkSize % ChunkSize);
                hash.AddCode(codes.Bytes(id));
                count++;
                string code = codes.Path(id);
                perOre[code] = perOre.GetValueOrDefault(code) + 1;
            }
        }

        return new OreDigest(cx, cz, hash.Finish(), count, string.Join(";", perOre.Select(p => FormattableString.Invariant($"{p.Key}={p.Value}"))));
    }

    /// <summary>
    /// The water sources that GenRivulets left in the given chunks (bottom up), by its own shape test: a water-still-7 fluid over
    /// an empty solid layer with exactly five neighbours of stone material and one of air, read from the solid layer of the
    /// column (the test of tryGenRivulet, which cannot see past the column: only local x and z 1 to 30 are scanned, as GenRivulets
    /// only picks those). Meant for a column peeked up to the Vegetation pass, where GenRivulets has run and no block update has:
    /// in a loaded column the fluid simulation moves water between cells within a few ticks of Done, so the cells of a loaded
    /// column differ from boot to boot. Only water: lava rivulets draw after the mountain side rivulets, whose count depends on the
    /// random state the previously generated column left behind, so their cells depend on the order in which columns were generated.
    /// Rapid water (the mountain side kind) is left out for the same reason. In x, z, y order.
    /// </summary>
    public static List<RivuletCell> RivuletSources(IWorldSession world, IReadOnlyList<IWorldChunk> chunks, int cx, int cz)
    {
        IList<Block> blocks = world.Api.World.Blocks;
        int maxY = chunks.Count * ChunkSize - 1;
        var found = new List<RivuletCell>();
        for (int lx = 1; lx < ChunkSize - 1; lx++)
        {
            for (int lz = 1; lz < ChunkSize - 1; lz++)
            {
                for (int y = 1; y < maxY; y++)
                {
                    int index = (y % ChunkSize * ChunkSize + lz) * ChunkSize + lx;
                    IChunkBlocks data = chunks[y / ChunkSize].Data;
                    int fluid = data.GetFluid(index);
                    if (fluid == 0 || data.GetBlockId(index, BlockLayersAccess.Solid) != 0 || blocks[fluid].Code.Path != RivuletWater)
                    {
                        continue;
                    }

                    int stone = 0;
                    int air = 0;
                    foreach (BlockFacing face in BlockFacing.ALLFACES)
                    {
                        int ny = y + face.Normali.Y;
                        int id = chunks[ny / ChunkSize].Data.GetBlockId(
                            (ny % ChunkSize * ChunkSize + lz + face.Normali.Z) * ChunkSize + lx + face.Normali.X, BlockLayersAccess.Solid);
                        EnumBlockMaterial material = blocks[id].BlockMaterial;
                        stone += material == EnumBlockMaterial.Stone ? 1 : 0;
                        air += material == EnumBlockMaterial.Air ? 1 : 0;
                    }

                    if (stone == 5 && air == 1)
                    {
                        found.Add(new RivuletCell(cx * ChunkSize + lx, y, cz * ChunkSize + lz));
                    }
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Every position of a column, solid layer then fluid layer, as SHA-256 over the code of each
    /// (an empty layer hashes as an empty code). For peeked and pregenerated columns, where the
    /// whole content is compared pass by pass; trees, structures and creatures included.
    /// </summary>
    public static string ColumnBlocks(IWorldSession world, IReadOnlyList<IWorldChunk> chunks)
    {
        BlockCodes codes = new(world);
        using var hash = new Digester();
        for (int cy = 0; cy < chunks.Count; cy++)
        {
            IChunkBlocks data = chunks[cy].Data;
            for (int index = 0; index < ChunkVolume; index++)
            {
                int solid = data.GetBlockId(index, BlockLayersAccess.Solid);
                int fluid = data.GetFluid(index);
                hash.AddCode(solid == 0 ? Array.Empty<byte>() : codes.Bytes(solid));
                hash.AddCode(fluid == 0 ? Array.Empty<byte>() : codes.Bytes(fluid));
            }
        }

        return hash.Finish();
    }

    /// <summary>ColumnBlocks of a column that is loaded in the world.</summary>
    public static string ColumnBlocks(IWorldSession world, int cx, int cz) => ColumnBlocks(world, Column(world, cx, cz));

    /// <summary>One SHA-256 over several digests (for example the per column hashes of an area), order matters.</summary>
    public static string Combine(IEnumerable<string> parts)
    {
        using var hash = new Digester();
        foreach (string part in parts)
        {
            hash.AddCode(Encoding.ASCII.GetBytes(part));
        }

        return hash.Finish();
    }

    private static string HashHeights(ushort[] heights)
    {
        using var hash = new Digester();
        foreach (ushort height in heights)
        {
            hash.AddUShort(height);
        }

        return hash.Finish();
    }

    /// <summary>Per call cache of block id to code bytes and family, so a scan is one array read per block.</summary>
    private sealed class BlockCodes
    {
        private const sbyte NotStone = 1;
        private const sbyte PlainRock = 2;
        private const sbyte Folded = 3;      // ore or cracked rock: hashed as its host rock
        private const sbyte DepositRock = 4;
        private const sbyte Ore = 5;

        private readonly IList<Block> blocks;
        private readonly byte[]?[] bytes;
        private readonly byte[]?[] stone;
        private readonly sbyte[] family; // 0 unknown

        public BlockCodes(IWorldSession world)
        {
            blocks = world.Api.World.Blocks;
            bytes = new byte[blocks.Count][];
            stone = new byte[blocks.Count][];
            family = new sbyte[blocks.Count];
        }

        public byte[] Bytes(int id) => bytes[id] ??= Encoding.UTF8.GetBytes(blocks[id].Code.ToString());

        public string Path(int id) => blocks[id].Code.Path;

        public bool IsOre(int id) => Family(id) == Ore;

        public bool IsFullCube(int id) => blocks[id].SideSolid.All;

        public bool IsDepositRock(int id) => Family(id) == DepositRock;

        /// <summary>The code to hash for a stone block (a deposit product as its host rock), null for everything that is not stone.</summary>
        public byte[]? StoneCode(int id)
        {
            switch (Family(id))
            {
                case PlainRock:
                case DepositRock:
                    return Bytes(id);
                case Folded:
                case Ore:
                    string path = blocks[id].Code.Path;
                    return stone[id] ??= Encoding.UTF8.GetBytes(
                        blocks[id].Code.Domain + ":rock-" + path.Substring(path.LastIndexOf('-') + 1));
                default:
                    return null;
            }
        }

        private sbyte Family(int id)
        {
            if (family[id] != 0)
            {
                return family[id];
            }

            string path = blocks[id].Code.Path;
            sbyte kind = NotStone;
            if (path.StartsWith("ore-", StringComparison.Ordinal))
            {
                kind = Ore;
            }
            else if (path.StartsWith("crackedrock-", StringComparison.Ordinal))
            {
                kind = Folded;
            }
            else if (DepositRockPaths.Contains(path, StringComparer.Ordinal))
            {
                kind = DepositRock;
            }
            else if (path.StartsWith("rock-", StringComparison.Ordinal))
            {
                kind = PlainRock;
            }

            return family[id] = kind;
        }
    }

    /// <summary>SHA-256 over a buffered byte stream; codes are written with a zero terminator.</summary>
    private sealed class Digester : IDisposable
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly byte[] buffer = new byte[1 << 16];
        private int used;

        public void AddByte(byte value)
        {
            Reserve(1);
            buffer[used++] = value;
        }

        public void AddUShort(ushort value)
        {
            Reserve(2);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(used), value);
            used += 2;
        }

        public void AddInt(int value)
        {
            Reserve(4);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(used), value);
            used += 4;
        }

        public void AddCode(byte[] code)
        {
            Reserve(code.Length + 1);
            code.CopyTo(buffer.AsSpan(used));
            used += code.Length;
            buffer[used++] = 0;
        }

        public string Finish()
        {
            Flush();
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        public void Dispose() => hash.Dispose();

        private void Reserve(int length)
        {
            if (used + length > buffer.Length)
            {
                Flush();
            }
        }

        private void Flush()
        {
            hash.AppendData(buffer, 0, used);
            used = 0;
        }
    }
}
