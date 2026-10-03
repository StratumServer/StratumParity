using Atlas.XUnit;
using Xunit;

namespace StratumParity.Scenarios;

/// <summary>
/// Same probe as <see cref="RandomTickProbes"/>, but the seeded
/// stratum-performance.json turns SimulationDistance.LimitRandomTicks off before boot.
/// Stratum must then random-tick far chunks again; vanilla ignores the file entirely.
/// One unconditional assertion proves parity on both flavors and proves the toggle
/// restores vanilla behavior. As with every Stratum config fixture, stratum.json must be
/// seeded too or the performance file is never read.
/// </summary>
[AtlasWorld(Mods = new[] { "mods/randomtickprobe" })]
[AtlasDataFiles("fixtures/stratum-randomticks-off", TargetPath = "")]
public class RandomTickDisabledScenarios : AtlasScenarioBase
{
    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task FarPlatform_Should_Convert_When_LimitDisabledByConfig()
    {
        RandomTickProbes.PlatformSet platforms =
            await RandomTickProbes.PlacePlatforms(World, "rt-anchor2");

        // Both platforms must convert; converging waits absorb the engine's asynchronous
        // chunk bookkeeping (see RandomTickProbes).
        await RandomTickProbes.WaitForConversions(World, platforms, platforms.Near, "near");
        await RandomTickProbes.WaitForConversions(World, platforms, platforms.Far, "far");
        // On Stratum a far conversion proves the toggle only if the centre never moved.
        RandomTickProbes.AssertAnchorChunkPinned(World, platforms.Anchor, platforms.AnchorChunkIndex,
            "after the far platform converted");
    }
}
