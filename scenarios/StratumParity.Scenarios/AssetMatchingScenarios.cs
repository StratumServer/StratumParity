using System.Diagnostics;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.Util;
using Xunit;
using Xunit.Abstractions;

namespace StratumParity.Scenarios;

/// <summary>
/// Asset matching parity: everything the server derives from asset codes must come out the same on
/// both flavors. Stratum rewrote <c>WildcardUtil.Match</c> (the three argument overload that decides
/// which collectibles a recipe ingredient, a loot table or a block patch refers to) on top of its
/// own <c>fastMatch</c>, and moved <c>*ByType</c> resolution and type loading to a lazy, parallel
/// path; the four scenarios pin the observable contract of both.
///
/// <list type="bullet">
/// <item>Two pure truth tables on <c>WildcardUtil.Match</c>, expected values written down from the
/// vanilla implementation: the rows both flavors agree on, and the edge rows where the rewrite
/// differs (empty allowedVariants, a leading '@', a '*' domain with a literal path, letter case, a
/// null wildcard). The edge table is a probe: it asserts the vanilla answer on vanilla and what the
/// rewrite returns on Stratum, because the difference is under discussion and not reported yet. It
/// collects every diverging row before it asserts, so one failing run names them all.</item>
/// <item>A per-code content digest of every block, item and entity type and of the grid recipes
/// (<see cref="AssetRegistryDigest"/>) against a golden captured from the vanilla leg.</item>
/// <item>Crafting four recipes picked by their data (shaped, shapeless, wildcard with allowedVariants,
/// tool) through the player's crafting grid: the output slot shows the recipe output and taking it
/// consumes exactly the inputs.</item>
/// </list>
///
/// The golden lives in <c>fixtures/assetmatching-registry/registry-&lt;game version&gt;.json</c>. It is not
/// data-file seeded: the class reads it from the fixtures folder next to the scenario assembly. Capture it once per game
/// version from a vanilla run with <c>PARITY_REGISTRY_CAPTURE=&lt;file&gt;</c> (the scenario then writes
/// the file and fails on purpose, so a capture run is never green; it refuses to run on Stratum), commit it,
/// and refresh it on a game bump. When a hash differs, run both flavors with <c>PARITY_REGISTRY_DUMP=&lt;dir&gt;</c> and
/// diff the per-section text files: they hold the canonical description behind every hash.
/// </summary>
public class AssetMatchingScenarios : AtlasScenarioBase
{
    private readonly ITestOutputHelper output;

    public AssetMatchingScenarios(ITestOutputHelper output) => this.output = output;

    // ---------------------------------------------------------------- wildcard truth tables

    /// <summary>Expected is the vanilla outcome ("True", "False" or the exception type name); Stratum, when set, is
    /// what the Stratum rewrite returns instead.</summary>
    private sealed record Row(string Label, AssetLocation Wildcard, AssetLocation? Code, string[]? Variants, string Expected, string? Stratum = null,
        System.Func<string>? Run = null);

    /// <summary>Two-argument AssetLocation constructor on purpose: unlike the one-string constructor it
    /// does not lowercase, which the case rows need.</summary>
    private static AssetLocation Loc(string domain, string path) => new(domain, path);

    private static Row R(string label, AssetLocation wildcard, AssetLocation? code, string[]? variants, bool expected) =>
        new(label, wildcard, code, variants, expected.ToString());

    private static Row E(string label, AssetLocation wildcard, AssetLocation? code, string[]? variants, bool vanilla, bool stratum) =>
        new(label, wildcard, code, variants, vanilla.ToString(), stratum.ToString());

