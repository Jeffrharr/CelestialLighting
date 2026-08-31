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
    public void BiomeDef_HasInVacuum()
    {
        // The single discriminator behind the whole §18 vacuum epic (Source/Vacuum.cs). Two things
        // matter about it and both are asserted here.
        //
        // First, that it exists at all: if it is renamed or moved, every vacuum branch in the mod
        // silently stops firing and space maps quietly go back to rendering sunsets and auroras —
        // a failure with no error, no log line, and nothing visibly broken on any surface map.
        //
        // Second, that it lives on BASE RimWorld.BiomeDef rather than on an Odyssey-only type. That
        // is the entire reason Vacuum.InVacuumForMap is a plain field read with no ModsConfig
        // .OdysseyActive guard and no soft-reference plumbing — all DLC code ships in the base
        // assembly, so this compiles and evaluates to false with Odyssey uninstalled. Because we
        // resolve BiomeDef out of Assembly-CSharp here, this test failing to find the type is
        // itself the signal that the assumption stopped holding.
        var type = GetType("RimWorld.BiomeDef");
        Assert.That(type, Is.Not.Null, "RimWorld.BiomeDef no longer exists");
        var field = type!.Fields.SingleOrDefault(f => f.Name == "inVacuum" && f.IsPublic);
        Assert.That(field, Is.Not.Null,
            "BiomeDef.inVacuum no longer exists or is no longer public — every §18 vacuum branch "
            + "(twilight, sky colour temperature, aurora tint) is gated on it");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("System.Boolean"),
            "BiomeDef.inVacuum changed shape — Vacuum.InVacuumForMap returns it directly as a bool");
    }

    // --- Verse.SnowGrid and Map.Area (§21, SurfaceBuildup) ---

    [Test]
    public void SnowGrid_HasTotalDepth_AsAMaintainedRunningTotal()
    {
        // §21's whole-map ambient term reads map.snowGrid.TotalDepth once per sky update, and the
        // justification for doing that per frame is that TotalDepth is a PROPERTY over a maintained
        // `totalDepth` accumulator (incremented inside AddDepth/SetDepth), not a grid scan. Two
        // things are pinned here and they are pinned separately on purpose.
        //
        // First that the member exists at all: SurfaceBuildup would not compile without it, so a
        // rename is caught by the build — but this test names the reason, which the build does not.
        //
        // Second, and the one the build CANNOT catch: that it is still cheap. If Ludeon ever
        // reimplements TotalDepth as a loop over the depth grid, nothing breaks, nothing errors, and
        // §21 quietly starts scanning the whole map twice per frame on every map with weather. The
        // backing-field assertion is the cheapest available proxy for "this is still an accumulator";
        // if it fails, re-decompile Verse.SnowGrid before assuming the read is still free.
        var type = GetType("Verse.SnowGrid");
        Assert.That(type, Is.Not.Null, "Verse.SnowGrid no longer exists");

        var totalDepth = type!.Properties.SingleOrDefault(p => p.Name == "TotalDepth");
        Assert.That(totalDepth, Is.Not.Null,
            "SnowGrid.TotalDepth no longer exists — §21's whole-map buildup average reads it");
        Assert.That(totalDepth!.PropertyType.FullName, Is.EqualTo("System.Single"),
            "SnowGrid.TotalDepth changed shape — SurfaceBuildup divides it by Map.Area as a float");

        Assert.That(
            type.Fields.Any(f => f.Name == "totalDepth" && !f.IsPublic),
            Is.True,
            "SnowGrid no longer keeps a private totalDepth accumulator — TotalDepth may now be a "
            + "per-call grid scan, which would make §21's per-frame read expensive with no visible "
            + "symptom. Re-decompile Verse.SnowGrid before trusting SurfaceBuildup's cost claim.");
    }

    [Test]
    public void SnowGrid_HasMaxDepthOfOne()
    {
        // §21 treats the buildup depth as ALREADY NORMALIZED — BuildupSurfaceAlbedo clamps its input
        // to [0,1] and ramps across that range with no scaling constant of its own. That is only
        // correct while SnowGrid.MaxDepth is 1. If it ever changes, the ramp silently compresses into
        // a fraction of its range (or saturates), and a snowed-in map stops reaching fresh-snow
        // albedo without anything failing.
        var type = GetType("Verse.SnowGrid");
        Assert.That(type, Is.Not.Null, "Verse.SnowGrid no longer exists");

        var maxDepth = type!.Fields.SingleOrDefault(f => f.Name == "MaxDepth" && f.HasConstant);
        Assert.That(maxDepth, Is.Not.Null, "SnowGrid.MaxDepth is no longer a compile-time constant");
        Assert.That(maxDepth!.Constant, Is.EqualTo(1f),
            "SnowGrid.MaxDepth is no longer 1 — §21 assumes buildup depth is already normalized");
    }

    [Test]
    public void SnowGrid_HasGetDepth_ForTheDeferredPerCellHalf()
    {
        // NOT called by anything today, and pinned anyway. §21 shipped the whole-map ambient half and
        // deliberately deferred the per-cell shadow-fill half (DESIGN.md §21, on §16's
        // section-invalidation cost ledger); GetDepth(IntVec3) is the read that half would need, so
        // this test is the standing check that the deferred option is still available before someone
        // plans against it. A failure here is a design signal, not a bug — it means the per-cell
        // route would have to be re-costed rather than picked up where it was left.
        var type = GetType("Verse.SnowGrid");
        Assert.That(type, Is.Not.Null, "Verse.SnowGrid no longer exists");

        var getDepth = type!.Methods.SingleOrDefault(
            m => m.Name == "GetDepth" && m.Parameters.Count == 1);
        Assert.That(getDepth, Is.Not.Null, "SnowGrid.GetDepth(IntVec3) no longer exists");
        Assert.That(getDepth!.Parameters[0].ParameterType.FullName, Is.EqualTo("Verse.IntVec3"));
        Assert.That(getDepth.ReturnType.FullName, Is.EqualTo("System.Single"));
    }

    [Test]
    public void Map_HasSnowGridAndArea()
    {
        // The two live reads SurfaceBuildup makes. Area is the divisor that turns TotalDepth into a
        // mean depth, and it must stay a cell COUNT (Size.x * Size.z) rather than becoming something
        // scaled, or the mean silently changes units and every gain moves with it.
        var type = GetType("Verse.Map");
        Assert.That(type, Is.Not.Null, "Verse.Map no longer exists");

        var snowGrid = type!.Fields.SingleOrDefault(f => f.Name == "snowGrid" && f.IsPublic);
        Assert.That(snowGrid, Is.Not.Null, "Map.snowGrid no longer exists or is no longer public");
        Assert.That(snowGrid!.FieldType.FullName, Is.EqualTo("Verse.SnowGrid"));

        var area = type.Properties.SingleOrDefault(p => p.Name == "Area");
        Assert.That(area, Is.Not.Null, "Map.Area no longer exists");
        Assert.That(area!.PropertyType.FullName, Is.EqualTo("System.Int32"),
            "Map.Area is no longer an integer cell count — §21 divides TotalDepth by it");
    }

    [Test]
    public void Map_HasSandGrid_ShapedLikeSnowGrid()
    {
        // §21's generalization claim, pinned rather than merely asserted in a comment. RimWorld 1.6
        // generalized snow into "weather buildup" and Odyssey's sand rides the same shape — a
        // separate grid on Map, with the same TotalDepth accumulator. That is what makes
        // AlbedoCavityMath keying on ALBEDO rather than on snow depth pay off: the sand arm is one
        // adapter read plus AlbedoCavityMath.SandAlbedo, with no new maths.
        //
        // Note the correction this test records. Sand is NOT the same grid as snow reached through
        // WeatherBuildupUtility — SnowGrid.GetCategory and SandGrid both route through
        // WeatherBuildupUtility.GetBuildupCategory, but the DEPTHS live in two separate grids. A
        // future sand arm therefore reads map.sandGrid, not a category off the snow grid.
        var map = GetType("Verse.Map");
        Assert.That(map, Is.Not.Null, "Verse.Map no longer exists");
        Assert.That(map!.Fields.Any(f => f.Name == "sandGrid" && f.IsPublic), Is.True,
            "Map.sandGrid no longer exists — §21's deferred sand arm would need re-planning");

        var sandGrid = GetType("Verse.SandGrid");
        Assert.That(sandGrid, Is.Not.Null, "Verse.SandGrid no longer exists");
        Assert.That(sandGrid!.Properties.Any(p => p.Name == "TotalDepth"), Is.True,
            "SandGrid.TotalDepth no longer exists — the sand arm assumed SnowGrid's shape");
    }

    [Test]
    public void WeatherBuildupUtility_StillDrawsTheDustingBoundaryWhereAlbedoCavityMathDoes()
    {
        // AlbedoCavityMath.ShallowBuildupDepth (0.25) is the knee between its optical-cover segment
        // and its fresh-versus-settled segment, and it is deliberately vanilla's own Dusting/Thin
        // boundary rather than a number of ours — "you can no longer see the dirt" is the same
        // threshold in both models, so the two agree by construction.
        //
        // Reading the IL for the literal is the only way to pin a value buried in a chain of
        // comparisons, and it is worth pinning: if Ludeon retunes the boundary, our knee should move
        // with it rather than drift into disagreeing about what a dusting is.
        var type = GetType("Verse.WeatherBuildupUtility");
        Assert.That(type, Is.Not.Null, "Verse.WeatherBuildupUtility no longer exists");

        var getCategory = type!.Methods.SingleOrDefault(m => m.Name == "GetBuildupCategory");
        Assert.That(getCategory, Is.Not.Null, "WeatherBuildupUtility.GetBuildupCategory no longer exists");
        Assert.That(
            getCategory!.Body.Instructions.Any(i => i.Operand is float f && f == 0.25f),
            Is.True,
            "WeatherBuildupUtility.GetBuildupCategory no longer draws a boundary at 0.25 — "
            + "AlbedoCavityMath.ShallowBuildupDepth was chosen to match vanilla's Dusting/Thin knee");
    }

    // --- §18d's two anchor-provenance guards (issue #32) ---
    //
    // These do not pin anything the mod CALLS. They pin the two facts that justify §18d picking its
    // orbit altitude by hand instead of reading one off the game, which is the single least
    // comfortable decision in the subsystem. If either fact stops holding, the honest response is to
    // stop anchoring and start reading — so the assertions are written to fail loudly in exactly that
    // case rather than to protect a call site.

    [Test]
    public void PlanetLayerDef_ElevationString_IsStillOnlyADisplayString()
    {
        // ANCHOR 2's provenance. §18d takes its 200 km from Odyssey's OrbitLayer elevationString,
        // and calls it an anchor rather than a lookup precisely because this field is a
        // [MustTranslate] format string for the world-map UI — "{0}m" in Core, "200km" in Odyssey.
        // A string cannot be a simulation quantity, which is what makes the hand-picked value honest
        // rather than lazy.
        //
        // If this ever becomes numeric, RimWorld has grown a real altitude and §18d should derive
        // from it instead of anchoring. That is the failure this test is for.
        var type = GetType("RimWorld.PlanetLayerDef");
        Assert.That(type, Is.Not.Null, "RimWorld.PlanetLayerDef no longer exists");
        var field = type!.Fields.SingleOrDefault(f => f.Name == "elevationString" && f.IsPublic);
        Assert.That(field, Is.Not.Null,
            "PlanetLayerDef.elevationString no longer exists — §18d's stated source for its 200 km "
            + "anchor is gone and DESIGN.md §18d needs revisiting");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("System.String"),
            "PlanetLayerDef.elevationString is no longer a string — if RimWorld now carries a real "
            + "numeric layer altitude, §18d should derive its geometry from it rather than anchor "
            + "to a hand-picked 200 km");
    }

    [Test]
    public void PlanetLayerSettings_ExtraCameraAltitude_IsStillACameraParameterWeDoNotRead()
    {
        // The trap §18d explicitly declines to fall into, recorded so nobody "fixes" the anchor by
        // wiring this up. extraCameraAltitude looks like the altitude field the subsystem wants, and
        // it is not one: it lives in a struct of pure view parameters (origin, radius, viewAngle,
        // subdivisions, backgroundWorldCameraOffset), and Odyssey's OrbitLayer sets it to 300 against
        // a sphere radius of 130 — over two planetary radii of pull-back. Read as a physical altitude
        // that is roughly 15000 km, not 200.
        //
        // Asserting the neighbours is the point: a lone float named "altitude" proves nothing, but a
        // float sitting next to the camera framing parameters is self-evidently one of them.
        var type = GetType("RimWorld.PlanetLayerSettings");
        Assert.That(type, Is.Not.Null, "RimWorld.PlanetLayerSettings no longer exists");

        var altitude = type!.Fields.SingleOrDefault(f => f.Name == "extraCameraAltitude" && f.IsPublic);
        Assert.That(altitude, Is.Not.Null,
            "PlanetLayerSettings.extraCameraAltitude no longer exists — DESIGN.md §18d names it as "
            + "the camera parameter it deliberately does not use");
        Assert.That(altitude!.FieldType.FullName, Is.EqualTo("System.Single"));

        foreach (var neighbour in new[] { "radius", "viewAngle", "backgroundWorldCameraOffset" })
        {
            Assert.That(type.Fields.Any(f => f.Name == neighbour && f.IsPublic), Is.True,
                $"PlanetLayerSettings.{neighbour} no longer exists — the evidence that "
                + "extraCameraAltitude is a camera framing parameter rather than a physical "
                + "altitude has weakened, so §18d's rejection of it needs re-checking");
        }
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

    [Test]
    public void GameConditionDefOf_Aurora_Exists()
    {
        // The second of AuroraConditions' two drivers. Unlike SolarFlare this one IS on the DefOf,
        // so we resolve it there rather than by defName.
        var type = GetType("RimWorld.GameConditionDefOf");
        Assert.That(type, Is.Not.Null, "RimWorld.GameConditionDefOf no longer exists");
        var field = type!.Fields.SingleOrDefault(f => f.Name == "Aurora" && f.IsStatic);
        Assert.That(field, Is.Not.Null, "GameConditionDefOf.Aurora no longer exists");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("Verse.GameConditionDef"),
            "GameConditionDefOf.Aurora is no longer a GameConditionDef");
    }

    [Test]
    public void GameConditionAurora_CurrentColor_Exists()
    {
        // AuroraConditions.TintColorFor borrows this so an aurora event tints toward whichever entry
        // of vanilla's own eight-colour palette is currently up, instead of our flare shimmer.
        var type = GetType("RimWorld.GameCondition_Aurora");
        Assert.That(type, Is.Not.Null, "RimWorld.GameCondition_Aurora no longer exists");
        var prop = type!.Properties.SingleOrDefault(p => p.Name == "CurrentColor");
        Assert.That(prop, Is.Not.Null, "GameCondition_Aurora.CurrentColor no longer exists");
        Assert.That(prop!.PropertyType.FullName, Is.EqualTo("UnityEngine.Color"));
        Assert.That(prop.GetMethod?.IsPublic, Is.True, "GameCondition_Aurora.CurrentColor is no longer public");
    }

    [Test]
    public void GameConditionManager_IsAlwaysDarkOutside_Exists()
    {
        // ActiveTintDriver stands down on always-dark maps, mirroring GameCondition_Aurora's own
        // IsAlwaysDarkOutside guard, so we never paint an aurora onto a rock ceiling.
        var type = GetType("RimWorld.GameConditionManager");
        Assert.That(type, Is.Not.Null, "RimWorld.GameConditionManager no longer exists");
        var prop = type!.Properties.SingleOrDefault(p => p.Name == "IsAlwaysDarkOutside");
        Assert.That(prop, Is.Not.Null, "GameConditionManager.IsAlwaysDarkOutside no longer exists");
        Assert.That(prop!.PropertyType.FullName, Is.EqualTo("System.Boolean"));
    }

    [Test]
    public void GameConditionNoSunlight_StillTheBlackoutBaseClass()
    {
        // §17's dynamic gate (MapSky.SkyBlackedOut) keys on this class rather than on a def-name list,
        // so every blackout source in the game — Odyssey's DarkenedSkies, Royalty's SunBlocker, a
        // modded one — is caught for free. If Ludeon ever gives DarkenedSkies its own unrelated base
        // class, that gate silently stops firing on the headline case.
        var type = GetType("RimWorld.GameCondition_NoSunlight");
        Assert.That(type, Is.Not.Null, "RimWorld.GameCondition_NoSunlight no longer exists");
        Assert.That(type!.BaseType?.FullName, Is.EqualTo("RimWorld.GameCondition"));

        // Odyssey's DarkenedSkies uses the _Instant subclass, so the `is` test above has to keep
        // reaching it through inheritance.
        var instant = GetType("RimWorld.GameCondition_NoSunlight_Instant");
        Assert.That(instant, Is.Not.Null, "RimWorld.GameCondition_NoSunlight_Instant no longer exists");
        Assert.That(instant!.BaseType?.FullName, Is.EqualTo("RimWorld.GameCondition_NoSunlight"),
            "GameCondition_NoSunlight_Instant no longer derives from GameCondition_NoSunlight — "
            + "Odyssey's DarkenedSkies would stop matching §17's blackout gate");
    }

    [Test]
    public void GameConditionUnnaturalDarkness_StillAForceWeatherCondition()
    {
        // Anomaly's UnnaturalDarkness is the fifth blackout source (§17 / MapSkyMath.
        // ConditionBlacksOutSky), and deliberately caught by its OWN class check rather than by
        // widening the GameCondition_NoSunlight test above — it derives from GameCondition_ForceWeather,
        // not GameCondition_NoSunlight, so the two `is` tests in MapSky.BlacksOutSky are independent and
        // both need pinning. It is also MapSky.UnnaturalDarknessActive's own type test, which decides
        // whether §7a's MinNightBrightness floor is allowed to lift the screen above the event's own
        // darkness — see NightRadianceMath.EffectiveMinNightBrightness.
        var type = GetType("RimWorld.GameCondition_UnnaturalDarkness");
        Assert.That(type, Is.Not.Null, "RimWorld.GameCondition_UnnaturalDarkness no longer exists");
        Assert.That(type!.BaseType?.FullName, Is.EqualTo("RimWorld.GameCondition_ForceWeather"),
            "GameCondition_UnnaturalDarkness no longer derives from GameCondition_ForceWeather — "
            + "confirm it hasn't been folded into GameCondition_NoSunlight instead, which would make "
            + "the separate `is GameCondition_UnnaturalDarkness` checks in MapSky redundant (harmless) "
            + "or, if the rename dropped the old name, silently miss the class entirely (not harmless)");
    }

    [Test]
    public void GameConditionManager_ActiveConditionsAndParent_Exist()
    {
        // MapSky.SkyBlackedOut walks the manager chain itself (map's own conditions, then the world's)
        // rather than calling GetAllGameConditionsAffectingMap, which allocates into a caller-supplied
        // list on a per-frame path. That means it depends on both of these directly.
        var type = GetType("RimWorld.GameConditionManager");
        Assert.That(type, Is.Not.Null, "RimWorld.GameConditionManager no longer exists");

        var active = type!.Properties.SingleOrDefault(p => p.Name == "ActiveConditions");
        Assert.That(active, Is.Not.Null, "GameConditionManager.ActiveConditions no longer exists");
        Assert.That(active!.PropertyType.FullName,
            Is.EqualTo("System.Collections.Generic.List`1<RimWorld.GameCondition>"));

        var parent = type.Properties.SingleOrDefault(p => p.Name == "Parent");
        Assert.That(parent, Is.Not.Null, "GameConditionManager.Parent no longer exists");
        Assert.That(parent!.PropertyType.FullName, Is.EqualTo("RimWorld.GameConditionManager"));
    }

    [Test]
    public void GameCondition_CanApplyOnMap_Exists()
    {
        // SkyBlackedOut filters on CanApplyOnMap and nothing else, because that is exactly the filter
        // SkyManager.CurrentSkyTarget applies when composing a condition's SkyTarget — so our gate
        // opens and closes on the same frames vanilla's own darkening does.
        var type = GetType("RimWorld.GameCondition");
        Assert.That(type, Is.Not.Null, "RimWorld.GameCondition no longer exists");
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "CanApplyOnMap" && m.Parameters.Count == 1
            && m.Parameters[0].ParameterType.FullName == "Verse.Map");
        Assert.That(method, Is.Not.Null, "GameCondition.CanApplyOnMap(Map) no longer exists");
        Assert.That(method!.ReturnType.FullName, Is.EqualTo("System.Boolean"));
    }

    [Test]
    public void SkyColorSet_LerpDarken_StillTakesPerChannelMin()
    {
        // The whole §11 premise for tinting during a vanilla aurora rests on this: SkyManager
        // composes each condition's SkyTarget with LerpDarken, which mins per channel, so
        // GameCondition_Aurora can only darken and its brighter-than-night colour set is discarded.
        // If Ludeon ever swaps this for a plain Lerp, vanilla's aurora starts rendering for real and
        // AuroraConditions' driver set needs re-deciding.
        var type = GetType("Verse.SkyColorSet");
        Assert.That(type, Is.Not.Null, "Verse.SkyColorSet no longer exists");
        var method = type!.Methods.SingleOrDefault(m => m.Name == "LerpDarken" && m.Parameters.Count == 3);
        Assert.That(method, Is.Not.Null, "SkyColorSet.LerpDarken(A, B, t) no longer exists");

        var skyTarget = GetType("Verse.SkyTarget");
        Assert.That(skyTarget, Is.Not.Null, "Verse.SkyTarget no longer exists");
        var targetLerpDarken = skyTarget!.Methods.SingleOrDefault(m => m.Name == "LerpDarken" && m.Parameters.Count == 3);
        Assert.That(targetLerpDarken, Is.Not.Null, "SkyTarget.LerpDarken(A, B, t) no longer exists");
    }

    // --- §11a aurora curtain (AuroraCurtainOverlay / Patch_AuroraCurtainDraw) ---

    [Test]
    public void SkyOverlay_AbstractContract_IsUnchanged()
    {
        // AuroraCurtainOverlay derives from this. The three abstract members are the load-bearing part:
        // if Ludeon adds a fourth, or renames one, our subclass silently stops overriding it and the
        // curtain either never draws or never animates — the "silent override breakage" failure mode the
        // repo CLAUDE.md warns about, which compiles perfectly and does nothing.
        var type = GetType("Verse.SkyOverlay");
        Assert.That(type, Is.Not.Null, "Verse.SkyOverlay no longer exists");
        Assert.That(type!.IsAbstract, Is.True, "Verse.SkyOverlay is no longer abstract");

        foreach (string name in new[] { "TickOverlay", "DrawOverlay", "SetOverlayColor" })
        {
            var method = type.Methods.SingleOrDefault(m => m.Name == name);
            Assert.That(method, Is.Not.Null, $"SkyOverlay.{name} no longer exists");
            Assert.That(method!.IsAbstract, Is.True, $"SkyOverlay.{name} is no longer abstract");
        }

        // Reset is virtual rather than abstract — we override it, so it must stay overridable.
        var reset = type.Methods.SingleOrDefault(m => m.Name == "Reset");
        Assert.That(reset, Is.Not.Null, "SkyOverlay.Reset no longer exists");
        Assert.That(reset!.IsVirtual, Is.True, "SkyOverlay.Reset is no longer virtual");
    }

    [Test]
    public void SkyOverlay_DrawWorldOverlay_TakesAnExplicitAltitude()
    {
        // AuroraCurtainOverlay.DrawOverlay calls the four-argument form specifically, because it must pass
        // AltitudeLayer.VisEffects rather than accept the AltitudeLayer.Weather default — see the comment
        // there, and the ordering test below, for why a weather-altitude aurora gets dimmed out.
        var type = GetType("Verse.SkyOverlay");
        Assert.That(type, Is.Not.Null, "Verse.SkyOverlay no longer exists");

        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "DrawWorldOverlay" && m.Parameters.Count == 4);
        Assert.That(method, Is.Not.Null,
            "SkyOverlay.DrawWorldOverlay(Map, Material, float altitude, int layer) no longer exists");
        Assert.That(method!.IsStatic, Is.True, "DrawWorldOverlay is no longer static");
        Assert.That(method.Parameters[2].ParameterType.FullName, Is.EqualTo("System.Single"),
            "DrawWorldOverlay's third parameter is no longer the altitude");
    }

    [Test]
    public void AltitudeLayer_VisEffects_SitsAboveLightingOverlayAndBelowFogOfWar()
    {
        // §11a's whole reason for choosing VisEffects. The curtain must draw ABOVE LightingOverlay, or
        // §7a's pitch-black nights multiply it toward invisibility in exactly the conditions it exists
        // for, and BELOW FogOfWar so unexplored map stays fogged. That is an ordering claim about a
        // vanilla enum, so assert the ordering rather than merely that the names still exist.
        var type = GetType("Verse.AltitudeLayer");
        Assert.That(type, Is.Not.Null, "Verse.AltitudeLayer no longer exists");

        int Ordinal(string name)
        {
            var field = type!.Fields.SingleOrDefault(f => f.Name == name);
            Assert.That(field, Is.Not.Null, $"AltitudeLayer.{name} no longer exists");
            return System.Convert.ToInt32(field!.Constant);
        }

        Assert.That(Ordinal("VisEffects"), Is.GreaterThan(Ordinal("LightingOverlay")),
            "AltitudeLayer.VisEffects no longer sits above LightingOverlay — the curtain would be dimmed by night darkening");
        Assert.That(Ordinal("VisEffects"), Is.LessThan(Ordinal("FogOfWar")),
            "AltitudeLayer.VisEffects no longer sits below FogOfWar — the curtain would draw over unexplored map");
    }

    [Test]
    public void GameConditionManager_GameConditionManagerDraw_Exists()
    {
        // Patch_AuroraCurtainDraw's injection point. Non-virtual and public, which is why it was chosen
        // over GameCondition.SkyOverlays (virtual, and never actually draws anything) — see that file's
        // header for the full derivation.
        var type = GetType("RimWorld.GameConditionManager");
        Assert.That(type, Is.Not.Null, "RimWorld.GameConditionManager no longer exists");

        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "GameConditionManagerDraw" && m.Parameters.Count == 1);
        Assert.That(method, Is.Not.Null, "GameConditionManager.GameConditionManagerDraw(Map) no longer exists");
        Assert.That(method!.IsPublic, Is.True, "GameConditionManagerDraw is no longer public");
        Assert.That(method.IsVirtual, Is.False,
            "GameConditionManagerDraw became virtual — a base-method patch may no longer apply to every caller");
        Assert.That(method.Parameters[0].ParameterType.Name, Is.EqualTo("Map"));
    }

    [Test]
    public void ShaderDatabase_MoteGlow_Exists()
    {
        // The additive shader the curtain composites with. Additive is not a preference here: under alpha
        // blending a bright ribbon over a near-black night has to be almost opaque before it reads at all,
        // which is the flat-wash failure §11a exists to escape.
        var type = GetType("Verse.ShaderDatabase");
        Assert.That(type, Is.Not.Null, "Verse.ShaderDatabase no longer exists");

        var field = type!.Fields.SingleOrDefault(f => f.Name == "MoteGlow");
        Assert.That(field, Is.Not.Null, "ShaderDatabase.MoteGlow no longer exists");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("UnityEngine.Shader"));
        Assert.That(field.IsStatic, Is.True, "ShaderDatabase.MoteGlow is no longer static");
    }

    [Test]
    public void MeshPool_WholeMapPlane_Exists()
    {
        // What SkyOverlay.DrawWorldOverlay draws the curtain onto — a single map-sized quad, which is why
        // this subsystem costs one draw call per layer rather than touching any section mesh.
        var type = GetType("Verse.MeshPool");
        Assert.That(type, Is.Not.Null, "Verse.MeshPool no longer exists");

        var field = type!.Fields.SingleOrDefault(f => f.Name == "wholeMapPlane");
        Assert.That(field, Is.Not.Null, "MeshPool.wholeMapPlane no longer exists");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("UnityEngine.Mesh"));
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
    public void PlanetTile_HasValid()
    {
        // Source/MapWorldTile.cs's whole implementation. Every pocket map — Anomaly's labyrinth,
        // metal hell and undercave, Odyssey's ancient stockpile, insect lair and space pocket, and
        // any modded generator calling PocketMapUtility — carries PlanetTile.Invalid, and this is
        // vanilla's own predicate for spotting it.
        //
        // Reusing vanilla's predicate rather than writing `tileId >= 0` ourselves is deliberate: it
        // is the same comparison PlanetLayer's indexer bounds-checks with, so the gate cannot drift
        // away from the thing it is protecting. If Ludeon changes what "invalid" means, this test is
        // what tells us, instead of a hand-rolled comparison silently continuing to agree with the
        // old definition.
        //
        // A rename here does not fail loudly on its own — the mod would simply stop declining to read
        // the world grid, and cloud cover would go back to throwing ArgumentOutOfRangeException on
        // every rendered frame of a labyrinth, taking the sky composite with it.
        var type = GetType("RimWorld.Planet.PlanetTile");
        Assert.That(type, Is.Not.Null, "RimWorld.Planet.PlanetTile no longer exists");
        var valid = type!.Properties.SingleOrDefault(p => p.Name == "Valid");
        Assert.That(valid, Is.Not.Null,
            "PlanetTile.Valid no longer exists — MapWorldTile.HasWorldTile is the only thing keeping "
            + "cloud cover off a pocket map's missing world tile");
        Assert.That(valid!.PropertyType.FullName, Is.EqualTo("System.Boolean"),
            "PlanetTile.Valid changed shape — MapWorldTile treats it as a plain predicate");
    }

    [Test]
    public void GenTemperature_SeasonalTemperature_TakesPlanetTile()
    {
        // The call MapWorldTile.HasWorldTile exists to guard (Source/CloudCoverClock.cs). It is the
        // only vanilla helper this mod hands a raw PlanetTile to that reaches Find.WorldGrid's
        // UNCHECKED indexer, so it is the only one that throws rather than returning null or quietly
        // answering about the player's home tile.
        //
        // Asserted by signature rather than by body: what matters to us is that we are still the ones
        // choosing which tile it sees. If the parameter stops being a tile — if it grows a Map
        // overload that resolves pocketTileInfo itself, say — the guard is no longer describing the
        // call it guards, and that is worth a failing test even though nothing would crash.
        var type = GetType("Verse.GenTemperature");
        Assert.That(type, Is.Not.Null, "Verse.GenTemperature no longer exists");
        var method = type!.Methods.SingleOrDefault(m => m.Name == "GetTemperatureFromSeasonAtTile");
        Assert.That(method, Is.Not.Null,
            "GenTemperature.GetTemperatureFromSeasonAtTile no longer exists — cloud cover's seasonal "
            + "wet-fraction estimate reads the biome's weather list against it");
        Assert.That(
            method!.Parameters.Any(p => p.ParameterType.FullName == "RimWorld.Planet.PlanetTile"),
            Is.True,
            "GetTemperatureFromSeasonAtTile no longer takes a PlanetTile — MapWorldTile's guard is "
            + "written against the assumption that we pick the tile it looks up");
    }

    [Test]
    public void Tile_HasElevation()
    {
        // §20's single live read (Source/SiteAltitude.cs). Three things matter and all three are
        // asserted here.
        //
        // First, that it exists at all. If it is renamed or moved, every map silently falls back to
        // the sea-level column and §8's sunsets go back to being identical everywhere — a failure
        // with no exception, no log line, and nothing that looks wrong on any individual map.
        //
        // Second, that it lives on BASE RimWorld.Planet.Tile rather than on the Odyssey-era
        // SurfaceTile subclass. That is what lets SiteAltitude read it with no DLC gate, exactly as
        // §18 reads BiomeDef.inVacuum, and it is the assumption most likely to quietly stop holding
        // as Ludeon moves fields down into the layer-specific tile types.
        //
        // Third, that it is a float. We divide it by a scale height in metres; an int or a curve
        // would mean RimWorld had changed what the field means, not merely its type.
        var type = GetType("RimWorld.Planet.Tile");
        Assert.That(type, Is.Not.Null, "RimWorld.Planet.Tile no longer exists");
        var field = type!.Fields.SingleOrDefault(f => f.Name == "elevation" && f.IsPublic);
        Assert.That(field, Is.Not.Null,
            "Tile.elevation no longer exists or is no longer public on base Tile — §20's site-altitude "
            + "reddening reads it, and a SurfaceTile-only field would need a cast SiteAltitude does not make");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("System.Single"),
            "Tile.elevation changed shape — SiteAltitude divides it by AtmosphericColumn's scale height in metres");
    }

    [Test]
    public void Tile_HasPollution()
    {
        // §20b's live read (Source/SiteAltitude.cs), pinned for the same three reasons Tile_HasElevation
        // above pins its own field.
        //
        // Existence first: if it is renamed or moved, every polluted tile silently falls back to the
        // clean-air column and §8's sunsets stop responding to pollution — again a failure with no
        // exception, no log line, and nothing that looks wrong on any individual map.
        //
        // Then that it lives on BASE RimWorld.Planet.Tile. This one is the assertion most worth
        // having, because pollution is a Biotech mechanic and the obvious assumption is that its
        // field is DLC-side. It is not: all DLC code ships in the base assembly, exactly as with
        // BiomeDef.inVacuum in §18, which is why SiteAltitude reads it with no ModsConfig.BiotechActive
        // gate and simply sees 0 everywhere when Biotech is absent. If Ludeon ever moved it onto a
        // DLC-specific tile subclass, that no-gate read is what would have to change.
        //
        // Then that it is a float in [0, 1]-ish units. We multiply it straight into an aerosol column
        // fraction; an int or a percentage would mean the field had changed meaning, not merely type.
        var type = GetType("RimWorld.Planet.Tile");
        Assert.That(type, Is.Not.Null, "RimWorld.Planet.Tile no longer exists");
        var field = type!.Fields.SingleOrDefault(f => f.Name == "pollution" && f.IsPublic);
        Assert.That(field, Is.Not.Null,
            "Tile.pollution no longer exists or is no longer public on base Tile — §20b's aerosol "
            + "loading reads it, and a Biotech-only field would need a DLC gate SiteAltitude does not make");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("System.Single"),
            "Tile.pollution changed shape — AtmosphericColumn.AerosolLoadFraction multiplies it into a "
            + "column fraction and clamps it to [0, 1]");
    }

    [Test]
    public void Tile_HasRainfall()
    {
        // §20d's live read (Source/SiteAltitude.AngstromExponentForMap), pinned for the same reasons
        // Tile_HasElevation and Tile_HasPollution pin theirs.
        //
        // Existence, because losing it fails silently in the worst possible way: every tile would fall
        // back to the reference Angstrom exponent, the whole subsystem would quietly collapse back to
        // the single §20b sunset it exists to generalise, and nothing anywhere would look broken.
        //
        // That it is on BASE Tile, because the guard in SiteAltitude is written for a base-Tile field
        // and would need rethinking if rainfall ever moved onto SurfaceTile.
        //
        // And that it is a float. AerosolSpectrum.AngstromExponentForRainfall interpolates between
        // vanilla's own 340 mm and 2000 m breakpoints, which is only meaningful while the units are
        // millimetres per year — the same units BiomeWorker_Desert compares against 600f.
        var type = GetType("RimWorld.Planet.Tile");
        Assert.That(type, Is.Not.Null, "RimWorld.Planet.Tile no longer exists");
        var field = type!.Fields.SingleOrDefault(f => f.Name == "rainfall" && f.IsPublic);
        Assert.That(field, Is.Not.Null,
            "Tile.rainfall no longer exists or is no longer public on base Tile — §20d keys the "
            + "aerosol's Angstrom exponent off it, and would silently flatten to one exponent everywhere");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("System.Single"),
            "Tile.rainfall changed shape — §20d interpolates it against vanilla's own millimetre "
            + "biome breakpoints, which only means anything while it is a float in mm/year");
    }

    [Test]
    public void PlanetLayer_HasIsRootSurface()
    {
        // The guard that goes with the read above. SiteAltitude only trusts `elevation` on the root
        // surface layer, because an orbital-ring tile carries the same field (it is on base Tile)
        // while it means nothing up there. If this property disappears the guard has to be rewritten
        // rather than dropped — §18's vacuum gate covers the same maps today, but relying on that
        // alone would make two independently-motivated gates silently load-bearing on each other.
        var type = GetType("RimWorld.Planet.PlanetLayer");
        Assert.That(type, Is.Not.Null, "RimWorld.Planet.PlanetLayer no longer exists");
        var property = type!.Properties.SingleOrDefault(p => p.Name == "IsRootSurface");
        Assert.That(property, Is.Not.Null,
            "PlanetLayer.IsRootSurface no longer exists — SiteAltitude uses it to reject non-surface tiles");
        Assert.That(property!.PropertyType.FullName, Is.EqualTo("System.Boolean"));
    }

    [Test]
    public void PlanetTile_HasTileId()
    {
        // §20c's noise seed (Source/AerosolDriftClock.cs). The tile id is what makes a map's weather
        // history stable across save/load and independent of every other map's, and it is chosen
        // precisely because RimWorld already persists it — so if it stops being a plain int field the
        // seed has to be re-sourced rather than patched around.
        //
        // Note this is the STRUCT PlanetTile's own field, not Tile's. SunClock reads it the same way
        // for its per-tile day cache, so this pin covers both.
        var type = GetType("RimWorld.Planet.PlanetTile");
        Assert.That(type, Is.Not.Null, "RimWorld.Planet.PlanetTile no longer exists");
        var field = type!.Fields.SingleOrDefault(f => f.Name == "tileId" && f.IsPublic);
        Assert.That(field, Is.Not.Null,
            "PlanetTile.tileId no longer exists or is no longer public — §20c seeds its aerosol drift "
            + "with it and SunClock keys its per-day cache on it");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("System.Int32"));
    }

    [Test]
    public void TickManager_HasTicksAbs()
    {
        // §20c's clock (Source/AerosolDriftClock.cs), and already the clock for the moon phase and the
        // geometry memo's stamp. It has to be the ABSOLUTE tick rather than TicksGame: the drift
        // sequence is defined against it, so a counter that reset on load would give a reloaded colony
        // a different evening from the one it just had — the exact reproducibility property §20c pins.
        var type = GetType("Verse.TickManager");
        Assert.That(type, Is.Not.Null, "Verse.TickManager no longer exists");
        var property = type!.Properties.SingleOrDefault(p => p.Name == "TicksAbs");
        Assert.That(property, Is.Not.Null,
            "TickManager.TicksAbs no longer exists — §20c's drift, §6's moon phase and FrameStamp all read it");
        Assert.That(property!.PropertyType.FullName, Is.EqualTo("System.Int32"),
            "TickManager.TicksAbs changed shape — AerosolDrift.SampleIndex takes an int tick");
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

    // §11a draws the aurora one AltInc above FogOfWar, so both the constant and the two layers it is
    // measured between have to keep existing. If AltInc were renamed the mod would not compile; if the
    // LAYER ORDER changed the mod would compile and quietly draw the aurora in the wrong place — under
    // the fog, or over the map-edge clipper — which is why the ordering is asserted and not just the
    // names.
    [Test]
    public void Altitudes_AltInc_Exists()
    {
        var type = GetType("Verse.Altitudes");
        Assert.That(type, Is.Not.Null, "Verse.Altitudes no longer exists");
        Assert.That(type!.Fields.Any(f => f.Name == "AltInc"), Is.True,
            "Altitudes.AltInc no longer exists — AuroraCurtainOverlay offsets above FogOfWar by it");
    }

    [Test]
    public void AltitudeLayer_KeepsTheOrderTheAuroraDependsOn()
    {
        var type = GetType("Verse.AltitudeLayer");
        Assert.That(type, Is.Not.Null, "Verse.AltitudeLayer no longer exists");

        int Index(string name)
        {
            var field = type!.Fields.SingleOrDefault(f => f.Name == name);
            Assert.That(field, Is.Not.Null, $"AltitudeLayer.{name} no longer exists");
            return System.Convert.ToInt32(field!.Constant);
        }

        int weather = Index("Weather");
        int lighting = Index("LightingOverlay");
        int visEffects = Index("VisEffects");
        int fog = Index("FogOfWar");
        int clipper = Index("WorldClipper");

        // Weather below LightingOverlay is why we cannot draw there: §7a's pitch-black nights would
        // multiply the aurora away in exactly the conditions it exists for.
        Assert.That(weather, Is.LessThan(lighting), "Weather is no longer below LightingOverlay");

        // We draw above FogOfWar so neither roofs nor unexplored ground hide the sky...
        Assert.That(lighting, Is.LessThan(visEffects));
        Assert.That(visEffects, Is.LessThan(fog), "VisEffects is no longer below FogOfWar");

        // ...but below WorldClipper, which must keep covering us so a patch cannot spill off the map.
        Assert.That(fog, Is.LessThan(clipper), "FogOfWar is no longer below WorldClipper");
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
        // Half of our "is this cell the mountain itself" test — the other half is
        // BuildingProperties.isNaturalRock below, and only the two together bury a cell. Vanilla's own
        // corner pass short-circuits its roof-holder exclusion on this same flag, though it stops at
        // raising the cover to its 100 floor rather than blacking the cell out (see #129).
        var type = GetType("Verse.RoofDef");
        Assert.That(type, Is.Not.Null, "Verse.RoofDef no longer exists");
        Assert.That(type!.Fields.Any(f => f.Name == "isThickRoof" && f.IsPublic), Is.True,
            "RoofDef.isThickRoof no longer exists — IndoorOcclusionMath.BlocksSky's mountain exception depends on it");
    }

    [Test]
    public void BuildingProperties_IsNaturalRock_Exists()
    {
        // The other half. Losing this field would not fail loudly — every rock would read as a built
        // wall and mountain interiors would quietly go a shade light — so it is pinned rather than left
        // to the eye. Reached as ThingDef.building, which is why that property is checked too.
        var thingDef = GetType("Verse.ThingDef");
        Assert.That(thingDef, Is.Not.Null, "Verse.ThingDef no longer exists");
        Assert.That(thingDef!.Fields.Any(f => f.Name == "building" && f.IsPublic), Is.True,
            "ThingDef.building no longer exists — Patch_IndoorSkyOcclusion reads isNaturalRock through it");

        var type = GetType("RimWorld.BuildingProperties");
        Assert.That(type, Is.Not.Null, "RimWorld.BuildingProperties no longer exists");
        Assert.That(type!.Fields.Any(f => f.Name == "isNaturalRock" && f.IsPublic), Is.True,
            "BuildingProperties.isNaturalRock no longer exists — IndoorOcclusionMath.BlocksSky tells unmined "
            + "stone from a built wall under a mountain roof with it");
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
    public void MapDrawer_MapMeshDrawerUpdate_First_Exists()
    {
        // Patch_VectorLightBuild prefixes this so the vector-light polygons are rebuilt BEFORE the
        // sections that read them regenerate (issue #218). A rename would leave the prefix unattached
        // and hand the draw path back its one-frame stale bake — silently, because every scenario in
        // this repo captures after a settle and would still read the correct final value.
        var type = GetType("Verse.MapDrawer");
        Assert.That(type, Is.Not.Null, "Verse.MapDrawer no longer exists");
        Assert.That(
            type!.Methods.Any(m => m.Name == "MapMeshDrawerUpdate_First" && m.IsPublic
                && m.Parameters.Count == 0),
            Is.True,
            "MapDrawer.MapMeshDrawerUpdate_First() no longer exists or changed signature — "
            + "Patch_VectorLightBuild targets it");
    }

    [Test]
    public void MapDrawer_map_Field_Exists()
    {
        // Patch_VectorLightBuild takes the map by Harmony's private-field injection (___map) rather
        // than off Find.CurrentMap, so the build is charged to the drawer's own map. Harmony resolves
        // that by NAME at runtime, which means a rename is a silent no-op rather than a build error:
        // the prefix would run with a null map and decline to build anything, every frame.
        var type = GetType("Verse.MapDrawer");
        Assert.That(type, Is.Not.Null, "Verse.MapDrawer no longer exists");
        Assert.That(
            type!.Fields.Any(f => f.Name == "map" && f.FieldType.Name == "Map"),
            Is.True,
            "MapDrawer.map no longer exists or was renamed — Patch_VectorLightBuild injects it as ___map");
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
    public void GameConditionNoSunlight_SkyTarget_ExistsAndReturnsNullableSkyTarget()
    {
        // §18e's injection point. Patch_EclipseDarkening reshapes how FAST the sky reaches the umbra;
        // this method is WHAT the umbra is, and the vacuum postfix rewrites it. Vanilla returns
        // `SkyTarget?` — a plain SkyTarget return would mean the postfix's `ref SkyTarget? __result`
        // silently stops binding, which Harmony reports as a patch failure rather than a compile one.
        var type = GetType("RimWorld.GameCondition_NoSunlight");
        Assert.That(type, Is.Not.Null, "RimWorld.GameCondition_NoSunlight no longer exists");
        var method = type!.Methods.SingleOrDefault(m => m.Name == "SkyTarget" && m.Parameters.Count == 1);
        Assert.That(method, Is.Not.Null,
            "GameCondition_NoSunlight.SkyTarget(Map) no longer exists — §18e's vacuum umbra postfixes it");
        Assert.That(method!.ReturnType.FullName, Is.EqualTo("System.Nullable`1<Verse.SkyTarget>"),
            "GameCondition_NoSunlight.SkyTarget no longer returns SkyTarget?");
    }

    [Test]
    public void GameConditionNoSunlight_EclipseSkyColors_Exists()
    {
        // The SEA-LEVEL ANCHOR of §18e. Vanilla's own umbral colour set is the wan grey that a total
        // eclipse leaves behind at sea level (the unshadowed atmosphere scattering light into the
        // umbra), and §18e is defined entirely relative to it: the vacuum arm scales this colour set
        // toward the night floor, the sea-level arm multiplies it by exactly 1. If it stops existing,
        // the comparison VacuumEclipseMathTests makes has lost the thing it compares against.
        var type = GetType("RimWorld.GameCondition_NoSunlight");
        Assert.That(type, Is.Not.Null, "RimWorld.GameCondition_NoSunlight no longer exists");
        var field = type!.Fields.SingleOrDefault(f => f.Name == "EclipseSkyColors");
        Assert.That(field, Is.Not.Null,
            "GameCondition_NoSunlight.EclipseSkyColors no longer exists — §18e's sea-level umbral anchor");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("Verse.SkyColorSet"));
    }

    [Test]
    public void SkyColorSet_LerpDarken_Exists()
    {
        // VacuumEclipseMath.EclipsedSkyBrightness is an offline model OF this method — it is how
        // SkyManager.CurrentSkyTarget composes an active condition's target onto the weather's. If
        // vanilla ever switched the condition composition to plain Lerp, our model would keep
        // agreeing with itself while diverging from the game.
        var type = GetType("Verse.SkyColorSet");
        Assert.That(type, Is.Not.Null, "Verse.SkyColorSet no longer exists");
        Assert.That(type!.Methods.Any(m => m.Name == "LerpDarken" && m.Parameters.Count == 3), Is.True,
            "SkyColorSet.LerpDarken(A, B, t) no longer exists — §18e models the composed umbra with it");
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
        //
        // §17's MapSky.SkyBlackedOut compares against the same field in the opposite direction — it
        // treats every GameCondition_NoSunlight EXCEPT this one as a blackout. Losing this field is
        // therefore the single change that would make the blackout gate swallow our own §10/§10a
        // eclipse handling, so it is now load-bearing for two subsystems.
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

    [Test]
    public void BiomeDef_InVacuum_Exists()
    {
        // MapSky.IsEnclosed uses this to tell "no atmosphere" apart from "rock ceiling" — the one
        // distinction that keeps orbit out of the skyless gate that Biomes! Caverns' caverns fall
        // into. Both answer false to MapSky.HasSky, and only this field separates them, so losing it
        // would silently strip every sky effect from orbit while the cave gate went on looking
        // correct. That failure mode is invisible in a screenshot of a cave, which is exactly why it
        // is pinned here rather than left to a scenario.
        var type = GetType("RimWorld.BiomeDef");
        Assert.That(type, Is.Not.Null, "RimWorld.BiomeDef no longer exists");
        var field = type!.Fields.SingleOrDefault(f => f.Name == "inVacuum" && f.IsPublic);
        Assert.That(field, Is.Not.Null, "BiomeDef.inVacuum no longer exists or is no longer public");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("System.Boolean"));
    }

    [Test]
    public void SectionLayerSunShadows_StillHonoursDisableShadows()
    {
        // The justification for Gate B, in the same shape as the disableSkyLighting pin above: our
        // shadow subsystems suppress themselves on a disableShadows biome only because vanilla's own
        // SectionLayer_SunShadows already refuses to draw there. If vanilla stops reading the field,
        // our reading of it is no longer "agreeing with vanilla" and needs rethinking.
        var type = GetType("Verse.SectionLayer_SunShadows");
        Assert.That(type, Is.Not.Null, "Verse.SectionLayer_SunShadows no longer exists");
        Assert.That(
            type!.Methods.Any(m =>
                m.HasBody
                && m.Body.Instructions.Any(i =>
                    i.Operand is Mono.Cecil.FieldReference r && r.Name == "disableShadows")),
            Is.True,
            "SectionLayer_SunShadows no longer reads BiomeDef.disableShadows — Gate B was justified "
            + "by vanilla itself using this field to mean 'this map draws no shadows'");
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

    // --- §22 partial cloud cover (CloudCoverClock, Patch_CloudCoverSky, Patch_CloudCoverLabel) ---
    //
    // §22 reads the same three CurrentWeatherCommonality ingredients §13's guard does not need —
    // WeatherCommonalityRecord.weather, WeatherDef.commonalityRainfallFactor, WeatherDef.temperatureRange
    // — plus the biome and season lookups that get it there. Grouped separately from §13's section
    // above because the two subsystems ask BiomeDef.baseWeatherCommonalities two different questions:
    // §13 counts records, §22 walks the whole list and evaluates each one.

    [Test]
    public void Map_TileInfo_ReturnsTile()
    {
        // Distinct from Map.Tile (a PlanetTile id, already pinned above) — this is the STRUCT with the
        // biome/rainfall/temperature data on it. CloudCoverClock reads both off the same map for two
        // different reasons: Tile for tileId as AerosolDriftClock/SunClock already establish, TileInfo
        // for everything SeasonalWetFractionFor evaluates.
        var type = GetType("Verse.Map");
        Assert.That(type, Is.Not.Null, "Verse.Map no longer exists");
        var property = type!.Properties.SingleOrDefault(p => p.Name == "TileInfo");
        Assert.That(property, Is.Not.Null, "Map.TileInfo no longer exists");
        Assert.That(property!.PropertyType.FullName, Is.EqualTo("RimWorld.Planet.Tile"),
            "Map.TileInfo no longer returns RimWorld.Planet.Tile");
    }

    [Test]
    public void Tile_HasPrimaryBiome()
    {
        var type = GetType("RimWorld.Planet.Tile");
        Assert.That(type, Is.Not.Null, "RimWorld.Planet.Tile no longer exists");
        var property = type!.Properties.SingleOrDefault(p => p.Name == "PrimaryBiome");
        Assert.That(property, Is.Not.Null,
            "Tile.PrimaryBiome no longer exists — CloudCoverClock reads its baseWeatherCommonalities off it");
        Assert.That(property!.PropertyType.FullName, Is.EqualTo("RimWorld.BiomeDef"));
    }

    [Test]
    public void WeatherCommonalityRecord_HasWeather()
    {
        // §13 above only needed .commonality; §22 also dereferences .weather itself, to read each
        // candidate's rainRate/snowRate/temperatureRange/commonalityRainfallFactor.
        var type = GetType("RimWorld.WeatherCommonalityRecord");
        Assert.That(type, Is.Not.Null, "RimWorld.WeatherCommonalityRecord no longer exists");
        var field = type!.Fields.SingleOrDefault(f => f.Name == "weather" && f.IsPublic);
        Assert.That(field, Is.Not.Null,
            "WeatherCommonalityRecord.weather no longer exists or is no longer public");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("Verse.WeatherDef"));
    }

    [Test]
    public void WeatherDef_HasTemperatureRange()
    {
        // The eligibility gate CurrentWeatherCommonality itself uses (weather.temperatureRange
        // .Includes(currentTemperature)) — see CloudCoverClock's header for why §22 mirrors this
        // exactly rather than inventing its own eligibility rule.
        var type = GetType("Verse.WeatherDef");
        Assert.That(type, Is.Not.Null, "Verse.WeatherDef no longer exists");
        var field = type!.Fields.SingleOrDefault(f => f.Name == "temperatureRange" && f.IsPublic);
        Assert.That(field, Is.Not.Null,
            "WeatherDef.temperatureRange no longer exists or is no longer public");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("Verse.FloatRange"));
    }

    [Test]
    public void WeatherDef_HasCommonalityRainfallFactor()
    {
        // A nullable SimpleCurve — CloudCoverClock's null check (factor 1, not 0, when absent) mirrors
        // CurrentWeatherCommonality's own `if (... != null) num *= ...` exactly, so the field being
        // nullable is itself part of what is being pinned here, not an incidental detail.
        var type = GetType("Verse.WeatherDef");
        Assert.That(type, Is.Not.Null, "Verse.WeatherDef no longer exists");
        var field = type!.Fields.SingleOrDefault(f => f.Name == "commonalityRainfallFactor" && f.IsPublic);
        Assert.That(field, Is.Not.Null,
            "WeatherDef.commonalityRainfallFactor no longer exists or is no longer public");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("Verse.SimpleCurve"),
            "WeatherDef.commonalityRainfallFactor changed shape — a non-nullable curve would break "
            + "CloudCoverClock's null-means-factor-of-1 mirroring of CurrentWeatherCommonality");
    }

    [Test]
    public void FloatRange_HasIncludes()
    {
        var type = GetType("Verse.FloatRange");
        Assert.That(type, Is.Not.Null, "Verse.FloatRange no longer exists");
        var method = type!.Methods.SingleOrDefault(m => m.Name == "Includes" && m.Parameters.Count == 1);
        Assert.That(method, Is.Not.Null, "FloatRange.Includes(float) no longer exists");
        Assert.That(method!.ReturnType.FullName, Is.EqualTo("System.Boolean"));
    }

    [Test]
    public void GenTemperature_HasGetTemperatureFromSeasonAtTile()
    {
        // The deliberately-not-live-weather estimate CloudCoverClock feeds temperatureRange.Includes —
        // see that file's header for why this is used instead of map.mapTemperature.OutdoorTemp.
        var type = GetType("Verse.GenTemperature");
        Assert.That(type, Is.Not.Null, "Verse.GenTemperature no longer exists");
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "GetTemperatureFromSeasonAtTile" && m.Parameters.Count == 2);
        Assert.That(method, Is.Not.Null,
            "GenTemperature.GetTemperatureFromSeasonAtTile(int, PlanetTile) no longer exists");
        Assert.That(method!.ReturnType.FullName, Is.EqualTo("System.Single"));
    }

    [Test]
    public void WeatherManager_HasMapField()
    {
        // Patch_CloudCoverLabel reads __instance.map to call CloudCoverClock.FractionForMap; the null
        // guard on it mirrors the same possibility Section.map's own pin elsewhere in this file notes.
        var type = GetType("RimWorld.WeatherManager");
        Assert.That(type, Is.Not.Null, "RimWorld.WeatherManager no longer exists");
        var field = type!.Fields.SingleOrDefault(f => f.Name == "map" && f.IsPublic);
        Assert.That(field, Is.Not.Null, "WeatherManager.map no longer exists or is no longer public");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("Verse.Map"));
    }

    [Test]
    public void WeatherManager_HasCurWeatherPerceived()
    {
        // Patch_CloudCoverLabel gates its suffix on this, not on curWeather directly — see that
        // patch's header for why the label needs to agree with what CurWeatherPerceived itself renders
        // rather than with the underlying transition state.
        var type = GetType("RimWorld.WeatherManager");
        Assert.That(type, Is.Not.Null, "RimWorld.WeatherManager no longer exists");
        var property = type!.Properties.SingleOrDefault(p => p.Name == "CurWeatherPerceived");
        Assert.That(property, Is.Not.Null, "WeatherManager.CurWeatherPerceived no longer exists");
        Assert.That(property!.PropertyType.FullName, Is.EqualTo("Verse.WeatherDef"));
    }

    [Test]
    public void WeatherManager_DoWeatherGUI_StillHasTheDrawSeamPatchCloudCoverLabelTranspiles()
    {
        // Patch_CloudCoverLabel is a Transpiler, and the seam it splices into is the single
        // Widgets.Label(Rect, TaggedString) call DoWeatherGUI draws the weather name with. A
        // transpiler that finds no seam does not throw — it silently emits the method unchanged, so
        // the mod would ship with a feature that reads as enabled in the settings panel and never
        // appears on screen. This test is the thing that fails instead.
        //
        // The DRAW call is the anchor rather than the Def.LabelCap the string is built from, and that
        // is deliberate: in vanilla IL the two sites are adjacent and either would do, but another
        // mod inserting between them (Uncompromising Fires does exactly this) makes the choice decide
        // whether we run before or after their contribution. Anchoring on the draw call puts us last
        // in either load order, which is what lets WithCloudCover measure the finished string. Both
        // halves of the shape are pinned below: the getter still produces the TaggedString, and the
        // overload that consumes one still exists, so our TaggedString-in/TaggedString-out insert
        // stays stack-neutral and leaves the argument type the call site expects.
        var type = GetType("RimWorld.WeatherManager");
        Assert.That(type, Is.Not.Null, "RimWorld.WeatherManager no longer exists");
        var method = type!.Methods.SingleOrDefault(m => m.Name == "DoWeatherGUI" && m.Parameters.Count == 1);
        Assert.That(method, Is.Not.Null, "WeatherManager.DoWeatherGUI(Rect) no longer exists");
        Assert.That(method!.Parameters[0].ParameterType.FullName, Is.EqualTo("UnityEngine.Rect"),
            "DoWeatherGUI's Rect parameter is what the transpiler loads (Ldarg_1) to measure the label against");

        var drawCalls = method.Body.Instructions
            .Where(i => i.Operand is MethodReference m
                && m.DeclaringType.FullName == "Verse.Widgets" && m.Name == "Label"
                && m.Parameters.Count == 2
                && m.Parameters[0].ParameterType.FullName == "UnityEngine.Rect"
                && m.Parameters[1].ParameterType.FullName == "Verse.TaggedString")
            .ToList();
        Assert.That(drawCalls.Count, Is.EqualTo(1),
            "DoWeatherGUI no longer calls Widgets.Label(Rect, TaggedString) exactly once — "
            + "Patch_CloudCoverLabel's transpiler splices its cloud-cover suffix in front of that single "
            + "call and takes the first match");

        var labelCapCalls = method.Body.Instructions
            .Where(i => i.Operand is MethodReference m
                && m.DeclaringType.FullName == "Verse.Def" && m.Name == "get_LabelCap")
            .ToList();
        Assert.That(labelCapCalls.Count, Is.EqualTo(1),
            "DoWeatherGUI no longer builds its label from a single Def.LabelCap — the transpiler no "
            + "longer anchors here, but the label still has to be the TaggedString our insert wraps");
        Assert.That(((MethodReference)labelCapCalls[0].Operand!).ReturnType.FullName,
            Is.EqualTo("Verse.TaggedString"),
            "Def.LabelCap no longer returns TaggedString — Patch_CloudCoverLabel.WithCloudCover takes and "
            + "returns one to keep the insert stack-neutral");
    }

    [Test]
    public void Text_HasCalcSize_ForTheLabelFitCheck()
    {
        // Patch_CloudCoverLabel measures the finished label with this before deciding whether its
        // suffix fits on one line, and drops the suffix if it does not. If this disappears the fit
        // rule cannot be evaluated at all — which is the difference between "our readout yields to
        // another mod's" and "the weather panel wraps over the temperature row".
        var type = GetType("Verse.Text");
        Assert.That(type, Is.Not.Null, "Verse.Text no longer exists");
        var method = type!.Methods.SingleOrDefault(m => m.Name == "CalcSize" && m.Parameters.Count == 1
            && m.Parameters[0].ParameterType.FullName == "System.String");
        Assert.That(method, Is.Not.Null, "Text.CalcSize(string) no longer exists");
        Assert.That(method!.ReturnType.FullName, Is.EqualTo("UnityEngine.Vector2"));
    }

    // --- §27 phase 5b: the glow grid's accumulated value and its projection ---
    //
    // WHAT THESE GUARD, and why the pair rather than either alone. VectorLightMask.CorrectCell asks
    // vanilla what it DISPLAYED at a cell (`GlowGrid.VisualGlowAt`) and reconstructs how vanilla got
    // there by summing the per-emitter arrays and applying vanilla's own projection. The projection
    // is reimplemented in VectorLightSaturationMath rather than called — it is four lines of integer
    // arithmetic on a struct — so the risk is not that a call site breaks but that the OPERATOR
    // changes underneath a copy of it that goes on compiling. If `ProjectToColor32Fast` stops
    // existing, the odds are that the rule it encodes has changed too, and phase 5b's whole premise
    // ("over 255 vanilla scales the colour rather than clipping the channel") needs re-reading.
    //
    // A Cecil test can only say the members exist. That is exactly the failure it is here for: a
    // silent rename would leave the mask reading a stale reflection path and standing down, or
    // reconstructing against a projection vanilla no longer performs — neither of which shows up as
    // an error at run time.

    [Test]
    public void GlowGrid_VisualGlowAt_TakesACell()
    {
        var type = GetType("Verse.GlowGrid");
        Assert.That(type, Is.Not.Null, "Verse.GlowGrid no longer exists");

        var method = type!.Methods.SingleOrDefault(m => m.Name == "VisualGlowAt"
            && m.Parameters.Count == 1
            && m.Parameters[0].ParameterType.FullName == "Verse.IntVec3");

        Assert.That(method, Is.Not.Null, "GlowGrid.VisualGlowAt(IntVec3) no longer exists");
        Assert.That(method!.ReturnType.FullName, Is.EqualTo("UnityEngine.Color32"));
    }

    // The two private fields §27 phase 3 reads by reflection, and phase 5b now walks in full to
    // reconstruct vanilla's sum. GlowGridPerLight treats an unreadable pair as a defined stand-down,
    // so a rename here costs the whole subsystem silently at run time and must fail loudly at build.
    [Test]
    public void GlowGrid_HasPerLightFieldsWeReadByReflection()
    {
        var type = GetType("Verse.GlowGrid");
        Assert.That(type, Is.Not.Null, "Verse.GlowGrid no longer exists");
        Assert.That(type!.Fields.Any(f => f.Name == "lights"), Is.True,
            "GlowGrid.lights no longer exists — GlowGridPerLight cannot enumerate emitters");
        Assert.That(type.Fields.Any(f => f.Name == "glowPool"), Is.True,
            "GlowGrid.glowPool no longer exists — GlowGridPerLight cannot read per-emitter glow");
    }

    // §27e reads GlowGrid.lightBlockers to answer "is this cell already a blocker", so a door's
    // reconcile can skip the write when the bit already holds what it wants. That skip is worth a
    // silhouette rescan per door swing, and GlowGridAccess degrades to writing unconditionally when
    // the field cannot be found — so a rename costs performance silently rather than loudly, which
    // is exactly the failure mode this file exists to convert into a build error.
    [Test]
    public void GlowGrid_StillKeepsTheBlockerBitsWeReadBeforeWriting()
    {
        var type = GetType("Verse.GlowGrid");
        Assert.That(type, Is.Not.Null, "Verse.GlowGrid no longer exists");

        var field = type!.Fields.FirstOrDefault(f => f.Name == "lightBlockers");
        Assert.That(field, Is.Not.Null,
            "GlowGrid.lightBlockers no longer exists — §27e's door reconcile falls back to writing "
            + "the bit on every door event, which discards the silhouette memo four times a swing");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("Unity.Collections.NativeBitArray"),
            "GlowGrid.lightBlockers changed type — GlowGridAccess.TryGetBlocksLight reads it as a "
            + "NativeBitArray and indexes it with map.cellIndices.CellToIndex");

        // Both writes we call, and the pair our own postfixes hang off. If either is renamed the
        // reconcile stops moving the bit at all, which reads as the feature being switched off.
        Assert.That(type.Methods.Any(m => m.Name == "LightBlockerAdded"), Is.True,
            "GlowGrid.LightBlockerAdded no longer exists");
        Assert.That(type.Methods.Any(m => m.Name == "LightBlockerRemoved"), Is.True,
            "GlowGrid.LightBlockerRemoved no longer exists");
    }

    // The index GlowGridAccess turns a cell into before touching those bits. GlowGrid's own
    // `indices` is private and is assigned from map.cellIndices in the constructor, so we use the
    // public one — this pins that they are still the same kind of thing.
    [Test]
    public void CellIndices_StillMapsACellToAnIndex()
    {
        var type = GetType("Verse.CellIndices");
        Assert.That(type, Is.Not.Null, "Verse.CellIndices no longer exists");
        Assert.That(
            type!.Methods.Any(m => m.Name == "CellToIndex"
                && m.Parameters.Count == 1
                && m.Parameters[0].ParameterType.FullName == "Verse.IntVec3"),
            Is.True,
            "CellIndices.CellToIndex(IntVec3) no longer exists — GlowGridAccess cannot locate a "
            + "cell's blocker bit");
    }

    [Test]
    public void ColorInt_StillProjectsRatherThanClipping()
    {
        var type = GetType("Verse.ColorInt");
        Assert.That(type, Is.Not.Null, "Verse.ColorInt no longer exists");
        Assert.That(type!.Methods.Any(m => m.Name == "ProjectToColor32Fast"), Is.True,
            "ColorInt.ProjectToColor32Fast no longer exists — re-read whether the glow grid still "
            + "SCALES an over-255 sum by 255/max instead of clipping each channel, which is the "
            + "premise VectorLightSaturationMath is built on");
        Assert.That(type.Methods.Any(m => m.Name == "ProjectToColor32"), Is.True,
            "ColorInt.ProjectToColor32 no longer exists — SectionLayer_LightingOverlay's own vertex "
            + "projection, which the offline sweep transcribes");
    }

    // --- The one-time update notice (Patch_UpdateNotice / UpdateNotice / Dialog_UpdateNotice) ---

    // Where the notice is raised. This is the ONE patch in the mod whose failure is completely
    // silent: everything else here draws something every frame, so a dead patch shows up as a sky
    // that stopped changing, while a dead Init postfix just means a window nobody was expecting
    // never appears — and the acknowledgement is never written either, so the bug does not even
    // accumulate into something a player could report.
    [Test]
    public void UIRoot_Entry_Init_Exists()
    {
        var type = GetType("Verse.UIRoot_Entry");
        Assert.That(type, Is.Not.Null, "Verse.UIRoot_Entry no longer exists");
        Assert.That(
            type!.Methods.Any(m => m.Name == "Init" && m.IsPublic && m.Parameters.Count == 0),
            Is.True,
            "UIRoot_Entry.Init() no longer exists or changed signature — Patch_UpdateNotice postfixes "
            + "it to raise the what's-new notice at the main menu");
    }

    // Vanilla adds a Dialog_MessageBox from inside Init itself (the missing-Steam-client warning),
    // which is what makes it a PROVEN place to add a window rather than a plausible one. If that
    // stops being true the argument in Patch_UpdateNotice's header needs re-checking before the
    // timing is trusted.
    [Test]
    public void UIRoot_Entry_Init_StillAddsAWindowItself()
    {
        var type = GetType("Verse.UIRoot_Entry");
        Assert.That(type, Is.Not.Null, "Verse.UIRoot_Entry no longer exists");
        var init = type!.Methods.SingleOrDefault(m => m.Name == "Init" && m.Parameters.Count == 0);
        Assert.That(init?.Body, Is.Not.Null, "UIRoot_Entry.Init has no body to inspect");
        Assert.That(
            init!.Body.Instructions.Any(i =>
                i.Operand is MethodReference called && called.Name == "Add"
                && called.DeclaringType.Name == "WindowStack"),
            Is.True,
            "UIRoot_Entry.Init no longer adds a window of its own — re-verify that the window stack "
            + "is live at this point before trusting Patch_UpdateNotice's timing");
    }

    // How CelestialLightingSettings.LoadedFromDisk tells "a settings file existed" from "these are
    // the field initialisers", which is the entire basis for not showing the notice to a first-time
    // install. Both halves are vanilla API the mod reads directly rather than patches, so nothing
    // else in this file would notice a rename.
    [Test]
    public void Scribe_StillExposesLoadingVarsMode()
    {
        var scribe = GetType("Verse.Scribe");
        Assert.That(scribe, Is.Not.Null, "Verse.Scribe no longer exists");
        Assert.That(scribe!.Fields.Any(f => f.Name == "mode" && f.IsStatic), Is.True,
            "Verse.Scribe.mode no longer exists — CelestialLightingSettings cannot tell a loaded "
            + "settings file from a fresh one, and the update notice would show on a new install");

        var mode = GetType("Verse.LoadSaveMode");
        Assert.That(mode, Is.Not.Null, "Verse.LoadSaveMode no longer exists");
        Assert.That(mode!.Fields.Any(f => f.Name == "LoadingVars"), Is.True,
            "LoadSaveMode.LoadingVars no longer exists — same consequence as above");
    }

    // ReadModSettings skipping the load entirely when no file exists is what makes the signal above
    // meaningful: ExposeData is never called on a first-time install, so LoadedFromDisk stays false.
    // If this ever gained a "construct and expose defaults anyway" path the notice would start
    // showing to new players, and nothing else would break.
    [Test]
    public void LoadedModManager_ReadModSettings_StillSkipsAMissingFile()
    {
        var type = GetType("Verse.LoadedModManager");
        Assert.That(type, Is.Not.Null, "Verse.LoadedModManager no longer exists");
        var read = type!.Methods.SingleOrDefault(m => m.Name == "ReadModSettings");
        Assert.That(read?.Body, Is.Not.Null, "LoadedModManager.ReadModSettings has no body to inspect");
        Assert.That(
            read!.Body.Instructions.Any(i =>
                i.Operand is MethodReference called && called.Name == "Exists"
                && called.DeclaringType.Name == "File"),
            Is.True,
            "LoadedModManager.ReadModSettings no longer guards on the settings file existing — "
            + "CelestialLightingSettings.LoadedFromDisk may no longer distinguish a returning player "
            + "from a first-time install");
    }

    // --- helpers ---

    private TypeDefinition? GetType(string fullName) =>
        _module.Types.FirstOrDefault(t => t.FullName == fullName);

    private TypeDefinition? GetNestedType(string declaringTypeFullName, string nestedTypeName) =>
        GetType(declaringTypeFullName)?.NestedTypes.FirstOrDefault(t => t.Name == nestedTypeName);
}
