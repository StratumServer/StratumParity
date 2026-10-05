using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Xunit;

namespace StratumParity.Scenarios;

/// <summary>
/// Survival-side inventory resync parity: what a player's death does to the inventory, on the
/// server and on the wire. Runs in a survival world (surviveandbuild), and guards that the
/// player really is in survival and that its entity does not keep its contents.
///
/// Two behaviors are pinned, both on exact world state rather than timing:
/// - the drop itself: every stack of the hotbar and the backpack's bag slots comes out as an
///   item entity near the body, and the multiset of dropped stacks equals the former inventory;
/// - the flush that follows: InventoryBasePlayer.DropAll (and the backpack and hotbar overrides
///   on top of it) marks the emptied slots with a direct <c>dirtySlots.Add(i)</c>, never through
///   MarkSlotDirty, and ServerSystemInventory.SendDirtySlots sends and clears those sets every
///   30 ms. Stratum's SendDirtySlots returns early while the static
///   <c>InventoryBase.StratumAnySlotDirty</c> flag is 0, and only MarkSlotDirty and DiscardAll
///   raise that flag, so on a quiet server the emptied slots may stay dirty (and unsent) until
///   some unrelated inventory change raises the flag. The flush scenario is therefore written
///   to keep the server quiet: one player, no other inventory traffic, and the flag (when it
///   can be read) is required to be 0 on the very call that kills the player.
///
/// The flush bug is confirmed on both Stratum builds the suite knows (see
/// <see cref="UnsentDrop"/>): the emptied slots stay dirty and unsent, and only an unrelated
/// MarkSlotDirty anywhere on the server (which raises the flag) sends them. Vanilla, and any
/// Stratum build that is not listed, must flush within the window.
///
/// Scenarios share the class's world and run in no fixed order, so each uses its own player name,
/// and the drop scenario works 200 blocks from where the flush scenario's player dies.
/// </summary>
[AtlasWorld(PlayStyle = "surviveandbuild")]
public class InventoryResyncScenarios : AtlasScenarioBase
{
    // SendDirtySlots listens every 30 ms, so a healthy server flushes within a pass or two.
    private const int FlushWindowTicks = 10;
    private const int QuietTimeoutTicks = 150;
    // The engine empties the slots one main-thread task after Die (EnqueueMainThreadTask).
    private const int DropTimeoutTicks = 60;
    private const int DropAreaHalfWidth = 8;
    private const string BagCode = "game:backpack-normal";

    // Non-perishable and distinct on purpose: a perishable stack would let UpdateTransitionStates
    // raise the dirty flag by itself (and mask the flush bug), and two stacks of one code could
    // merge into one item entity.
    private static readonly (string Code, int Quantity)[] Stock =
    {
        ("game:flint", 3),
        ("game:stick", 12),
        ("game:rock-granite", 5),
        ("game:soil-medium-normal", 7),
    };

    // Confirmed locally on both builds. Delete the entry when the pinned tag moves to a build that
    // raises StratumAnySlotDirty on the DropAll path.
    private static readonly KnownDivergence UnsentDrop = new(
        "StratumServer/Stratum#357", StratumBuild.Stable2, StratumBuild.Indev1);

