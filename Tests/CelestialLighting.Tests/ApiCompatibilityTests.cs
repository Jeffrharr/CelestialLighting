using Mono.Cecil;

namespace CelestialLighting.Tests;

/// <summary>
/// Verifies that the RimWorld API surface CelestialLighting depends on still exists.
/// Run these after every RimWorld update. Failures mean the mod needs updating.
/// </summary>
[TestFixture]
[Category("RequiresGameDll")]
public class ApiCompatibilityTests
{
    private const string FallbackDllPath =
        "/home/deck/.local/share/Steam/steamapps/common/RimWorld/RimWorldLinux_Data/Managed/Assembly-CSharp.dll";

    private static string DllPath =>
        Environment.GetEnvironmentVariable("RIMWORLD_ASSEMBLY") ?? FallbackDllPath;

    private ModuleDefinition _module = null!;

    [OneTimeSetUp]
    public void LoadAssembly()
    {
        if (!File.Exists(DllPath))
            Assert.Ignore($"Assembly-CSharp.dll not found at {DllPath} — set RIMWORLD_ASSEMBLY to run these tests.");
        _module = ModuleDefinition.ReadModule(DllPath);
    }

    [OneTimeTearDown]
    public void Dispose() => _module?.Dispose();

    // --- GenCelestial (Patch_ShadowDirection) ---

