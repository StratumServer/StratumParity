using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Xunit;

namespace StratumParity.Scenarios;

/// <summary>
/// Command surface parity. Vanilla commands must behave identically on both flavors;
/// Stratum's own commands (/stratum, homes, tpa) must exist on Stratum only, so a vanilla
/// server rejecting them and a Stratum server accepting them are both part of the
/// expected contract.
/// </summary>
public class CommandParityScenarios : AtlasScenarioBase
{
    [AtlasScenario]
    public async Task TimeAdd_Should_AdvanceCalendar_When_Executed()
    {
        double before = World.Calendar.TotalHours;
        CommandResult result = await World.ExecuteCommand("/time add 2");

        Assert.True(result.Ok, $"/time add failed on {ServerFlavor.Name} ({result.Status}): {result.Message}");
        await World.Until(() => World.Calendar.TotalHours > before, timeoutTicks: 100);
    }

    [AtlasScenario]
    public async Task UnknownCommand_Should_ReportSameErrorCode_When_Executed()
    {
        CommandResult result = await World.ExecuteCommand("/nosuchcommandanywhere");

        Assert.False(result.Ok);
        Assert.Equal(EnumCommandStatus.NoSuchCommand, result.Status);
        Assert.Equal("nosuchcommand", result.ErrorCode);
    }

    [AtlasScenario]
    public async Task StratumCommand_Should_ExistOnlyOnStratum_When_Executed()
    {
        CommandResult result = await World.ExecuteCommand("/stratum");

        if (ServerFlavor.IsStratum)
        {
            Assert.NotEqual(EnumCommandStatus.NoSuchCommand, result.Status);
        }
        else
        {
            Assert.Equal(EnumCommandStatus.NoSuchCommand, result.Status);
        }
    }

    [AtlasScenario]
    public async Task StratumSetting_Should_RestorePreviousValue_When_HandleDisposed()
    {
        // Guards the toggle helper the runtime-toggle scenarios build on: a changed reply
        // format of /stratum get or set would otherwise surface as unrelated failures there.
        const string Key = "Performance.Physics.MaxActivationsPerTick";
        string? before = await StratumSetting.Get(World, Key);
        Assert.Equal(ServerFlavor.IsStratum, before != null);

        await using (await StratumSetting.Set(World, Key, "7"))
        {
            if (ServerFlavor.IsStratum)
            {
                Assert.Equal("7", await StratumSetting.Get(World, Key));
            }
        }

        Assert.Equal(before, await StratumSetting.Get(World, Key));
    }

    [AtlasScenario]
    public async Task HomeCommands_Should_ExistOnlyOnStratum_When_Executed()
    {
        // Registration is the contract under test, not successful execution: the console
        // caller has no world position, so /sethome may legitimately error on Stratum for
        // a different reason than "no such command".
        CommandResult result = await World.ExecuteCommand("/sethome");

        if (ServerFlavor.IsStratum)
        {
            Assert.NotEqual(EnumCommandStatus.NoSuchCommand, result.Status);
        }
        else
        {
            Assert.Equal(EnumCommandStatus.NoSuchCommand, result.Status);
        }
    }
}
