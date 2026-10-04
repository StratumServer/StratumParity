using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Xunit;
using Xunit.Abstractions;

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
///
/// Anchors. Every scenario keeps its own column, 200 blocks or more from the others (the
/// world is shared and the scenarios have no order): +200/0, 0/+200 and +200/+200 from the
/// spawn for the first three, -200/0 (natural unload), 0/-200 (resting entities) and -200/-200
/// (block types) for the rest. No scenario joins a player: that keeps the natural unload
/// premise true and leaves every entity inactive and at rest.
/// </summary>
public class ChunkPersistenceScenarios : AtlasScenarioBase
{
    // Chunk of block types. The palette doubles up to 512 entries for 301 values (wide bit planes, and
    // the palette itself is stored compressed above 18), and it only compacts when it is FULL: the
    // write of the 512th distinct value, with unused entries around, runs CleanUpPalette. So the
    // scenario writes FirstBatch codes, then brings the palette to 511 values (plus air) with a
    // second batch while most of the first batch is gone, and the last code is the one that
    // triggers the compaction.
    private const int FirstBatch = 300;
    private const int TotalCodes = 511;          // first batch + 211 more; the last one triggers the shrink
    private const int KeepEvery = 8;             // of the first batch only every 8th block survives the removal
    private const int PaletteLength = 512;
    private const int SlabLocalY = 8;
    private const int WideBitPlanesMinLength = 257;
    private const int ShrunkMaxLength = 256;
    private const int IdleTimeoutTicks = 2400;   // the packer waits 15 s of idle, scans every 4 s, one chunk per pass
    private const double RestTolerance = 0.01;
    private const string AirCode = "game:air";
    private const string TorchCode = "game:torch-basic-lit-up";
    private const string RaccoonCode = "game:raccoon-common-adult-male";
    private const string DummyCode = "game:strawdummy";
    private const string FlintCode = "game:flint";

    private readonly ITestOutputHelper output;

    public ChunkPersistenceScenarios(ITestOutputHelper output) => this.output = output;

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

    // ---------------------------------------------------------------------------------------------
    // Natural unload
    // ---------------------------------------------------------------------------------------------

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task ModifiedColumn_Should_PersistOnNaturalUnload_When_NoSaveIsRequested()
    {
        // The other scenarios save and unload by hand. This one asks for neither: the column is
        // loaded without keepLoaded, nobody is near it, so the engine ages it out by itself (about
        // 12 to 15 s, measured 420 to 450 ticks on vanilla and both Stratum builds) and whatever
        // it writes on that path is all that survives. Moddata only comes from the database, so
        // the reload gate on the chunk moddata is the proof that the stored copy was applied.
        // The map chunk is not unloaded with its column (it lives on in memory), so its moddata
        // is checked for completeness but does not prove the database copy.
        BlockPos anchor = World.Spawn.AddCopy(-200, 1, 0);
        await ChunkPersistence.LoadColumn(World, anchor);
        AssertNoPlayerNear(anchor);

        List<BlockPos> pattern = await ChunkPersistence.WritePatternConfirmed(World, anchor, saltForCycle: 0);
        byte[] chunkPayload = { 0x5B, 0x02, 0x31, 0x08, 0x6E };
        byte[] mapPayload = { 0x7D, 0x04, 0x19 };
        ChunkPersistence.SetColumnModdata(World, anchor, chunkPayload);
        ChunkPersistence.SetMapChunkModdata(World, anchor, mapPayload);
        Assert.Equal(chunkPayload, ChunkPersistence.ReadColumnModdata(World, anchor));
        Assert.Equal(mapPayload, ChunkPersistence.ReadMapChunkModdata(World, anchor));

        long startMs = World.Api.World.ElapsedMilliseconds;
        try
        {
            await ChunkPersistence.WaitNaturalUnload(World, anchor, timeoutTicks: 1200);
        }
        catch (ScenarioTimeoutException)
        {
            Assert.Fail($"the engine did not unload the unused column within 1200 ticks on {ServerFlavor.Name}: " +
                        "nothing keeps it loaded, so this is either a divergence or an invalid setup");
        }
        output.WriteLine($"{ServerFlavor.Name} {ServerFlavor.Version ?? "none"}: natural unload after {(World.Api.World.ElapsedMilliseconds - startMs) / 1000.0:F1} s");

        await ChunkPersistence.ReloadColumn(World, anchor);

        Assert.True(ChunkPersistence.PatternPresent(World, pattern),
            $"the pattern did not survive the natural unload on {ServerFlavor.Name}");
        Assert.Equal(chunkPayload, ChunkPersistence.ReadColumnModdata(World, anchor));
        Assert.Equal(mapPayload, ChunkPersistence.ReadMapChunkModdata(World, anchor));
    }

