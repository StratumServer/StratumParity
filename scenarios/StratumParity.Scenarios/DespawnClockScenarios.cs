using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Xunit;
using Xunit.Abstractions;

namespace StratumParity.Scenarios;

/// <summary>
/// The despawn clock of dropped items must run at the same speed wherever the item lies.
/// A dropped item carries the "despawn" behavior (minSeconds 600, no player distance, no light
/// condition): every call it adds the delta time it was handed to its death clock, clamped at
/// 0.2 s per call (EntityBehaviorDespawn.OnGameTick), and the item is removed when the clock
/// passes minSeconds. On vanilla every loaded entity is ticked every entity-simulation tick, so
/// the clock runs in real time wherever the item lies.
///
/// Stratum throttles entity ticks by distance band (32/64/96 blocks, strides 1/2/5/10) and hands
/// the delta time accumulated over the skipped ticks to the entity. One tick is about 0.033 s, so
/// the very-far band (beyond 96 blocks) hands 10 x 0.033 = 0.33 s per call and the behavior
/// clamps it at 0.2 s: the clock of a far item runs at 0.6 of real time, and the item lives about
/// 1000 s instead of 600 s. The far band (64 to 96 blocks) hands 5 x 0.033 = 0.17 s, under the
/// clamp, so the 80-block item keeps pace. Confirmed by runs on 1.22.7-stratum.2 and
/// 1.22.7-stratum.2-indev.1 (+200 clock 36.0 s over a 60 s window, +80 and +16 at 60 s); the
/// second scenario proves the throttle is the cause. The first scenario is strict parity on every
/// build except the two listed in <see cref="FarItemClock"/>, where it asserts the bug shape.
///
/// What is read. The death clock is stored in the entity attribute "deathTime", but only written
/// when the behavior's check fires, every 2.5 to 3.5 s on vanilla and up to 7.5 s on Stratum
/// (Stratum widens the stagger window), which is up to 12 percent of the one-minute window and
/// would eat the margin of the ratio. The seconds the behavior has accumulated since its last
/// check live in its private field accumSeconds, so the exact clock is deathTime + accumSeconds,
/// read by reflection (a missing member fails as setup is invalid).
///
/// Geometry. One Playing anchor is teleported to a fixed position; three flint items lie at
/// anchor +16 (near band), +80 (far band on Stratum) and +200 (very far, beyond the item
/// simulation range of 96 blocks, so it is inactive and no wind or physics moves it), each in
/// its own kept-loaded column, with zero velocity and out of pickup range. The two scenarios
/// anchor 400 blocks apart: the test players of a class persist for the rest of the class.
/// </summary>
public class DespawnClockScenarios : AtlasScenarioBase
{
    // About 60 s at the engine's default pacing (33.3 ms per pass).
    private const int WindowTicks = 1800;
    // Lets the items land and the physics settle before the first reading.
    private const int SettleTicks = 90;
    // The window must really span about a minute, or the near-item floor below proves nothing.
    private const double MinWindowSeconds = 50;
    // The near item must have aged about the window, with margin for the tail of the last check.
    private const double MinNearAgeSeconds = 45;
    private const double MinAgeRatio = 0.85;
    // Stratum's SkipMovingEntities threshold (blocks per tick): a faster item is never throttled.
    private const double StratumMovingSpeed = 0.01;

    // Observed on both listed builds: 0.2 s accepted per 0.33 s handed over, so 0.6 of real time.
    // The band tolerates a window longer than the nominal minute and stays far from parity (1.0).
    private const double BugShapeMinRatio = 0.3;
    private const double BugShapeMaxRatio = 0.75;

    private const string EntityTickingKey = "Performance.EntityTicking.Enabled";
    private const string DespawnBehaviorName = "timeddespawn";
    private const string ClockAttribute = "deathTime";

    private static readonly int[] Offsets = { 16, 80, 200 };

    // The 0.2 s clamp in EntityBehaviorDespawn.OnGameTick meets the 10-tick hand-out of the
    // very-far band. The patches are identical on every Stratum tag since 1.22.1-stratum.1.
    private static readonly KnownDivergence FarItemClock =
        new("StratumServer/Stratum#359", StratumBuild.Stable2, StratumBuild.Indev1);

    private const string StratumHint =
        " Stratum hands a far item the delta time of all its skipped ticks and the despawn behavior " +
        "clamps each call at 0.2 s, so beyond 96 blocks the clock is expected to run at about 0.6 of real time.";

    private readonly ITestOutputHelper output;

    public DespawnClockScenarios(ITestOutputHelper output) => this.output = output;

