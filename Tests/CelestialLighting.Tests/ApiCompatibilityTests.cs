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

    // --- GenDate / WorldGrid / Map (LatitudeEffect) ---

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

    // --- SectionLayer_SunShadows / SectionLayer / Section (Patch_ShadowTilt) ---

    [Test]
    public void SectionLayer_SunShadows_DrawLayer_Exists()
    {
        var type = GetType("Verse.SectionLayer_SunShadows");
        Assert.That(type, Is.Not.Null, "Verse.SectionLayer_SunShadows no longer exists");
        var method = type!.Methods.SingleOrDefault(m => m.Name == "DrawLayer" && m.Parameters.Count == 0);
        Assert.That(method, Is.Not.Null, "SectionLayer_SunShadows.DrawLayer() no longer exists");
    }

    [Test]
    public void SectionLayer_HasProtectedSectionField()
    {
        var type = GetType("Verse.SectionLayer");
        Assert.That(type, Is.Not.Null, "Verse.SectionLayer no longer exists");
        var field = type!.Fields.SingleOrDefault(f => f.Name == "section");
        Assert.That(field, Is.Not.Null,
            "SectionLayer.section field no longer exists — Patch_ShadowTilt's reflection accessor will fail");
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
    }

    [Test]
    public void LayerSubMesh_HasExpectedPublicFields()
    {
        var type = GetType("Verse.LayerSubMesh");
        Assert.That(type, Is.Not.Null, "Verse.LayerSubMesh no longer exists");
        foreach (var fieldName in new[] { "finalized", "disabled", "material", "renderLayer", "mesh" })
        {
            Assert.That(type!.Fields.Any(f => f.Name == fieldName && f.IsPublic), Is.True,
                $"LayerSubMesh.{fieldName} no longer exists or is no longer public");
        }
    }

    [Test]
    public void ShaderPropertyIDs_MapSunLightDirection_Exists()
    {
        var type = GetType("Verse.ShaderPropertyIDs");
        Assert.That(type, Is.Not.Null, "Verse.ShaderPropertyIDs no longer exists");
        var field = type!.Fields.SingleOrDefault(f => f.Name == "MapSunLightDirection");
        Assert.That(field, Is.Not.Null, "ShaderPropertyIDs.MapSunLightDirection no longer exists");
    }

    // --- helpers ---

    private TypeDefinition? GetType(string fullName) =>
        _module.Types.FirstOrDefault(t => t.FullName == fullName);

    private TypeDefinition? GetNestedType(string declaringTypeFullName, string nestedTypeName) =>
        GetType(declaringTypeFullName)?.NestedTypes.FirstOrDefault(t => t.Name == nestedTypeName);
}
