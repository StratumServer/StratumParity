using System.Reflection;

namespace StratumParity.Scenarios;

/// <summary>
/// Detects which server flavor the suite is running against. Stratum ships a rebuilt
/// VintagestoryLib that contains its runtime type; vanilla does not.
/// </summary>
public static class ServerFlavor
{
    public static bool IsStratum { get; } =
        Type.GetType("Vintagestory.Server.StratumRuntime, VintagestoryLib") != null;

    public static string Name => IsStratum ? "stratum" : "vanilla";

    /// <summary>
    /// The Stratum build string ("1.22.7-stratum.2", "1.22.7-stratum.2-indev.1"), read from the
    /// internal static Vintagestory.Server.StratumInfo.Version property. Null on vanilla, and
    /// null on a Stratum build that does not carry the type: <see cref="KnownDivergence"/> then
    /// never applies, which keeps the strict-parity side of the policy.
    /// </summary>
    public static string? Version { get; } = ReadVersion();

    private static string? ReadVersion()
    {
        if (!IsStratum)
        {
            return null;
        }

        try
        {
            Type? info = Type.GetType("Vintagestory.Server.StratumInfo, VintagestoryLib");
            object? value = info?
                .GetProperty("Version", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?
                .GetValue(null);
            return value as string;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
