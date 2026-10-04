using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace StratumParity.Scenarios;

/// <summary>
/// Per-code content digest of everything the server resolved from assets: every block, item and
/// entity type, plus the grid recipes grouped by output code. Used only by
/// <see cref="AssetMatchingScenarios"/>.
///
/// Each registry object is described by a canonical text (a bounded reflective walk over the data
/// classes of the API assembly, see <see cref="Walker"/>) and reduced to a short SHA-256 prefix.
/// The text never contains runtime ids (block, item, entity, tag and recipe ids depend on load
/// order, which Stratum's parallel type loader may legitimately change): tag sets are written as
/// sorted tag names, references to other registry objects as their code, dictionaries and sets in
/// sorted order, JSON attributes with sorted keys. Lists keep their order, so a reordered shape
/// alternate or drop table is a difference. The attribute arrays the engine fills with block ids at load
/// (hostRock, hostRockFor, see <see cref="CanonicalJson"/>) are written as sorted block codes: the first
/// golden kept the ids of the machine it was captured on and failed on every other one. Coverage ceiling:
/// lists keep the order JSON patches produced, which depends on the asset file enumeration order. The golden
/// was checked against installs whose directories were created in shuffled orders (a CI runner lists a
/// directory in another order than the machine the golden came from): if a runner still differs by a list
/// order alone, find the list with PARITY_REGISTRY_DUMP and sort it here or leave it out explicitly.
///
/// Capture and diagnosis (the golden is committed, see <see cref="AssetMatchingScenarios"/>):
/// <c>PARITY_REGISTRY_CAPTURE=&lt;file&gt;</c> writes the golden from a vanilla run,
/// <c>PARITY_REGISTRY_DUMP=&lt;dir&gt;</c> writes the canonical text of every object (one file per
/// section) so that two runs can be compared with a plain diff when a hash differs.
/// </summary>
internal static class AssetRegistryDigest
{
    internal const int Format = 1;
    private const int HashChars = 12;
    private const string NoOutput = "(no output)";

    internal sealed class Digest
    {
        public string GameVersion { get; set; } = "";
        public int GridRecipeCount { get; set; }
        public SortedDictionary<string, string> Blocks { get; } = new(StringComparer.Ordinal);
        public SortedDictionary<string, string> Items { get; } = new(StringComparer.Ordinal);
        public SortedDictionary<string, string> Entities { get; } = new(StringComparer.Ordinal);
        /// <summary>Grid recipes grouped by resolved output code: one hash per output over the sorted hashes of its recipes.</summary>
        public SortedDictionary<string, string> GridRecipes { get; } = new(StringComparer.Ordinal);

        /// <summary>Roots whose walk hit the node budget (the text then ends in a marker): a systematic count here means a type needs a skip rule.</summary>
        public int TruncatedRoots { get; set; }

        /// <summary>Subtrees cut at the depth limit (written as a marker): content that falls out of the comparison, like <see cref="TruncatedRoots"/>.</summary>
        public int DepthCutoffs { get; set; }

        /// <summary>Roots whose walk threw outside a member getter (a bug in the walker, not a content difference), with the first message.</summary>
        public int FailedRoots { get; set; }

        public string? FirstFailure { get; set; }

        /// <summary>Canonical text per section and code, filled only when requested.</summary>
        public Dictionary<string, SortedDictionary<string, string>>? Text { get; set; }

        public IEnumerable<(string Section, SortedDictionary<string, string> Entries)> Sections()
        {
            yield return ("blocks", Blocks);
            yield return ("items", Items);
            yield return ("entities", Entities);
            yield return ("gridRecipes", GridRecipes);
        }

        public string SectionHash(SortedDictionary<string, string> entries)
        {
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, string> e in entries)
            {
                sb.Append(e.Key).Append('=').Append(e.Value).Append('\n');
            }

