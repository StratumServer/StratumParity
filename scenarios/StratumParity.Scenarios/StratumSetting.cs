using System.Net;
using System.Text.RegularExpressions;
using Atlas.Api;
using Xunit;

namespace StratumParity.Scenarios;

/// <summary>
/// Hot config toggles through <c>/stratum set</c> (dotted PascalCase keys, for example
/// <c>Performance.Physics.MaxActivationsPerTick</c>). Use it for settings the engine reads
/// per tick or per entity init; a key that is only read at boot needs a fixture folder with
/// <c>stratum.json</c> instead (see the conventions in the extension plan).
/// <code>
/// await using var toggle = await StratumSetting.Set(World, "Performance.Physics.MaxActivationsPerTick", "0");
/// </code>
/// On vanilla there is no <c>/stratum</c> command: Get returns null and Set returns a no-op
/// handle, so the same scenario body runs on both flavors and the vanilla leg proves that
/// the default behavior is the vanilla one. On Stratum every command must answer Ok, and the
/// previous value is restored when the handle is disposed. Scalar bool, number and enum keys
/// only: string values come back quoted and are refused.
/// </summary>
public static class StratumSetting
{
    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);

    /// <summary>The current formatted value of a key ("true", "50", "0.5"), or null on vanilla.</summary>
    public static async Task<string?> Get(IWorldSession world, string path)
    {
        if (!ServerFlavor.IsStratum)
        {
            return null;
        }

        CommandResult result = await world.ExecuteCommand("/stratum get " + path);
        Assert.True(result.Ok,
            $"/stratum get {path} failed on {ServerFlavor.Name} ({result.Status}): {result.Message}; setup is invalid");

        // The reply is one colored row per scalar: "<font ..>Path:</font> value".
        string prefix = path + ":";
        foreach (string line in Tags.Replace(result.Message, string.Empty).Split('\n'))
        {
            string row = line.Trim();
            if (row.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return WebUtility.HtmlDecode(row.Substring(prefix.Length)).Trim();
            }
        }

        Assert.Fail($"/stratum get {path} answered no scalar row on {ServerFlavor.Name}: {result.Message}; setup is invalid");
        return null;
    }

    /// <summary>Sets a key on Stratum and returns the handle that restores the previous value;
    /// a no-op on vanilla.</summary>
    public static async Task<IAsyncDisposable> Set(IWorldSession world, string path, string value)
    {
        if (!ServerFlavor.IsStratum)
        {
            return new Restore(null, path, string.Empty);
        }

        string previous = (await Get(world, path))!;
        Assert.False(previous.StartsWith('"'), $"{path} is a string setting, which StratumSetting does not support; setup is invalid");
        await Run(world, $"/stratum set {path} {value}");
        return new Restore(world, path, previous);
    }

    private static async Task Run(IWorldSession world, string command)
    {
        CommandResult result = await world.ExecuteCommand(command);
        Assert.True(result.Ok,
            $"{command} failed on {ServerFlavor.Name} ({result.Status}): {result.Message}; setup is invalid");
    }

    private sealed class Restore(IWorldSession? world, string path, string previous) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (world != null)
            {
                await Run(world, $"/stratum set {path} {previous}");
            }
        }
    }
}