    [AtlasScenario]
    public Task WildcardMatch_Should_FollowVanillaTruthTable_When_CommonPatternsMatched()
    {
        Row[] rows =
        {
            R("prefix hit", Loc("game", "rock-*"), Loc("game", "rock-granite"), null, true),
            R("prefix miss", Loc("game", "rock-*"), Loc("game", "plank-oak"), null, false),
            R("prefix with empty expansion", Loc("game", "rock-*"), Loc("game", "rock-"), null, true),
            R("suffix hit", Loc("game", "*-granite"), Loc("game", "rock-granite"), null, true),
            R("suffix miss", Loc("game", "*-granite"), Loc("game", "rock-andesite"), null, false),
            R("middle hit", Loc("game", "log-*-ud"), Loc("game", "log-oak-ud"), null, true),
            R("middle miss", Loc("game", "log-*-ud"), Loc("game", "log-oak-ns"), null, false),
            R("two stars hit", Loc("game", "log-*-*-ud"), Loc("game", "log-placed-oak-ud"), null, true),
            R("two stars miss", Loc("game", "log-*-*-ud"), Loc("game", "log-placed-ud"), null, false),
            R("any domain, game code", Loc("*", "plank-*"), Loc("game", "plank-oak"), null, true),
            R("any domain, other domain code", Loc("*", "plank-*"), Loc("othermod", "plank-oak"), null, true),
            R("domain mismatch", Loc("game", "plank-*"), Loc("othermod", "plank-oak"), null, false),
            R("match everything", Loc("*", "*"), Loc("game", "anything-at-all"), null, true),
            R("match everything ignores variants", Loc("*", "*"), Loc("game", "x"), new[] { "nothing" }, true),
            R("exact equal", Loc("game", "plank-oak"), Loc("game", "plank-oak"), null, true),
            R("exact equal ignores variants", Loc("game", "plank-oak"), Loc("game", "plank-oak"), new[] { "birch" }, true),
            R("exact different", Loc("game", "plank-oak"), Loc("game", "plank-birch"), null, false),
            R("wildcard in the code only", Loc("game", "plank-oak"), Loc("game", "plank-*"), null, false),
            R("allowed variant hit", Loc("game", "plank-*"), Loc("game", "plank-oak"), new[] { "oak", "birch" }, true),
            R("allowed variant miss", Loc("game", "plank-*"), Loc("game", "plank-oak"), new[] { "birch" }, false),
            R("allowed middle hit", Loc("game", "log-*-ud"), Loc("game", "log-oak-ud"), new[] { "oak" }, true),
            R("allowed middle miss", Loc("game", "log-*-ud"), Loc("game", "log-oak-ud"), new[] { "pine" }, false),
            R("allowed multi-part hit", Loc("game", "log-*"), Loc("game", "log-placed-oak-ud"), new[] { "placed-oak-ud" }, true),
            R("allowed multi-part miss", Loc("game", "log-*"), Loc("game", "log-placed-oak-ud"), new[] { "oak" }, false),
            // The engine reuses the three argument overload for skipVariants: true means "skip this one".
            R("skip list hit", Loc("game", "hide-soaked-*"), Loc("game", "hide-soaked-small"), new[] { "small", "medium" }, true),
            R("skip list miss", Loc("game", "hide-soaked-*"), Loc("game", "hide-soaked-huge"), new[] { "small", "medium" }, false),
        };

        Assert.True(rows.Count(r => r.Expected == "True") >= 10 && rows.Count(r => r.Expected == "False") >= 10,
            "the table needs at least ten hits and ten misses to be a truth table; setup is invalid");
        AssertRows("common", rows);
        return Task.CompletedTask;
    }