    // ---------------------------------------------------------------------------------------------
    // Chunk of block types: packed, saved, shrunk
    // ---------------------------------------------------------------------------------------------

    [AtlasScenario(TimeoutMs = 300_000)]
    public async Task ChunkWithThreeHundredBlockTypes_Should_RoundTrip_When_PackedSavedAndShrunk()
    {
        // One 32^3 chunk above the terrain (all air to start with) gets 300 distinct block types:
        // the palette doubles to 512 entries (9 bit planes, compressed palette on disk), the very
        // shape Stratum's rewritten chunk data layer (hoisted plane fields, SIMD decode) handles
        // differently from vanilla. The same blocks must read back exactly
        //   1. right after the write,
        //   2. after the engine packed the idle chunk to its compressed form (waited for, not assumed),
        //   3. after SaveNow, unload and reload, with a torch beside them reading 14 and 13,
        //   4. after a palette shrink (see the constants) and a second round trip.
        // The torch comes after step 2 on purpose: its block entity reads its block every few seconds,
        // which keeps resetting the idle clock that the packer waits on.
        // The block types are plain blocks (class Block, no entity, no behavior but the reinforcement
        // marker the game puts on every stone and brick block, no light, no fluid), so nothing falls,
        // ticks or reacts to a neighbour. The 300 and the 211 after them are the first of the sorted
        // list, the same on both flavors.
        string[] codes = PlainBlockCodes();
        Assert.True(codes.Length >= TotalCodes,
            $"only {codes.Length} plain stone or brick block types resolve on {ServerFlavor.Name}, {TotalCodes} are needed; setup is invalid");

        BlockPos source = World.Spawn.AddCopy(-200, 0, -200);
        int chunkY = source.Y / ChunkPersistence.ChunkSize + 1;
        BlockPos origin = new BlockPos(
            source.X / ChunkPersistence.ChunkSize * ChunkPersistence.ChunkSize,
            chunkY * ChunkPersistence.ChunkSize,
            source.Z / ChunkPersistence.ChunkSize * ChunkPersistence.ChunkSize,
            0);
        int columnX = origin.X / ChunkPersistence.ChunkSize;
        int columnZ = origin.Z / ChunkPersistence.ChunkSize;

        // Index 0 to TotalCodes - 1 are the slab positions, the last index is the torch.
        int torchIndex = TotalCodes;
        var positions = new BlockPos[TotalCodes + 1];
        for (int i = 0; i < TotalCodes; i++)
        {
            positions[i] = origin.AddCopy(i % 32, SlabLocalY, i / 32);
        }
        positions[torchIndex] = origin.AddCopy(16, 20, 24);
        BlockPos besideTorch = positions[torchIndex].AddCopy(1, 0, 0);
        var expected = new string[TotalCodes + 1];
        Array.Fill(expected, AirCode);

        try
        {
            await ChunkPersistence.LoadColumn(World, origin, keepLoaded: true);
            IWorldChunk chunk = World.Api.World.BlockAccessor.GetChunkAtBlockPos(origin);
            Assert.True(chunk != null, $"the chunk at {origin} is not loaded on {ServerFlavor.Name}; setup is invalid");
            AssertNoPlayerNear(origin);
            Assert.True(World.BlockAt(besideTorch).Code.ToString() == AirCode,
                $"the position beside the torch holds a block on {ServerFlavor.Name}; setup is invalid");
            AssertWorldMatches("before the write (the chunk is not empty)", positions, expected, setup: true);

            // 1. Write.
            for (int i = 0; i < FirstBatch; i++)
            {
                Place(positions, expected, i, codes[i]);
            }
            byte[] payload = { 0xC3, 0x07, 0x2A, 0x11 };
            ChunkPersistence.SetColumnModdata(World, origin, payload);
            AssertWorldMatches("right after the write", positions, expected);

            (int count, int length) = PaletteOf(origin);
            output.WriteLine($"{ServerFlavor.Name}: palette after the first batch {count} entries in a palette of {length}");
            Assert.True(length >= WideBitPlanesMinLength,
                $"the palette holds {count} entries in {length} slots after {FirstBatch} block types on {ServerFlavor.Name}, " +
                $"under {WideBitPlanesMinLength}; the wide bit plane branch is not reached, setup is invalid");

            // 2. Idle packing: the chunk must be gone from memory in its decoded form before the read.
            try
            {
                await World.Until(() => chunk!.Data == null, timeoutTicks: IdleTimeoutTicks);
            }
            catch (ScenarioTimeoutException)
            {
                Assert.Fail($"the idle chunk was not packed within {IdleTimeoutTicks} ticks on {ServerFlavor.Name}; setup is invalid");
            }
            AssertWorldMatches("after the idle chunk was packed", positions, expected);

            // 3. Torch, then the first round trip.
            Place(positions, expected, torchIndex, TorchCode);
            await WaitForTorchLight(positions[torchIndex], besideTorch, "after the torch was placed");
            await ChunkPersistence.SaveNow(World);
            await ChunkPersistence.UnloadColumn(World, origin);
            await ChunkPersistence.ReloadColumn(World, origin, keepLoaded: true);
            AssertWorldMatches("after the first save, unload and reload", positions, expected);
            AssertTorchLight(positions[torchIndex], besideTorch, "after the first reload");
            Assert.Equal(payload, ChunkPersistence.ReadColumnModdata(World, origin));

            // 4. Palette shrink. Most of the first batch goes, so the palette holds unused entries;
            // the second batch then fills it, and the last code finds it full.
            for (int i = 0; i < FirstBatch; i++)
            {
                if (i % KeepEvery != 0)
                {
                    Place(positions, expected, i, AirCode);
                }
            }
            for (int i = FirstBatch; i < TotalCodes - 1; i++)
            {
                Place(positions, expected, i, codes[i]);
            }
            (count, length) = PaletteOf(origin);
            output.WriteLine($"{ServerFlavor.Name}: palette before the last code {count} entries in a palette of {length}");
            Assert.True(count == PaletteLength && length == PaletteLength,
                $"the palette holds {count} entries in {length} slots before the last block type on {ServerFlavor.Name}, " +
                $"expected {PaletteLength} of {PaletteLength} (full); setup is invalid");

            Place(positions, expected, TotalCodes - 1, codes[TotalCodes - 1]);
            (int shrunkCount, int shrunkLength) = PaletteOf(origin);
            output.WriteLine($"{ServerFlavor.Name}: palette after the last code {shrunkCount} entries in a palette of {shrunkLength}");
            Assert.True(shrunkLength <= ShrunkMaxLength,
                $"the palette is still {shrunkLength} slots long ({shrunkCount} entries) after the write that finds it full on {ServerFlavor.Name}; " +
                "the compaction did not run, setup is invalid");
            AssertWorldMatches("after the palette shrink", positions, expected);
            AssertTorchLight(positions[torchIndex], besideTorch, "after the palette shrink");

            // The light of the slab is still being recomputed: give it a moment, the assertion is on the torch.
            await World.Ticks(30);
            await ChunkPersistence.SaveNow(World);
            await ChunkPersistence.UnloadColumn(World, origin);
            await ChunkPersistence.ReloadColumn(World, origin, keepLoaded: true);
            AssertWorldMatches("after the second save, unload and reload", positions, expected);
            AssertTorchLight(positions[torchIndex], besideTorch, "after the second reload");
            Assert.Equal(payload, ChunkPersistence.ReadColumnModdata(World, origin));

            (int reloadedCount, int reloadedLength) = PaletteOf(origin);
            output.WriteLine($"{ServerFlavor.Name} {ServerFlavor.Version ?? "none"}: palette after the second reload {reloadedCount} entries in a palette of {reloadedLength}");
        }
        finally
        {
            if (ChunkPersistence.IsLoaded(World, origin))
            {
                World.Api.WorldManager.UnloadChunkColumn(columnX, columnZ);
            }
        }
    }