    [AtlasScenario(TimeoutMs = 150_000)]
    public async Task FarDroppedItem_Should_AgeLikeNearItem_When_NoPlayerIsClose()
    {
        await AssertItemsAgeAlike("despawn-clock", anchorShiftZ: 0, FarItemClock);
    }

    [AtlasScenario(TimeoutMs = 150_000)]
    public async Task FarDroppedItem_Should_AgeLikeNearItem_When_EntityTickingDisabled()
    {
        // Same measurement with the distance throttle off: proves the throttle is the cause of
        // the divergence above. A no-op on vanilla, where the leg proves the default behavior.
        await using var toggle = await StratumSetting.Set(World, EntityTickingKey, "false");
        if (ServerFlavor.IsStratum)
        {
            string? applied = await StratumSetting.Get(World, EntityTickingKey);
            Assert.True(string.Equals(applied, "false", StringComparison.OrdinalIgnoreCase),
                $"{EntityTickingKey} reads '{applied}' after the toggle on stratum; setup is invalid");
        }

        await World.Ticks(5);
        await AssertItemsAgeAlike("despawn-off", anchorShiftZ: 400, divergence: null);
    }

    private async Task AssertItemsAgeAlike(string anchorName, int anchorShiftZ, KnownDivergence? divergence)
    {
        ITestPlayer anchor = await World.JoinPlayer(anchorName);
        await World.Ticks(2);
        // Pin the anchor: the join scatters players around spawn, and every throttle band is
        // measured from the nearest Playing client. Read the position only after the teleport settles.
        await anchor.TeleportTo(World.Spawn.AddCopy(0, 0, anchorShiftZ));
        await World.Ticks(2);
        BlockPos anchorPos = anchor.Position;

        Item? flint = World.Api.World.GetItem(new AssetLocation("game:flint"));
        Assert.True(flint != null, $"game:flint does not resolve on {ServerFlavor.Name}; setup is invalid");

        var items = new List<Entity>();
        try
        {
            foreach (int offset in Offsets)
            {
                await LoadColumn(anchorPos.AddCopy(offset, 0, 0));
            }

            foreach (int offset in Offsets)
            {
                items.Add(SpawnFlint(flint!, anchorPos.AddCopy(offset, 1, 0), offset));
            }

            await World.Ticks(SettleTicks);

            ItemReading[] before = ReadAll(items);
            AssertSetupHolds(anchor, items, before, "after the settle");
            long startMs = World.Api.World.ElapsedMilliseconds;

            await World.Ticks(WindowTicks);

            double windowSeconds = (World.Api.World.ElapsedMilliseconds - startMs) / 1000.0;
            ItemReading[] after = ReadAll(items);
            AssertSetupHolds(anchor, items, after, "after the window");
            Assert.True(windowSeconds >= MinWindowSeconds,
                $"the {WindowTicks}-tick window lasted {windowSeconds:F1} s on {ServerFlavor.Name}, " +
                $"under {MinWindowSeconds:F0} s; setup is invalid");

            string table = Describe(before, after, windowSeconds);
            output.WriteLine($"{ServerFlavor.Name} {ServerFlavor.Version ?? "none"}: {table}");

            double nearDelta = after[0].Age - before[0].Age;
            Assert.True(nearDelta >= MinNearAgeSeconds,
                $"near item aged only {nearDelta:F1} s over a {windowSeconds:F1} s window on {ServerFlavor.Name}, " +
                $"expected at least {MinNearAgeSeconds:F0} s; {table}");

            for (int i = 1; i < Offsets.Length; i++)
            {
                double ratio = (after[i].Age - before[i].Age) / nearDelta;
                // Only the very-far item carries the known bug: the +80 item stays strict.
                if (divergence is { Applies: true } && i == Offsets.Length - 1)
                {
                    Assert.True(ratio is >= BugShapeMinRatio and <= BugShapeMaxRatio,
                        $"{divergence.Tag}: item at +{Offsets[i]} aged {ratio:P0} as fast as the near item on {ServerFlavor.Name}, " +
                        $"expected the observed bug shape of {BugShapeMinRatio:P0} to {BugShapeMaxRatio:P0} " +
                        $"(0.2 s clamp over a 0.33 s hand-out); a clock keeping pace means the bug is fixed on this build, drop the exemption; {table}");
                    continue;
                }

                Assert.True(ratio >= MinAgeRatio,
                    $"item at +{Offsets[i]} aged {ratio:P0} as fast as the near item on {ServerFlavor.Name}, " +
                    $"expected at least {MinAgeRatio:P0}; {table}" + (ServerFlavor.IsStratum ? StratumHint : string.Empty));
            }
        }
        finally
        {
            // The world is shared with the other scenario of the class: leave no clock running.
            foreach (Entity item in items)
            {
                if (World.Api.World.GetEntityById(item.EntityId) != null)
                {
                    World.Api.World.DespawnEntity(item, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
                }
            }
        }
    }

    private async Task LoadColumn(BlockPos pos)
    {
        // KeepLoaded: no player is near the far columns to keep them alive, and the entity
        // throttle has no force-loaded exemption, so this cannot bias the measurement.
        World.Api.WorldManager.LoadChunkColumnPriority(pos.X / 32, pos.Z / 32, new ChunkLoadOptions { KeepLoaded = true });
        await World.Until(
            () => World.Api.World.BlockAccessor.GetChunkAtBlockPos(pos) != null,
            timeoutTicks: 600);
    }

    private Entity SpawnFlint(Item flint, BlockPos pos, int offset)
    {
        // Zero velocity: a null velocity gets a random one, and a moving item is never throttled.
        Entity? item = World.Api.World.SpawnItemEntity(
            new ItemStack(flint), new Vec3d(pos.X + 0.5, pos.Y, pos.Z + 0.5), new Vec3d());
        Assert.True(item != null && item.Alive,
            $"the flint at +{offset} did not spawn alive on {ServerFlavor.Name}; setup is invalid");
        return item!;
    }

    private ItemReading[] ReadAll(List<Entity> items) => items.Select(Read).ToArray();

    private static ItemReading Read(Entity item)
    {
        EntityBehavior? despawn = item.GetBehavior(DespawnBehaviorName);
        Assert.True(despawn != null,
            $"item {item.EntityId} has no {DespawnBehaviorName} behavior on {ServerFlavor.Name}; setup is invalid");
        FieldInfo? accum = despawn!.GetType().GetField(
            "accumSeconds", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.True(accum != null && accum.FieldType == typeof(float),
            $"{despawn.GetType().Name}.accumSeconds (float) not found on {ServerFlavor.Name}; setup is invalid");

        float deathTime = item.Attributes.GetFloat(ClockAttribute);
        float pending = (float)accum!.GetValue(despawn)!;
        Vec3d motion = item.Pos.Motion;
        double speed = Math.Sqrt(motion.X * motion.X + motion.Y * motion.Y + motion.Z * motion.Z);
        return new ItemReading(deathTime + pending, deathTime, speed, item.Pos.X, item.Pos.Y, item.Pos.Z);
    }

    /// <summary>Setup guards, same semantics as the other classes: a drifted anchor, a vanished
    /// item or a moving far item must read as an invalid setup, never as a wrong clock.</summary>
    private void AssertSetupHolds(ITestPlayer anchor, List<Entity> items, ItemReading[] readings, string when)
    {
        BlockPos p = anchor.Position;
        for (int i = 0; i < items.Count; i++)
        {
            Assert.True(items[i].Alive && World.Api.World.GetEntityById(items[i].EntityId) != null,
                $"the item at +{Offsets[i]} is gone {when} on {ServerFlavor.Name}; setup is invalid");

            double dx = readings[i].X - p.X;
            double dy = readings[i].Y - p.Y;
            double dz = readings[i].Z - p.Z;
            double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            // Bands are 32 / 64 / 96 blocks from the nearest Playing client.
            bool inBand = i switch
            {
                0 => distance < 28,
                1 => distance > 68 && distance < 92,
                _ => distance > 110,
            };
            Assert.True(inBand,
                $"the item at +{Offsets[i]} lies {distance:F1} blocks from the anchor {when} on {ServerFlavor.Name}, " +
                "outside its intended throttle band; setup is invalid");
        }

        // The very-far item must be at rest, or Stratum would not throttle it at all and the
        // measurement would pass for the wrong reason.
        int far = items.Count - 1;
        Assert.True(readings[far].Speed <= StratumMovingSpeed,
            $"the item at +{Offsets[far]} moves at {readings[far].Speed:F4} blocks per tick {when} on {ServerFlavor.Name}, " +
            $"above the {StratumMovingSpeed} skip threshold; setup is invalid");
    }

    private static string Describe(ItemReading[] before, ItemReading[] after, double windowSeconds)
    {
        var rows = new List<string>();
        for (int i = 0; i < Offsets.Length; i++)
        {
            rows.Add($"+{Offsets[i]}: clock {after[i].Age - before[i].Age:F1} s " +
                     $"(deathTime attribute {after[i].DeathTime - before[i].DeathTime:F1} s)");
        }

        return $"window {windowSeconds:F1} s, " + string.Join(", ", rows);
    }

    private sealed record ItemReading(double Age, double DeathTime, double Speed, double X, double Y, double Z);
}
