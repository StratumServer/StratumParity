using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace StratumParity.Scenarios;

/// <summary>
/// Counts how many times the server ticks an entity. Attached at runtime from scenarios;
/// Entity.OnGameTick drives behavior ticks, so when Stratum's distance-band throttling
/// skips an entity's tick, this counter freezes with it. <see cref="DtSum"/> and
/// <see cref="MaxDt"/> record the delta time each tick was handed: a throttled entity is
/// expected to receive the time it skipped on its next tick, so DtSum keeps pace with an
/// unthrottled entity's even though Ticks does not.
/// </summary>
public sealed class TickCounterBehavior : EntityBehavior
{
    public int Ticks;

    /// <summary>Sum of the deltaTime (seconds) of every OnGameTick call. Read between two
    /// measurement points and subtract, like Ticks.</summary>
    public double DtSum;

    /// <summary>Largest single deltaTime (seconds) any OnGameTick call received since the
    /// behavior was attached or since the last <see cref="ResetMaxDt"/>.</summary>
    public float MaxDt;

    public TickCounterBehavior(Entity entity) : base(entity)
    {
    }

    public override void OnGameTick(float deltaTime)
    {
        Ticks++;
        DtSum += deltaTime;
        if (deltaTime > MaxDt)
        {
            MaxDt = deltaTime;
        }
    }

    /// <summary>Starts a fresh MaxDt window, so a spawn-time spike does not count.</summary>
    public void ResetMaxDt() => MaxDt = 0;

    public override string PropertyName() => "stratumparity:tickcounter";
}
