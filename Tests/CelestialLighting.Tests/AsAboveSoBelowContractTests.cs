using System.Runtime.CompilerServices;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CelestialLighting.Tests;

/// <summary>
/// Pins the "As above, So below II" members <c>AsAboveSoBelowCompat</c> binds by string.
/// </summary>
/// <remarks>
/// <para>
/// Every member that class touches is resolved by name at runtime, so nothing about the interop
/// fails at compile time. Upstream can rename a field and our compat degrades to "AASB2 treated as
/// absent" — correct, deliberate, and completely silent unless someone reads the log. These tests
/// are what turn that into an offline failure with the member's name in it.
/// </para>
/// <para>
/// They read their installed assembly with Cecil rather than loading it, since it references
/// <c>Assembly-CSharp</c> and Unity and cannot be loaded in a net8.0 test host. They self-ignore
/// when AASB2 is not subscribed, matching <c>ApiCompatibilityTests</c>' treatment of a missing
/// <c>Assembly-CSharp.dll</c>: most machines running this suite will not have their mod, and a hard
/// failure there would say nothing about our code.
/// </para>
/// </remarks>
[TestFixture]
[Category("RequiresAsAboveSoBelow")]
public class AsAboveSoBelowContractTests
{
    // Their Workshop id, hard-coded the same way ApiCompatibilityTests hard-codes RimWorld's install
    // path — this suite already assumes one developer machine, and a wrong path self-ignores rather
    // than reporting a false pass.
    private const string DllPath =
        "/home/deck/.local/share/Steam/steamapps/workshop/content/294100/3776015553/Assemblies/AsAboveSoBelow.dll";

    private const string BandsTypeName = "AsAboveSoBelow.ABBands";
    private const string GuardTypeName = "AsAboveSoBelow.ABGuard";
    private const string LayerTypeName = "AsAboveSoBelow.SectionLayer_ABBelowLighting";

    private ModuleDefinition _module = null!;

    [OneTimeSetUp]
    public void LoadAssembly()
    {
        if (!File.Exists(DllPath))
            Assert.Ignore($"As above, So below II not installed at {DllPath} — the interop it pins cannot be checked.");
        _module = ModuleDefinition.ReadModule(DllPath);
    }

    [OneTimeTearDown]
    public void Dispose() => _module?.Dispose();

    // --- The band API: what OwnsOverlay asks ---

    [Test]
    public void ABBands_BandedTakesAMapAndReturnsBool()
    {
        MethodDefinition banded = Method(BandsTypeName, "Banded");

        Assert.Multiple(() =>
        {
            Assert.That(banded.IsStatic, Is.True, "ABBands.Banded must be static — we bind it as a Func<Map, bool>.");
            Assert.That(banded.IsPublic, Is.True, "ABBands.Banded must be public.");
            Assert.That(banded.ReturnType.FullName, Is.EqualTo("System.Boolean"));
            Assert.That(banded.Parameters.Select(p => p.ParameterType.FullName), Is.EqualTo(new[] { "Verse.Map" }));
        });
    }

    // The guard half of the stand-down. Its absence is the failure that matters most and reads least
    // like one: without it we would stand down on a banded map whose renderer they had already
    // disabled, leaving vanilla's overlay visible with our contribution silently missing from it.
    [Test]
    public void ABGuard_ExposesARenderingSwitchAndAnOnPredicateOverIt()
    {
        TypeDefinition guard = Type(GuardTypeName);
        FieldDefinition? rendering = guard.Fields.SingleOrDefault(f => f.Name == "Rendering");

        Assert.That(rendering, Is.Not.Null, "ABGuard.Rendering is gone; the stand-down cannot tell a live renderer from a disabled one.");
        Assert.That(rendering!.IsStatic, Is.True, "ABGuard.Rendering must be static — we close a delegate over its value once.");

        MethodDefinition on = Method(GuardTypeName, "On");

        Assert.Multiple(() =>
        {
            Assert.That(on.IsStatic, Is.True);
            Assert.That(on.ReturnType.FullName, Is.EqualTo("System.Boolean"));
            Assert.That(
                on.Parameters.Select(p => p.ParameterType.FullName),
                Is.EqualTo(new[] { rendering.FieldType.FullName }),
                "ABGuard.On must take exactly the type ABGuard.Rendering holds — Delegate.CreateDelegate "
                + "binds that value as its first argument.");
        });
    }

