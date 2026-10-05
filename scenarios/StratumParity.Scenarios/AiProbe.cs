using System.Reflection;
using Atlas.Api;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Xunit;

namespace StratumParity.Scenarios;

/// <summary>A counting AI task attached to one entity by <see cref="AiProbe.Attach"/>.</summary>
public sealed class AiScanCounter
{
    private readonly object task;
    private readonly FieldInfo calls;

    internal AiScanCounter(object task, FieldInfo calls)
    {
        this.task = task;
        this.calls = calls;
    }

    /// <summary>How many times the entity's AI task manager has asked this task whether it should
    /// execute, which is once per task start scan (per entity tick on vanilla, on Stratum's timer).</summary>
    public long ShouldExecuteCalls => (long)calls.GetValue(task)!;
}

/// <summary>
/// Reaches the counting AI task of the staged stratumparityai mod (mods/aiprobe). The scenario
/// project does not reference VSEssentials, so it cannot implement IAiTask: the mod does, and
/// this class calls its AiProbeModSystem.Attach through api.ModLoader and reflection. The class
/// must stage the mod with [AtlasWorld(Mods = new[] { "mods/aiprobe" })].
/// <code>
/// RigEntity raccoon = EntityProbeRig.SpawnCounted(World, pos, "game:raccoon-common-adult-male");
/// AiScanCounter scan = AiProbe.Attach(World, raccoon.Entity);
/// long before = scan.ShouldExecuteCalls;
/// </code>
/// The task has its own slot and never executes, so it observes the scan without changing what the
/// creature does. Attach it only to an entity with a taskai behavior (the raccoon has one).
/// </summary>
public static class AiProbe
{
    private const string ModSystemName = "StratumParityAi.AiProbeModSystem";

    public static AiScanCounter Attach(IWorldSession world, Entity entity)
    {
        ModSystem? system = world.Api.ModLoader.GetModSystem(ModSystemName);
        Assert.True(system != null,
            $"mod system {ModSystemName} is not loaded on {ServerFlavor.Name}: the class must stage mods/aiprobe; setup is invalid");

        MethodInfo? attach = system!.GetType().GetMethod("Attach");
        Assert.True(attach != null, $"{ModSystemName}.Attach is missing; setup is invalid");

        object? task = attach!.Invoke(system, new object[] { entity });
        Assert.True(task != null,
            $"{entity.Code} has no taskai behavior to attach the scan counter to on {ServerFlavor.Name}; setup is invalid");

        FieldInfo? calls = task!.GetType().GetField("Calls");
        Assert.True(calls != null && calls.FieldType == typeof(long), "ScanCounterTask.Calls is missing or not a long; setup is invalid");

        return new AiScanCounter(task, calls!);
    }
}