    // A probe of a difference that is under discussion and not reported yet. Stratum rewrote the three
    // argument overload on top of fastMatch (patches/VintagestoryApi/Util/WildcardUtil.cs.patch, identical on
    // v1.22.7-stratum.2, v1.22.7-stratum.2-indev.1 and upstream/indev), and every row below answers differently
    // there than on vanilla (vanilla, then Stratum):
    //   - empty allowedVariants: no match, then match; through SatisfiesAsIngredient an ingredient with
    //     allowedVariants [] accepts nothing, then everything, and one with skipVariants [] accepts everything,
    //     then nothing
    //   - a leading '@': an ordinary character, then the marker of a regex pattern
    //   - a '*' domain with a literal path: no match, then an exact match on the path
    //   - letter case (prefix, matched code, exact compare, middle pattern): case-sensitive, then not
    //   - "*:*" with a null code: match, then no match
    //   - a null wildcard: NullReferenceException, then no match
    // The scenario asserts both sides: the vanilla answer on vanilla and the rewrite's answer on Stratum. If
    // the difference gets reported, the Stratum column moves into a KnownDivergence with the issue.
    [AtlasScenario]
    public Task WildcardMatch_Should_FollowVanillaOnVanillaAndTheFastMatchRewriteOnStratum_When_EdgePatternsMatched()
    {
        // Expected values are what the vanilla implementation does, read from its source: a literal prefix
        // test plus a case-sensitive regex, no '@' handling, no exact fallback for a '*' domain, no null guard,
        // and the "*:*" shortcut ahead of the null check. The Stratum column is what its fastMatch based
        // rewrite returns, measured on stratum.2 and indev.1.
        Row[] rows =
        {
            E("empty allowedVariants", Loc("game", "plank-*"), Loc("game", "plank-oak"), Array.Empty<string>(), vanilla: false, stratum: true),
            E("a leading @ is not a regex", Loc("game", "@plank-.*"), Loc("game", "plank-oak"), null, vanilla: false, stratum: true),
            E("a leading @ is a literal prefix", Loc("game", "@plank-.*"), Loc("game", "@plank-.xyz"), null, vanilla: true, stratum: false),
            E("'*' domain with a literal path", Loc("*", "plank-oak"), Loc("game", "plank-oak"), null, vanilla: false, stratum: true),
            E("prefix compare is case-sensitive", Loc("game", "Plank-*"), Loc("game", "plank-oak"), null, vanilla: false, stratum: true),
            E("matched code is case-sensitive", Loc("game", "plank-*"), Loc("game", "Plank-Oak"), null, vanilla: false, stratum: true),
            E("exact compare is case-sensitive", Loc("game", "plank-oak"), Loc("game", "Plank-Oak"), null, vanilla: false, stratum: true),
            E("middle pattern is case-sensitive", Loc("game", "log-*-ud"), Loc("game", "log-oak-UD"), null, vanilla: false, stratum: true),
            E("match everything with a null code", Loc("*", "*"), null, null, vanilla: true, stratum: false),
            new Row("null wildcard", null!, Loc("game", "plank-oak"), null, nameof(NullReferenceException), "False"),
            // The same two rows seen through the recipe ingredient that calls Match (a wildcard ingredient
            // with an empty list in the recipe json): vanilla matches nothing for allowedVariants [] and
            // everything for skipVariants []; Stratum does the opposite on both.
            new Row("ingredient with allowedVariants []", null!, null, null, "False", "True", () => IngredientAccepts(allowed: Array.Empty<string>(), skip: null)),
            new Row("ingredient with skipVariants []", null!, null, null, "True", "False", () => IngredientAccepts(allowed: null, skip: Array.Empty<string>())),
        };

        bool stratumShape = ServerFlavor.IsStratum;
        var diverging = new List<string>();
        foreach (Row row in rows)
        {
            string? problem = Evaluate(row, stratumShape);
            if (problem != null)
            {
                diverging.Add(problem);
            }
        }

        Assert.True(diverging.Count == 0, stratumShape
            ? $"{diverging.Count} of {rows.Length} edge rows no longer return what the probe pins for the Stratum rewrite on " +
              $"{ServerFlavor.Name} ({ServerFlavor.Version}) (a row now equal to vanilla means the rewrite changed: update the probe): " +
              string.Join("; ", diverging)
            : $"{diverging.Count} of {rows.Length} edge rows differ from the vanilla contract on {ServerFlavor.Name} " +
              $"({ServerFlavor.Version ?? "vanilla"}): {string.Join("; ", diverging)}");
        return Task.CompletedTask;
    }

    private static void AssertRows(string table, Row[] rows)
    {
        var diverging = new List<string>();
        foreach (Row row in rows)
        {
            string? problem = Evaluate(row, stratumShape: false);
            if (problem != null)
            {
                diverging.Add(problem);
            }
        }

        Assert.True(diverging.Count == 0,
            $"{diverging.Count} of {rows.Length} {table} rows differ from the vanilla truth table on {ServerFlavor.Name}: " +
            string.Join("; ", diverging));
    }

    /// <summary>Does a wildcard ingredient "plank-*" with the given lists accept a plank-oak stack?
    /// World-free: a hand-built item is enough for the matching path.</summary>
    private static string IngredientAccepts(string[]? allowed, string[]? skip)
    {
        var stack = new ItemStack(new Item { Code = new AssetLocation("game", "plank-oak") }, 1);
        var ingredient = new CraftingRecipeIngredient
        {
            Code = new AssetLocation("game", "plank-*"),
            Type = EnumItemClass.Item,
            MatchingType = EnumRecipeMatchType.Wildcard,
            AllowedVariants = allowed!,
            SkipVariants = skip!,
            Quantity = 1,
        };
        return ingredient.SatisfiesAsIngredient(stack).ToString();
    }

    private static string? Evaluate(Row row, bool stratumShape)
    {
        string expected = stratumShape ? row.Stratum ?? row.Expected : row.Expected;
        string actual;
        try
        {
            actual = row.Run != null ? row.Run() : WildcardUtil.Match(row.Wildcard, row.Code!, row.Variants!).ToString();
        }
        catch (Exception e)
        {
            actual = e.GetType().Name;
        }

        if (actual == expected)
        {
            return null;
        }

        string call = row.Run != null
            ? "SatisfiesAsIngredient(plank-oak)"
            : $"Match({row.Wildcard?.ToString() ?? "null"}, {row.Code?.ToString() ?? "null"}, " +
              $"{(row.Variants == null ? "null" : "[" + string.Join(",", row.Variants) + "]")})";
        return $"{row.Label}: {call} gave {actual}, expected {expected}";
    }