    // --- The layer we postfix, and the mesh we read out of it ---

    [Test]
    public void BelowLightingLayer_IsASectionLayerWithARegenerateToPostfix()
    {
        TypeDefinition layer = Type(LayerTypeName);

        Assert.Multiple(() =>
        {
            Assert.That(
                layer.BaseType?.FullName, Is.EqualTo("Verse.SectionLayer"),
                "We type our postfix's __instance as Verse.SectionLayer and read `section` off it.");
            Assert.That(
                layer.Methods.Any(m => m.Name == "Regenerate" && m.Parameters.Count == 0), Is.True,
                "SectionLayer_ABBelowLighting.Regenerate is the method we postfix.");
        });
    }

    // The one private member the interop reads. Called out on its own so a failure names it, because
    // it is the member most likely to move in a tidy-up that renames nothing public — and it is the
    // one carrying the vertex list, without which vector lighting falls back to its pre-mask
    // behaviour on their maps.
    [Test]
    public void BelowLightingLayer_CachesTheLayerSubMeshWeCompositeOnto()
    {
        FieldDefinition? mesh = Type(LayerTypeName).Fields.SingleOrDefault(f => f.Name == "mesh");

        Assert.That(mesh, Is.Not.Null, "SectionLayer_ABBelowLighting.mesh is gone; we have no mesh to composite onto.");
        Assert.That(
            mesh!.FieldType.FullName, Is.EqualTo("Verse.LayerSubMesh"),
            "We read it as a Verse.LayerSubMesh, for both its Mesh and its vertex list.");
    }

    // THE ASSUMPTION THE INDEX ARITHMETIC RESTS ON, and the only test here about behaviour rather
    // than shape.
    //
    // Both of our passes index the mesh as vanilla's lattice: (Width+1)*(Height+1) corners followed by
    // Width*Height centres. That is true of their mesh only because they build it with vanilla's own
    // SectionLayer_LightingOverlay.Bake. If they ever hand-roll the geometry instead, every index we
    // compute is against a layout we are guessing at — and the guess would not throw, it would write
    // plausible colours into the wrong vertices. The count guards in ApplyOcclusion and
    // VectorLightMask.Apply catch a different SIZE; nothing at runtime catches a different ORDER.
    [Test]
    public void BelowLightingLayer_BuildsItsMeshWithVanillasOwnBake()
    {
        MethodDefinition regenerate = Type(LayerTypeName).Methods
            .Single(m => m.Name == "Regenerate" && m.Parameters.Count == 0);

        Assert.That(
            Calls(regenerate, "Verse.SectionLayer_LightingOverlay", "Bake"), Is.True,
            "Their Regenerate no longer bakes with SectionLayer_LightingOverlay.Bake, so we can no "
            + "longer assume its vertex order is vanilla's lattice. Our passes index it as if it were.");
    }

    // --- helpers ---

    private TypeDefinition Type(string fullName)
    {
        TypeDefinition? type = _module.GetType(fullName);
        Assert.That(type, Is.Not.Null, $"{fullName} is gone from As above, So below II; the interop cannot bind.");
        return type!;
    }

    private MethodDefinition Method(string typeName, string methodName)
    {
        MethodDefinition? method = Type(typeName).Methods.FirstOrDefault(m => m.Name == methodName);
        Assert.That(method, Is.Not.Null, $"{typeName}.{methodName} is gone; the interop cannot bind.");
        return method!;
    }

    private static bool Calls(MethodDefinition method, string declaringTypeFullName, string calleeName) =>
        method.HasBody
        && method.Body.Instructions.Any(i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            && i.Operand is MethodReference callee
            && callee.Name == calleeName
            && callee.DeclaringType.FullName == declaringTypeFullName);
}

