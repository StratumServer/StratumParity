using System.Globalization;
using Xunit;

namespace StratumParity.Scenarios;

/// <summary>World-free checks of the tree digest and the golden file, so a changed layout or a
/// mangled golden cannot turn into a comparison against the wrong sample.</summary>
public class TreeGenerationDigestTests
{
    private static readonly StagedBlock[] Tree =
    {
        new(0, 1, 0, "game:log-grown-oak-ud", "-", ""),
        new(0, 2, 0, "game:log-grown-oak-ud", "-", "0:game:attachingplant-spottymoss;2:game:attachingplant-spottymoss"),
        new(1, 2, 0, "game:leaves-grown-oak", "-", ""),
        new(0, 3, 1, "-", "-", "4:game:wildvine-section-north"),
    };

    [Fact]
    public void Canonical_Should_NotDependOnTheOrderBlocksWereStaged_When_TheFinalStateIsTheSame()
    {
        string forward = TreeGenerationDigest.Canonical(Tree);
        string backward = TreeGenerationDigest.Canonical(Enumerable.Reverse(Tree).ToArray());

        Assert.Equal(forward, backward);
        Assert.Equal(TreeGenerationDigest.Hash(forward), TreeGenerationDigest.Hash(backward));
        Assert.StartsWith("0,1,0 solid=game:log-grown-oak-ud fluid=- decors=\n", forward);
    }

    [Fact]
    public void Canonical_Should_UseTheInvariantCulture_When_TheCurrentCultureWritesAnotherMinusSign()
    {
        CultureInfo saved = CultureInfo.CurrentCulture;
        var other = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        other.NumberFormat.NegativeSign = "\u2212";
        try
        {
            CultureInfo.CurrentCulture = other;
            Assert.StartsWith("-2,1,-3 solid=game:rock-granite", TreeGenerationDigest.Canonical(new[] { new StagedBlock(-2, 1, -3, "game:rock-granite", "-", "") }));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void Row_Should_Change_When_OneBlockOrOneDecorChanges()
    {
        string reference = TreeGenerationDigest.Row(Tree.Length, TreeGenerationDigest.Canonical(Tree));

        StagedBlock[] otherBlock = (StagedBlock[])Tree.Clone();
        otherBlock[2] = otherBlock[2] with { Solid = "game:leavesbranchy-grown-oak" };
        StagedBlock[] otherDecor = (StagedBlock[])Tree.Clone();
        otherDecor[1] = otherDecor[1] with { Decors = "0:game:attachingplant-spottymoss" };

        Assert.NotEqual(reference, TreeGenerationDigest.Row(Tree.Length, TreeGenerationDigest.Canonical(otherBlock)));
        Assert.NotEqual(reference, TreeGenerationDigest.Row(Tree.Length, TreeGenerationDigest.Canonical(otherDecor)));
        Assert.Matches("^4:[0-9a-f]{16}$", reference);
    }

    [Fact]
    public void Golden_Should_RoundTripAndNameTheSample_When_OneRowDiffers()
    {
        var rows = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["acacia/sapling/1"] = TreeGenerationDigest.Row(Tree.Length, TreeGenerationDigest.Canonical(Tree)),
            ["acacia/worldgen/2"] = "error:NullReferenceException",
        };

        (string version, SortedDictionary<string, string> parsed) = TreeGenerationDigest.FromJson(TreeGenerationDigest.ToJson("1.22.7", rows));

        Assert.Equal("1.22.7", version);
        Assert.Equal(rows, parsed);
        Assert.Empty(TreeGenerationDigest.Mismatches(rows, parsed));

        var actual = new SortedDictionary<string, string>(parsed, StringComparer.Ordinal)
        {
            ["acacia/sapling/1"] = "5:0000000000000000",
            ["birch/sapling/1"] = "1:1111111111111111",
        };
        actual.Remove("acacia/worldgen/2");

        List<string> found = TreeGenerationDigest.Mismatches(rows, actual);

        Assert.Equal(3, found.Count);
        Assert.Contains(found, m => m.StartsWith("acacia/sapling/1: got 5:0000000000000000"));
        Assert.Contains(found, m => m.StartsWith("acacia/worldgen/2: golden row but no tree grown"));
        Assert.Contains(found, m => m.StartsWith("birch/sapling/1: tree grown"));
    }
}