    [Test]
    public void GenCelestial_GetLightSourceInfo_Exists()
    {
        var type = GetType("RimWorld.GenCelestial");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m => m.Name == "GetLightSourceInfo" && m.Parameters.Count == 2);
        Assert.That(method, Is.Not.Null,
            "GenCelestial.GetLightSourceInfo(Map, LightType) no longer exists");
    }

    [Test]
    public void GenCelestial_LightInfo_HasVectorAndIntensityFields()
    {
        var type = GetNestedType("RimWorld.GenCelestial", "LightInfo");
        Assert.That(type, Is.Not.Null, "GenCelestial.LightInfo no longer exists");
        Assert.That(type!.Fields.Any(f => f.Name == "vector"), Is.True,
            "GenCelestial.LightInfo.vector no longer exists");
        Assert.That(type.Fields.Any(f => f.Name == "intensity"), Is.True,
            "GenCelestial.LightInfo.intensity no longer exists");
    }

    [Test]
    public void GenCelestial_LightType_HasShadowMember()
    {
        var type = GetNestedType("RimWorld.GenCelestial", "LightType");
        Assert.That(type, Is.Not.Null, "GenCelestial.LightType no longer exists");
        Assert.That(type!.Fields.Any(f => f.Name == "Shadow"), Is.True,
            "GenCelestial.LightType.Shadow no longer exists");
    }

    [Test]
    public void GenCelestial_CurCelestialSunGlow_Exists()
    {
        var type = GetType("RimWorld.GenCelestial");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m => m.Name == "CurCelestialSunGlow");
        Assert.That(method, Is.Not.Null, "GenCelestial.CurCelestialSunGlow(Map) no longer exists");
        Assert.That(method!.ReturnType.FullName, Is.EqualTo("System.Single"));
    }

    [Test]
    public void GenCelestial_CurShadowStrength_Exists()
    {
        var type = GetType("RimWorld.GenCelestial");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m => m.Name == "CurShadowStrength");
        Assert.That(method, Is.Not.Null, "GenCelestial.CurShadowStrength(Map) no longer exists");
    }

    // --- WeatherWorker / SkyTarget / SkyColorSet (Patch_TwilightColor) ---

    [Test]
    public void WeatherWorker_CurSkyTarget_ReturnsSkyTarget()
    {
        var type = GetType("Verse.WeatherWorker");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m => m.Name == "CurSkyTarget");
        Assert.That(method, Is.Not.Null, "WeatherWorker.CurSkyTarget(Map) no longer exists");
        Assert.That(method!.ReturnType.FullName, Is.EqualTo("Verse.SkyTarget"),
            "WeatherWorker.CurSkyTarget no longer returns Verse.SkyTarget");
    }

    [Test]
    public void SkyTarget_HasColorsField()
    {
        var type = GetType("Verse.SkyTarget");
        Assert.That(type, Is.Not.Null, "Verse.SkyTarget no longer exists");
        var field = type!.Fields.SingleOrDefault(f => f.Name == "colors");
        Assert.That(field, Is.Not.Null, "SkyTarget.colors no longer exists");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("Verse.SkyColorSet"));
    }

    [Test]
    public void SkyTarget_HasGlowField()
    {
        // Two subsystems depend on this public float: Patch_NightRadiance (§7) writes the night
        // floor into it, and Patch_LowLightDesaturation (§9) reads it to key the Purkinje shift on
        // actual (weather-dimmed) brightness.
        var type = GetType("Verse.SkyTarget");
        Assert.That(type, Is.Not.Null, "Verse.SkyTarget no longer exists");
        var field = type!.Fields.SingleOrDefault(f => f.Name == "glow" && f.IsPublic);
        Assert.That(field, Is.Not.Null, "SkyTarget.glow no longer exists or is no longer public");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("System.Single"));
    }

    [Test]
    public void SkyColorSet_HasExpectedPublicFields()
    {
        // Types are asserted, not just presence: §13's whole classifier reads `sky` as an RGB
        // colour and `saturation` as a scalar. If either changed shape the formula would still
        // compile against a renamed member but classify every weather wrongly.
        var expected = new Dictionary<string, string>
        {
            ["sky"] = "UnityEngine.Color",
            ["shadow"] = "UnityEngine.Color",
            ["overlay"] = "UnityEngine.Color",
            ["saturation"] = "System.Single",
        };

        var type = GetType("Verse.SkyColorSet");
        Assert.That(type, Is.Not.Null, "Verse.SkyColorSet no longer exists");
        foreach (var (fieldName, fieldType) in expected)
        {
            var field = type!.Fields.SingleOrDefault(f => f.Name == fieldName && f.IsPublic);
            Assert.That(field, Is.Not.Null,
                $"SkyColorSet.{fieldName} no longer exists or is no longer public");
            Assert.That(field!.FieldType.FullName, Is.EqualTo(fieldType),
                $"SkyColorSet.{fieldName} changed type");
        }
    }

    // --- WeatherManager / WeatherDef (Patch_WeatherDimming, §13) ---
    //
    // §13 classifies weather from its own def data and blends across transitions. It reads all of
    // this off map.weatherManager rather than WeatherWorker's own `def` field — see
    // WeatherWorker_DefFieldIsNotPublic below for why that is the deliberate choice.

    [Test]
    public void Map_WeatherManager_Exists()
    {
        var type = GetType("Verse.Map");
        Assert.That(type, Is.Not.Null);
        var field = type!.Fields.SingleOrDefault(f => f.Name == "weatherManager" && f.IsPublic);
        Assert.That(field, Is.Not.Null, "Map.weatherManager no longer exists or is no longer public");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("RimWorld.WeatherManager"));
    }

    [Test]
    public void WeatherManager_HasCurrentAndLastWeatherFields()
    {
        var type = GetType("RimWorld.WeatherManager");
        Assert.That(type, Is.Not.Null, "RimWorld.WeatherManager no longer exists");
        foreach (var fieldName in new[] { "curWeather", "lastWeather" })
        {
            var field = type!.Fields.SingleOrDefault(f => f.Name == fieldName && f.IsPublic);
            Assert.That(field, Is.Not.Null,
                $"WeatherManager.{fieldName} no longer exists or is no longer public");
            Assert.That(field!.FieldType.FullName, Is.EqualTo("Verse.WeatherDef"));
        }
    }

    [Test]
    public void WeatherManager_HasTransitionAndRateProperties()
    {
        // TransitionLerpFactor is what §13 blends the outgoing and incoming weather's cloud opacity
        // by; the three rates drive the precipitation-intensity band. All four are already
        // transition-lerped by vanilla, which is why §13 does not lerp the rates itself.
        var type = GetType("RimWorld.WeatherManager");
        Assert.That(type, Is.Not.Null, "RimWorld.WeatherManager no longer exists");
        foreach (var name in new[] { "TransitionLerpFactor", "RainRate", "SnowRate", "SandRate" })
        {
            var property = type!.Properties.SingleOrDefault(p => p.Name == name);
            Assert.That(property, Is.Not.Null, $"WeatherManager.{name} no longer exists");
            Assert.That(property!.PropertyType.FullName, Is.EqualTo("System.Single"),
                $"WeatherManager.{name} is no longer a float");
        }
    }

    [Test]
    public void WeatherDef_HasSkyColorsDayForClassification()
    {
        var type = GetType("Verse.WeatherDef");
        Assert.That(type, Is.Not.Null, "Verse.WeatherDef no longer exists");
        var field = type!.Fields.SingleOrDefault(f => f.Name == "skyColorsDay" && f.IsPublic);
        Assert.That(field, Is.Not.Null,
            "WeatherDef.skyColorsDay no longer exists or is no longer public");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("Verse.SkyColorSet"));
    }

    [Test]
    public void WeatherDef_HasPrecipitationRateFields()
    {
        var type = GetType("Verse.WeatherDef");
        Assert.That(type, Is.Not.Null, "Verse.WeatherDef no longer exists");
        // sandRate is Odyssey's; it exists on the base def regardless, and §13 reads all three both
        // for the intensity band and as the precipitation evidence that overrides
        // an unconvincing modded palette (WeatherDimmingMath.PrecipitationEvidence).
        foreach (var fieldName in new[] { "rainRate", "snowRate", "sandRate" })
        {
            var field = type!.Fields.SingleOrDefault(f => f.Name == fieldName && f.IsPublic);
            Assert.That(field, Is.Not.Null,
                $"WeatherDef.{fieldName} no longer exists or is no longer public");
            Assert.That(field!.FieldType.FullName, Is.EqualTo("System.Single"));
        }
    }

    // --- BiomeDef weather census (§13's structural guard) ---
    //
    // WeatherDimming.HasSky decides whether a map has a sky at all by asking its biome how many
    // weathers it can actually roll. That closes the cave / pocket-map / orbit class without a defName
    // list, but it does mean §13 now depends on three more vanilla members. If any of them moves, the
    // guard silently answers "no sky" everywhere and weather dimming quietly stops working.

    [Test]
    public void Map_Biome_Exists()
    {
        var type = GetType("Verse.Map");
        Assert.That(type, Is.Not.Null);
        var property = type!.Properties.SingleOrDefault(p => p.Name == "Biome");
        Assert.That(property, Is.Not.Null, "Map.Biome no longer exists");
        Assert.That(property!.PropertyType.FullName, Is.EqualTo("RimWorld.BiomeDef"));
    }

    [Test]
    public void BiomeDef_HasBaseWeatherCommonalities()
    {
        var type = GetType("RimWorld.BiomeDef");
        Assert.That(type, Is.Not.Null, "RimWorld.BiomeDef no longer exists");
        var field = type!.Fields.SingleOrDefault(
            f => f.Name == "baseWeatherCommonalities" && f.IsPublic);
        Assert.That(field, Is.Not.Null,
            "BiomeDef.baseWeatherCommonalities no longer exists or is no longer public");
        Assert.That(field!.FieldType.FullName,
            Is.EqualTo("System.Collections.Generic.List`1<RimWorld.WeatherCommonalityRecord>"),
            "BiomeDef.baseWeatherCommonalities changed shape");
    }

    [Test]
    public void WeatherCommonalityRecord_HasCommonality()
    {
        // §13 counts records whose commonality is above zero, not records outright — Biomes! Caverns
        // lists vanilla's rain weathers at commonality 0 specifically to suppress them on its cavern
        // biomes, and an entry count would read those caverns as having a climate.
        var type = GetType("RimWorld.WeatherCommonalityRecord");
        Assert.That(type, Is.Not.Null, "RimWorld.WeatherCommonalityRecord no longer exists");
        var field = type!.Fields.SingleOrDefault(f => f.Name == "commonality" && f.IsPublic);
        Assert.That(field, Is.Not.Null,
            "WeatherCommonalityRecord.commonality no longer exists or is no longer public");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("System.Single"));
    }

    [Test]
    public void BiomeDef_HasDisableSkyLightingAndSkyManagerStillBranchesOnIt()
    {
        // The second half of the guard, and the reason to trust it: this is not a field we invented a
        // meaning for. SkyManagerUpdate itself branches on it to stop writing curSky.colors.sky into
        // MatBases.LightOverlay at all, so it is vanilla's own declaration that a map has nothing
        // overhead. If SkyManagerUpdate stops reading it, our reading of it needs rethinking too.
        var biome = GetType("RimWorld.BiomeDef");
        Assert.That(biome, Is.Not.Null, "RimWorld.BiomeDef no longer exists");
        var field = biome!.Fields.SingleOrDefault(f => f.Name == "disableSkyLighting" && f.IsPublic);
        Assert.That(field, Is.Not.Null,
            "BiomeDef.disableSkyLighting no longer exists or is no longer public");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("System.Boolean"));

        var skyManager = GetType("Verse.SkyManager");
        Assert.That(skyManager, Is.Not.Null, "Verse.SkyManager no longer exists");
        var update = skyManager!.Methods.SingleOrDefault(m => m.Name == "SkyManagerUpdate");
        Assert.That(update, Is.Not.Null, "SkyManager.SkyManagerUpdate no longer exists");
        Assert.That(
            update!.Body.Instructions.Any(i =>
                i.Operand is Mono.Cecil.FieldReference r && r.Name == "disableSkyLighting"),
            Is.True,
            "SkyManagerUpdate no longer reads BiomeDef.disableSkyLighting — §13's skyless-map guard "
            + "was justified by vanilla using this field for exactly that meaning");
    }

    [Test]
    public void Def_HasGetModExtension()
    {
        // §13's per-def escape hatch (CelestialLighting.WeatherCloudDeck) is read through this.
        var type = GetType("Verse.Def");
        Assert.That(type, Is.Not.Null, "Verse.Def no longer exists");
        var method = type!.Methods.SingleOrDefault(
            m => m.Name == "GetModExtension" && m.HasGenericParameters && m.Parameters.Count == 0);
        Assert.That(method, Is.Not.Null,
            "Def.GetModExtension<T>() no longer exists — WeatherCloudDeck cannot be read");

        var extension = GetType("Verse.DefModExtension");
        Assert.That(extension, Is.Not.Null,
            "Verse.DefModExtension no longer exists — WeatherCloudDeck's base class is gone");
    }

    [Test]
    public void WeatherDef_MaxGlowStillExistsAndStillDefaultsToNoClamp()
    {
        // The premise §13 was built on. `maxGlow` is vanilla's ONLY per-weather lighting-magnitude
        // knob, it defaults to 1.0, and across all vanilla XML it is set exactly once (Odyssey's
        // Overcast, 0.95) — which is why weather does not meaningfully dim glow and why §13 works
        // the colour channel instead. If a future RimWorld starts dimming glow per-weather, this
        // field is where it would show up and §13's design note needs revisiting.
        var type = GetType("Verse.WeatherDef");
        Assert.That(type, Is.Not.Null, "Verse.WeatherDef no longer exists");
        var field = type!.Fields.SingleOrDefault(f => f.Name == "maxGlow" && f.IsPublic);
        Assert.That(field, Is.Not.Null, "WeatherDef.maxGlow no longer exists or is no longer public");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("System.Single"));
    }

    [Test]
    public void WeatherWorker_DefFieldIsNotPublic()
    {
        // Pinning the road not taken. §13 could have read the active WeatherDef straight off the
        // worker being postfixed, but `def` is private, so that would need FieldRefAccess. Reading
        // map.weatherManager instead is exact rather than merely equivalent: SkyManager calls
        // CurSkyTarget on BOTH the current and last worker and lerps the results by the same
        // factor, so a uniform map-level multiply factors straight back out —
        // Lerp(a*k, b*k, t) == k*Lerp(a, b, t). If this ever becomes public, the per-def read
        // becomes available, but it would buy nothing.
        var type = GetType("Verse.WeatherWorker");
        Assert.That(type, Is.Not.Null, "Verse.WeatherWorker no longer exists");
        var field = type!.Fields.SingleOrDefault(f => f.Name == "def");
        Assert.That(field, Is.Not.Null, "WeatherWorker.def no longer exists");
        Assert.That(field!.IsPublic, Is.False,
            "WeatherWorker.def became public — see Patch_WeatherDimming for why we still don't need it");
    }

    // --- GameComponent / Game / GenDate (GameComponent_MoonPhase, MoonPosition) ---

    [Test]
    public void GameComponent_IsAbstractExposableBase()
    {
        var type = GetType("Verse.GameComponent");
        Assert.That(type, Is.Not.Null, "Verse.GameComponent no longer exists — GameComponent_MoonPhase's base class is gone");
        Assert.That(type!.IsAbstract, Is.True, "Verse.GameComponent is no longer abstract");
        Assert.That(type.Interfaces.Any(i => i.InterfaceType.FullName == "Verse.IExposable"), Is.True,
            "Verse.GameComponent no longer implements IExposable — ExposeData override will silently detach");
        Assert.That(type.Methods.Any(m => m.Name == "ExposeData" && m.IsVirtual), Is.True,
            "GameComponent.ExposeData is no longer virtual — our override would silently stop running");
    }

    [Test]
    public void Game_GetComponentGeneric_Exists()
    {
        var type = GetType("Verse.Game");
        Assert.That(type, Is.Not.Null, "Verse.Game no longer exists");
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "GetComponent" && m.HasGenericParameters && m.Parameters.Count == 0);
        Assert.That(method, Is.Not.Null,
            "Game.GetComponent<T>() no longer exists — GameComponent_MoonPhase.Current can't resolve the moon component");
    }

    [Test]
    public void GenDate_TicksPerDay_Is60000()
    {
        var type = GetType("RimWorld.GenDate");
        Assert.That(type, Is.Not.Null);
        var field = type!.Fields.SingleOrDefault(f => f.Name == "TicksPerDay");
        Assert.That(field, Is.Not.Null, "GenDate.TicksPerDay no longer exists");
        Assert.That(field!.Constant, Is.EqualTo(60000),
            "GenDate.TicksPerDay changed — GameComponent_MoonPhase converts the synodic period in days to ticks with it");
    }

    // --- GameCondition / GameConditionManager / DefDatabase (Patch_AuroraTint, AuroraConditions) ---

    [Test]
    public void Map_GameConditionManager_Exists()
    {
        var type = GetType("Verse.Map");
        Assert.That(type, Is.Not.Null);
        var field = type!.Fields.SingleOrDefault(f => f.Name == "gameConditionManager");
        Assert.That(field, Is.Not.Null, "Map.gameConditionManager no longer exists");
        Assert.That(field!.IsPublic, Is.True, "Map.gameConditionManager is no longer public");
        Assert.That(field.FieldType.FullName, Is.EqualTo("RimWorld.GameConditionManager"));
    }

    [Test]
    public void GameConditionManager_GetActiveCondition_Exists()
    {
        var type = GetType("RimWorld.GameConditionManager");
        Assert.That(type, Is.Not.Null, "RimWorld.GameConditionManager no longer exists");
        // The non-generic GetActiveCondition(GameConditionDef) — AuroraConditions calls this to find
        // the active solar-flare condition by def.
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "GetActiveCondition" && !m.HasGenericParameters
            && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.Name == "GameConditionDef");
        Assert.That(method, Is.Not.Null, "GameConditionManager.GetActiveCondition(GameConditionDef) no longer exists");
        Assert.That(method!.ReturnType.FullName, Is.EqualTo("RimWorld.GameCondition"));
    }

    [Test]
    public void GameCondition_HasFadeMembers()
    {
        var type = GetType("RimWorld.GameCondition");
        Assert.That(type, Is.Not.Null, "RimWorld.GameCondition no longer exists");
        foreach (var name in new[] { "TicksPassed", "TicksLeft" })
        {
            var prop = type!.Properties.SingleOrDefault(p => p.Name == name);
            Assert.That(prop, Is.Not.Null, $"GameCondition.{name} no longer exists — AuroraConditions.RampFor depends on it");
            Assert.That(prop!.PropertyType.FullName, Is.EqualTo("System.Int32"));
        }
        var permanent = type!.Properties.SingleOrDefault(p => p.Name == "Permanent");
        Assert.That(permanent, Is.Not.Null, "GameCondition.Permanent no longer exists");
        Assert.That(permanent!.PropertyType.FullName, Is.EqualTo("System.Boolean"));
    }

    [Test]
    public void SolarFlare_GameConditionDef_Exists()
    {
        // SolarFlare is a core GameConditionDef but is NOT on GameConditionDefOf, so AuroraConditions
        // resolves it by defName via DefDatabase.GetNamedSilentFail. This asserts the DefDatabase
        // lookup method still exists (the def itself is data, verified live, not in the assembly).
        var type = _module.Types.FirstOrDefault(t => t.FullName == "Verse.DefDatabase`1");
        Assert.That(type, Is.Not.Null, "Verse.DefDatabase<T> no longer exists");
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "GetNamedSilentFail" && m.Parameters.Count == 1);
        Assert.That(method, Is.Not.Null, "DefDatabase<T>.GetNamedSilentFail(string) no longer exists");
    }

    // --- GenLocalDate (Patch_ShadowDirection) ---

    [Test]
    public void GenLocalDate_DayPercent_Exists()
    {
        var type = GetType("RimWorld.GenLocalDate");
        Assert.That(type, Is.Not.Null, "RimWorld.GenLocalDate no longer exists");
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "DayPercent" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.Name == "Map");
        Assert.That(method, Is.Not.Null, "GenLocalDate.DayPercent(Map) no longer exists");
    }

    // --- GenDate / WorldGrid / Map (LatitudeEffect, Patch_ShadowDirection) ---

    [Test]
    public void GenDate_DayOfYear_Exists()
    {
        var type = GetType("RimWorld.GenDate");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "DayOfYear" && m.Parameters.Count == 2);
        Assert.That(method, Is.Not.Null, "GenDate.DayOfYear(long, float) no longer exists");
    }

    [Test]
    public void GenDate_DaysPerYear_Is60()
    {
        var type = GetType("RimWorld.GenDate");
        Assert.That(type, Is.Not.Null);
        var field = type!.Fields.SingleOrDefault(f => f.Name == "DaysPerYear");
        Assert.That(field, Is.Not.Null, "GenDate.DaysPerYear no longer exists");
        Assert.That(field!.Constant, Is.EqualTo(60),
            "GenDate.DaysPerYear no longer equals 60 — LatitudeEffect's /60f divisor will desync seasons");
    }

    [Test]
    public void WorldGrid_LongLatOf_Exists()
    {
        var type = GetType("RimWorld.Planet.WorldGrid");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m => m.Name == "LongLatOf");
        Assert.That(method, Is.Not.Null, "WorldGrid.LongLatOf(PlanetTile) no longer exists");
    }

    [Test]
    public void Map_Tile_Exists()
    {
        var type = GetType("Verse.Map");
        Assert.That(type, Is.Not.Null);
        var property = type!.Properties.SingleOrDefault(p => p.Name == "Tile");
        Assert.That(property, Is.Not.Null, "Map.Tile no longer exists");
    }

    [Test]
    public void Map_Size_Exists()
    {
        var type = GetType("Verse.Map");
        Assert.That(type, Is.Not.Null);
        var property = type!.Properties.SingleOrDefault(p => p.Name == "Size");
        Assert.That(property, Is.Not.Null, "Map.Size no longer exists");
    }

    [Test]
    public void Map_UniqueID_Exists()
    {
        // Issue #12's per-frame geometry memo keys on this (GeometryMemo, via SolarPosition and
        // MoonPosition). An int field, not a property — asserted as a field so a future change to a
        // computed property is noticed rather than silently compiling.
        var type = GetType("Verse.Map");
        Assert.That(type, Is.Not.Null);
        var field = type!.Fields.SingleOrDefault(f => f.Name == "uniqueID");
        Assert.That(field, Is.Not.Null, "Map.uniqueID no longer exists — GeometryMemo keys on it");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("System.Int32"));
    }

    // --- SectionLayer_SunShadows / SectionLayer / Section (Patch_ShadowMeshPerimeter,
    //     Patch_ShadowRoofInvalidation) ---
    //
    // DrawLayer() used to be pinned here too, for the per-draw shadow-tilt Prefix. That patch and
    // the across-map tilt it served are both gone (DESIGN.md §3), so vanilla's own DrawLayer runs
    // again and we no longer depend on its shape.

    [Test]
    public void SectionLayer_SunShadows_HasSectionConstructor()
    {
        // Patch_ShadowRoofInvalidation's subscription-widening Postfix hangs off this
        // constructor: it is the only place a layer instance exists before anything can dirty it.
        var type = GetType("Verse.SectionLayer_SunShadows");
        Assert.That(type, Is.Not.Null, "Verse.SectionLayer_SunShadows no longer exists");
        Assert.That(
            type!.Methods.Any(m => m.IsConstructor && m.Parameters.Count == 1
                && m.Parameters[0].ParameterType.FullName == "Verse.Section"),
            Is.True,
            "SectionLayer_SunShadows(Section) no longer exists — Patch_ShadowRoofInvalidation "
            + "resolves it by TargetMethod");
    }

    [Test]
    public void MapComponent_HasMapConstructor()
    {
        // The only MapComponent left is the inert MapComponent_SunShadowAxis tombstone, which
        // exists so saves written before the tilt was removed still load without a Scribe error.
        // Map.FillComponents instantiates it reflectively through this constructor; if the
        // signature moved, the tombstone would fail to construct and log the error it exists to
        // prevent. Delete this test with the tombstone.
        var type = GetType("Verse.MapComponent");
        Assert.That(type, Is.Not.Null, "Verse.MapComponent no longer exists");
        Assert.That(
            type.Methods.Any(m => m.IsConstructor && m.Parameters.Count == 1
                && m.Parameters[0].ParameterType.FullName == "Verse.Map"),
            Is.True, "MapComponent(Map) no longer exists — Map.FillComponents will not construct ours");
    }

    [Test]
    public void SectionLayer_SunShadows_Regenerate_Exists()
    {
        var type = GetType("Verse.SectionLayer_SunShadows");
        Assert.That(type, Is.Not.Null, "Verse.SectionLayer_SunShadows no longer exists");
        var method = type!.Methods.SingleOrDefault(m => m.Name == "Regenerate" && m.Parameters.Count == 0);
        Assert.That(method, Is.Not.Null,
            "SectionLayer_SunShadows.Regenerate() no longer exists — Patch_ShadowMeshPerimeter's TargetMethod will fail");
    }

    [Test]
    public void SectionLayer_HasProtectedSectionField()
    {
        var type = GetType("Verse.SectionLayer");
        Assert.That(type, Is.Not.Null, "Verse.SectionLayer no longer exists");
        var field = type!.Fields.SingleOrDefault(f => f.Name == "section");
        Assert.That(field, Is.Not.Null,
            "SectionLayer.section field no longer exists — SectionLayerAccess's reflection accessor will fail");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("Verse.Section"));
    }

    [Test]
    public void Section_HasPublicMapFieldAndCellRectProperty()
    {
        var type = GetType("Verse.Section");
        Assert.That(type, Is.Not.Null, "Verse.Section no longer exists");
        var mapField = type!.Fields.SingleOrDefault(f => f.Name == "map");
        Assert.That(mapField, Is.Not.Null, "Section.map no longer exists");
        Assert.That(mapField!.IsPublic, Is.True, "Section.map is no longer public");
        var cellRectProperty = type.Properties.SingleOrDefault(p => p.Name == "CellRect");
        Assert.That(cellRectProperty, Is.Not.Null, "Section.CellRect no longer exists");
    }

    [Test]
    public void CellRect_CenterVector3_Exists()
    {
        var type = GetType("Verse.CellRect");
        Assert.That(type, Is.Not.Null);
        var property = type!.Properties.SingleOrDefault(p => p.Name == "CenterVector3");
        Assert.That(property, Is.Not.Null, "CellRect.CenterVector3 no longer exists");
    }

    [Test]
    public void MapDrawLayer_HasExpectedPublicSurface()
    {
        var type = GetType("Verse.MapDrawLayer");
        Assert.That(type, Is.Not.Null, "Verse.MapDrawLayer no longer exists");
        Assert.That(type!.Fields.Any(f => f.Name == "subMeshes" && f.IsPublic), Is.True,
            "MapDrawLayer.subMeshes no longer exists or is no longer public");
        Assert.That(type.Properties.Any(p => p.Name == "Visible"), Is.True,
            "MapDrawLayer.Visible no longer exists");
        Assert.That(type.Methods.Any(m => m.Name == "RefreshSubMeshBounds"), Is.True,
            "MapDrawLayer.RefreshSubMeshBounds no longer exists");
        var getSubMesh = type.Methods.SingleOrDefault(m => m.Name == "GetSubMesh" && m.Parameters.Count == 1);
        Assert.That(getSubMesh, Is.Not.Null,
            "MapDrawLayer.GetSubMesh(Material) no longer exists — Patch_ShadowMeshPerimeter calls it directly");
        Assert.That(getSubMesh!.IsPublic, Is.True, "MapDrawLayer.GetSubMesh is no longer public");
    }

    [Test]
    public void LayerSubMesh_HasExpectedPublicFields()
    {
        var type = GetType("Verse.LayerSubMesh");
        Assert.That(type, Is.Not.Null, "Verse.LayerSubMesh no longer exists");
        foreach (var fieldName in new[] { "finalized", "disabled", "material", "renderLayer", "mesh", "verts", "tris", "colors" })
        {
            Assert.That(type!.Fields.Any(f => f.Name == fieldName && f.IsPublic), Is.True,
                $"LayerSubMesh.{fieldName} no longer exists or is no longer public");
        }
        foreach (var methodName in new[] { "Clear", "FinalizeMesh" })
        {
            Assert.That(type!.Methods.Any(m => m.Name == methodName && m.IsPublic), Is.True,
                $"LayerSubMesh.{methodName}(MeshParts) no longer exists or is no longer public");
        }
    }

    // --- EdificeGrid / CellIndices / AltitudeLayer / ThingDef / MatBases (Patch_ShadowMeshPerimeter) ---

    [Test]
    public void Map_HasPublicEdificeGridAndCellIndicesFields()
    {
        var type = GetType("Verse.Map");
        Assert.That(type, Is.Not.Null);
        var edificeGrid = type!.Fields.SingleOrDefault(f => f.Name == "edificeGrid");
        Assert.That(edificeGrid, Is.Not.Null, "Map.edificeGrid no longer exists");
        Assert.That(edificeGrid!.IsPublic, Is.True, "Map.edificeGrid is no longer public");
        var cellIndices = type.Fields.SingleOrDefault(f => f.Name == "cellIndices");
        Assert.That(cellIndices, Is.Not.Null, "Map.cellIndices no longer exists");
        Assert.That(cellIndices!.IsPublic, Is.True, "Map.cellIndices is no longer public");
    }

    [Test]
    public void EdificeGrid_InnerArray_Exists()
    {
        var type = GetType("Verse.EdificeGrid");
        Assert.That(type, Is.Not.Null, "Verse.EdificeGrid no longer exists");
        var property = type!.Properties.SingleOrDefault(p => p.Name == "InnerArray");
        Assert.That(property, Is.Not.Null, "EdificeGrid.InnerArray no longer exists");
    }

    [Test]
    public void CellIndices_CellToIndex_ExistsForIntCoordinates()
    {
        var type = GetType("Verse.CellIndices");
        Assert.That(type, Is.Not.Null, "Verse.CellIndices no longer exists");
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "CellToIndex" && m.Parameters.Count == 2
            && m.Parameters[0].ParameterType.FullName == "System.Int32");
        Assert.That(method, Is.Not.Null, "CellIndices.CellToIndex(int, int) no longer exists");
    }

    [Test]
    public void AltitudeLayer_HasShadowsMember()
    {
        var type = GetType("Verse.AltitudeLayer");
        Assert.That(type, Is.Not.Null, "Verse.AltitudeLayer no longer exists");
        Assert.That(type!.Fields.Any(f => f.Name == "Shadows"), Is.True, "AltitudeLayer.Shadows no longer exists");
    }

    [Test]
    public void Altitudes_AltitudeFor_Exists()
    {
        var type = GetType("Verse.Altitudes");
        Assert.That(type, Is.Not.Null, "Verse.Altitudes no longer exists");
        var method = type!.Methods.SingleOrDefault(m => m.Name == "AltitudeFor" && m.Parameters.Count == 1);
        Assert.That(method, Is.Not.Null, "Altitudes.AltitudeFor(AltitudeLayer) no longer exists");
    }

    [Test]
    public void ThingDef_StaticSunShadowHeight_Exists()
    {
        var type = GetType("Verse.ThingDef");
        Assert.That(type, Is.Not.Null);
        var field = type!.Fields.SingleOrDefault(f => f.Name == "staticSunShadowHeight");
        Assert.That(field, Is.Not.Null, "ThingDef.staticSunShadowHeight no longer exists");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("System.Single"));
    }

    [Test]
    public void MatBases_SunShadow_Exists()
    {
        var type = GetType("Verse.MatBases");
        Assert.That(type, Is.Not.Null, "Verse.MatBases no longer exists");
        Assert.That(type!.Fields.Any(f => f.Name == "SunShadow" && f.IsPublic), Is.True,
            "MatBases.SunShadow no longer exists or is no longer public");
    }

    [Test]
    public void SkyManager_SkyManagerUpdate_Exists()
    {
        // Patch_PitchBlackOverlay (§7a) postfixes this to darken the light overlay after vanilla
        // composes it.
        var type = GetType("Verse.SkyManager");
        Assert.That(type, Is.Not.Null, "Verse.SkyManager no longer exists");
        Assert.That(type!.Methods.Any(m => m.Name == "SkyManagerUpdate" && m.IsPublic && m.Parameters.Count == 0),
            Is.True, "SkyManager.SkyManagerUpdate() no longer exists or changed signature — Patch_PitchBlackOverlay patches it");
    }

    [TestCase("LightOverlay")]
    [TestCase("FogOfWar")]
    public void MatBases_OverlayMaterials_Exist(string fieldName)
    {
        // Patch_PitchBlackOverlay darkens both of these material colours toward black at night.
        var type = GetType("Verse.MatBases");
        Assert.That(type, Is.Not.Null, "Verse.MatBases no longer exists");
        Assert.That(type!.Fields.Any(f => f.Name == fieldName && f.IsPublic), Is.True,
            $"MatBases.{fieldName} no longer exists or is no longer public — Patch_PitchBlackOverlay writes its .color");
    }

    [Test]
    public void GenCelestial_CelestialSunGlowPercent_Exists()
    {
        // §14 realistic mode postfixes this. It is private — deliberately so, but it is also the single
        // funnel every glow path runs through, and it takes primitives, so it is the correct seam. If
        // Ludeon renames it or changes its parameters, realistic mode silently stops applying and day
        // length reverts with no error; this test is what turns that into a loud failure.
        var type = GetType("RimWorld.GenCelestial");
        Assert.That(type, Is.Not.Null, "RimWorld.GenCelestial no longer exists");
        var method = type!.Methods.SingleOrDefault(m => m.Name == "CelestialSunGlowPercent");
        Assert.That(method, Is.Not.Null, "GenCelestial.CelestialSunGlowPercent no longer exists — Patch_SunGlow patches it");
        Assert.That(method!.Parameters.Count, Is.EqualTo(3),
            "CelestialSunGlowPercent changed arity — Patch_SunGlow's (latitude, dayOfYear, dayPercent) postfix needs revisiting");
    }

    [Test]
    public void GenCelestial_CelestialSunGlow_TileOverload_Exists()
    {
        // SunClock measures vanilla's day length by sampling this rather than re-implementing the
        // curve, which is what keeps locked mode from drifting when Ludeon retunes their sun.
        var type = GetType("RimWorld.GenCelestial");
        Assert.That(type, Is.Not.Null, "RimWorld.GenCelestial no longer exists");
        Assert.That(type!.Methods.Any(m => m.Name == "CelestialSunGlow" && m.IsPublic && m.Parameters.Count == 2),
            Is.True, "GenCelestial.CelestialSunGlow(tile, ticksAbs) no longer exists — SunClock samples it");
    }

    [Test]
    public void SkyColorSet_Shadow_Exists()
    {
        // Patch_MoonShadowColor (§6a) writes this field at night so the moon-shadow alpha has a colour
        // dark enough to be visible. Vanilla ships it near-white for night, which is the bug.
        var type = GetType("Verse.SkyColorSet");
        Assert.That(type, Is.Not.Null, "Verse.SkyColorSet no longer exists");
        Assert.That(type!.Fields.Any(f => f.Name == "shadow" && f.IsPublic), Is.True,
            "SkyColorSet.shadow no longer exists or is no longer public — Patch_MoonShadowColor writes it");
    }

    // --- §7b indoor sky occlusion (Patch_IndoorSkyOcclusion) ---

    [Test]
    public void SectionLayer_LightingOverlay_Regenerate_Exists()
    {
        // Patch_IndoorSkyOcclusion postfixes this to raise the baked per-vertex sky cover for roofed cells.
        var type = GetType("Verse.SectionLayer_LightingOverlay");
        Assert.That(type, Is.Not.Null, "Verse.SectionLayer_LightingOverlay no longer exists");
        Assert.That(type!.Methods.Any(m => m.Name == "Regenerate" && m.IsPublic && m.Parameters.Count == 0),
            Is.True, "SectionLayer_LightingOverlay.Regenerate() no longer exists or changed signature — Patch_IndoorSkyOcclusion patches it");
    }

    [Test]
    public void SectionLayer_LightingOverlay_RoofedAreaMinSkyCover_StillEqualsOurMirroredBaseline()
    {
        // The whole reason §7b exists: vanilla clamps a roofed cell's sky cover to this constant and never
        // raises it, so a sealed cave renders at ~61% of the sky. IndoorOcclusionMath mirrors the value as
        // its documented baseline, so if Ludeon ever retunes the compromise we want a loud failure here
        // rather than a silently-stale comment (and possibly a feature that is no longer needed).
        var type = GetType("Verse.SectionLayer_LightingOverlay");
        Assert.That(type, Is.Not.Null, "Verse.SectionLayer_LightingOverlay no longer exists");
        var field = type!.Fields.SingleOrDefault(f => f.Name == "RoofedAreaMinSkyCover");
        Assert.That(field, Is.Not.Null, "SectionLayer_LightingOverlay.RoofedAreaMinSkyCover no longer exists");
        Assert.That(field!.Constant, Is.EqualTo((byte)100),
            "Vanilla's roofed-cell minimum sky cover changed — IndoorOcclusionMath.VanillaRoofedMinSkyCover and its rationale need revisiting");
    }

    [Test]
    public void MapDrawLayer_GetSubMesh_Exists()
    {
        // How Patch_IndoorSkyOcclusion reaches the lighting mesh whose vertex alphas it rewrites.
        var type = GetType("Verse.MapDrawLayer");
        Assert.That(type, Is.Not.Null, "Verse.MapDrawLayer no longer exists");
        Assert.That(type!.Methods.Any(m => m.Name == "GetSubMesh" && m.IsPublic && m.Parameters.Count == 1),
            Is.True, "MapDrawLayer.GetSubMesh(Material) no longer exists or is no longer public");
    }

    [Test]
    public void Section_SizeAndBotLeft_Exist()
    {
        // Patch_IndoorSkyOcclusion recomputes the section's CellRect (a Section.Size square at botLeft,
        // clipped to the map) the same way Regenerate does, instead of reflecting its private cache.
        var type = GetType("Verse.Section");
        Assert.That(type, Is.Not.Null, "Verse.Section no longer exists");
        var size = type!.Fields.SingleOrDefault(f => f.Name == "Size");
        Assert.That(size, Is.Not.Null, "Section.Size no longer exists");
        Assert.That(size!.Constant, Is.EqualTo(17), "Section.Size changed — the recomputed section rect must follow");
        Assert.That(type.Fields.Any(f => f.Name == "botLeft" && f.IsPublic), Is.True,
            "Section.botLeft no longer exists or is no longer public");
        Assert.That(type.Fields.Any(f => f.Name == "map" && f.IsPublic), Is.True,
            "Section.map no longer exists or is no longer public");
    }

    [Test]
    public void RoofGrid_RoofAt_Exists()
    {
        // The roof *def*, not the Roofed() bool: thick roof is one of the inputs to
        // IndoorOcclusionMath.BlocksSky (a mountain buries even a wall), so the adapter needs the def.
        var type = GetType("Verse.RoofGrid");
        Assert.That(type, Is.Not.Null, "Verse.RoofGrid no longer exists");
        Assert.That(type!.Methods.Any(m => m.Name == "RoofAt" && m.IsPublic && m.Parameters.Count == 1
                && m.Parameters[0].ParameterType.Name == "IntVec3"),
            Is.True, "RoofGrid.RoofAt(IntVec3) no longer exists — Patch_IndoorSkyOcclusion reads it per cell");
    }

    [Test]
    public void RoofDef_IsThickRoof_Exists()
    {
        // Vanilla's own cover test short-circuits its roof-holder exclusion on this flag, and so does
        // ours: under a mountain even a wall cell counts as interior.
        var type = GetType("Verse.RoofDef");
        Assert.That(type, Is.Not.Null, "Verse.RoofDef no longer exists");
        Assert.That(type!.Fields.Any(f => f.Name == "isThickRoof" && f.IsPublic), Is.True,
            "RoofDef.isThickRoof no longer exists — IndoorOcclusionMath.BlocksSky's mountain exception depends on it");
    }

    [Test]
    public void ThingDef_HoldsRoof_Exists()
    {
        // The wall test. Vanilla excludes roof-holding edifices from sky cover in *both* of its vertex
        // passes, which is why an exterior wall is a boundary rather than an interior for us too; without
        // this field the feature blacks out every wall and the ground past it.
        var type = GetType("Verse.ThingDef");
        Assert.That(type, Is.Not.Null, "Verse.ThingDef no longer exists");
        Assert.That(type!.Fields.Any(f => f.Name == "holdsRoof" && f.IsPublic), Is.True,
            "ThingDef.holdsRoof no longer exists — IndoorOcclusionMath.BlocksSky's wall exclusion depends on it");
    }

    [Test]
    public void EdificeGrid_CellIndexer_Exists()
    {
        // Used for the door test, which mirrors vanilla's own (AltitudeLayer.DoorMoveable).
        var type = GetType("Verse.EdificeGrid");
        Assert.That(type, Is.Not.Null, "Verse.EdificeGrid no longer exists");
        Assert.That(type!.Methods.Any(m => m.Name == "get_Item" && m.IsPublic && m.Parameters.Count == 1
                && m.Parameters[0].ParameterType.Name == "IntVec3"),
            Is.True, "EdificeGrid's IntVec3 indexer no longer exists");
    }

    [Test]
    public void AltitudeLayer_DoorMoveable_Exists()
    {
        // Vanilla's SectionLayer_LightingOverlay identifies doors by this altitude layer, and so do we, so
        // the two can never disagree about which cell is a doorway.
        var type = GetType("Verse.AltitudeLayer");
        Assert.That(type, Is.Not.Null, "Verse.AltitudeLayer no longer exists");
        Assert.That(type!.Fields.Any(f => f.Name == "DoorMoveable"), Is.True,
            "AltitudeLayer.DoorMoveable no longer exists — the door-leak test in Patch_IndoorSkyOcclusion depends on it");
    }

    [Test]
    public void MapDrawer_WholeMapChanged_Exists()
    {
        // IndoorOcclusionRedraw calls this so a settings change rebuilds the baked meshes immediately.
        var type = GetType("Verse.MapDrawer");
        Assert.That(type, Is.Not.Null, "Verse.MapDrawer no longer exists");
        Assert.That(type!.Methods.Any(m => m.Name == "WholeMapChanged" && m.IsPublic && m.Parameters.Count == 1),
            Is.True, "MapDrawer.WholeMapChanged(ulong) no longer exists or changed signature");
    }

    [Test]
    public void MapMeshFlagDefOf_GroundGlow_Exists()
    {
        var type = GetType("RimWorld.MapMeshFlagDefOf");
        Assert.That(type, Is.Not.Null, "RimWorld.MapMeshFlagDefOf no longer exists");
        Assert.That(type!.Fields.Any(f => f.Name == "GroundGlow" && f.IsPublic), Is.True,
            "MapMeshFlagDefOf.GroundGlow no longer exists — IndoorOcclusionRedraw dirties this flag");
    }

    // --- Room / GridsUtility / relevantChangeTypes (§15 eaves — EaveShadowGrid,
    //     Patch_IndoorSkyOcclusion, Patch_ShadowRoofInvalidation) ---

    [Test]
    public void Room_UsesOutdoorTemperature_Exists()
    {
        // The single predicate §15 is built on: it separates a porch from a sealed room for both the
        // shadow mesh and §7b's occlusion. If it ever goes away, every roofed cell silently becomes
        // "enclosed" again and porches go black at noon — a look regression nothing else here catches.
        var type = GetType("Verse.Room");
        Assert.That(type, Is.Not.Null, "Verse.Room no longer exists");
        var property = type!.Properties.SingleOrDefault(p => p.Name == "UsesOutdoorTemperature");
        Assert.That(property, Is.Not.Null,
            "Room.UsesOutdoorTemperature no longer exists — EavesMath's callers depend on it");
        Assert.That(property!.PropertyType.FullName, Is.EqualTo("System.Boolean"),
            "Room.UsesOutdoorTemperature no longer returns bool");
    }

    [Test]
    public void RegionGrid_GetValidRegionAtNoRebuild_Exists()
    {
        // RoomLookup deliberately avoids GridsUtility.GetRoom, whose GetValidRegionAt forces a
        // region/room rebuild from inside the render path and log-warns per call while the updater is
        // disabled. This is the no-rebuild variant it uses instead; losing it would silently push us
        // back onto the rebuilding path only if someone rewrote RoomLookup, so pin it here.
        var type = GetType("Verse.RegionGrid");
        Assert.That(type, Is.Not.Null, "Verse.RegionGrid no longer exists");
        Assert.That(type!.Methods.Any(m => m.Name == "GetValidRegionAt_NoRebuild" && m.IsPublic
                && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.Name == "IntVec3"),
            Is.True, "RegionGrid.GetValidRegionAt_NoRebuild(IntVec3) no longer exists — RoomLookup depends on it");
    }

    [Test]
    public void RegionToRoomWalk_Exists()
    {
        // RoomLookup mirrors RegionAndRoomQuery.RoomAt's Region -> District -> Room walk by hand,
        // because the public helper only exists on the rebuilding path.
        Assert.That(GetType("Verse.Map")!.Fields.Any(f => f.Name == "regionGrid" && f.IsPublic), Is.True,
            "Map.regionGrid no longer exists or is no longer public");
        Assert.That(GetType("Verse.Region")!.Properties.Any(p => p.Name == "District"), Is.True,
            "Region.District no longer exists");
        Assert.That(GetType("Verse.District")!.Properties.Any(p => p.Name == "Room"), Is.True,
            "District.Room no longer exists");
    }

    [Test]
    public void MapDrawLayer_RelevantChangeTypes_IsPublicUlong()
    {
        // Patch_ShadowRoofInvalidation ORs MapMeshFlagDefOf.Roofs into this so a roof change rebuilds
        // the sun-shadow mesh. A type change would break the OR at compile time; a visibility change
        // would break it at runtime, which is what this pins.
        var type = GetType("Verse.MapDrawLayer");
        Assert.That(type, Is.Not.Null, "Verse.MapDrawLayer no longer exists");
        var field = type!.Fields.SingleOrDefault(f => f.Name == "relevantChangeTypes");
        Assert.That(field, Is.Not.Null, "MapDrawLayer.relevantChangeTypes no longer exists");
        Assert.That(field!.IsPublic, Is.True, "MapDrawLayer.relevantChangeTypes is no longer public");
        Assert.That(field.FieldType.FullName, Is.EqualTo("System.UInt64"),
            "MapDrawLayer.relevantChangeTypes is no longer a ulong");
    }

    [Test]
    public void SectionLayer_SunShadows_ConstructorStillSetsRelevantChangeTypes()
    {
        // Patch_ShadowRoofInvalidation targets this constructor. It is also where vanilla assigns
        // relevantChangeTypes, so if that assignment ever moves, our Postfix would be OR-ing into a
        // field something else overwrites afterwards.
        var type = GetType("Verse.SectionLayer_SunShadows");
        Assert.That(type, Is.Not.Null, "Verse.SectionLayer_SunShadows no longer exists");
        var ctor = type!.Methods.SingleOrDefault(m => m.IsConstructor && m.Parameters.Count == 1
            && m.Parameters[0].ParameterType.Name == "Section");
        Assert.That(ctor, Is.Not.Null,
            "SectionLayer_SunShadows(Section) no longer exists — Patch_ShadowRoofInvalidation's TargetMethod will fail");
        Assert.That(ctor!.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "relevantChangeTypes"),
            Is.True,
            "SectionLayer_SunShadows(Section) no longer assigns relevantChangeTypes — find where the subscription moved");
    }

    [Test]
    public void MapMeshFlagDefOf_Roofs_And_Buildings_Exist()
    {
        var type = GetType("RimWorld.MapMeshFlagDefOf");
        Assert.That(type, Is.Not.Null, "RimWorld.MapMeshFlagDefOf no longer exists");
        Assert.That(type!.Fields.Any(f => f.Name == "Roofs" && f.IsPublic), Is.True,
            "MapMeshFlagDefOf.Roofs no longer exists — Patch_ShadowRoofInvalidation subscribes to it");
        Assert.That(type.Fields.Any(f => f.Name == "Buildings" && f.IsPublic), Is.True,
            "MapMeshFlagDefOf.Buildings no longer exists — EaveShadowRedraw dirties this flag");
    }

    [Test]
    public void RoofGrid_SetRoof_StillDirtiesTheMapMesh()
    {
        // Why Patch_ShadowRoofInvalidation is needed at all: SetRoof dirties Roofs and nothing else, so
        // a layer subscribed only to Buildings never hears about a new roof. If Ludeon ever widens
        // this, our patch becomes redundant rather than wrong — but we still want to know.
        var type = GetType("Verse.RoofGrid");
        Assert.That(type, Is.Not.Null, "Verse.RoofGrid no longer exists");
        var setRoof = type!.Methods.SingleOrDefault(m => m.Name == "SetRoof");
        Assert.That(setRoof, Is.Not.Null, "RoofGrid.SetRoof no longer exists");
        Assert.That(setRoof!.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "MapMeshDirty"),
            Is.True, "RoofGrid.SetRoof no longer calls MapMeshDirty — roof changes may no longer dirty any section");
    }

    [Test]
    public void BiomeDef_DisableSkyLighting_Exists()
    {
        // Both §7a and §7b bail out on these biomes (the Odyssey undercave), where vanilla deliberately
        // switches the sky overlay off entirely.
        var type = GetType("RimWorld.BiomeDef");
        Assert.That(type, Is.Not.Null, "RimWorld.BiomeDef no longer exists");
        Assert.That(type!.Fields.Any(f => f.Name == "disableSkyLighting" && f.IsPublic), Is.True,
            "BiomeDef.disableSkyLighting no longer exists — the undercave guards depend on it");
    }

    [Test]
    public void ShaderPropertyIDs_MapSunLightDirection_Exists()
    {
        var type = GetType("Verse.ShaderPropertyIDs");
        Assert.That(type, Is.Not.Null, "Verse.ShaderPropertyIDs no longer exists");
        var field = type!.Fields.SingleOrDefault(f => f.Name == "MapSunLightDirection");
        Assert.That(field, Is.Not.Null, "ShaderPropertyIDs.MapSunLightDirection no longer exists");
    }

    // --- GameCondition_NoSunlight / GameCondition / GameConditionDefOf (Patch_EclipseDarkening) ---

    [Test]
    public void GameConditionNoSunlight_SkyTargetLerpFactor_Exists()
    {
        var type = GetType("RimWorld.GameCondition_NoSunlight");
        Assert.That(type, Is.Not.Null, "RimWorld.GameCondition_NoSunlight no longer exists");
        var method = type!.Methods.SingleOrDefault(m => m.Name == "SkyTargetLerpFactor" && m.Parameters.Count == 1);
        Assert.That(method, Is.Not.Null,
            "GameCondition_NoSunlight.SkyTargetLerpFactor(Map) no longer exists — the eclipse-darkening postfix targets it");
        Assert.That(method!.ReturnType.FullName, Is.EqualTo("System.Single"),
            "GameCondition_NoSunlight.SkyTargetLerpFactor no longer returns float");
    }

    [Test]
    public void GameCondition_ProgressMembers_Exist()
    {
        // Patch_EclipseDarkening (and EclipseCoverageProbe) derive eclipse progress from these.
        var type = GetType("RimWorld.GameCondition");
        Assert.That(type, Is.Not.Null, "RimWorld.GameCondition no longer exists");
        Assert.That(type!.Properties.Any(p => p.Name == "TicksPassed"), Is.True,
            "GameCondition.TicksPassed no longer exists");
        Assert.That(type.Properties.Any(p => p.Name == "TicksLeft"), Is.True,
            "GameCondition.TicksLeft no longer exists");
        Assert.That(type.Fields.Any(f => f.Name == "def"), Is.True,
            "GameCondition.def no longer exists");
    }

    [Test]
    public void GameConditionDefOf_Eclipse_Exists()
    {
        // The postfix gates on this def so it only reshapes the real Eclipse event (not the
        // Royalty SunBlocker machine, which shares the GameCondition_NoSunlight class).
        var type = GetType("RimWorld.GameConditionDefOf");
        Assert.That(type, Is.Not.Null, "RimWorld.GameConditionDefOf no longer exists");
        var field = type!.Fields.SingleOrDefault(f => f.Name == "Eclipse");
        Assert.That(field, Is.Not.Null, "GameConditionDefOf.Eclipse no longer exists");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("Verse.GameConditionDef"),
            "GameConditionDefOf.Eclipse is no longer a GameConditionDef");
    }

    // --- GameConditionManager.ConditionIsActive / DefDatabase.GetNamedSilentFail (BloodMoon soft-dep) ---

    [Test]
    public void GameConditionManager_ConditionIsActive_Exists()
    {
        var type = GetType("RimWorld.GameConditionManager");
        Assert.That(type, Is.Not.Null, "RimWorld.GameConditionManager no longer exists");
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "ConditionIsActive" && m.Parameters.Count == 1
            && m.Parameters[0].ParameterType.Name == "GameConditionDef");
        Assert.That(method, Is.Not.Null,
            "GameConditionManager.ConditionIsActive(GameConditionDef) no longer exists");
        Assert.That(method!.ReturnType.FullName, Is.EqualTo("System.Boolean"));
    }

    [Test]
    public void DefDatabase_GetNamedSilentFail_Exists()
    {
        var type = GetType("Verse.DefDatabase`1");
        Assert.That(type, Is.Not.Null, "Verse.DefDatabase<T> no longer exists");
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "GetNamedSilentFail" && m.Parameters.Count == 1);
        Assert.That(method, Is.Not.Null,
            "DefDatabase<T>.GetNamedSilentFail(string) no longer exists — BloodMoon resolves its "
            + "soft-dependency condition def through it");
    }

    // --- SkyManager glow accessors (§7 night radiance writes through these) ---

    [Test]
    public void SkyManager_CurSkyGlow_ExistsAsFloatProperty()
    {
        var type = GetType("Verse.SkyManager");
        Assert.That(type, Is.Not.Null, "Verse.SkyManager no longer exists");
        var property = type!.Properties.SingleOrDefault(p => p.Name == "CurSkyGlow");
        Assert.That(property, Is.Not.Null, "SkyManager.CurSkyGlow no longer exists — the floor reads it");
        Assert.That(property!.PropertyType.FullName, Is.EqualTo("System.Single"),
            "SkyManager.CurSkyGlow no longer returns a float");
        Assert.That(property.GetMethod, Is.Not.Null, "SkyManager.CurSkyGlow no longer has a getter");
    }

    [Test]
    public void SkyManager_ForceSetCurSkyGlow_ExistsTakingFloat()
    {
        var type = GetType("Verse.SkyManager");
        Assert.That(type, Is.Not.Null, "Verse.SkyManager no longer exists");
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "ForceSetCurSkyGlow" && m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.FullName == "System.Single");
        Assert.That(method, Is.Not.Null,
            "SkyManager.ForceSetCurSkyGlow(float) no longer exists — the floor writes the lifted glow through it");
    }

    // --- Natural eclipse trigger (§10a): the vanilla members GameComponent_NaturalEclipse and
    //     Patch_SuppressRandomEclipse fire/suppress a real Eclipse through. (GameConditionDefOf.Eclipse
    //     itself is already asserted above by GameConditionDefOf_Eclipse_Exists.) ---

    [Test]
    public void GameConditionMaker_MakeCondition_Exists()
    {
        // GameComponent_NaturalEclipse builds the timed Eclipse condition with MakeCondition(def, ticks).
        var type = GetType("RimWorld.GameConditionMaker");
        Assert.That(type, Is.Not.Null, "RimWorld.GameConditionMaker no longer exists");
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "MakeCondition" && m.IsStatic && m.Parameters.Count == 2
            && m.Parameters[0].ParameterType.Name == "GameConditionDef"
            && m.Parameters[1].ParameterType.FullName == "System.Int32");
        Assert.That(method, Is.Not.Null, "GameConditionMaker.MakeCondition(GameConditionDef, int) no longer exists");
        Assert.That(method!.ReturnType.FullName, Is.EqualTo("RimWorld.GameCondition"));
    }

    [Test]
    public void GameConditionManager_RegisterCondition_Exists()
    {
        // The trigger registers the freshly-made Eclipse on each map's condition manager.
        var type = GetType("RimWorld.GameConditionManager");
        Assert.That(type, Is.Not.Null, "RimWorld.GameConditionManager no longer exists");
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "RegisterCondition" && m.Parameters.Count == 1
            && m.Parameters[0].ParameterType.Name == "GameCondition");
        Assert.That(method, Is.Not.Null, "GameConditionManager.RegisterCondition(GameCondition) no longer exists");
    }

    [Test]
    public void IncidentDefOf_Eclipse_Exists()
    {
        // Patch_SuppressRandomEclipse vetoes exactly this incident while natural mode is on.
        var type = GetType("RimWorld.IncidentDefOf");
        Assert.That(type, Is.Not.Null, "RimWorld.IncidentDefOf no longer exists");
        var field = type!.Fields.SingleOrDefault(f => f.Name == "Eclipse" && f.IsStatic);
        Assert.That(field, Is.Not.Null, "IncidentDefOf.Eclipse no longer exists");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("RimWorld.IncidentDef"));
    }

    [Test]
    public void IncidentWorker_CanFireNowAndDef_Exist()
    {
        // Patch_SuppressRandomEclipse prefixes CanFireNow(IncidentParms) and reads the worker's `def`.
        var type = GetType("RimWorld.IncidentWorker");
        Assert.That(type, Is.Not.Null, "RimWorld.IncidentWorker no longer exists");
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "CanFireNow" && m.Parameters.Count == 1
            && m.Parameters[0].ParameterType.Name == "IncidentParms");
        Assert.That(method, Is.Not.Null, "IncidentWorker.CanFireNow(IncidentParms) no longer exists");
        Assert.That(method!.ReturnType.FullName, Is.EqualTo("System.Boolean"));
        var def = type.Fields.SingleOrDefault(f => f.Name == "def");
        Assert.That(def, Is.Not.Null, "IncidentWorker.def no longer exists");
        Assert.That(def!.FieldType.FullName, Is.EqualTo("RimWorld.IncidentDef"));
    }

    // --- §15b eave shade (SectionLayer_EaveShade, EaveShadeOverlay, Patch_EaveShade) ---

    [Test]
    public void SectionLayerGeometryMaker_Solid_MakeBaseGeometry_Exists()
    {
        // SectionLayer_EaveShade builds its quads with this — nine vertices per cell, in the order
        // the layer's colour loop assumes. A signature change here is a silently mis-shaded mesh.
        var type = GetType("Verse.SectionLayerGeometryMaker_Solid");
        Assert.That(type, Is.Not.Null, "Verse.SectionLayerGeometryMaker_Solid no longer exists");
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "MakeBaseGeometry" && m.Parameters.Count == 3
            && m.Parameters[0].ParameterType.Name == "Section"
            && m.Parameters[1].ParameterType.Name == "LayerSubMesh"
            && m.Parameters[2].ParameterType.Name == "AltitudeLayer");
        Assert.That(method, Is.Not.Null,
            "MakeBaseGeometry(Section, LayerSubMesh, AltitudeLayer) no longer exists");
    }

    [Test]
    public void ShaderDatabase_Transparent_Exists()
    {
        // EaveShadeOverlay's material is built on it, and the arithmetic in EaveShadeMath assumes
        // exactly this blend: alpha-blending black is scene * (1 - a), a multiply.
        var type = GetType("Verse.ShaderDatabase");
        Assert.That(type, Is.Not.Null, "Verse.ShaderDatabase no longer exists");
        var field = type!.Fields.SingleOrDefault(f => f.Name == "Transparent" && f.IsStatic);
        Assert.That(field, Is.Not.Null, "ShaderDatabase.Transparent no longer exists");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("UnityEngine.Shader"));
    }

    [Test]
    public void BiomeDef_DisableShadows_Exists()
    {
        // Patch_EaveShade holds the shade at zero for a biome with no shadows at all, the same flag
        // SectionLayer_SunShadows.Visible checks. Losing it would shade porches on a map whose cast
        // shadows do not exist — the exact mismatch the subsystem removes, inverted.
        var type = GetType("RimWorld.BiomeDef");
        Assert.That(type, Is.Not.Null, "RimWorld.BiomeDef no longer exists");
        var field = type!.Fields.SingleOrDefault(f => f.Name == "disableShadows");
        Assert.That(field, Is.Not.Null, "BiomeDef.disableShadows no longer exists");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("System.Boolean"));
    }

    // --- §16 section-layer fan-out ---

    // DESIGN.md §16 tabulates how many section layers regenerate per map-mesh dirty flag, and the
    // whole point of writing it down was that the number lives in the interaction between files and
    // is invisible from any one of them. The vanilla half of that table is just as invisible: it
    // comes from Ludeon's constructors, so a RimWorld update that adds or drops one subscriber moves
    // our documented baseline without touching a line of our code. This reads the subscriptions back
    // out of the shipped Assembly-CSharp so the table fails loudly instead of going quietly stale.
    //
    // Deliberately asserts the exact set of type names rather than a count: "three layers take
    // Roofs" stays true if Ludeon swaps one for another, and §16's per-flag microsecond totals are
    // per-layer sums that such a swap would invalidate.
    [TestCase("Roofs", "Verse.SectionLayer_IndoorMask,Verse.SectionLayer_LightingOverlay,RimWorld.SectionLayer_GravshipHull")]
    [TestCase("GroundGlow", "Verse.SectionLayer_Darkness,Verse.SectionLayer_LightingOverlay")]
    [TestCase("Buildings", "Verse.SectionLayer_BuildingsDamage,Verse.SectionLayer_EdgeShadows,Verse.SectionLayer_IndoorMask,Verse.SectionLayer_SunShadows,RimWorld.SectionLayer_GravshipHull,RimWorld.SectionLayer_SubstructureProps")]
    public void VanillaSectionLayers_SubscribedTo_MatchDesignSection16(string flagName, string expectedCsv)
    {
        var expected = expectedCsv.Split(',').OrderBy(n => n).ToArray();
        var actual = VanillaSectionLayers()
            .Where(t => SubscribesTo(t, flagName))
            .Select(t => t.FullName)
            .OrderBy(n => n)
            .ToArray();

        Assert.That(actual, Is.EqualTo(expected),
            $"The set of vanilla section layers subscribed to MapMeshFlagDefOf.{flagName} has "
            + "changed. DESIGN.md §16's flag-to-layers table and its per-flag timings are now wrong "
            + "— update both rather than this expectation alone.");
    }

    // Every non-abstract SectionLayer subclass in Assembly-CSharp, which is the set Verse.Section's
    // constructor instantiates (typeof(SectionLayer).AllSubclassesNonAbstract()). Section's own
    // exclusions are runtime conditions — biome, map info, DLC — not type-level ones, so none of
    // them can be applied here and §16 names them in prose instead.
    private IEnumerable<TypeDefinition> VanillaSectionLayers()
    {
        var byName = _module.Types.ToDictionary(t => t.FullName);

        bool DerivesFromSectionLayer(TypeDefinition type)
        {
            var current = type.BaseType;
            while (current != null)
            {
                if (current.FullName == "Verse.SectionLayer")
                    return true;

                current = byName.TryGetValue(current.FullName, out var resolved) ? resolved.BaseType : null;
            }

            return false;
        }

        return _module.Types.Where(t => !t.IsAbstract && DerivesFromSectionLayer(t));
    }

    // A layer declares its subscription as `relevantChangeTypes = (ulong)MapMeshFlagDefOf.X | ...`
    // in its constructor, so the flags it takes are the MapMeshFlagDefOf fields that constructor
    // loads. Reading the IL rather than running anything keeps this offline like the rest of the
    // file.
    private static bool SubscribesTo(TypeDefinition layer, string flagName) =>
        layer.Methods
            .Where(m => m.IsConstructor && m.HasBody)
            .SelectMany(m => m.Body.Instructions)
            .Any(i => i.Operand is FieldReference field
                && field.DeclaringType.FullName == "RimWorld.MapMeshFlagDefOf"
                && field.Name == flagName);

    // --- helpers ---

    private TypeDefinition? GetType(string fullName) =>
        _module.Types.FirstOrDefault(t => t.FullName == fullName);

    private TypeDefinition? GetNestedType(string declaringTypeFullName, string nestedTypeName) =>
        GetType(declaringTypeFullName)?.NestedTypes.FirstOrDefault(t => t.Name == nestedTypeName);
}
