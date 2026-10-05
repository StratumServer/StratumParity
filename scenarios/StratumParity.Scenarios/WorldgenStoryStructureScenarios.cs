using System.Collections;
using System.Reflection;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Xunit;
using Xunit.Abstractions;

namespace StratumParity.Scenarios;

/// <summary>
/// Story structure locations and ruin placement on the fixed seed world. This is the story structure scenario of the
/// WorldgenTerrainScenarios plan entry, in a class of its own: those scenarios run on the creative building
/// playstyle, whose world config sets loreContent to false, and GenStoryStructures switches itself off then
/// (no location is ever determined). The survive and build playstyle leaves loreContent at its default,
/// true (and generates the ordinary worldgen structures, which the creative one does not). Nothing here
/// compares terrain, so the playstyle does not touch any golden.
///
/// Stratum patched the placement code (ClampStructureY, IsStructureTooTall: a structure is moved in Y to
/// fit the world height, or skipped when it cannot), so the contract pinned here is the vanilla one:
/// <list type="bullet">
/// <item>the six locations are the vanilla ones: <see cref="VanillaLocations"/> holds the centre x, centre z and east-west
/// direction of each story structure, taken from the vanilla leg (identical on every build measured), so a location drawn from
/// another random stream fails even when it still honours the configured distances;</item>
/// <item>as closed forms read from the structure config: every configured structure has a location, its box is the schematic
/// size around the centre, and its distance from the structure it depends on (the map middle for "spawn") is inside the
/// configured bounds, with the same east-west direction as that structure;</item>
/// <item>the Y of the location is the sea level plus the schematic offset for the surface placements and 1
/// for the others (vanilla never moves it before generation);</item>
/// <item>after the column at the structure's origin is generated, the structure was placed, and its Y
/// is still the vanilla one (sea level plus offset, terrain height based for the ruin and world height
/// placements, 1 underground): on a 256 high world nothing is shifted, and the box stays inside the world;</item>
/// <item>ruins (the structures whose code says ruin) generated in a 12x12 area of columns never overlap each other and are at
/// least <see cref="RuinFloor"/>: the regions of the vanilla area hold 60 to 62 (62 when the class is the first world of its
/// process, 60 after another standard world; the ore noise differs between the two and with it where the vugs fit), Stratum 63.
/// Other structures are left out on purpose: vanilla lets the underground vugs and lakes overlap each other and the ruins
/// (measured: 5 pairs in 5 areas), because the overlap test of the middle-centred underground placement is shifted by half a
/// schematic. The ruin placement moves by a few structures with the ore noise, so the ruins are bounded, not pinned.</item>
/// </list>
/// </summary>
[AtlasWorld(WorldType = "standard", Seed = WorldgenGoldens.Seed, PlayStyle = "surviveandbuild")]
public class WorldgenStoryStructureScenarios : AtlasScenarioBase
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string SystemName = "Vintagestory.GameContent.GenStoryStructures";

    // Centre x, centre z and east-west direction of each story structure on seed 7351, from the vanilla leg (the same on
    // 1.22.7-stratum.2 and its prerelease). Re-pin on a Vintage Story bump or a change of the story structure config.
    internal static readonly (string Code, int X, int Z, int DirX)[] VanillaLocations =
    {
        ("resonancearchive", 512191, 517015, 1),
        ("lazaret", 519366, 513583, 1),
        ("village", 526137, 515429, 1),
        ("devastationarea", 527601, 523176, 1),
        ("tobiascave", 528658, 516444, 1),
        ("treasurehunter", 512572, 511087, 1),
    };

    // A 12x12 area of columns (the golden rectangle's corner: the creative building world of the terrain class does not
    // generate structures, this one does) and the fewest ruins that the regions it touches may hold: vanilla has 60 to 62, Stratum 63.
    private const int RuinAreaX = WorldgenGoldens.RectChunkX;
    private const int RuinAreaZ = WorldgenGoldens.RectChunkZ;
    private const int RuinAreaEdge = 12;
    private const int RuinFloor = 50;

    private readonly ITestOutputHelper output;

    public WorldgenStoryStructureScenarios(ITestOutputHelper output) => this.output = output;

    [AtlasScenario(TimeoutMs = 300_000)]
    public async Task StoryStructures_Should_KeepVanillaLocations_When_WorldCreated()
    {
        ModSystem? system = World.Api.ModLoader.GetModSystem(SystemName);
        Assert.True(system != null, $"mod system {SystemName} is not loaded on {Describe()}; setup is invalid");

        object? config = system!.GetType().GetField("scfg", Any)?.GetValue(system);
        object? locations = system.GetType().GetField("Structures", Any)?.GetValue(system);
        Assert.True(config != null && locations is IEnumerable,
            $"GenStoryStructures.scfg or Structures is null or missing on {Describe()}: the world has no story content (loreContent off?); setup is invalid");

        List<StoryConfig> configs = ((IEnumerable)Member(config!, "Structures")!).Cast<object>().Select(c => new StoryConfig(c)).ToList();
        var byCode = new Dictionary<string, object>();
        foreach (object entry in (IEnumerable)locations!)
        {
            byCode[(string)Member(entry, "Key")!] = Member(entry, "Value")!;
        }

        Assert.True(configs.Count >= 1, $"the story structure config is empty on {Describe()}; setup is invalid");
        Assert.True(byCode.Count == configs.Count && configs.All(c => byCode.ContainsKey(c.Code)),
            $"{byCode.Count} story locations for {configs.Count} configured structures on {Describe()} " +
            $"(configured: {string.Join(",", configs.Select(c => c.Code))}, located: {string.Join(",", byCode.Keys)})");
        output.WriteLine($"story locations on {Describe()}:\n" + string.Join("\n", configs.Select(c => Describe(c, byCode[c.Code]))));

        // The vanilla locations themselves, not only the rules they follow.
        var moved = new List<string>();
        foreach ((string code, int x, int z, int dir) in VanillaLocations)
        {
            if (!byCode.TryGetValue(code, out object? placed))
            {
                moved.Add($"{code} has no location");
                continue;
            }

            BlockPos at = (BlockPos)Member(placed, "CenterPos")!;
            int placedDir = (int)Member(placed, "DirX")!;
            if (at.X != x || at.Z != z || placedDir != dir)
            {
                moved.Add($"{code} at ({at.X},{at.Z}) direction {placedDir}, vanilla ({x},{z}) direction {dir}");
            }
        }

        Assert.True(moved.Count == 0 && byCode.Count == VanillaLocations.Length,
            $"the story structure locations are not the vanilla ones on {Describe()} ({byCode.Count} located, {VanillaLocations.Length} in the vanilla table): {string.Join("; ", moved)}");

        int mapMiddleX = World.Api.WorldManager.MapSizeX / 2;
        int mapMiddleZ = World.Api.WorldManager.MapSizeZ / 2;
        int seaLevel = World.Api.World.SeaLevel;
        int mapSizeY = World.Api.WorldManager.MapSizeY;
        var problems = new List<string>();

        foreach (StoryConfig c in configs)
        {
            object location = byCode[c.Code];
            BlockPos center = (BlockPos)Member(location, "CenterPos")!;
            Cuboidi box = (Cuboidi)Member(location, "Location")!;
            int dirX = (int)Member(location, "DirX")!;

            // Where the structure it depends on stands.
            int baseX;
            int baseZ;
            int baseDirX = dirX;
            if (c.DependsOn == "spawn")
            {
                baseX = mapMiddleX;
                baseZ = mapMiddleZ;
            }
            else
            {
                Assert.True(c.DependsOn != null && byCode.ContainsKey(c.DependsOn),
                    $"story structure {c.Code} depends on '{c.DependsOn}', which has no location on {Describe()}; setup is invalid");
                object parent = byCode[c.DependsOn!];
                BlockPos parentCenter = (BlockPos)Member(parent, "CenterPos")!;
                baseX = parentCenter.X;
                baseZ = parentCenter.Z;
                baseDirX = (int)Member(parent, "DirX")!;
            }

            if (dirX != baseDirX)
            {
                problems.Add($"{c.Code}: east-west direction {dirX}, expected {baseDirX}");
            }

            int distanceX = (center.X - baseX) * dirX;
            if (distanceX < c.MinSpawnDistX || distanceX > c.MaxSpawnDistX)
            {
                problems.Add($"{c.Code}: distance in x {distanceX} outside [{c.MinSpawnDistX},{c.MaxSpawnDistX}]");
            }

            int deltaZ = center.Z - baseZ;
            if (!InRange(deltaZ, c.MinSpawnDistZ, c.MaxSpawnDistZ) && !InRange(-deltaZ, c.MinSpawnDistZ, c.MaxSpawnDistZ))
            {
                problems.Add($"{c.Code}: distance in z {deltaZ} outside [{c.MinSpawnDistZ},{c.MaxSpawnDistZ}] in either direction");
            }

            int expectedY = c.IsSurfacePlacement ? seaLevel + c.OffsetY : 1;
            if (center.Y != expectedY)
            {
                problems.Add($"{c.Code}: centre Y {center.Y}, expected {expectedY} (sea level {seaLevel}, offset {c.OffsetY}, placement {c.Placement})");
            }

            int minX = center.X - c.SizeX / 2;
            int minZ = center.Z - c.SizeZ / 2;
            if (box.X1 != minX || box.Z1 != minZ || box.X2 != minX + c.SizeX || box.Z2 != minZ + c.SizeZ
                || box.Y1 != center.Y || box.Y2 != center.Y + c.SizeY)
            {
                problems.Add($"{c.Code}: box {Box(box)} is not the {c.SizeX}x{c.SizeY}x{c.SizeZ} schematic around ({center.X},{center.Y},{center.Z})");
            }
        }

        for (int i = 0; i < configs.Count; i++)
        {
            for (int k = i + 1; k < configs.Count; k++)
            {
                BlockPos a = (BlockPos)Member(byCode[configs[i].Code], "CenterPos")!;
                BlockPos b = (BlockPos)Member(byCode[configs[k].Code], "CenterPos")!;
                double apart = Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Z - b.Z) * (a.Z - b.Z));
                int needed = configs[i].LandformRadius + configs[k].LandformRadius;
                if (apart < needed)
                {
                    problems.Add($"{configs[i].Code} and {configs[k].Code} are {apart:F0} blocks apart, their landforms need {needed}");
                }
            }
        }

        Assert.True(problems.Count == 0,
            $"the story structure locations break the vanilla placement rules on {Describe()}: {string.Join("; ", problems)}");

        // Generate the column at each structure's origin: the structure is placed there and its Y stays the vanilla one.
        foreach (StoryConfig c in configs)
        {
            object location = byCode[c.Code];
            Cuboidi box = (Cuboidi)Member(location, "Location")!;
            int cx = box.X1 / WorldgenArea.ChunkSize;
            int cz = box.Z1 / WorldgenArea.ChunkSize;

            await WorldgenArea.LoadDone(World, cx, cz, 1, 1);
            try
            {
                Assert.True((bool)Member(location, "DidGenerate")!,
                    $"story structure {c.Code} did not generate when its origin column ({cx},{cz}) reached Done on {Describe()}");

                int worldgenHeight = (int)Member(location, "WorldgenHeight")!;
                if (c.UseWorldgenHeight || c.Placement == "SurfaceRuin")
                {
                    Assert.True(worldgenHeight >= 0,
                        $"story structure {c.Code} has no recorded worldgen height after its origin column was generated on {Describe()}");
                }

                int expectedY1 = c.UseWorldgenHeight ? worldgenHeight + c.OffsetY
                    : c.Placement == "SurfaceRuin" ? worldgenHeight - c.SizeY + c.OffsetY
                    : c.Placement == "Surface" ? seaLevel + c.OffsetY
                    : 1;
                Cuboidi generated = (Cuboidi)Member(location, "Location")!;
                Assert.True(generated.Y1 == expectedY1 && generated.Y2 == expectedY1 + c.SizeY,
                    $"story structure {c.Code} spans y {generated.Y1} to {generated.Y2} after generation on {Describe()}, " +
                    $"vanilla places it at {expectedY1} to {expectedY1 + c.SizeY} (placement {c.Placement}, worldgen height {worldgenHeight}, " +
                    $"offset {c.OffsetY}, size {c.SizeY}, world height {mapSizeY})");
                Assert.True(generated.Y1 >= 1 && generated.Y2 <= mapSizeY,
                    $"story structure {c.Code} spans y {generated.Y1} to {generated.Y2}, outside the world (1 to {mapSizeY}) on {Describe()}");

                int regionSize = World.Api.WorldManager.RegionSize;
                IMapRegion? region = World.Api.WorldManager.GetMapRegion(generated.X1 / regionSize, generated.Z1 / regionSize);
                Assert.True(region != null && region.GeneratedStructures.Any(g => g.Code.StartsWith(c.Code + ":", StringComparison.Ordinal)),
                    $"the map region of story structure {c.Code} does not list it as generated on {Describe()}");
            }
            finally
            {
                WorldgenArea.Unload(World, cx, cz, 1, 1);
            }
        }
    }

    [AtlasScenario(TimeoutMs = 300_000)]
    public async Task RuinStructures_Should_NotOverlap_When_AreaGenerated()
    {
        await WorldgenArea.LoadDone(World, RuinAreaX, RuinAreaZ, RuinAreaEdge, RuinAreaEdge, timeoutTicks: 20_000);
        try
        {
            int regionSize = World.Api.WorldManager.RegionSize;
            int minX = RuinAreaX * WorldgenArea.ChunkSize;
            int maxX = (RuinAreaX + RuinAreaEdge) * WorldgenArea.ChunkSize;
            int minZ = RuinAreaZ * WorldgenArea.ChunkSize;
            int maxZ = (RuinAreaZ + RuinAreaEdge) * WorldgenArea.ChunkSize;

            // Every ruin of the regions the area touches (the columns of the ring around the area generate too, so some
            // of them were placed by columns outside it; the set is the same on every boot).
            var ruins = new List<GeneratedStructure>();
            int structures = 0;
            for (int rx = minX / regionSize; rx <= maxX / regionSize; rx++)
            {
                for (int rz = minZ / regionSize; rz <= maxZ / regionSize; rz++)
                {
                    IMapRegion? region = World.Api.WorldManager.GetMapRegion(rx, rz);
                    Assert.True(region != null, $"map region ({rx},{rz}) of the ruin area is missing on {Describe()}; setup is invalid");
                    foreach (GeneratedStructure structure in region!.GeneratedStructures)
                    {
                        structures++;
                        // The code is "schematic file/structure code": the ruins are named by their structure code.
                        if (structure.Code[(structure.Code.LastIndexOf('/') + 1)..].Contains("ruin", StringComparison.Ordinal))
                        {
                            ruins.Add(structure);
                        }
                    }
                }
            }

            output.WriteLine($"ruins on {Describe()}: {ruins.Count} ruins among {structures} generated structures in the regions of area ({RuinAreaX},{RuinAreaZ}) edge {RuinAreaEdge}");

            var overlapping = new List<string>();
            for (int i = 0; i < ruins.Count; i++)
            {
                for (int k = i + 1; k < ruins.Count; k++)
                {
                    if (ruins[i].Location.Intersects(ruins[k].Location))
                    {
                        overlapping.Add($"{ruins[i].Code} {Box(ruins[i].Location)} and {ruins[k].Code} {Box(ruins[k].Location)}");
                    }
                }
            }

            Assert.True(overlapping.Count == 0,
                $"{overlapping.Count} pairs of ruins overlap on {Describe()}: {string.Join("; ", overlapping.Take(5))}");
            Assert.True(ruins.Count >= RuinFloor,
                $"only {ruins.Count} ruins were generated in the regions of the ruin area on {Describe()}, vanilla holds 60 to 62 and the floor is {RuinFloor}");
        }
        finally
        {
            WorldgenArea.Unload(World, RuinAreaX, RuinAreaZ, RuinAreaEdge, RuinAreaEdge);
        }
    }

    private static string Describe() => $"{ServerFlavor.Name} ({ServerFlavor.Version ?? "vanilla"})";

    private static bool InRange(int value, int min, int max) => value >= min && value <= max;

    private static string Box(Cuboidi box) => $"({box.X1},{box.Y1},{box.Z1})-({box.X2},{box.Y2},{box.Z2})";

    private static string Describe(StoryConfig c, object location)
    {
        BlockPos center = (BlockPos)Member(location, "CenterPos")!;
        Cuboidi box = (Cuboidi)Member(location, "Location")!;
        return $"  {c.Code}: centre ({center.X},{center.Y},{center.Z}) box {Box(box)} dirX {Member(location, "DirX")} placement {c.Placement} depends on {c.DependsOn}";
    }

    /// <summary>A public or internal field or property by name, failing as "setup is invalid" when the build does not have it.</summary>
    private static object? Member(object target, string name)
    {
        Type type = target.GetType();
        FieldInfo? field = type.GetField(name, Any);
        if (field != null)
        {
            return field.GetValue(target);
        }

        PropertyInfo? property = type.GetProperty(name, Any);
        Assert.True(property != null, $"{type.Name}.{name} was not found by reflection on {ServerFlavor.Name}; setup is invalid");
        return property!.GetValue(target);
    }

    /// <summary>The members of one WorldGenStoryStructure the checks need, read once.</summary>
    private sealed class StoryConfig
    {
        public StoryConfig(object structure)
        {
            Code = (string)Member(structure, "Code")!;
            Placement = Member(structure, "Placement")!.ToString()!;
            UseWorldgenHeight = (bool)Member(structure, "UseWorldgenHeight")!;
            DependsOn = Member(structure, "DependsOnStructure") as string;
            MinSpawnDistX = (int)Member(structure, "MinSpawnDistX")!;
            MaxSpawnDistX = (int)Member(structure, "MaxSpawnDistX")!;
            MinSpawnDistZ = (int)Member(structure, "MinSpawnDistZ")!;
            MaxSpawnDistZ = (int)Member(structure, "MaxSpawnDistZ")!;
            LandformRadius = (int)Member(structure, "LandformRadius")!;

            object schematic = Member(structure, "schematicData")!;
            SizeX = (int)Member(schematic, "SizeX")!;
            SizeY = (int)Member(schematic, "SizeY")!;
            SizeZ = (int)Member(schematic, "SizeZ")!;
            OffsetY = (int)Member(schematic, "OffsetY")!;
        }

        public string Code { get; }

        public string Placement { get; }

        public bool UseWorldgenHeight { get; }

        public string? DependsOn { get; }

        public int MinSpawnDistX { get; }

        public int MaxSpawnDistX { get; }

        public int MinSpawnDistZ { get; }

        public int MaxSpawnDistZ { get; }

        public int LandformRadius { get; }

        public int SizeX { get; }

        public int SizeY { get; }

        public int SizeZ { get; }

        public int OffsetY { get; }

        public bool IsSurfacePlacement => Placement == "Surface" || Placement == "SurfaceRuin";
    }
}