    /// <summary>The plain stone and brick block types of the game domain, sorted: class Block, no block
    /// entity, no behavior except the reinforcement marker, no light, not on the fluid layer.</summary>
    private string[] PlainBlockCodes()
    {
        var codes = new List<string>();
        foreach (Block? block in World.Api.World.Blocks)
        {
            if (block?.Code == null || block.BlockId == 0 || block.Code.Domain != "game")
            {
                continue;
            }
            if (block.Class != "Block" || block.EntityClass != null || block.ForFluidsLayer)
            {
                continue;
            }
            if (block.BlockMaterial != EnumBlockMaterial.Stone && block.BlockMaterial != EnumBlockMaterial.Ceramic)
            {
                continue;
            }
            if (block.LightHsv[2] != 0 || block.BlockEntityBehaviors is { Length: > 0 })
            {
                continue;
            }
            if (block.BlockBehaviors.Any(b => b.GetType().Name != "BlockBehaviorReinforcable"))
            {
                continue;
            }
            codes.Add(block.Code.ToString());
        }
        codes.Sort(StringComparer.Ordinal);
        return codes.ToArray();
    }

    private void Place(BlockPos[] positions, string[] expected, int index, string code)
    {
        World.SetBlock(code, positions[index]);
        expected[index] = code;
    }

