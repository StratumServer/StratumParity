using Xunit;

namespace StratumParity.Scenarios;

/// <summary>World-free check of the block id arrays of the registry digest: the engine writes runtime block ids
/// into hostRock and hostRockFor, and they differ from one machine to the next.</summary>
public class AssetRegistryDigestTests
{
    [Fact]
    public void CanonicalJson_Should_WriteBlockIdArraysAsSortedCodes_When_TheEngineFilledThem()
    {
        // The same two blocks under the ids two different machines gave them.
        var machineA = new Dictionary<int, string> { [11159] = "game:rock-andesite", [7354] = "game:soil-low-none" };
        var machineB = new Dictionary<int, string> { [5798] = "game:rock-andesite", [6415] = "game:soil-low-none" };

        string a = AssetRegistryDigest.CanonicalJson("{\"propickable\":true,\"hostRock\":[11159,11159],\"hostRockFor\":[7354,11159]}", id => machineA[id]);
        string b = AssetRegistryDigest.CanonicalJson("{\"propickable\":true,\"hostRock\":[5798,5798],\"hostRockFor\":[6415,5798]}", id => machineB[id]);

        Assert.Equal(a, b);
        Assert.Equal(
            "{\"hostRock\":[\"game:rock-andesite\",\"game:rock-andesite\"],\"hostRockFor\":[\"game:rock-andesite\",\"game:soil-low-none\"],\"propickable\":true}",
            a);
        Assert.Contains("[11159,11159]", AssetRegistryDigest.CanonicalJson("{\"hostRock\":[11159,11159]}"));
    }
}