            return Hash(sb.ToString());
        }
    }

    /// <summary>Digests the live registry of a booted server. Runs on the game thread.</summary>
    internal static Digest Compute(ICoreAPI api, bool keepText)
    {
        IWorldAccessor world = api.World;
        var digest = new Digest { GameVersion = RunningGameVersion() };
        if (keepText)
        {
            digest.Text = new Dictionary<string, SortedDictionary<string, string>>
            {
                ["blocks"] = new(StringComparer.Ordinal),
                ["items"] = new(StringComparer.Ordinal),
                ["entities"] = new(StringComparer.Ordinal),
                ["gridRecipes"] = new(StringComparer.Ordinal),
            };
        }

        var walker = new Walker(api);

        foreach (Block? block in world.Blocks)
        {
            if (block?.Code != null)
            {
                Add(digest, "blocks", digest.Blocks, block.Code.ToString(), walker.Describe(block));
            }
        }

        foreach (Item? item in world.Items)
        {
            if (item?.Code != null)
            {
                Add(digest, "items", digest.Items, item.Code.ToString(), walker.Describe(item));
            }
        }

        foreach (EntityProperties? type in world.EntityTypes)
        {
            if (type?.Code != null)
            {
                Add(digest, "entities", digest.Entities, type.Code.ToString(), walker.Describe(type));
            }
        }

        // Recipes: grouped by output code and order-insensitive inside a group, so the digest does
        // not depend on the order the asset files were enumerated in.
        var byOutput = new SortedDictionary<string, List<(string Hash, string Text)>>(StringComparer.Ordinal);
        foreach (GridRecipe recipe in world.GridRecipes)
        {
            digest.GridRecipeCount++;
            string key = recipe.Output?.Code?.ToString() ?? NoOutput;
            string text = walker.Describe(recipe);
            if (!byOutput.TryGetValue(key, out List<(string, string)>? group))
            {
                byOutput[key] = group = new List<(string, string)>();
            }

            group.Add((Hash(text), text));
        }

        foreach (KeyValuePair<string, List<(string Hash, string Text)>> group in byOutput)
        {
            group.Value.Sort((a, b) => string.CompareOrdinal(a.Hash, b.Hash));
            string joined = string.Join("\n", group.Value.Select(r => r.Text));
            Add(digest, "gridRecipes", digest.GridRecipes, group.Key, joined);
        }

        digest.TruncatedRoots = walker.TruncatedRoots;
        digest.DepthCutoffs = walker.DepthCutoffs;
        digest.FailedRoots = walker.FailedRoots;
        digest.FirstFailure = walker.FirstFailure;
        return digest;
    }

    /// <summary>GameVersion.ShortGameVersion is a const, so reading it directly would bake the build-time API version
    /// into this assembly: Atlas stages the target install's API dll over the build-time copy, and only a read through
    /// reflection sees the one that actually runs.</summary>
    internal static string RunningGameVersion() =>
        (string?)typeof(Vintagestory.API.Config.GameVersion).GetField(nameof(Vintagestory.API.Config.GameVersion.ShortGameVersion))?.GetValue(null) ?? "unknown";

    private static void Add(Digest digest, string section, SortedDictionary<string, string> target, string key, string text)
    {
        // A duplicated code would hide one of the two objects: keep both under distinct keys.
        string finalKey = key;
        for (int n = 2; target.ContainsKey(finalKey); n++)
        {
            finalKey = string.Create(CultureInfo.InvariantCulture, $"{key}#{n}");
        }

        target[finalKey] = Hash(text);
        if (digest.Text != null)
        {
            digest.Text[section][finalKey] = text;
        }
    }

    internal static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).Substring(0, HashChars).ToLowerInvariant();

    // ---------------------------------------------------------------- golden file

    internal static string ToJson(Digest digest)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"format\": ").Append(Format.ToString(CultureInfo.InvariantCulture)).Append(",\n");
        sb.Append("  \"gameVersion\": ").Append(JsonSerializer.Serialize(digest.GameVersion)).Append(",\n");
        sb.Append("  \"gridRecipeCount\": ").Append(digest.GridRecipeCount.ToString(CultureInfo.InvariantCulture)).Append(",\n");

        var sections = digest.Sections().ToList();
        for (int s = 0; s < sections.Count; s++)
        {
            sb.Append("  ").Append(JsonSerializer.Serialize(sections[s].Section)).Append(": {\n");
            int i = 0;
            foreach (KeyValuePair<string, string> e in sections[s].Entries)
            {
                sb.Append("    ").Append(JsonSerializer.Serialize(e.Key)).Append(": ").Append(JsonSerializer.Serialize(e.Value));
                sb.Append(++i < sections[s].Entries.Count ? ",\n" : "\n");
            }

            sb.Append(s < sections.Count - 1 ? "  },\n" : "  }\n");
        }

        sb.Append("}\n");
        return sb.ToString();
    }

    internal static Digest FromJson(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        int format = root.GetProperty("format").GetInt32();
        if (format != Format)
        {
            throw new InvalidDataException($"registry golden has format {format}, this build reads format {Format}");
        }

        var digest = new Digest
        {
            GameVersion = root.GetProperty("gameVersion").GetString() ?? "",
            GridRecipeCount = root.GetProperty("gridRecipeCount").GetInt32(),
        };
        foreach ((string section, SortedDictionary<string, string> entries) in digest.Sections())
        {
            foreach (JsonProperty p in root.GetProperty(section).EnumerateObject())
            {
                entries[p.Name] = p.Value.GetString() ?? "";
            }
        }

        return digest;
    }

    /// <summary>Empty when the digests agree, otherwise one line per section naming the diverging codes.</summary>
    internal static string Compare(Digest golden, Digest actual)
    {
        var lines = new List<string>();
        if (golden.GridRecipeCount != actual.GridRecipeCount)
        {
            lines.Add($"grid recipe count: golden {golden.GridRecipeCount}, actual {actual.GridRecipeCount}");
        }

        var actualSections = actual.Sections().ToDictionary(s => s.Section, s => s.Entries);
        foreach ((string section, SortedDictionary<string, string> expected) in golden.Sections())
        {
            SortedDictionary<string, string> got = actualSections[section];
            var changed = new List<string>();
            var missing = new List<string>();
            var extra = new List<string>();
            foreach (KeyValuePair<string, string> e in expected)
            {
                if (!got.TryGetValue(e.Key, out string? hash))
                {
                    missing.Add(e.Key);
                }
                else if (hash != e.Value)
                {
                    changed.Add($"{e.Key} ({e.Value} -> {hash})");
                }
            }

            extra.AddRange(got.Keys.Where(k => !expected.ContainsKey(k)));
            if (changed.Count + missing.Count + extra.Count > 0)
            {
                lines.Add($"{section}: {changed.Count} changed [{Head(changed)}], {missing.Count} missing [{Head(missing)}], {extra.Count} extra [{Head(extra)}]");
            }
        }

        return string.Join("; ", lines);
    }

    /// <summary>For the first few codes whose hash differs, the first differing spot of the two canonical
    /// texts (needs both digests computed with keepText), so an unstable or diverging member is named
    /// without a manual diff.</summary>
    internal static string FirstDifferences(Digest a, Digest b, int limit = 6)
    {
        var lines = new List<string>();
        if (a.Text == null || b.Text == null)
        {
            return "";
        }

        foreach ((string section, SortedDictionary<string, string> left) in a.Text)
        {
            foreach (KeyValuePair<string, string> e in left)
            {
                if (lines.Count >= limit)
                {
                    return string.Join(" | ", lines);
                }

                if (!b.Text[section].TryGetValue(e.Key, out string? other) || other == e.Value)
                {
                    continue;
                }

                int i = 0;
                while (i < e.Value.Length && i < other.Length && e.Value[i] == other[i])
                {
                    i++;
                }

                int from = Math.Max(0, i - 80);
                lines.Add($"{e.Key} at {i}: [{e.Value.Substring(from, Math.Min(160, e.Value.Length - from))}] vs [{other.Substring(from, Math.Min(160, other.Length - from))}]");
            }
        }

        return string.Join(" | ", lines);
    }

    /// <summary>"section/code" for every code that differs between the two digests (changed, missing or extra).</summary>
    internal static List<string> DivergingCodes(Digest golden, Digest actual)
    {
        var codes = new List<string>();
        var actualSections = actual.Sections().ToDictionary(s => s.Section, s => s.Entries);
        foreach ((string section, SortedDictionary<string, string> expected) in golden.Sections())
        {
            SortedDictionary<string, string> got = actualSections[section];
            codes.AddRange(expected.Where(e => !got.TryGetValue(e.Key, out string? hash) || hash != e.Value).Select(e => section + "/" + e.Key));
            codes.AddRange(got.Keys.Where(k => !expected.ContainsKey(k)).Select(k => section + "/" + k));
        }

        return codes;
    }

    private static string Head(List<string> codes)
    {
        const int Limit = 25;
        string head = string.Join(", ", codes.Take(Limit));
        return codes.Count > Limit ? $"{head}, ... {codes.Count - Limit} more" : head;
    }

    // ---------------------------------------------------------------- canonical text

    /// <summary>
    /// Bounded reflective walk. Only data classes of the API assembly are opened up (declaring type in
    /// a <c>Vintagestory.API</c> namespace): runtime subclasses contribute their type name and nothing
    /// else, entity behaviors and other live objects are written as their type name (collectible behaviors
    /// add their resolved config), registry objects met
    /// below the root as their code. A node budget per root and a depth limit keep a pathological
    /// property (one that hands out a fresh object graph on every read) from running away; a root
    /// that hits the budget ends in a marker and is counted in <see cref="Digest.TruncatedRoots"/>, and
    /// a subtree cut at the depth limit is counted in <see cref="Digest.DepthCutoffs"/> (the scenario
    /// treats both as an invalid setup, since the cut content would no longer be compared).
    /// A member whose getter throws is written as the exception type name, which is as deterministic
    /// as everything else here.
    /// </summary>
    private sealed class Walker
    {
        private const int MaxDepth = 12;
        private const int NodeBudget = 12_000;
        private const int MaxItems = 20_000;

        // Runtime ids and client-only or derived state. Name-only entries apply to every type.
        private static readonly HashSet<string> SkippedNames = new(StringComparer.Ordinal)
        {
            "BlockId", "ItemId", "TextureSubId", "TextureSubIdForBlockColor", "FastTextureVariants",
            "Lod0Mesh", "Lod2Mesh", "ClimateColorMapResolved", "SeasonColorMapResolved",
            "BleedOverlayTextureBaked", "DropsPacket", "RecipeId", "RecipeIngredients", "RecipeOutput",
            "Baked", "BakedAlternates", "Renderer", "LoadedShape", "LoadedAlternateShapes",
            "LoadedShapeForEntity", "AnimationsByCrc32", "AnimationsByMetaCode",
            "ConsumeProperties",   // computed from Consume, IsTool and the durability fields of the same ingredient
            "doneInitialLoad",     // RuntimeSpawnConditions: set by the spawner once a player has been near, runtime state
        };

        // Content lives in fields almost everywhere. These declaring types keep theirs in properties
        // (recipes) or add a few small virtual ones (blocks and items); on every other type a property is
        // a computed view (Vec3d.AsBlockPos, Cuboidf.Center, CompositeShape.RotateXYZCopy) that would only
        // multiply the text, so properties are not read there.
        private static readonly HashSet<string> PropertyHolders = new(StringComparer.Ordinal)
        {
            "GridRecipe", "RecipeBase", "CraftingRecipeIngredient", "Block", "CollectibleObject", "Item",
        };

        // Id members that are runtime ids on these declaring types (CraftingRecipeIngredient.Id is content).
        private static readonly HashSet<string> SkippedQualified = new(StringComparer.Ordinal)
        {
            "Block.Id", "Item.Id", "CollectibleObject.Id", "EntityProperties.Id",
        };

        private static readonly HashSet<string> SkippedTypeNames = new(StringComparer.Ordinal)
        {
            "MeshData", "BakedCompositeTexture", "ColorMap", "Shape", "TextureAtlasPosition", "LoadedTexture",
        };

        private static readonly Dictionary<Type, Member[]> MemberCache = new();
        private static readonly Dictionary<Type, (PropertyInfo Key, PropertyInfo Value)> PairCache = new();
        private static readonly Dictionary<Type, bool> SetCache = new();

        private readonly ICoreAPI? api;
        private readonly List<object> path = new();
        private StringBuilder sb = new();
        private int nodes;

        public Walker(ICoreAPI? api) => this.api = api;

        public int TruncatedRoots { get; private set; }

        public int DepthCutoffs { get; private set; }

        public int FailedRoots { get; private set; }

        public string? FirstFailure { get; private set; }

        private sealed record Member(string Name, System.Func<object, object?> Get);

        public string Describe(object root)
        {
            sb = new StringBuilder();
            path.Clear();
            nodes = 0;

            // The root is always opened up, whatever namespace its runtime class lives in (a stairs
            // block is a Vintagestory.GameContent type): the runtime type name is part of the text, and
            // the members are the API-declared ones, which MembersOf filters by declaring type.
            Type type = root.GetType();
            sb.Append(type.FullName).Append(':');
            try
            {
                WriteObject(root, type, 0);
            }
            catch (Exception e)
            {
                FailedRoots++;
                FirstFailure ??= $"{type.FullName}: {e}";
                sb.Append("<walker failed ").Append(e.GetType().Name).Append('>');
            }

            if (nodes > NodeBudget)
            {
                TruncatedRoots++;
            }

            return sb.ToString();
        }

        private void Write(object? value, int depth)
        {
            if (++nodes > NodeBudget)
            {
                sb.Append("<budget>");
                return;
            }

            if (value == null)
            {
                sb.Append('~');
                return;
            }

            Type type = value.GetType();
            if (value is string s)
            {
                sb.Append('"').Append(s.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
            }
            else if (type.IsPrimitive || value is decimal)
            {
                sb.Append(value is IFormattable f ? f.ToString(value is float or double ? "R" : null, CultureInfo.InvariantCulture) : value.ToString());
            }
            else if (type.IsEnum)
            {
                sb.Append(type.Name).Append('.').Append(value);
            }
            else if (value is Type t)
            {
                sb.Append("type(").Append(t.FullName).Append(')');
            }
            else if (IsSkippedType(type))
            {
                sb.Append("<skipped ").Append(type.Name).Append('>');
            }
            else if (value is AssetLocation al)
            {
                sb.Append("al(").Append(al.ToString()).Append(')');
            }
            else if (value is JsonObject jo)
            {
                sb.Append("json(").Append(Safe(() => CanonicalJson(jo.ToString(), BlockCodeOf))).Append(')');
            }
            else if (value is IAttribute attr)
            {
                sb.Append("attr(").Append(Safe(() => CanonicalJson(attr.ToJsonToken(), BlockCodeOf))).Append(')');
            }
            else if (value is ItemStack stack)
            {
                WriteStack(stack);
            }
            else if (value is TagSet tags)
            {
                sb.Append("tags(").Append(Safe(() => TagNames(api?.CollectibleTagRegistry?.SlowEnumerateTagNames(tags)))).Append(')');
            }
            else if (value is TagSetFast entityTags)
            {
                sb.Append("tags(").Append(Safe(() => TagNames(api?.EntityTagRegistry?.SlowEnumerateTagNames(entityTags)))).Append(')');
            }
            else if (value is ComplexTagCondition<TagSet> condition)
            {
                WriteTagCondition(condition, set => api?.CollectibleTagRegistry?.SlowEnumerateTagNames(set));
            }
            else if (value is ComplexTagCondition<TagSetFast> entityCondition)
            {
                WriteTagCondition(entityCondition, set => api?.EntityTagRegistry?.SlowEnumerateTagNames(set));
            }
            else if (value is CollectibleBehavior collectibleBehavior)
            {
                // The resolved config (set by Initialize from the resolved json, propertiesByType included) is content.
                sb.Append("behavior(").Append(type.FullName).Append(',').Append(Safe(() => CanonicalJson(collectibleBehavior.propertiesAtString, BlockCodeOf))).Append(')');
            }
            else if (value is EntityBehavior)
            {
                // Entity behavior configs are covered through EntityServerProperties and EntityClientProperties.BehaviorsAsJsonObj.
                sb.Append("behavior(").Append(type.FullName).Append(')');
            }
            else if (depth > 0 && value is CollectibleObject co)
            {
                sb.Append("ref(").Append(co.Code).Append(')');
            }
            else if (depth > 0 && value is EntityProperties ep)
            {
                sb.Append("ref(").Append(ep.Code).Append(')');
            }
            else if (value is IEnumerable enumerable)
            {
                WriteEnumerable(enumerable, type, depth);
            }
            else if (type.Namespace?.StartsWith("Vintagestory.API", StringComparison.Ordinal) == true)
            {
                WriteObject(value, type, depth);
            }
            else
            {
                sb.Append('<').Append(type.FullName).Append('>');
            }
        }

        /// <summary>Tag names instead of registry ids (which depend on registration order); the order of the conditions is the json order.</summary>
        private void WriteTagCondition<TSet>(ComplexTagCondition<TSet> condition, System.Func<TSet, IEnumerable<string>?> names)
            where TSet : IEquatable<TSet>
        {
            sb.Append("tagcondition(").Append(condition.isDisjunctive ? "any" : "all");
            foreach (ComplexTagCondition<TSet>.Condition c in condition.conditions ?? Array.Empty<ComplexTagCondition<TSet>.Condition>())
            {
                ComplexTagCondition<TSet>.Condition inner = c;
                sb.Append(" [+").Append(Safe(() => TagNames(names(inner.RequiredTags))))
                    .Append(" -").Append(Safe(() => TagNames(names(inner.ForbiddenTags)))).Append(']');
            }

            sb.Append(')');
        }

        private void WriteStack(ItemStack stack)
        {
            sb.Append("stack(").Append(stack.Class).Append(',')
                .Append(stack.Collectible?.Code?.ToString() ?? "~").Append(',')
                .Append(stack.StackSize.ToString(CultureInfo.InvariantCulture)).Append(',');
            Write(stack.Attributes, 1);
            sb.Append(')');
        }

        private void WriteObject(object value, Type type, int depth)
        {
            if (depth >= MaxDepth)
            {
                DepthCutoffs++;
                sb.Append("<depth>");
                return;
            }

            if (!type.IsValueType)
            {
                if (path.Any(p => ReferenceEquals(p, value)))
                {
                    sb.Append("<cycle ").Append(type.Name).Append('>');
                    return;
                }

                path.Add(value);
            }

            sb.Append(type.Name).Append('{');
            foreach (Member member in MembersOf(type))
            {
                sb.Append(member.Name).Append('=');
                object? v;
                try
                {
                    v = member.Get(value);
                }
                catch (Exception e)
                {
                    sb.Append('!').Append((e is TargetInvocationException { InnerException: { } inner } ? inner : e).GetType().Name).Append(';');
                    continue;
                }

                Write(v, depth + 1);
                sb.Append(';');
            }

            sb.Append('}');
            if (!type.IsValueType)
            {
                path.RemoveAt(path.Count - 1);
            }
        }

        private void WriteEnumerable(IEnumerable enumerable, Type type, int depth)
        {
            if (depth >= MaxDepth)
            {
                DepthCutoffs++;
                sb.Append("<depth>");
                return;
            }

            if (!type.IsValueType)
            {
                if (path.Any(p => ReferenceEquals(p, enumerable)))
                {
                    sb.Append("<cycle ").Append(type.Name).Append('>');
                    return;
                }

                path.Add(enumerable);
            }

            var items = new List<string>();
            bool isMap = false;
            foreach (object? item in enumerable)
            {
                if (items.Count >= MaxItems)
                {
                    items.Add("<truncated>");
                    break;
                }

                Type? itemType = item?.GetType();
                if (itemType is { IsGenericType: true } && itemType.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
                {
                    isMap = true;
                    (PropertyInfo keyProp, PropertyInfo valueProp) = PairAccessors(itemType);
                    items.Add(Capture(keyProp.GetValue(item), depth + 1) + "=>" + Capture(valueProp.GetValue(item), depth + 1));
                }
                else
                {
                    items.Add(Capture(item, depth + 1));
                }
            }

            // Maps and sets are unordered in meaning: sort. Lists and arrays keep their order, and so does
            // the engine's own ordered dictionary (variant order is the code order).
            bool sort = isMap
                ? !type.Name.StartsWith("OrderedDictionary", StringComparison.Ordinal)
                : IsSet(type);
            if (sort)
            {
                items.Sort(StringComparer.Ordinal);
            }

            sb.Append(isMap ? "map[" : "list[").Append(string.Join(",", items)).Append(']');
            if (!type.IsValueType)
            {
                path.RemoveAt(path.Count - 1);
            }
        }

        private string Capture(object? value, int depth)
        {
            StringBuilder saved = sb;
            sb = new StringBuilder();
            Write(value, depth);
            string text = sb.ToString();
            sb = saved;
            return text;
        }

        private static bool IsSet(Type type)
        {
            lock (SetCache)
            {
                if (!SetCache.TryGetValue(type, out bool isSet))
                {
                    SetCache[type] = isSet = type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ISet<>));
                }

                return isSet;
            }
        }

        private static (PropertyInfo Key, PropertyInfo Value) PairAccessors(Type pairType)
        {
            lock (PairCache)
            {
                if (!PairCache.TryGetValue(pairType, out (PropertyInfo, PropertyInfo) accessors))
                {
                    PairCache[pairType] = accessors = (pairType.GetProperty("Key")!, pairType.GetProperty("Value")!);
                }

                return accessors;
            }
        }

        private static Member[] MembersOf(Type type)
        {
            lock (MemberCache)
            {
                if (MemberCache.TryGetValue(type, out Member[]? cached))
                {
                    return cached;
                }

                var members = new List<Member>();
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (IsContent(field.DeclaringType, field.Name, field.FieldType))
                    {
                        FieldInfo f = field;
                        System.Func<object, object?> get = o => f.GetValue(o);
                        if (f.Name == nameof(ClimateSpawnCondition.MapCode) && f.DeclaringType == typeof(ClimateSpawnCondition))
                        {
                            // Initialise picks it from RandomMapCodePool with a seed built on string.GetHashCode(),
                            // which .NET randomizes per process (and the pick is redone on a later Initialise):
                            // the pool itself is content, the pick is not.
                            get = o => o is ClimateSpawnCondition { RandomMapCodePool.Length: > 0 } ? "(pool pick)" : f.GetValue(o);
                        }

                        members.Add(new Member(f.Name, get));
                    }
                }

                foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (prop.GetIndexParameters().Length == 0
                        && PropertyHolders.Contains(prop.DeclaringType?.Name ?? "")
                        && prop.GetMethod is { IsPublic: true }
                        && !prop.PropertyType.IsByRef
                        && !IsObsolete(prop)
                        && IsContent(prop.DeclaringType, prop.Name, prop.PropertyType))
                    {
                        PropertyInfo p = prop;
                        members.Add(new Member(p.Name, o => p.GetValue(o)));
                    }
                }

                members.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                return MemberCache[type] = members.ToArray();
            }
        }

        // An attribute type that cannot be resolved (a metadata-only reference) must not abort the walk.
        private static bool IsObsolete(MemberInfo member)
        {
            try
            {
                return member.GetCustomAttribute<ObsoleteAttribute>() != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsContent(Type? declaring, string name, Type memberType) =>
            declaring?.Namespace?.StartsWith("Vintagestory.API", StringComparison.Ordinal) == true
            && !SkippedNames.Contains(name)
            && !SkippedQualified.Contains(declaring.Name + "." + name)
            && !IsSkippedType(memberType);

        private static bool IsSkippedType(Type type)
        {
            Type element = type.IsArray ? type.GetElementType()! : type;
            return SkippedTypeNames.Contains(element.Name)
                || typeof(Delegate).IsAssignableFrom(element)
                || typeof(MemberInfo).IsAssignableFrom(element)
                || typeof(Stream).IsAssignableFrom(element)
                || typeof(ICoreAPI).IsAssignableFrom(element)
                || typeof(IWorldAccessor).IsAssignableFrom(element)
                || typeof(IWorldChunk).IsAssignableFrom(element)
                || typeof(IBlockAccessor).IsAssignableFrom(element);
        }

        private static string Safe(Func<string> produce)
        {
            try
            {
                return produce();
            }
            catch (Exception e)
            {
                return "!" + e.GetType().Name;
            }
        }

        /// <summary>Code of the block a runtime id points at, for the attribute arrays that hold block ids (see <see cref="CanonicalJson"/>).</summary>
        private string BlockCodeOf(int id) => api?.World.GetBlock(id)?.Code?.ToString() ?? "(no block)";

        private static string TagNames(IEnumerable<string>? names) =>
            names == null ? "~" : string.Join(",", names.OrderBy(n => n, StringComparer.Ordinal));
    }

    // ---------------------------------------------------------------- canonical JSON

    /// <summary>Attribute keys whose arrays hold block ids written by the engine at load (the host rocks of an ore, the
    /// ores of a rock). Block ids follow the asset enumeration order, which differs from machine to machine (the golden
    /// was first captured on btrfs, a CI runner lists a directory in another order), so they are compared as codes.</summary>
    private static readonly HashSet<string> BlockIdArrays = new(StringComparer.Ordinal) { "hostRock", "hostRockFor" };

    /// <summary>Re-emits a JSON text compactly with object keys sorted; text that is not valid JSON is kept verbatim.
    /// With <paramref name="blockCode"/>, the arrays named in <see cref="BlockIdArrays"/> are written as the sorted
    /// codes of the blocks they point at instead of the ids.</summary>
    internal static string CanonicalJson(string? text, System.Func<int, string>? blockCode = null)
    {
        if (text == null)
        {
            return "~";
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            var sb = new StringBuilder(text.Length);
            Emit(doc.RootElement, sb, blockCode);
            return sb.ToString();
        }
        catch (JsonException)
        {
            return "raw:" + text;
        }
    }

    private static void Emit(JsonElement element, StringBuilder sb, System.Func<int, string>? blockCode)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                bool first = true;
                foreach (JsonProperty p in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    sb.Append(first ? "" : ",").Append(JsonSerializer.Serialize(p.Name)).Append(':');
                    first = false;
                    if (blockCode != null && BlockIdArrays.Contains(p.Name) && IsIntArray(p.Value))
                    {
                        List<string> codes = p.Value.EnumerateArray().Select(n => blockCode(n.GetInt32())).ToList();
                        codes.Sort(StringComparer.Ordinal);
                        sb.Append('[').Append(string.Join(",", codes.Select(c => JsonSerializer.Serialize(c)))).Append(']');
                    }
                    else
                    {
                        Emit(p.Value, sb, blockCode);
                    }
                }

                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                int i = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    sb.Append(i++ == 0 ? "" : ",");
                    Emit(item, sb, blockCode);
                }

                sb.Append(']');
                break;
            default:
                sb.Append(element.GetRawText());
                break;
        }
    }

    private static bool IsIntArray(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array && element.EnumerateArray().All(n => n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out _));
}