    // ---------------------------------------------------------------- registry digest

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task AssetRegistry_Should_MatchVanillaDigest_When_ServerBooted()
    {
        string? dumpDir = Environment.GetEnvironmentVariable("PARITY_REGISTRY_DUMP");
        string? capturePath = Environment.GetEnvironmentVariable("PARITY_REGISTRY_CAPTURE");

        var watch = Stopwatch.StartNew();
        AssetRegistryDigest.Digest actual = AssetRegistryDigest.Compute(World.Api, keepText: !string.IsNullOrEmpty(dumpDir));
        output.WriteLine($"digest computed in {watch.ElapsedMilliseconds} ms on {ServerFlavor.Name} ({ServerFlavor.Version ?? "vanilla"}), game {actual.GameVersion}");
        foreach ((string section, SortedDictionary<string, string> entries) in actual.Sections())
        {
            output.WriteLine($"  {section}: {entries.Count} codes, section hash {actual.SectionHash(entries)}");
        }

        output.WriteLine($"  grid recipes: {actual.GridRecipeCount}, roots that hit the node budget: {actual.TruncatedRoots}, subtrees cut at the depth limit: {actual.DepthCutoffs}, roots the walker failed on: {actual.FailedRoots}");
        Assert.True(actual.FailedRoots == 0,
            $"the registry walker threw on {actual.FailedRoots} objects on {ServerFlavor.Name}; first: {actual.FirstFailure}; setup is invalid");

        // A root that hits the node budget, or a subtree cut at the depth limit, silently drops the rest of its
        // content from the comparison (today both are zero: a new API type or a game bump can change that).
        Assert.True(actual.TruncatedRoots == 0 && actual.DepthCutoffs == 0,
            $"the registry walker dropped content on {ServerFlavor.Name}: {actual.TruncatedRoots} objects hit the node budget, " +
            $"{actual.DepthCutoffs} subtrees were cut at the depth limit, so part of the registry is not compared; " +
            "raise the limits or add a skip rule to AssetRegistryDigest; setup is invalid");

        // A digest over an empty or half-loaded registry would pass against nothing.
        Assert.True(actual.Blocks.Count >= 1000 && actual.Items.Count >= 500 && actual.Entities.Count >= 50 && actual.GridRecipeCount >= 300,
            $"registry too small on {ServerFlavor.Name}: {actual.Blocks.Count} blocks, {actual.Items.Count} items, " +
            $"{actual.Entities.Count} entity types, {actual.GridRecipeCount} grid recipes; setup is invalid");

        if (!string.IsNullOrEmpty(dumpDir))
        {
            WriteDump(dumpDir, actual);
        }

        // Stability inside one run: a getter that depends on time or on call order would make the
        // comparison below meaningless, whatever the flavor.
        await World.Ticks(5);
        AssetRegistryDigest.Digest again = AssetRegistryDigest.Compute(World.Api, keepText: !string.IsNullOrEmpty(dumpDir));
        string unstable = AssetRegistryDigest.Compare(actual, again);
        if (unstable.Length > 0)
        {
            unstable += " [first differences: " + AssetRegistryDigest.FirstDifferences(actual, again) + "]";
        }

        Assert.True(unstable.Length == 0,
            $"the registry digest changed between two reads in one run on {ServerFlavor.Name}: {unstable}; setup is invalid " +
            "(a walked member is not deterministic: add it to the skip rules of AssetRegistryDigest)");

        if (!string.IsNullOrEmpty(capturePath))
        {
            Assert.False(ServerFlavor.IsStratum,
                "the registry golden is captured from the vanilla leg only, never from Stratum; setup is invalid");
            string full = Path.GetFullPath(capturePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, AssetRegistryDigest.ToJson(actual));
            output.WriteLine($"registry golden captured to {full}: commit it as fixtures/assetmatching-registry/{GoldenFileName(actual.GameVersion)}");
            Assert.Fail($"registry golden captured to {full}: commit it as fixtures/assetmatching-registry/{GoldenFileName(actual.GameVersion)} " +
                "and rerun without PARITY_REGISTRY_CAPTURE (a capture run compares nothing, so it is never green)");
        }

        // Next to the scenario assembly: under Atlas AppContext.BaseDirectory is the game install, not the test output.
        string goldenPath = Path.Combine(Path.GetDirectoryName(typeof(AssetMatchingScenarios).Assembly.Location)!, "fixtures", "assetmatching-registry", GoldenFileName(actual.GameVersion));
        Assert.True(File.Exists(goldenPath),
            $"no registry golden for game version {actual.GameVersion} at {goldenPath}: capture it from a vanilla run with " +
            "PARITY_REGISTRY_CAPTURE=<file> and commit it as fixtures/assetmatching-registry/" +
            $"{GoldenFileName(actual.GameVersion)}; setup is invalid");

        AssetRegistryDigest.Digest golden = AssetRegistryDigest.FromJson(File.ReadAllText(goldenPath));
        Assert.True(golden.GameVersion == actual.GameVersion,
            $"the golden was captured on game {golden.GameVersion} but {ServerFlavor.Name} runs {actual.GameVersion}; refresh it; setup is invalid");

        float? warmth = World.Api.World.GetItem(new AssetLocation(SurgeonHood))?.Attributes?["warmth"]?.AsFloat(-1f);
        output.WriteLine($"{SurgeonHood} attributes.warmth = {warmth?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(no item)"} on {ServerFlavor.Name}");

        string differences = AssetRegistryDigest.Compare(golden, actual);
        if (NestedByTypeOrder.Applies)
        {
            // Stratum resolves a nested *ByType before it merges the outer one, vanilla merges first and resolves
            // after: the surgeon hood's own attributesByType says warmth 1, the nested warmthByType wins on vanilla
            // (0.25) and loses on Stratum. Nothing else in the registry may differ.
            List<string> diverging = AssetRegistryDigest.DivergingCodes(golden, actual);
            Assert.True(diverging.SequenceEqual(new[] { "items/" + SurgeonHood }),
                $"{NestedByTypeOrder.Tag}: the registry should differ from the vanilla golden in exactly items/{SurgeonHood}, " +
                $"found [{string.Join(", ", diverging)}] (an empty list means the bug is fixed on this build: drop it from the exemption)");

            // The hood's own hash as Stratum builds it: any further change to another member of the same item
            // (a second divergence hiding behind the known one) moves it.
            Assert.True(actual.Items[SurgeonHood] == SurgeonHoodStratumHash,
                $"{NestedByTypeOrder.Tag}: items/{SurgeonHood} hashes to {actual.Items[SurgeonHood]} on this build, the measured bug shape is {SurgeonHoodStratumHash}; " +
                "another member of the item differs besides warmth (rerun both flavors with PARITY_REGISTRY_DUMP=<dir> and diff items)");
            Assert.True(warmth == 1f,
                $"{NestedByTypeOrder.Tag}: {SurgeonHood} should carry the outer attributesByType warmth of 1 on this build, found {warmth}");
            return;
        }

        Assert.True(differences.Length == 0,
            $"the resolved registry differs from the vanilla golden on {ServerFlavor.Name} ({ServerFlavor.Version ?? "vanilla"}): {differences}. " +
            "Rerun both flavors with PARITY_REGISTRY_DUMP=<dir> and diff the per-section files to see which member differs.");
    }

