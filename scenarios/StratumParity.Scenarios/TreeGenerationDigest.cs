using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StratumParity.Scenarios;

/// <summary>One position a grown tree staged, relative to the block the tree was grown on: the solid
/// block code ("-" when only the fluid or a decor was staged), the fluid code ("-" when none) and the
/// decors on it as "face:code" pairs joined by ';' in ordinal order ("" when none).</summary>
internal sealed record StagedBlock(int X, int Y, int Z, string Solid, string Fluid, string Decors);

/// <summary>
/// The canonical text of one grown tree, its hash and the golden file of <see cref="TreeGenerationScenarios"/>.
/// No game type in here, so the layout and the golden comparison are covered by world-free unit tests.
///
/// The canonical text is one line per staged position, ordered by y, then x, then z, as
/// <c>x,y,z solid=&lt;code&gt; fluid=&lt;code&gt; decors=&lt;face:code;...&gt;</c>: the order the engine staged the blocks in
/// never matters, only the final state of each position does. A row of the golden is
/// <c>&lt;position count&gt;:&lt;first 16 hex digits of the SHA-256 of that text&gt;</c> under the key
/// <c>&lt;generator&gt;/&lt;profile&gt;/&lt;sample&gt;</c>. Changing the layout re-pins the golden: bump <see cref="Format"/>.
/// </summary>
internal static class TreeGenerationDigest
{
    public const int Format = 1;
    private const int HashChars = 16;

    public static string Canonical(IEnumerable<StagedBlock> blocks)
    {
        var text = new StringBuilder();
        foreach (StagedBlock b in blocks.OrderBy(b => b.Y).ThenBy(b => b.X).ThenBy(b => b.Z))
        {
            text.Append(CultureInfo.InvariantCulture, $"{b.X},{b.Y},{b.Z}")
                .Append(" solid=").Append(b.Solid).Append(" fluid=").Append(b.Fluid).Append(" decors=").Append(b.Decors)
                .Append('\n');
        }

        return text.ToString();
    }

    public static string Hash(string canonical) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).Substring(0, HashChars).ToLowerInvariant();

    public static string Row(int positions, string canonical) => string.Create(CultureInfo.InvariantCulture, $"{positions}:{Hash(canonical)}");

    public static string ToJson(string gameVersion, IReadOnlyDictionary<string, string> rows)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"format\": ").Append(Format.ToString(CultureInfo.InvariantCulture)).Append(",\n");
        sb.Append("  \"gameVersion\": ").Append(JsonSerializer.Serialize(gameVersion)).Append(",\n");
        sb.Append("  \"trees\": {\n");
        int i = 0;
        foreach (KeyValuePair<string, string> row in rows.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            sb.Append("    ").Append(JsonSerializer.Serialize(row.Key)).Append(": ").Append(JsonSerializer.Serialize(row.Value));
            sb.Append(++i < rows.Count ? ",\n" : "\n");
        }

        sb.Append("  }\n}\n");
        return sb.ToString();
    }

    public static (string GameVersion, SortedDictionary<string, string> Rows) FromJson(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        int format = root.GetProperty("format").GetInt32();
        if (format != Format)
        {
            throw new InvalidDataException($"tree golden has format {format}, this build reads format {Format}");
        }

        var rows = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty p in root.GetProperty("trees").EnumerateObject())
        {
            rows[p.Name] = p.Value.GetString() ?? "";
        }

        return (root.GetProperty("gameVersion").GetString() ?? "", rows);
    }

    /// <summary>One line per sample whose row differs from the golden, plus the samples only one side has.
    /// Empty when the two agree.</summary>
    public static List<string> Mismatches(IReadOnlyDictionary<string, string> golden, IReadOnlyDictionary<string, string> actual)
    {
        var lines = new List<string>();
        foreach (KeyValuePair<string, string> want in golden.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (!actual.TryGetValue(want.Key, out string? got))
            {
                lines.Add($"{want.Key}: golden row but no tree grown (generator missing)");
            }
            else if (got != want.Value)
            {
                lines.Add($"{want.Key}: got {got}, golden {want.Value}");
            }
        }

        foreach (string key in actual.Keys.Where(k => !golden.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal))
        {
            lines.Add($"{key}: tree grown ({actual[key]}) but no golden row (new generator)");
        }

        return lines;
    }
}