    private void AssertWorldMatches(string phase, BlockPos[] positions, string[] expected, bool setup = false)
    {
        var bad = new List<string>();
        for (int i = 0; i < positions.Length; i++)
        {
            string got = World.BlockAt(positions[i]).Code.ToString();
            if (got != expected[i])
            {
                bad.Add($"{positions[i]} expected {expected[i]} got {got}");
            }
        }
        Assert.True(bad.Count == 0,
            $"{bad.Count} of {positions.Length} positions differ {phase} on {ServerFlavor.Name}: {string.Join("; ", bad.Take(5))}" +
            (setup ? "; setup is invalid" : string.Empty));
    }

    private void AssertTorchLight(BlockPos torch, BlockPos beside, string phase)
    {
        (int Sun, int Block)[] light = ChunkPersistence.ReadLightLevels(World, new[] { torch, beside });
        Assert.True(light[0].Block == 14 && light[1].Block == 13,
            $"block light {phase} on {ServerFlavor.Name}: torch {light[0].Block} (expected 14), beside it {light[1].Block} (expected 13)");
    }

    private async Task WaitForTorchLight(BlockPos torch, BlockPos beside, string phase)
    {
        try
        {
            await World.Until(
                () =>
                {
                    (int Sun, int Block)[] light = ChunkPersistence.ReadLightLevels(World, new[] { torch, beside });
                    return light[0].Block == 14 && light[1].Block == 13;
                },
                timeoutTicks: 300);
        }
        catch (ScenarioTimeoutException)
        {
            AssertTorchLight(torch, beside, phase);
        }
    }