    // clothes-face-surgeonhood: itemtypes/wearable/seraph/face.json sets warmth 1 in attributesByType and warmthByType
    // (0.25 for everything else) inside attributes. The issue is the one named by the KnownDivergence below.
    private const string SurgeonHood = "game:clothes-face-surgeonhood";

    private static readonly KnownDivergence NestedByTypeOrder =
        new("StratumServer/Stratum#363", StratumBuild.Stable2, StratumBuild.Indev1);

    // Hash of items/game:clothes-face-surgeonhood on stratum.2 and indev.1 (digest format and walker rules as committed):
    // refresh it with the golden whenever the walker changes.
    private const string SurgeonHoodStratumHash = "35899cef194e";

    private static string GoldenFileName(string gameVersion) => $"registry-{gameVersion}.json";

    private static void WriteDump(string dir, AssetRegistryDigest.Digest digest)
    {
        Directory.CreateDirectory(dir);
        foreach ((string section, SortedDictionary<string, string> text) in digest.Text!)
        {
            using var writer = new StreamWriter(Path.Combine(dir, $"{ServerFlavor.Name}-{section}.txt"));
            foreach (KeyValuePair<string, string> entry in text)
            {
                writer.WriteLine(entry.Key + "\t" + entry.Value.Replace("\r", "").Replace("\n", "\\n"));
            }
        }
    }

    // ---------------------------------------------------------------- grid crafting

    // The 1.22 recipe API annotates fields as nullable that the loader always fills for a resolved
    // recipe (Output, ResolvedIngredients, ResolvedItemStack): eligibility is checked once in Eligible
    // and the rest of the section reads them directly.
#pragma warning disable CS8600, CS8602, CS8603, CS8604

    private const string GridInventoryClass = Vintagestory.API.Config.GlobalConstants.craftingInvClassName;
    private const int GridWidth = 3;
    private const int GridCells = GridWidth * GridWidth;
    // Inputs are given a little more than the recipe consumes, so "exactly the inputs" is a
    // difference of stack sizes and not only an emptied slot.
    private const int Surplus = 2;

