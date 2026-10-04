using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.MathTools;
using Xunit;
using Xunit.Abstractions;

namespace StratumParity.Scenarios;

/// <summary>
/// Baseline parity: the fundamentals a Stratum server must do exactly like vanilla.
/// Every scenario here asserts the same thing on both flavors; a failure on Stratum
/// only is a behavior regression.
/// </summary>
public class SmokeParityScenarios : AtlasScenarioBase
{
    private readonly ITestOutputHelper output;

    public SmokeParityScenarios(ITestOutputHelper output) => this.output = output;

    [AtlasScenario]
    public Task LoadedServer_Should_MatchExpectedFlavor_When_CiArmsTheGuard()
    {
        // Catches a misconfigured CI leg that ran the wrong install under a flavor's label:
        // without this, every differential scenario would read ServerFlavor.IsStratum,
        // silently pick the vanilla expectations, and the suite would report parity between
        // vanilla and vanilla. Unset (a developer running `dotnet test` by hand) is not
        // armed, so it passes and just says so.
        string? expected = Environment.GetEnvironmentVariable("PARITY_EXPECTED_FLAVOR");
        if (string.IsNullOrEmpty(expected))
        {
            output.WriteLine("PARITY_EXPECTED_FLAVOR not set; flavor guard not armed.");
            return Task.CompletedTask;
        }

        // StartsWith, not an exact match: the indev scout workflow labels its Stratum leg
        // "stratum-indev", not "stratum".
        bool expectStratum = expected.StartsWith("stratum", StringComparison.OrdinalIgnoreCase);
        Assert.True(expectStratum == ServerFlavor.IsStratum,
            $"CI expected flavor '{expected}' but the loaded server reports {ServerFlavor.Name}");
        return Task.CompletedTask;
    }

    [AtlasScenario]
    public Task LoadedServer_Should_ReportBuildVersion_When_ItIsStratum()
    {
        // KnownDivergence matches this string exactly, so it must be readable on every
        // Stratum build the suite runs against and absent on vanilla.
        output.WriteLine($"flavor={ServerFlavor.Name} version={ServerFlavor.Version ?? "none"}");
        if (ServerFlavor.IsStratum)
        {
            Assert.Contains("-stratum.", ServerFlavor.Version);
        }
        else
        {
            Assert.Null(ServerFlavor.Version);
        }

        return Task.CompletedTask;
    }

    [AtlasScenario]
    public async Task Server_Should_BootAndAdvanceClock_When_Ticked()
    {
        // The game calendar pauses on an empty server (same on both flavors), so the boot
        // check reads the server clock instead.
        long before = World.Api.World.ElapsedMilliseconds;
        await World.Ticks(30);
        Assert.True(World.Api.World.ElapsedMilliseconds > before,
            $"server clock did not advance on {ServerFlavor.Name}");
    }

    [AtlasScenario]
    public async Task SetBlock_Should_ReadBackSameCode_When_Placed()
    {
        BlockPos pos = World.Spawn.AddCopy(2, 1, 2);
        World.SetBlock("game:rock-granite", pos);
        await World.Ticks(5);
        Assert.Equal("game:rock-granite", World.BlockAt(pos).Code.ToString());
    }

    [AtlasScenario]
    public async Task JoinedPlayer_Should_StayConnected_When_ServerTicks()
    {
        // On Stratum this doubles as a packet-policing check: StratumPacketLimiter has no
        // single-player exemption, so a kicked fake player would show up as IsConnected false.
        ITestPlayer player = await World.JoinPlayer("parity-smoke");
        await World.Ticks(100);
        Assert.True(player.IsConnected,
            $"fake player was disconnected on {ServerFlavor.Name}");
    }
}
