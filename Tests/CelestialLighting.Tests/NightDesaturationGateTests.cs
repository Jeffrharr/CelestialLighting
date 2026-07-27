using System.Runtime.CompilerServices;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CelestialLighting.Tests;

/// <summary>
/// Structural tests for §9's <c>!Visible</c> early return and its one-glow-query-per-cell shape.
/// </summary>
/// <remarks>
/// <para>
/// These read the IL of the shipped <c>CelestialLighting.dll</c> rather than calling anything,
/// because both properties live in a method that cannot run offline: <c>Regenerate</c> takes a
/// <c>Verse.Section</c>, writes a <c>LayerSubMesh</c> and reads a live <c>GlowGrid</c>. What they
/// pin is exactly what a unit test cannot reach — that the gate is *in front of* the work, that the
/// work asks the glow grid from one place, and that flipping the setting still rebuilds the meshes.
/// The arithmetic those meshes end up carrying is covered by <c>NightWashWindowTests</c>, which does
/// run.
/// </para>
/// <para>
/// They run against the last <c>./build.sh</c> output, so <c>./build.sh</c> before <c>./test.sh</c>
/// or these are inspecting a stale assembly. They self-ignore rather than fail when it is absent,
/// matching <c>ApiCompatibilityTests</c>' treatment of a missing <c>Assembly-CSharp.dll</c>.
/// </para>
/// </remarks>
[TestFixture]
[Category("RequiresModDll")]
public class NightDesaturationGateTests
{
    private const string LayerTypeName = "CelestialLighting.SectionLayer_NightDesaturation";

    private ModuleDefinition _module = null!;

    // Resolved from this file's own compile-time path rather than the test binary's working directory,
    // which moves with the target framework and the runner.
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

    // --- The gate ---

    [Test]
    public void Regenerate_AsksVisibleBeforeItDoesAnythingElse()
    {
        // Verse.Section.TryUpdate calls Regenerate without consulting Visible (only DrawLayer does), so
        // this branch is the only thing standing between a player who has §9 switched off and the full
        // 271 µs bake on every lamp toggle. If a later edit moves any work in front of it, the layer
        // silently goes back to costing what it cost.
        MethodDefinition regenerate = Method(LayerTypeName, "Regenerate");
        MethodReference? firstCall = Calls(regenerate).FirstOrDefault();

        Assert.That(firstCall, Is.Not.Null, "Regenerate calls nothing at all — did the body change shape?");
        Assert.That(firstCall!.Name, Is.EqualTo("get_Visible"),
            $"Regenerate's first call is {firstCall.Name}, not the Visible gate — work moved in front of it");
    }

    [Test]
    public void Regenerate_ReturnsBeforeTouchingTheGlowGrid()
    {
        // The gate has to be a return, not a flag: the expensive half is the glow queries plus the mesh
        // upload, and a gate that merely records "invisible" and carries on would cost the same.
        MethodDefinition regenerate = Method(LayerTypeName, "Regenerate");
        IList<Instruction> body = regenerate.Body.Instructions;

        int firstGate = IndexOfCall(body, "get_Visible");
        int firstReturn = IndexOf(body, i => i.OpCode == OpCodes.Ret);
        int firstBake = IndexOfCall(body, "ResolveWash");

        Assert.Multiple(() =>
        {
            Assert.That(firstGate, Is.GreaterThanOrEqualTo(0), "Regenerate never reads Visible");
            Assert.That(firstReturn, Is.GreaterThan(firstGate),
                "Regenerate's first `ret` is not behind the Visible check — there is no early return");
            Assert.That(firstBake, Is.GreaterThan(firstReturn),
                "the wash window is resolved before the early return, so the gate saves nothing");
        });
    }

    [Test]
    public void Regenerate_LeavesNoDrawableMeshBehindWhenInvisible()
    {
        // Half the gate's job. TryUpdate clears the layer's Dirty flag even when Regenerate returns
        // early, so a mesh baked before the toggle would otherwise stay in subMeshes describing a glow
        // grid that has since moved, ready to be drawn the moment the feature came back.
        MethodDefinition discard = Method(LayerTypeName, "DiscardMesh");
        bool disablesSubMeshes = discard.Body.Instructions.Any(i =>
            i.OpCode == OpCodes.Stfld && (i.Operand as FieldReference)?.Name == "disabled");

        Assert.Multiple(() =>
        {
            Assert.That(disablesSubMeshes, Is.True,
                "DiscardMesh no longer writes LayerSubMesh.disabled — an invisible layer can hold a stale mesh");
            Assert.That(Calls(Method(LayerTypeName, "Regenerate")).Any(c => c.Name == "DiscardMesh"), Is.True,
                "Regenerate's early return no longer discards the mesh");
        });
    }

    [Test]
    public void Visible_ReadsTheFeatureToggleItself()
    {
        // Pins the gate to the user-facing setting rather than to some cached copy of it, which is what
        // makes an off-toggle actually stop the work.
        MethodDefinition visible = Method(LayerTypeName, "get_Visible");
        bool readsFlag = visible.Body.Instructions.Any(i =>
            i.OpCode == OpCodes.Ldsfld && (i.Operand as FieldReference)?.Name == "LowLightDesaturation");

        Assert.That(readsFlag, Is.True,
            "SectionLayer_NightDesaturation.Visible no longer reads CelestialLightingFeatures.LowLightDesaturation");
    }