    /// <summary>Entries and slots of the block palette of the chunk at <paramref name="pos"/>, read by
    /// reflection on the engine's chunk data (the palette is an implementation detail that the scenario
    /// only uses to prove it reached the branch it is about). A missing member fails as setup is invalid.</summary>
    private (int Count, int Length) PaletteOf(BlockPos pos)
    {
        World.BlockAt(pos); // unpacks the chunk if it is in its compressed form
        IWorldChunk chunk = World.Api.World.BlockAccessor.GetChunkAtBlockPos(pos);
        object? data = chunk?.Data;
        Assert.True(data != null, $"the chunk data at {pos} is packed while its palette is read on {ServerFlavor.Name}; setup is invalid");

        FieldInfo? layerField = FindField(data!.GetType(), "blocksLayer");
        Assert.True(layerField != null, $"{data.GetType().Name}.blocksLayer not found on {ServerFlavor.Name}; setup is invalid");
        object? layer = layerField!.GetValue(data);
        if (layer == null)
        {
            return (0, 0);
        }

        FieldInfo? paletteField = FindField(layer.GetType(), "palette");
        FieldInfo? countField = FindField(layer.GetType(), "paletteCount");
        Assert.True(paletteField != null && countField != null,
            $"{layer.GetType().Name}.palette or paletteCount not found on {ServerFlavor.Name}; setup is invalid");
        int[]? palette = paletteField!.GetValue(layer) as int[];
        return palette == null ? (0, 0) : ((int)countField!.GetValue(layer)!, palette.Length);
    }

    private static FieldInfo? FindField(Type type, string name)
    {
        for (Type? t = type; t != null; t = t.BaseType)
        {
            FieldInfo? field = t.GetField(
                name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null)
            {
                return field;
            }
        }
        return null;
    }

    // ---------------------------------------------------------------------------------------------
    // Entities at rest
    // ---------------------------------------------------------------------------------------------

