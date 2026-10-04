using Atlas.XUnit;
using Vintagestory.API.MathTools;
using Xunit;

namespace StratumParity.Scenarios;

/// <summary>
/// Chunk persistence parity: a modified chunk column must survive a full
/// save / unload / reload cycle identically on both flavors. This targets the riskiest
/// surface of Stratum's changes: the save pipeline (incremental autosave, DbChunk batch
/// reuse) and the chunk read path (pooled reads). A silent divergence here would be the
/// worst possible Stratum regression, so every assertion is unconditional.
///
/// The steps (load, confirmed pattern, save, unload, reload) live in <see cref="ChunkPersistence"/>
/// so the other persistence classes reuse them; its summary explains the two synchronization
/// lessons they embody. The explicit save is World.SaveNow.
/// </summary>
public class ChunkPersistenceScenarios : AtlasScenarioBase
{
    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task ModifiedColumn_Should_SurviveUnloadReload_When_SavedBeforeUnload()
    {
        BlockPos anchor = World.Spawn.AddCopy(200, 1, 0);
        await ChunkPersistence.LoadColumn(World, anchor);

        List<BlockPos> pattern = await ChunkPersistence.WritePatternConfirmed(World, anchor, saltForCycle: 0);
        byte[] payload = { 0xA7, 0x01, 0x22, 0x03, 0x15 };
        ChunkPersistence.SetColumnModdata(World, anchor, payload);

        await ChunkPersistence.SaveUnloadReload(World, anchor);

        foreach (BlockPos pos in pattern)
        {
            Assert.Equal("game:rock-granite", World.BlockAt(pos).Code.ToString());
        }
        Assert.Equal(payload, ChunkPersistence.ReadColumnModdata(World, anchor));
    }

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task RepeatedCycles_Should_PersistEveryMutation_When_ColumnIsRecycled()
    {
        // Exercises the incremental dirty-flush path repeatedly: each cycle mutates the
        // column, round-trips it through disk, and re-verifies the cumulative state.
        BlockPos anchor = World.Spawn.AddCopy(0, 1, 200);
        var written = new List<BlockPos>();

        for (int cycle = 0; cycle < 3; cycle++)
        {
            if (cycle == 0)
            {
                await ChunkPersistence.LoadColumn(World, anchor);
            }
            written.AddRange(await ChunkPersistence.WritePatternConfirmed(World, anchor, saltForCycle: cycle));
            ChunkPersistence.SetColumnModdata(World, anchor, new byte[] { (byte)cycle, 0x51 });

            await ChunkPersistence.SaveUnloadReload(World, anchor);

            foreach (BlockPos pos in written)
            {
                Assert.Equal("game:rock-granite", World.BlockAt(pos).Code.ToString());
            }
            byte[]? moddata = ChunkPersistence.ReadColumnModdata(World, anchor);
            Assert.NotNull(moddata);
            Assert.Equal((byte)cycle, moddata![0]);
        }
    }

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task LightLevels_Should_SurviveUnloadReload_When_TorchIsLit()
    {
        // Stratum rewrote the chunk data layer (bit planes, AVX decode) but nothing rereads
        // light, so a light byte surviving that round trip is untested by the block/moddata
        // assertions above.
        const string TorchCode = "game:torch-basic-lit-up";
        BlockPos anchor = World.Spawn.AddCopy(200, 1, 200);
        await ChunkPersistence.LoadColumn(World, anchor);

        BlockPos torchPos = anchor.AddCopy(4, 2, 4);
        World.SetBlock(TorchCode, torchPos);
        Assert.Equal(TorchCode, World.BlockAt(torchPos).Code.ToString());
        // SaveUnloadReload's own reload wait is gated on moddata being readable again (see
        // its comment): without any, the predicate can never turn true.
        ChunkPersistence.SetColumnModdata(World, anchor, new byte[] { 0x4C });

        // Let the light flood-fill settle before the first reading.
        await World.Ticks(30);

        BlockPos[] samples =
        {
            torchPos,
            torchPos.AddCopy(1, 0, 0),
            torchPos.AddCopy(-1, 0, 0),
            torchPos.AddCopy(0, 0, 1),
            torchPos.AddCopy(0, 1, 0),
            anchor.AddCopy(-4, 2, -4), // far from the torch, same column
        };
        (int Sun, int Block)[] before = ChunkPersistence.ReadLightLevels(World, samples);

        await ChunkPersistence.SaveUnloadReload(World, anchor);

        (int Sun, int Block)[] after = ChunkPersistence.ReadLightLevels(World, samples);
        for (int i = 0; i < samples.Length; i++)
        {
            Assert.True(before[i].Sun == after[i].Sun,
                $"sunlight at sample {i} changed after reload on {ServerFlavor.Name}: {before[i].Sun} -> {after[i].Sun}");
            Assert.True(before[i].Block == after[i].Block,
                $"block light at sample {i} changed after reload on {ServerFlavor.Name}: {before[i].Block} -> {after[i].Block}");
        }
    }
}