    // --- Toggling the feature back on mid-game ---

    [Test]
    public void ApplyToRuntime_RebuildsTheWashMeshesWhenTheToggleMoves()
    {
        // The other half of the mid-game toggle. Because the bake is skipped while the feature is off
        // AND TryUpdate clears Dirty regardless, nothing on the map is marked dirty by the time a
        // player ticks the box back on — without this call the wash stays missing until the next lamp
        // or roof edit, which reads as "the setting did nothing" (the failure §7b and §15 both hit).
        MethodDefinition apply = Method("CelestialLighting.CelestialLightingSettings", "ApplyToRuntime");
        bool syncs = Calls(apply).Any(c =>
            c.Name == "SyncTo" && c.DeclaringType.Name == "NightDesaturationRedraw");

        Assert.That(syncs, Is.True,
            "ApplyToRuntime no longer calls NightDesaturationRedraw.SyncTo — toggling §9 back on leaves the map unwashed");
    }

    [Test]
    public void Redraw_DirtiesEveryMapRatherThanWaitingForOne()
    {
        MethodDefinition sync = Method("CelestialLighting.NightDesaturationRedraw", "SyncTo");
        MethodDefinition force = Method("CelestialLighting.NightDesaturationRedraw", "ForceRebuild");
        MethodDefinition rebuild = Method("CelestialLighting.NightDesaturationRedraw", "RebuildWashMeshes");

        Assert.Multiple(() =>
        {
            // Change-detected: ApplyToRuntime runs every frame the settings window is open, and a
            // whole-map rebuild at 60 Hz would be worse than the cost this whole change removes.
            Assert.That(Calls(sync).Any(c => c.Name == "RebuildWashMeshes"), Is.True,
                "SyncTo no longer rebuilds anything");
            Assert.That(Calls(force).Any(c => c.Name == "RebuildWashMeshes"), Is.True,
                "ForceRebuild no longer rebuilds anything — the harness's SetFeature step would A/B one bake twice");
            Assert.That(Calls(rebuild).Any(c => c.Name == "WholeMapChanged"), Is.True,
                "the rebuild no longer dirties the map's sections");
        });
    }

    // --- The memoisation, structurally ---

    [Test]
    public void TheLayerAsksTheGlowGridFromExactlyOnePlace()
    {
        // Before the window there were nine GroundGlowAt call sites' worth of reads coming out of one
        // WashAt, run once per neighbour per cell. One call site, inside the fill loop, is what makes
        // "once per cell" enforceable at all — a second one anywhere in the type would mean some read
        // path had gone back around the window.
        TypeDefinition layer = Type(LayerTypeName);
        int callSites = layer.Methods
            .Where(m => m.HasBody)
            .SelectMany(m => Calls(m))
            .Count(c => c.Name == "GroundGlowAt");

        Assert.That(callSites, Is.EqualTo(1),
            "SectionLayer_NightDesaturation should read GlowGrid.GroundGlowAt from exactly one place (the window fill)");
    }

    [Test]
    public void TheVertexLoopReadsTheWindowAndNotTheMap()
    {
        // AddCellColors is the nine-reads-per-cell loop. It must resolve its neighbours out of the baked
        // window; if it can reach the map at all, the memoisation is not actually load-bearing.
        MethodDefinition addCellColors = Method(LayerTypeName, "AddCellColors");
        List<MethodReference> calls = Calls(addCellColors).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(calls.Count(c => c.Name == "At"), Is.EqualTo(9),
                "AddCellColors should read exactly the nine cells it averages out of the window");
            Assert.That(calls.Any(c => c.Name == "GroundGlowAt"), Is.False,
                "AddCellColors reaches the glow grid directly — the window is being bypassed");
        });
    }

    // --- Helpers ---

    private static IEnumerable<MethodReference> Calls(MethodDefinition method) =>
        method.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            .Select(i => (MethodReference)i.Operand);

    private static int IndexOfCall(IList<Instruction> body, string methodName) =>
        IndexOf(body, i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            && ((MethodReference)i.Operand).Name == methodName);

    private static int IndexOf(IList<Instruction> body, Func<Instruction, bool> predicate)
    {
        for (int i = 0; i < body.Count; i++)
        {
            if (predicate(body[i]))
                return i;
        }

        return -1;
    }

    private TypeDefinition Type(string fullName)
    {
        TypeDefinition? type = _module.GetType(fullName);
        Assert.That(type, Is.Not.Null, $"{fullName} no longer exists");
        return type!;
    }

    private MethodDefinition Method(string typeFullName, string methodName)
    {
        MethodDefinition? method = Type(typeFullName).Methods.FirstOrDefault(m => m.Name == methodName);
        Assert.That(method, Is.Not.Null, $"{typeFullName}.{methodName} no longer exists");
        return method!;
    }

    private static string ThisFile([CallerFilePath] string path = "") => path;
}