    private enum Kind { Shaped, Shapeless, WildcardWithAllowedVariants, Tool }

    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task GridCrafting_Should_ConsumeExactInputs_When_RecipesCrafted()
    {
        IWorldAccessor world = World.Api.World;
        ITestPlayer player = await World.JoinPlayer("asset-crafter");

        IInventory grid = player.Player.InventoryManager.GetOwnInventory(GridInventoryClass);
        Assert.True(grid != null && grid.Count == GridCells + 1,
            $"the player has no 3x3 crafting grid with an output slot on {ServerFlavor.Name} (found {grid?.Count.ToString() ?? "none"} slots); setup is invalid");
        ItemSlot sink = FirstEmptyHotbarSlot(player);

        var picked = new List<GridRecipe>();
        var failures = new List<string>();
        foreach (Kind kind in Enum.GetValues<Kind>())
        {
            GridRecipe? recipe = Pick(world, kind, picked);
            Assert.True(recipe != null, $"no grid recipe of kind {kind} qualifies on {ServerFlavor.Name}; setup is invalid");
            picked.Add(recipe!);
            output.WriteLine($"{kind}: {Describe(recipe!)}");

            CraftAndVerify(world, grid, sink, kind, recipe!, failures);
            await World.Ticks(2);
        }

        Assert.True(player.IsConnected, $"the crafting player was disconnected on {ServerFlavor.Name}");
        Assert.True(failures.Count == 0,
            $"{failures.Count} crafting checks failed on {ServerFlavor.Name}: {string.Join("; ", failures)}");
    }

    private static ItemSlot FirstEmptyHotbarSlot(ITestPlayer player)
    {
        IInventory hotbar = player.Player.InventoryManager.GetHotbarInventory();
        for (int i = 0; i < Math.Min(hotbar.Count, 10); i++)
        {
            if (hotbar[i].Empty)
            {
                return hotbar[i];
            }
        }

        Assert.Fail($"the fresh test player has no empty hotbar slot to take crafted items into on {ServerFlavor.Name}; setup is invalid");
        return null!;
    }

    // Recipe selection works on data only, so both flavors pick the same recipes whenever the
    // registry digest agrees. Sorted by output code, preferring outputs only one recipe makes (a
    // recipe whose output has no sibling cannot lose the match to one).

