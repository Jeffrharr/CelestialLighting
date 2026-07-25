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

    // --- GenCelestial (Patch_ShadowDirection, Patch_ShadowTilt) ---

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
        var type = GetType("Verse.SkyColorSet");
        Assert.That(type, Is.Not.Null, "Verse.SkyColorSet no longer exists");
        foreach (var fieldName in new[] { "sky", "shadow", "overlay", "saturation" })
        {
            Assert.That(type!.Fields.Any(f => f.Name == fieldName && f.IsPublic), Is.True,
                $"SkyColorSet.{fieldName} no longer exists or is no longer public");
        }
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

    // --- SectionLayer_SunShadows / SectionLayer / Section (Patch_ShadowTilt, Patch_ShadowMeshPerimeter) ---

    [Test]
    public void SectionLayer_SunShadows_DrawLayer_Exists()
    {
        var type = GetType("Verse.SectionLayer_SunShadows");
        Assert.That(type, Is.Not.Null, "Verse.SectionLayer_SunShadows no longer exists");
        var method = type!.Methods.SingleOrDefault(m => m.Name == "DrawLayer" && m.Parameters.Count == 0);
        Assert.That(method, Is.Not.Null, "SectionLayer_SunShadows.DrawLayer() no longer exists");
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
    public void RoofGrid_Roofed_Exists()
    {
        var type = GetType("Verse.RoofGrid");
        Assert.That(type, Is.Not.Null, "Verse.RoofGrid no longer exists");
        Assert.That(type!.Methods.Any(m => m.Name == "Roofed" && m.IsPublic && m.Parameters.Count == 1
                && m.Parameters[0].ParameterType.Name == "IntVec3"),
            Is.True, "RoofGrid.Roofed(IntVec3) no longer exists — Patch_IndoorSkyOcclusion reads it per cell");
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

    // --- SkyManager glow accessors (Patch_BrightnessFloor: the accessibility minimum-brightness floor) ---

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

    // --- helpers ---

    private TypeDefinition? GetType(string fullName) =>
        _module.Types.FirstOrDefault(t => t.FullName == fullName);

    private TypeDefinition? GetNestedType(string declaringTypeFullName, string nestedTypeName) =>
        GetType(declaringTypeFullName)?.NestedTypes.FirstOrDefault(t => t.Name == nestedTypeName);
}
