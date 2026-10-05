using Atlas.Api;
using Xunit;

namespace StratumParity.Scenarios;

/// <summary>
/// Decides when a sampled value has stopped changing: stable once <c>quietReads</c> consecutive
/// reads equal the one before them. World-free, so the rule is unit tested.
/// </summary>
internal sealed class StableTracker<T>
{
    private readonly int quietReads;
    private readonly IEqualityComparer<T> comparer;
    private bool hasPrevious;
    private T previous = default!;
    private int quiet;

    public StableTracker(int quietReads, IEqualityComparer<T>? comparer = null)
    {
        if (quietReads < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(quietReads), "at least one quiet read is needed");
        }

        this.quietReads = quietReads;
        this.comparer = comparer ?? EqualityComparer<T>.Default;
    }

    /// <summary>Feeds the next read; true once the value has stayed the same for the required reads.</summary>
    public bool Feed(T value)
    {
        if (hasPrevious && comparer.Equals(previous, value))
        {
            quiet++;
        }
        else
        {
            quiet = 0;
        }

        previous = value;
        hasPrevious = true;
        return quiet >= quietReads;
    }
}

/// <summary>
/// Waits for a world state to settle, for the scenarios that must read it after relighting,
/// fluid spreading or falling blocks have finished ("until two identical consecutive reads"):
/// never count ticks for those, the engine defers lighting and fluid passes by an unknown number.
/// <code>
/// string levels = await WorldStable.Until(World, () => string.Join(",", positions.Select(Read)));
/// </code>
/// </summary>
internal static class WorldStable
{
    /// <summary>Samples <paramref name="read"/> once per tick, starting now, until it returns the same value for
    /// <paramref name="quietTicks"/> consecutive ticks, and returns that value. <typeparamref name="T"/> needs value
    /// equality (a number, a string, a record): hash an array into a string. The scenario fails with "setup is
    /// invalid" naming <paramref name="what"/> when nothing settles within <paramref name="timeoutTicks"/>.</summary>
    public static async Task<T> Until<T>(IWorldSession world, Func<T> read, int quietTicks = 2, int timeoutTicks = 600, string what = "the sampled state")
    {
        var tracker = new StableTracker<T>(quietTicks);
        T value = read();
        tracker.Feed(value);
        for (int tick = 0; tick < timeoutTicks; tick++)
        {
            await world.Ticks(1);
            value = read();
            if (tracker.Feed(value))
            {
                return value;
            }
        }

        Assert.Fail($"{what} did not settle within {timeoutTicks} ticks on {ServerFlavor.Name} (last value {value}); setup is invalid");
        return value;
    }
}
