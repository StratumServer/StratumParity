using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace StratumParityAi
{
    /// <summary>
    /// Offers a task that counts the ShouldExecute calls of the AI task manager, which is the
    /// task start scan: vanilla runs it on every entity tick, Stratum on a timer. The scenario
    /// project cannot implement IAiTask itself (VSEssentials is not referenced there) and the
    /// ModLoader compiles this source into its own assembly, so the scenario side calls
    /// <see cref="Attach"/> through api.ModLoader.GetModSystem("StratumParityAi.AiProbeModSystem")
    /// and reflection, and reads the public Calls field of the task it gets back
    /// (StratumParity.Scenarios.AiProbe owns both).
    /// </summary>
    public class AiProbeModSystem : ModSystem
    {
        public const string TaskCode = "stratumparityscancounter";

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            // AiTaskManager.AddTask looks the task type up here to name its profiler marker.
            AiTaskRegistry.Register<ScanCounterTask>(TaskCode);
        }

        /// <summary>Adds a counting task to the entity's task manager and returns it, or null
        /// when the entity has no taskai behavior.</summary>
        public object Attach(Entity entity)
        {
            EntityBehaviorTaskAI behavior = entity.GetBehavior<EntityBehaviorTaskAI>();
            if (behavior == null)
            {
                return null;
            }

            var task = new ScanCounterTask();
            behavior.TaskManager.AddTask(task);
            return task;
        }
    }

    /// <summary>
    /// Never executes. Its own slot (the last of the eight) and a positive priority make the
    /// manager ask it on every scan: no other task can occupy the slot and block the question,
    /// and a negative priority would skip it.
    /// </summary>
    public class ScanCounterTask : IAiTask
    {
        /// <summary>Number of ShouldExecute calls so far. Read by reflection.</summary>
        public long Calls;

        public string Id => "stratumparityscancounter";
        public int Slot => 7;
        public float Priority => 1f;
        public float PriorityForCancel => 1f;
        public string ProfilerName { get; set; }

        public bool ShouldExecute()
        {
            Interlocked.Increment(ref Calls);
            return false;
        }

        public void StartExecute() { }
        public bool ContinueExecute(float dt) => false;
        public void FinishExecute(bool cancelled) { }
        public void AfterInitialize() { }
        public void OnStateChanged(EnumEntityState beforeState) { }
        public bool Notify(string key, object data) => false;
        public void OnEntityLoaded() { }
        public void OnEntitySpawn() { }
        public void OnEntityDespawn(EntityDespawnData reason) { }
        public void OnEntityHurt(DamageSource source, float damage) { }
        public bool CanContinueExecute() => true;
    }
}