    // Internal static on Stratum builds, absent on vanilla. Read for diagnostics and the quiet
    // guard only; a build that renames it just loses the extra precision.
    private static readonly FieldInfo? AnySlotDirtyFlag = typeof(InventoryBase).GetField(
        "StratumAnySlotDirty", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task DeathDrop_Should_FlushEmptiedSlots_When_ServerIsQuiet()
    {
        ITestPlayer player = await World.JoinPlayer("inv-flush");
        await World.Ticks(5);
        AssertDeathDropsInventory(player);

        StockedPlayer stocked = await StockPlayer(player, Stock.Take(1).ToArray());
        await WaitUntilQuiet(player);

        Cuboidi area = AreaAround(player.Position);
        try
        {
            // Nothing may await between the quiet check and Die: a pass in between could raise
            // the flag again and make the flush that follows legitimate on Stratum.
            AssertQuiet(player, "right before the death");
            Assert.True(player.Entity.Alive, $"player is already dead on {ServerFlavor.Name}; setup is invalid");
            player.Entity.Die(EnumDespawnReason.Death, new DamageSource { Type = EnumDamageType.Suffocation });

            // Die queues the drop as a main-thread task (EntityBehaviorPlayerInventory.OnEntityDeath).
            // The queue is FIFO and drained in one go, so a task queued right now runs right after
            // the drop and before any listener: it sees the dirty sets as DropAll left them, which
            // proves the flush check below is not vacuous (on a healthy server the set is gone
            // within a pass or two).
            int[]? hotbarDirtyAtDrop = null;
            int[]? backpackDirtyAtDrop = null;
            World.Api.Event.EnqueueMainThreadTask(() =>
            {
                hotbarDirtyAtDrop = stocked.Hotbar.DirtySlots.Order().ToArray();
                backpackDirtyAtDrop = stocked.Backpack.DirtySlots.Order().ToArray();
            }, "inventoryresync-probe");

            bool emptied = await UntilReached(() => AllEmpty(stocked) && hotbarDirtyAtDrop != null, DropTimeoutTicks);
            Assert.True(emptied,
                $"death never emptied the stocked slots within {DropTimeoutTicks} ticks on {ServerFlavor.Name} " +
                $"({DescribeDirty(stocked)})");
            Assert.True(
                hotbarDirtyAtDrop!.Contains(stocked.HotbarSlotIds[0]) && backpackDirtyAtDrop!.Contains(stocked.BagSlotId),
                $"the drop did not mark the emptied slots dirty on {ServerFlavor.Name} (hotbar slot {stocked.HotbarSlotIds[0]} " +
                $"dirty [{string.Join(",", hotbarDirtyAtDrop!)}], bag slot {stocked.BagSlotId} dirty " +
                $"[{string.Join(",", backpackDirtyAtDrop!)}]), so the flush check would be vacuous; setup is invalid");

            await AssertEmptiedSlotsFlushed(stocked);
        }
        finally
        {
            DespawnItemEntities(area);
        }
    }

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task DeathDrop_Should_SpawnFormerInventory_When_PlayerDies()
    {
        ITestPlayer player = await World.JoinPlayer("inv-drop");
        await World.Ticks(5);

        // Far from the flush scenario's body: the area check below counts every item entity.
        BlockPos farPos = World.Spawn.AddCopy(200, 0, 0);
        World.Api.WorldManager.LoadChunkColumnPriority(farPos.X / 32, farPos.Z / 32,
            new Vintagestory.API.Server.ChunkLoadOptions { KeepLoaded = true });
        await World.Until(
            () => World.Api.World.BlockAccessor.GetChunkAtBlockPos(farPos) != null,
            timeoutTicks: 600);
        await player.TeleportTo(farPos);
        await World.Ticks(2);

        AssertDeathDropsInventory(player);
        AssertNothingCarried(player);
        StockedPlayer stocked = await StockPlayer(player, Stock);
        await World.Ticks(2);

        List<string> expected = FormerContents(stocked);
        Assert.True(expected.Count == Stock.Length + 1,
            $"expected {Stock.Length + 1} stocked stacks, the inventories hold {expected.Count} on {ServerFlavor.Name}: " +
            $"{string.Join(", ", expected)}; setup is invalid");

        Cuboidi area = AreaAround(player.Position);
        Assert.True(ItemEntitiesIn(area).Count == 0,
            $"item entities already lie around the body on {ServerFlavor.Name}; setup is invalid");

        try
        {
            Assert.True(player.Entity.Alive, $"player is already dead on {ServerFlavor.Name}; setup is invalid");
            player.Entity.Die(EnumDespawnReason.Death, new DamageSource { Type = EnumDamageType.Suffocation });

            bool emptied = await UntilReached(() => AllEmpty(stocked), DropTimeoutTicks);
            Assert.True(emptied,
                $"death never emptied the stocked slots within {DropTimeoutTicks} ticks on {ServerFlavor.Name}");

            bool matched = await UntilReached(() => DroppedContents(area).SequenceEqual(expected), DropTimeoutTicks);
            List<string> dropped = DroppedContents(area);
            Assert.True(matched,
                $"dropped stacks differ from the former inventory on {ServerFlavor.Name}: " +
                $"expected [{string.Join(", ", expected)}], dropped [{string.Join(", ", dropped)}]");
        }
        finally
        {
            DespawnItemEntities(area);
        }
    }

    /// <summary>The emptied hotbar slot and bag slot must leave their dirty sets within
    /// <see cref="FlushWindowTicks"/> ticks of the drop (vanilla, and every build not listed in
    /// <see cref="UnsentDrop"/>). On a listed build the bug shape is asserted instead: both
    /// slots are still dirty after the whole window, and an unrelated slot change (which raises
    /// StratumAnySlotDirty) is what finally sends them, so the flag is the cause and not a slow pass.</summary>
    private async Task AssertEmptiedSlotsFlushed(StockedPlayer stocked)
    {
        if (!UnsentDrop.Applies)
        {
            bool flushed = await UntilReached(
                () => stocked.Hotbar.DirtySlots.Count == 0 && stocked.Backpack.DirtySlots.Count == 0,
                FlushWindowTicks);
            Assert.True(flushed,
                $"emptied slots were not flushed within {FlushWindowTicks} ticks of the drop on {ServerFlavor.Name}: " +
                DescribeDirty(stocked));
            return;
        }

        await World.Ticks(FlushWindowTicks);
        Assert.True(
            stocked.Hotbar.DirtySlots.Contains(stocked.HotbarSlotIds[0]) && stocked.Backpack.DirtySlots.Contains(stocked.BagSlotId),
            $"{UnsentDrop.Tag}: the emptied slots were flushed within {FlushWindowTicks} ticks, the bug shape is gone, " +
            $"drop the exemption ({DescribeDirty(stocked)})");

        // Any inventory change raises the flag. Another hotbar slot of the same (dead) player stands in for it.
        SlotOf(stocked.Hotbar, (stocked.HotbarSlotIds[0] + 1) % 10).MarkDirty();
        bool sentLate = await UntilReached(
            () => stocked.Hotbar.DirtySlots.Count == 0 && stocked.Backpack.DirtySlots.Count == 0,
            FlushWindowTicks);
        Assert.True(sentLate,
            $"{UnsentDrop.Tag}: the emptied slots stayed dirty even after an unrelated slot change raised the flag " +
            $"({DescribeDirty(stocked)})");
    }

    /// <summary>Death only drops the inventory when the entity does not carry keepContents
    /// (the deathPunishment world config, absent here, which means drop) and the player is in
    /// survival; anything else makes the drop scenarios vacuous.</summary>
    private static void AssertDeathDropsInventory(ITestPlayer player)
    {
        Assert.True(player.Player.WorldData.CurrentGameMode == EnumGameMode.Survival,
            $"player is in {player.Player.WorldData.CurrentGameMode} on {ServerFlavor.Name}, not survival; setup is invalid");
        bool keepContents = player.Entity.Properties.Server?.Attributes?.GetBool("keepContents", false) == true;
        Assert.False(keepContents,
            $"the player entity keeps its contents on death on {ServerFlavor.Name}; setup is invalid");
    }

    /// <summary>Every inventory that a death drops from (all but the character's clothes and the
    /// creative tab) must start empty, so the stocked stacks are the whole former inventory.</summary>
    private static void AssertNothingCarried(ITestPlayer player)
    {
        foreach (IInventory inventory in player.Player.InventoryManager.Inventories.Values)
        {
            if (inventory.ClassName is "character" or "creative")
            {
                continue;
            }

            Assert.True(inventory.Empty,
                $"{inventory.InventoryID} holds items before the test stocked anything on {ServerFlavor.Name}; setup is invalid");
        }
    }

    /// <summary>The inventories (for their dirty sets), the exact slots that were stocked and
    /// their ids in their inventory (what the dirty sets hold).</summary>
    private sealed record StockedPlayer(
        IInventory Hotbar, IInventory Backpack, ItemSlot[] HotbarSlots, int[] HotbarSlotIds, ItemSlot BagSlot, int BagSlotId);

    /// <summary>The first stack through <see cref="ITestPlayer.GiveItem"/> (it lands in the active
    /// hotbar slot), the following ones in the next hotbar slots, and an empty bag in bag slot 0
    /// so that the backpack has an emptied slot to flush as well.</summary>
    private async Task<StockedPlayer> StockPlayer(ITestPlayer player, (string Code, int Quantity)[] stacks)
    {
        IPlayerInventoryManager manager = player.Player.InventoryManager;
        IInventory hotbar = manager.GetHotbarInventory();
        IInventory backpack = manager.GetOwnInventory(GlobalConstants.backpackInvClassName)
            ?? throw new InvalidOperationException($"{player.Player.PlayerName} has no backpack on {ServerFlavor.Name}; setup is invalid");

        int first = manager.ActiveHotbarSlotNumber;
        var slots = new ItemSlot[stacks.Length];
        var slotIds = new int[stacks.Length];
        for (int i = 0; i < stacks.Length; i++)
        {
            slots[i] = SlotOf(hotbar, (first + i) % 10);
            slotIds[i] = hotbar.GetSlotId(slots[i]);
            Assert.True(slots[i].Empty, $"hotbar slot {slotIds[i]} is not empty on {ServerFlavor.Name}; setup is invalid");
            if (i == 0)
            {
                await player.GiveItem(stacks[i].Code, stacks[i].Quantity);
            }
            else
            {
                slots[i].Itemstack = StackOf(stacks[i].Code, stacks[i].Quantity);
                slots[i].MarkDirty();
            }
        }

        ItemSlot bagSlot = SlotOf(backpack, 0);
        Assert.True(bagSlot.Empty, $"bag slot 0 is not empty on {ServerFlavor.Name}; setup is invalid");
        bagSlot.Itemstack = StackOf(BagCode, 1);
        bagSlot.MarkDirty();

        return new StockedPlayer(hotbar, backpack, slots, slotIds, bagSlot, backpack.GetSlotId(bagSlot));
    }

    private static ItemSlot SlotOf(IInventory inventory, int index) =>
        inventory[index] ?? throw new InvalidOperationException(
            $"{inventory.InventoryID} has no slot {index} on {ServerFlavor.Name}; setup is invalid");

    private ItemStack StackOf(string code, int quantity)
    {
        var location = new AssetLocation(code);
        Item? item = World.Api.World.GetItem(location);
        if (item != null)
        {
            return new ItemStack(item, quantity);
        }

        Block? block = World.Api.World.GetBlock(location);
        Assert.True(block != null && !block.IsMissing, $"{code} does not resolve on {ServerFlavor.Name}; setup is invalid");
        return new ItemStack(block!, quantity);
    }

    private static bool AllEmpty(StockedPlayer stocked) =>
        stocked.HotbarSlots.All(slot => slot.Empty) && stocked.BagSlot.Empty;

    /// <summary>What is in the stocked slots right now, as sorted "code xN" strings, the same
    /// shape as <see cref="DroppedContents"/>.</summary>
    private static List<string> FormerContents(StockedPlayer stocked) =>
        stocked.HotbarSlots.Append(stocked.BagSlot)
            .Where(slot => !slot.Empty)
            .Select(slot => Describe(slot.Itemstack!))
            .Order(StringComparer.Ordinal)
            .ToList();

    private List<string> DroppedContents(Cuboidi area) =>
        ItemEntitiesIn(area)
            .Select(item => ((EntityItem)item).Itemstack is { } stack ? Describe(stack) : "<item entity without a stack>")
            .Order(StringComparer.Ordinal)
            .ToList();

    private static Cuboidi AreaAround(BlockPos pos) => new(
        pos.X - DropAreaHalfWidth, pos.Y - 4, pos.Z - DropAreaHalfWidth,
        pos.X + DropAreaHalfWidth, pos.Y + 6, pos.Z + DropAreaHalfWidth);

    private void DespawnItemEntities(Cuboidi area)
    {
        foreach (Entity item in ItemEntitiesIn(area))
        {
            item.Die(EnumDespawnReason.Removed);
        }
    }

    private List<Entity> ItemEntitiesIn(Cuboidi area) =>
        World.EntitiesIn(area).Where(e => e is EntityItem { Alive: true }).ToList();

    private static string Describe(ItemStack stack) => $"{stack.Collectible.Code} x{stack.StackSize}";

    private async Task WaitUntilQuiet(ITestPlayer player)
    {
        bool quiet = await UntilReached(() => IsQuiet(player), QuietTimeoutTicks);
        AssertQuiet(player, "after the stock was placed", quiet);
    }

    private static bool IsQuiet(ITestPlayer player) =>
        player.Player.InventoryManager.Inventories.Values.All(inv => inv.DirtySlots.Count == 0)
        && ReadFlag() != 1;

    private static void AssertQuiet(ITestPlayer player, string moment, bool? known = null) =>
        Assert.True(known ?? IsQuiet(player),
            $"the player's inventories are not quiet {moment} on {ServerFlavor.Name} " +
            $"({DescribeAllDirty(player)}, {DescribeFlag()}); setup is invalid");

    private async Task<bool> UntilReached(Func<bool> predicate, int timeoutTicks)
    {
        try
        {
            await World.Until(predicate, timeoutTicks);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int? ReadFlag() => AnySlotDirtyFlag?.GetValue(null) as int?;

    private static string DescribeFlag() =>
        ReadFlag() is int flag ? $"StratumAnySlotDirty={flag}" : "StratumAnySlotDirty not present";

    private static string DescribeDirty(StockedPlayer stocked) =>
        $"hotbar dirty [{string.Join(",", stocked.Hotbar.DirtySlots.Order())}], " +
        $"backpack dirty [{string.Join(",", stocked.Backpack.DirtySlots.Order())}], {DescribeFlag()}";

    private static string DescribeAllDirty(ITestPlayer player) =>
        string.Join("; ", player.Player.InventoryManager.Inventories.Values
            .Where(inv => inv.DirtySlots.Count > 0)
            .Select(inv => $"{inv.ClassName} dirty [{string.Join(",", inv.DirtySlots.Order())}]")
            .DefaultIfEmpty("no dirty slots"));
}
