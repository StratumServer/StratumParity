namespace StratumParity.Scenarios;

/// <summary>The Stratum build strings the suite has confirmed bugs on, as reported by
/// <see cref="ServerFlavor.Version"/>. Exact strings, never ranges: a new build is strict
/// parity until someone confirms the bug on it.</summary>
public static class StratumBuild
{
    /// <summary>The pinned lane (tag v1.22.7-stratum.2).</summary>
    public const string Stable2 = "1.22.7-stratum.2";

    /// <summary>The scouted prerelease at the time of writing (tag v1.22.7-stratum.2-indev.1).</summary>
    public const string Indev1 = "1.22.7-stratum.2-indev.1";
}

/// <summary>
/// One confirmed Stratum bug: the issue that tracks it and the exact builds known to carry
/// it. A bug-first scenario declares one as a private static readonly and branches on
/// <see cref="Applies"/>:
/// <code>
/// private static readonly KnownDivergence Rewrite = new("StratumServer/Stratum#353", StratumBuild.Stable2, StratumBuild.Indev1);
/// ...
/// if (Rewrite.Applies) Assert.True(rewritten >= 1, $"{Rewrite.Tag}: bug shape gone, drop the exemption");
/// else Assert.True(rewritten == 0, $"... on {ServerFlavor.Name}");
/// </code>
/// On a listed build the Stratum branch asserts the observed bug shape (so a silent fix turns
/// the scenario red and the entry gets removed). On vanilla, and on every Stratum build that is
/// not listed, including a fresh prerelease that still carries the bug, the scenario is strict
/// parity. Bumping the pinned tag past the fix means deleting the entry.
/// </summary>
public sealed class KnownDivergence
{
    public KnownDivergence(string issue, params string[] builds)
    {
        if (string.IsNullOrWhiteSpace(issue))
        {
            throw new ArgumentException("a known divergence needs an issue reference", nameof(issue));
        }

        if (builds.Length == 0)
        {
            throw new ArgumentException("a known divergence needs at least one build", nameof(builds));
        }

        Issue = issue;
        Builds = builds;
    }

    public string Issue { get; }

    public IReadOnlyList<string> Builds { get; }

    /// <summary>True only on a Stratum server whose build string is in <see cref="Builds"/>.</summary>
    public bool Applies => AppliesTo(ServerFlavor.Version);

    /// <summary>Message fragment naming the issue and the listed builds, valid on both sides of the branch.</summary>
    public string Tag => $"known divergence {Issue} (confirmed on {string.Join(", ", Builds)}; running {ServerFlavor.Version ?? ServerFlavor.Name})";

    internal bool AppliesTo(string? version) =>
        version != null && Builds.Contains(version, StringComparer.Ordinal);
}