    private sealed record EntityShot(long Id, string Code, double X, double Y, double Z, float Health, string? Nametag, string? Stack, bool Alive)
    {
        public override string ToString() =>
            $"{Code} #{Id} at {X:F2}/{Y:F2}/{Z:F2} health {Health} nametag {Nametag ?? "none"} stack {Stack ?? "none"} alive {Alive}";
    }

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task RestingEntities_Should_SurviveUnloadReload_When_ColumnSaved()
    {
        // A straw dummy, a named and wounded raccoon (an AI creature with an inventory and its
        // behaviors) and a stack of 64 flint, each saved with the chunk and read back after the
        // column was unloaded and loaded again. No player is anywhere, so every entity is inactive:
        // nothing moves them between the reading before the save and the reading after the reload.
        // Their health regeneration is the one thing that still ticks for an inactive creature, so
        // the raccoon's regeneration speed is set to zero (the health behavior reads that attribute
        // at every update).
        BlockPos anchor = World.Spawn.AddCopy(0, 0, -200);
        BlockPos origin = new BlockPos(
            anchor.X / ChunkPersistence.ChunkSize * ChunkPersistence.ChunkSize,
            anchor.Y,
            anchor.Z / ChunkPersistence.ChunkSize * ChunkPersistence.ChunkSize,
            0);
        int columnX = origin.X / ChunkPersistence.ChunkSize;
        int columnZ = origin.Z / ChunkPersistence.ChunkSize;
        var spawned = new List<long>();

        try
        {
            await ChunkPersistence.LoadColumn(World, origin, keepLoaded: true);
            AssertNoPlayerNear(origin);

            Item? flint = World.Api.World.GetItem(new AssetLocation(FlintCode));
            Assert.True(flint != null, $"{FlintCode} does not resolve on {ServerFlavor.Name}; setup is invalid");

            Entity dummy = World.SpawnEntity(DummyCode, origin.AddCopy(6, 1, 16));
            Entity raccoon = World.SpawnEntity(RaccoonCode, origin.AddCopy(14, 1, 16));
            Entity? stack = World.Api.World.SpawnItemEntity(
                new ItemStack(flint!, 64), new Vec3d(origin.X + 22.5, origin.Y + 1, origin.Z + 16.5), new Vec3d());
            Assert.True(stack != null, $"the flint stack did not spawn on {ServerFlavor.Name}; setup is invalid");
            Entity[] entities = { dummy, raccoon, stack! };
            spawned.AddRange(entities.Select(e => e.EntityId));
            foreach (Entity entity in entities)
            {
                Assert.True(entity.Alive, $"{entity.Code} did not spawn alive on {ServerFlavor.Name}; setup is invalid");
            }

            // The raccoon: wounded, named, and not healing.
            ITreeAttribute? healthTree = raccoon.WatchedAttributes.GetTreeAttribute("health");
            Assert.True(healthTree != null, $"{RaccoonCode} has no health tree on {ServerFlavor.Name}; setup is invalid");
            healthTree!.SetFloat("currenthealth", 2f);
            raccoon.WatchedAttributes.MarkPathDirty("health");
            raccoon.WatchedAttributes.SetFloat("regenSpeed", 0f);
            var nametag = new TreeAttribute();
            nametag.SetString("name", "Rocky");
            raccoon.WatchedAttributes.SetAttribute("nametag", nametag);
            raccoon.WatchedAttributes.MarkPathDirty("nametag");

            // A marker on the chunk gives the reload gate something that only the database can provide.
            ChunkPersistence.SetColumnModdata(World, origin, new byte[] { 0xE1, 0x0D });

            // At rest: two readings apart must agree, or the setup is invalid.
            await World.Ticks(60);
            EntityShot[] first = entities.Select(Shot).ToArray();
            await World.Ticks(30);
            EntityShot[] before = entities.Select(Shot).ToArray();
            for (int i = 0; i < entities.Length; i++)
            {
                Assert.True(Differences(first[i], before[i]).Count == 0,
                    $"{first[i].Code} changed while at rest on {ServerFlavor.Name}: {first[i]} then {before[i]}; setup is invalid");
            }
            Assert.True(before[1].Health == 2f && before[1].Nametag == "Rocky",
                $"the raccoon is not wounded and named before the save on {ServerFlavor.Name}: {before[1]}; setup is invalid");
            Assert.True(before[2].Stack == FlintCode + " x64",
                $"the item entity does not hold the 64 flint on {ServerFlavor.Name}: {before[2]}; setup is invalid");
            Assert.Equal((1, 1, 1), CountInColumn(origin));

            await ChunkPersistence.SaveNow(World);
            await ChunkPersistence.UnloadColumn(World, origin);
            try
            {
                await World.Until(() => spawned.All(id => World.Api.World.GetEntityById(id) == null), timeoutTicks: 120);
            }
            catch (ScenarioTimeoutException)
            {
                Assert.Fail($"the entities are still loaded after their column was unloaded on {ServerFlavor.Name}; setup is invalid");
            }

            await ChunkPersistence.ReloadColumn(World, origin, keepLoaded: true);
            try
            {
                await World.Until(() => spawned.All(id => World.Api.World.GetEntityById(id) != null), timeoutTicks: 300);
            }
            catch (ScenarioTimeoutException)
            {
                string missing = string.Join(", ", before.Where(s => World.Api.World.GetEntityById(s.Id) == null).Select(s => $"{s.Code} #{s.Id}"));
                Assert.Fail($"entities did not come back after the reload on {ServerFlavor.Name}: {missing}");
            }

            // The engine hands a reloaded entity out as a new object; the same object would mean no reload happened.
            for (int i = 0; i < entities.Length; i++)
            {
                Entity reloaded = World.Api.World.GetEntityById(before[i].Id)!;
                Assert.True(!ReferenceEquals(reloaded, entities[i]),
                    $"{before[i].Code} is the same object after the reload on {ServerFlavor.Name}; setup is invalid");
                EntityShot after = Shot(reloaded);
                List<string> differences = Differences(before[i], after);
                Assert.True(differences.Count == 0,
                    $"{before[i].Code} changed over the save, unload and reload on {ServerFlavor.Name}: {string.Join("; ", differences)}; before {before[i]}, after {after}");
            }

            Assert.True(CountInColumn(origin) == (1, 1, 1),
                $"entities were duplicated or lost over the reload on {ServerFlavor.Name}: (dummies, raccoons, flint stacks) now {CountInColumn(origin)}, expected (1, 1, 1)");
        }
        finally
        {
            foreach (long id in spawned)
            {
                Entity? entity = World.Api.World.GetEntityById(id);
                if (entity != null)
                {
                    World.Api.World.DespawnEntity(entity, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
                }
            }
            if (ChunkPersistence.IsLoaded(World, origin))
            {
                World.Api.WorldManager.UnloadChunkColumn(columnX, columnZ);
            }
        }
    }

    private EntityShot Shot(Entity entity)
    {
        EntityPos pos = entity.Pos;
        ItemStack? stack = (entity as EntityItem)?.Itemstack;
        return new EntityShot(
            entity.EntityId,
            entity.Code.ToString(),
            pos.X,
            pos.Y,
            pos.Z,
            World.StatsOf(entity).Health,
            entity.WatchedAttributes.GetTreeAttribute("nametag")?.GetString("name"),
            stack?.Collectible == null ? null : $"{stack.Collectible.Code} x{stack.StackSize}",
            entity.Alive);
    }

    private static List<string> Differences(EntityShot before, EntityShot after)
    {
        var differences = new List<string>();
        if (before.Code != after.Code)
        {
            differences.Add($"code {before.Code} -> {after.Code}");
        }
        if (Math.Abs(before.X - after.X) > RestTolerance || Math.Abs(before.Y - after.Y) > RestTolerance || Math.Abs(before.Z - after.Z) > RestTolerance)
        {
            differences.Add($"position {before.X:F2}/{before.Y:F2}/{before.Z:F2} -> {after.X:F2}/{after.Y:F2}/{after.Z:F2}");
        }
        if (before.Health != after.Health)
        {
            differences.Add($"health {before.Health} -> {after.Health}");
        }
        if (before.Nametag != after.Nametag)
        {
            differences.Add($"nametag {before.Nametag ?? "none"} -> {after.Nametag ?? "none"}");
        }
        if (before.Stack != after.Stack)
        {
            differences.Add($"stack {before.Stack ?? "none"} -> {after.Stack ?? "none"}");
        }
        if (before.Alive != after.Alive)
        {
            differences.Add($"alive {before.Alive} -> {after.Alive}");
        }
        return differences;
    }

    private (int Dummies, int Raccoons, int FlintStacks) CountInColumn(BlockPos origin)
    {
        Entity[] found = World.EntitiesIn(new Cuboidi(
            origin.X, 0, origin.Z, origin.X + ChunkPersistence.ChunkSize - 1, 255, origin.Z + ChunkPersistence.ChunkSize - 1)).ToArray();
        return (
            found.Count(e => e.Code.ToString() == DummyCode),
            found.Count(e => e.Code.ToString() == RaccoonCode),
            found.Count(e => e is EntityItem item && item.Itemstack?.Collectible?.Code.ToString() == FlintCode));
    }

    /// <summary>No player within 150 blocks of the anchor: the unload premise of the natural unload and
    /// the rest premise of the entities (an entity is only simulated near a player).</summary>
    private void AssertNoPlayerNear(BlockPos anchor)
    {
        foreach (IPlayer player in World.Api.World.AllOnlinePlayers)
        {
            EntityPos? pos = player.Entity?.Pos;
            if (pos == null)
            {
                continue;
            }
            double distance = Math.Sqrt((pos.X - anchor.X) * (pos.X - anchor.X) + (pos.Z - anchor.Z) * (pos.Z - anchor.Z));
            Assert.True(distance > 150,
                $"player {player.PlayerName} is {distance:F0} blocks from the anchor {anchor} on {ServerFlavor.Name}; setup is invalid");
        }
    }
}
