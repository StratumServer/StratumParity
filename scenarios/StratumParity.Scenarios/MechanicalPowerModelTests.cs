using Xunit;

namespace StratumParity.Scenarios;

/// <summary>World-free checks of the settled speed model behind <see cref="MechanicalPowerScenarios"/>, so a
/// slip in the bisection cannot move the expectation of all three scenarios at once.</summary>
public class MechanicalPowerModelTests
{
    [Fact]
    public void SettledSpeed_Should_MatchTheHandSolution_When_TheLineIsShort()
    {
        // 0.45 - 1.5 s = 0.0035 + 0.008 s^2 at the creative rotor defaults and seven axles (eight nodes).
        double speed = MechanicalPowerModel.SettledSpeed(powerSetting: 3, speedSetting: 3, passiveResistance: 7 * 0.0005, nodes: 8);

        Assert.InRange(speed, 0.2970, 0.2974);
    }

    [Fact]
    public void SettledSpeed_Should_StayBelowTheTargetAndFall_When_ResistanceGrows()
    {
        double light = MechanicalPowerModel.SettledSpeed(3, 10, 0.002, 5);
        double heavy = MechanicalPowerModel.SettledSpeed(3, 10, 0.102, 5);

        Assert.InRange(light, 0.9, 1.0);
        Assert.True(heavy < light, $"a quern on the line ({heavy}) must settle slower than a bare line ({light})");
    }

    [Fact]
    public void SettledSpeed_Should_BeZero_When_ResistanceBeatsTheRotor()
    {
        Assert.Equal(0, MechanicalPowerModel.SettledSpeed(1, 1, 5.0, 3), precision: 6);
    }
}