    private static GridRecipe? Pick(IWorldAccessor world, Kind kind, List<GridRecipe> already)
    {
        Dictionary<string, int> outputCounts = world.GridRecipes
            .Where(r => r.Output?.Code != null)
            .GroupBy(r => r.Output.Code.ToString())
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return world.GridRecipes
            .Where(r => !already.Contains(r) && Eligible(r) && IsKind(r, kind))
            .OrderBy(r => outputCounts.GetValueOrDefault(r.Output.Code.ToString()) == 1 ? 0 : 1)
            .ThenBy(r => r.Output.Code.ToString(), StringComparer.Ordinal)
            .ThenBy(r => r.Name?.ToString() ?? "", StringComparer.Ordinal)
            .ThenBy(Signature, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static List<CraftingRecipeIngredient> Ingredients(GridRecipe recipe) =>
        recipe.ResolvedIngredients.Where(i => i != null).ToList()!;

    private static bool Perishable(CollectibleObject? collectible) => collectible?.TransitionableProps is { Length: > 0 };

    private static bool HasAttributes(Vintagestory.API.Datastructures.JsonObject? attributes) =>
        attributes != null && attributes.Exists && attributes.Count > 0;

    private static bool Eligible(GridRecipe r)
    {
        if (!r.Enabled || r.RequiresTrait != null || HasAttributes(r.Attributes) || r.CopyAttributesFrom != null
            || r.MergeAttributesFrom is { Length: > 0 })
        {
            return false;
        }

        CollectibleObject? output = r.Output?.ResolvedItemStack?.Collectible;
        if (output == null || Perishable(output) || r.Output!.Code == null)
        {
            return false;
        }

        if (r.ResolvedIngredients == null || r.Width > GridWidth || r.Height > GridWidth
            || r.ResolvedIngredients.Length != r.Width * r.Height)
        {
            return false;
        }

        List<CraftingRecipeIngredient> ingredients = Ingredients(r);
        return ingredients.Count > 0 && ingredients.All(i =>
            i.ReturnedStack == null && !HasAttributes(i.Attributes) && !HasAttributes(i.RecipeAttributes) && i.ResolvedAttributes is null or { Count: 0 }
            && (i.MatchingType != EnumRecipeMatchType.Exact || (i.ResolvedItemStack?.Collectible != null && !Perishable(i.ResolvedItemStack.Collectible))));
    }

    private static bool IsPlainConsumed(CraftingRecipeIngredient i) =>
        i.MatchingType == EnumRecipeMatchType.Exact && !i.IsTool && i.ConsumeProperties.Consume;

    // A tool ingredient whose tag condition stayed unexpanded (the loader expands at most ten
    // permutations per recipe) is matched at runtime: any durable item satisfying it will do.
    private static bool IsUsableTool(CraftingRecipeIngredient i) =>
        i.IsTool && !i.ConsumeProperties.Consume && i.Quantity == 1 && i.ConsumeProperties.DurabilityCost == 1
        && (i.MatchingType == EnumRecipeMatchType.TagsOnly
            || (i.MatchingType == EnumRecipeMatchType.Exact && i.ResolvedItemStack?.Collectible != null
                && i.ResolvedItemStack.Collectible.GetMaxDurability(i.ResolvedItemStack) > 2));

    private static bool IsKind(GridRecipe r, Kind kind)
    {
        List<CraftingRecipeIngredient> ingredients = Ingredients(r);
        switch (kind)
        {
            case Kind.Shaped:
                return !r.Shapeless && ingredients.Count >= 2 && ingredients.All(IsPlainConsumed);
            case Kind.Shapeless:
                return r.Shapeless && ingredients.Count >= 2 && ingredients.All(IsPlainConsumed);
            case Kind.WildcardWithAllowedVariants:
                return ingredients.Any(i => i.MatchingType == EnumRecipeMatchType.Wildcard && i.AllowedVariants is { Length: > 0 } && !i.IsTool)
                    && ingredients.All(i => i.IsTool
                        ? IsUsableTool(i)
                        : i.ConsumeProperties.Consume && i.MatchingType is EnumRecipeMatchType.Exact or EnumRecipeMatchType.Wildcard);
            case Kind.Tool:
                return ingredients.Count(i => i.IsTool) == 1
                    && ingredients.All(i => i.IsTool ? IsUsableTool(i) : IsPlainConsumed(i));
            default:
                return false;
        }
    }

    private static string Signature(GridRecipe r) =>
        $"{r.Width}x{r.Height}{(r.Shapeless ? "s" : "")}:" +
        string.Join("|", r.ResolvedIngredients.Select(i => i == null ? "_" : $"{i.Code}x{i.Quantity}")) +
        $"=>{r.Output?.Code}x{r.Output?.Quantity}";

    private static string Describe(GridRecipe r) => $"{r.Name?.ToString() ?? "(unnamed)"} {Signature(r)}";

    /// <summary>One ingredient as placed in the grid.</summary>
    private sealed record Placed(int Cell, CraftingRecipeIngredient Ingredient, CollectibleObject Collectible, int Given, int Quantity, int DurabilityBefore);

    private void CraftAndVerify(IWorldAccessor world, IInventory grid, ItemSlot sink, Kind kind, GridRecipe recipe, List<string> failures)
    {
        string label = $"{kind} [{Describe(recipe)}]";
        ClearGrid(grid, sink);

        var placed = new List<Placed>();
        int next = 0;
        for (int k = 0; k < recipe.ResolvedIngredients.Length; k++)
        {
            CraftingRecipeIngredient? ingredient = recipe.ResolvedIngredients[k];
            if (ingredient == null)
            {
                continue;
            }

            int cell = recipe.Shapeless ? next++ : (k / recipe.Width) * GridWidth + (k % recipe.Width);
            int quantity = ingredient.MatchingType == EnumRecipeMatchType.Exact ? ingredient.ResolvedItemStack!.StackSize : ingredient.Quantity;
            CollectibleObject? collectible = ingredient.MatchingType == EnumRecipeMatchType.Exact
                ? ingredient.ResolvedItemStack!.Collectible
                : FirstSatisfying(world, ingredient, quantity, durable: ingredient.IsTool);
            Assert.True(collectible != null,
                $"no collectible satisfies ingredient {ingredient.Code} of {label} on {ServerFlavor.Name}; setup is invalid");

            int given = ingredient.ConsumeProperties.Consume
                ? quantity + Math.Max(0, Math.Min(Surplus, collectible!.MaxStackSize - quantity))
                : quantity;
            ItemStack stack = collectible is Block block ? new ItemStack(block, given) : new ItemStack((Item)collectible!, given);

            ItemSlot slot = grid[cell];
            slot.Itemstack = stack;
            slot.MarkDirty();
            placed.Add(new Placed(cell, ingredient, collectible!, given, quantity,
                ingredient.ConsumeProperties.Consume ? 0 : collectible!.GetRemainingDurability(stack)));
        }

        ItemSlot outputSlot = grid[GridCells];
        ItemStack? expected = recipe.Output.ResolvedItemStack;
        if (outputSlot.Empty)
        {
            failures.Add($"{label}: the output slot stayed empty after placing the ingredients (if vanilla shows the same, the pick or the placement is invalid)");
            return;
        }

        if (outputSlot.Itemstack.Collectible.Code.ToString() != expected!.Collectible.Code.ToString()
            || outputSlot.StackSize != expected.StackSize)
        {
            failures.Add($"{label}: the output slot shows {outputSlot.Itemstack.Collectible.Code} x{outputSlot.StackSize}, " +
                         $"the recipe makes {expected.Collectible.Code} x{expected.StackSize} " +
                         "(another recipe won the match: the pick is ambiguous, or recipe matching diverged on this flavor)");
            return;
        }

        // The engine's own record of which recipe it matched.
        System.Reflection.FieldInfo? matchingField = grid.GetType().GetField("MatchingRecipe");
        Assert.True(matchingField != null, $"{grid.GetType().Name} has no public MatchingRecipe field on {ServerFlavor.Name}; setup is invalid");
        if (matchingField!.GetValue(grid) is not GridRecipe matched || Signature(matched) != Signature(recipe))
        {
            failures.Add($"{label}: the grid matched " + (matchingField.GetValue(grid) is GridRecipe other ? Describe(other) : "no recipe") + " instead");
            return;
        }

        int moved = outputSlot.TryPutInto(world, sink, expected.StackSize);
        if (moved != expected.StackSize || sink.Empty || sink.Itemstack.Collectible.Code.ToString() != expected.Collectible.Code.ToString()
            || sink.StackSize != expected.StackSize)
        {
            failures.Add($"{label}: taking the output moved {moved} (wanted {expected.StackSize}), the hotbar slot holds " +
                         (sink.Empty ? "nothing" : $"{sink.Itemstack.Collectible.Code} x{sink.StackSize}"));
            return;
        }

        foreach (Placed p in placed)
        {
            ItemSlot slot = grid[p.Cell];
            if (!p.Ingredient.ConsumeProperties.Consume)
            {
                int after = slot.Empty ? -1 : p.Collectible.GetRemainingDurability(slot.Itemstack);
                if (slot.Empty || slot.StackSize != 1 || after != p.DurabilityBefore - p.Ingredient.ConsumeProperties.DurabilityCost)
                {
                    failures.Add($"{label}: tool {p.Collectible.Code} in cell {p.Cell} went from durability {p.DurabilityBefore} to " +
                                 (slot.Empty ? "gone" : $"{after} (stack of {slot.StackSize})") +
                                 $", expected {p.DurabilityBefore - p.Ingredient.ConsumeProperties.DurabilityCost}");
                }
            }
            else
            {
                int left = p.Given - p.Quantity;
                bool exact = left == 0 ? slot.Empty : !slot.Empty && slot.StackSize == left && slot.Itemstack.Collectible.Code.ToString() == p.Collectible.Code.ToString();
                if (!exact)
                {
                    failures.Add($"{label}: cell {p.Cell} ({p.Collectible.Code}) held {p.Given}, the recipe takes {p.Quantity}, " +
                                 $"expected {left} left but found " + (slot.Empty ? "nothing" : $"{slot.StackSize}"));
                }
            }
        }

        foreach (int cell in Enumerable.Range(0, GridCells).Where(c => placed.All(p => p.Cell != c)))
        {
            if (!grid[cell].Empty)
            {
                failures.Add($"{label}: crafting filled the unused cell {cell} with {grid[cell].Itemstack.Collectible.Code}");
            }
        }

        ClearGrid(grid, sink);
    }

    private static CollectibleObject? FirstSatisfying(IWorldAccessor world, CraftingRecipeIngredient ingredient, int quantity, bool durable)
    {
        IEnumerable<CollectibleObject> pool = ingredient.Type == EnumItemClass.Block
            ? world.Blocks.Where(b => b?.Code != null)
            : world.Items.Where(i => i?.Code != null);

        foreach (CollectibleObject c in pool.OrderBy(c => c.Code.ToString(), StringComparer.Ordinal))
        {
            if (c.IsMissing || Perishable(c))
            {
                continue;
            }

            ItemStack probe = c is Block block ? new ItemStack(block, quantity) : new ItemStack((Item)c, quantity);
            if (ingredient.SatisfiesAsIngredient(probe) && (!durable || c.GetMaxDurability(probe) > 2))
            {
                return c;
            }
        }

        return null;
    }

    private static void ClearGrid(IInventory grid, ItemSlot sink)
    {
        for (int cell = 0; cell < GridCells; cell++)
        {
            if (!grid[cell].Empty)
            {
                grid[cell].Itemstack = null;
                grid[cell].MarkDirty();
            }
        }

        if (!sink.Empty)
        {
            sink.Itemstack = null;
            sink.MarkDirty();
        }
    }
#pragma warning restore CS8600, CS8602, CS8603, CS8604
}