/// <summary>
/// Pins our own half of the interop: that both overlay postfixes stand down when somebody else owns
/// the overlay, and that the compat runs both passes on the mesh they draw instead.
/// </summary>
/// <remarks>
/// These read the IL of the shipped <c>CelestialLighting.dll</c>, because none of it can run
/// offline — every method involved takes a <c>SectionLayer</c> or writes a <c>Mesh</c>. What they
/// pin is wiring a unit test cannot reach: delete either stand-down and the mod still builds, still
/// passes every other test, and goes back to baking alphas into a mesh nobody draws.
/// </remarks>
[TestFixture]
[Category("RequiresModDll")]
public class AsAboveSoBelowStandDownTests
{
    private const string CompatTypeName = "CelestialLighting.AsAboveSoBelowCompat";

    private ModuleDefinition _module = null!;

    private static string ModDllPath
    {
        get
        {
            string testsDir = Path.GetDirectoryName(ThisFile())!;
            string repoRoot = Path.GetFullPath(Path.Combine(testsDir, "..", ".."));
            return Path.Combine(repoRoot, "1.6", "Assemblies", "CelestialLighting.dll");
        }
    }

    [OneTimeSetUp]
    public void LoadAssembly()
    {
        if (!File.Exists(ModDllPath))
            Assert.Ignore($"CelestialLighting.dll not found at {ModDllPath} — run ./build.sh first.");
        _module = ModuleDefinition.ReadModule(ModDllPath);
    }

    [OneTimeTearDown]
    public void Dispose() => _module?.Dispose();

    [TestCase("CelestialLighting.Patch_IndoorSkyOcclusion")]
    [TestCase("CelestialLighting.Patch_VectorLightSuppress")]
    public void VanillaOverlayPostfix_StandsDownWhenTheOverlayIsOwnedElsewhere(string patchType)
    {
        MethodDefinition postfix = Method(patchType, "Postfix");

        Assert.That(
            Calls(postfix, CompatTypeName, "OwnsOverlay"), Is.True,
            $"{patchType}.Postfix no longer asks AsAboveSoBelowCompat.OwnsOverlay, so on a banded map "
            + "it writes vanilla's mesh — which is baked and never drawn there. That is the exact "
            + "shape of the bug a player reported as the indoor darkness slider doing nothing.");
    }

    [TestCase("CelestialLighting.Patch_IndoorSkyOcclusion")]
    [TestCase("CelestialLighting.Patch_VectorLightSuppress")]
    public void CompatPostfix_RunsBothPassesOnTheMeshTheyDraw(string patchType)
    {
        MethodDefinition postfix = Method(CompatTypeName, "RegeneratePostfix");

        Assert.That(
            Calls(postfix, patchType, "ApplyToMesh"), Is.True,
            $"AsAboveSoBelowCompat.RegeneratePostfix no longer calls {patchType}.ApplyToMesh, so that "
            + "pass is absent from their banded maps while the other still runs.");
    }

    // The mask is the shipped default, and delivering it is the concrete reason this interop reaches
    // for a LayerSubMesh rather than a colour array. Re-splitting ApplyToMesh into something taking
    // only colours would compile, pass every other test here, and silently hand banded maps the
    // pre-mask behaviour — vector lighting with none of its shadows.
    [Test]
    public void VectorLightApplyToMesh_CanStillReachTheMask()
    {
        MethodDefinition apply = Method("CelestialLighting.Patch_VectorLightSuppress", "ApplyToMesh");

        Assert.That(
            Calls(apply, "CelestialLighting.VectorLightMask", "Apply"), Is.True,
            "Patch_VectorLightSuppress.ApplyToMesh no longer reaches VectorLightMask.Apply, so every "
            + "caller — vanilla's overlay included — gets the flooring path only.");
    }

    private MethodDefinition Method(string typeName, string methodName)
    {
        TypeDefinition? type = _module.GetType(typeName);
        Assert.That(type, Is.Not.Null, $"{typeName} is gone from the shipped assembly.");

        MethodDefinition? method = type!.Methods.FirstOrDefault(m => m.Name == methodName);
        Assert.That(method, Is.Not.Null, $"{typeName}.{methodName} is gone from the shipped assembly.");
        return method!;
    }

    private static bool Calls(MethodDefinition method, string declaringTypeFullName, string calleeName) =>
        method.HasBody
        && method.Body.Instructions.Any(i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            && i.Operand is MethodReference callee
            && callee.Name == calleeName
            && callee.DeclaringType.FullName == declaringTypeFullName);

    private static string ThisFile([CallerFilePath] string path = "") => path;
}
