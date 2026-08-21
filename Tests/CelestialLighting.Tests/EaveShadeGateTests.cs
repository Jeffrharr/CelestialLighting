using System.Runtime.CompilerServices;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CelestialLighting.Tests;

/// <summary>
/// Structural tests for the eave shade layer's <c>!Visible</c> early return (§15b).
/// </summary>
/// <remarks>
/// <para>
/// The direct twin of <c>NightDesaturationGateTests</c>' gate half, and deliberately the same shape:
/// the two layers had the same bug — <c>Verse.Section.TryUpdate</c> calls <c>Regenerate</c> without
/// consulting <c>Visible</c>, so a feature the player switched off still bakes a mesh nothing draws —
/// and they now carry the same fix, so a reader who has learned one has learned both.
/// </para>
/// <para>
/// These read the IL of the shipped <c>CelestialLighting.dll</c> rather than calling anything,
/// because <c>Regenerate</c> cannot run offline: it takes a <c>Verse.Section</c>, writes a
/// <c>LayerSubMesh</c> and asks a live map's room grid whether a cell is an eave. What they pin is
/// exactly what a unit test cannot reach — that the gate is *in front of* the work, that it is a
/// return rather than a flag, and that it leaves nothing drawable behind. Which cells the mesh ends
/// up shading is <c>EavesMathTests</c>' job, and that does run.
/// </para>
/// <para>
/// They run against the last <c>./build.sh</c> output, so <c>./build.sh</c> before <c>./test.sh</c>
/// or these are inspecting a stale assembly. They self-ignore rather than fail when it is absent,
/// matching <c>ApiCompatibilityTests</c>' treatment of a missing <c>Assembly-CSharp.dll</c>.
/// </para>
/// </remarks>
[TestFixture]
[Category("RequiresModDll")]
public class EaveShadeGateTests
{
    private const string LayerTypeName = "CelestialLighting.SectionLayer_EaveShade";

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
        // TryUpdate calls Regenerate without consulting Visible (only DrawLayer does), so this branch
        // is the only thing standing between a player who has the eave feature switched off and the
        // full bake on every roof edit and every lamp toggle. If a later edit moves any work in front
        // of it, the layer silently goes back to costing 13.2 µs per section for nothing.
        MethodDefinition regenerate = Method(LayerTypeName, "Regenerate");
        MethodReference? firstCall = Calls(regenerate).FirstOrDefault();

        Assert.That(firstCall, Is.Not.Null, "Regenerate calls nothing at all — did the body change shape?");
        Assert.That(firstCall!.Name, Is.EqualTo("get_Visible"),
            $"Regenerate's first call is {firstCall.Name}, not the Visible gate — work moved in front of it");
    }

    [Test]
    public void Regenerate_ReturnsBeforeItBakesAnything()
    {
        // The gate has to be a return, not a flag: the expensive halves are the per-cell room queries
        // behind EaveCells.IsEave and the Unity mesh upload in FinalizeMesh, and a gate that merely
        // recorded "invisible" and carried on would cost exactly the same.
        MethodDefinition regenerate = Method(LayerTypeName, "Regenerate");
        IList<Instruction> body = regenerate.Body.Instructions;

        int firstGate = IndexOfCall(body, "get_Visible");
        int firstReturn = IndexOf(body, i => i.OpCode == OpCodes.Ret);
        int firstBake = IndexOfCall(body, "GetSubMesh");

        Assert.Multiple(() =>
        {
            Assert.That(firstGate, Is.GreaterThanOrEqualTo(0), "Regenerate never reads Visible");
            Assert.That(firstReturn, Is.GreaterThan(firstGate),
                "Regenerate's first `ret` is not behind the Visible check — there is no early return");
            Assert.That(firstBake, Is.GreaterThan(firstReturn),
                "the sub-mesh is fetched before the early return, so the gate saves nothing");
        });
    }

    [Test]
    public void TheCellLoopIsBehindTheGateAndIsWhatReachesTheRoomQuery()
    {
        // AddCellColors is the 289-cell loop body, and it is the only path from this layer to
        // EaveCells.IsEave — the room query that makes a roofed section cost more than a bare one
        // (SectionRegenerateTimingProbe.SampleSections deliberately samples roofed sections for
        // exactly that reason). Both halves are asserted here rather than assumed: if the query ever
        // moves out of AddCellColors, placing the loop behind the gate stops proving the query is.
        MethodDefinition regenerate = Method(LayerTypeName, "Regenerate");
        int firstReturn = IndexOf(regenerate.Body.Instructions, i => i.OpCode == OpCodes.Ret);
        int cellLoop = IndexOfCall(regenerate.Body.Instructions, "AddCellColors");

        List<MethodDefinition> reachTheRoomQuery = Type(LayerTypeName).Methods
            .Where(m => m.HasBody && Calls(m).Any(c => c.Name == "IsEave"))
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(cellLoop, Is.GreaterThan(firstReturn),
                "Regenerate's per-cell loop runs before the early return — the gate saves nothing");
            Assert.That(reachTheRoomQuery.Select(m => m.Name), Is.EquivalentTo(new[] { "AddCellColors" }),
                "EaveCells.IsEave is reached from somewhere other than the gated cell loop");
        });
    }

    [Test]
    public void Regenerate_LeavesNoDrawableMeshBehindWhenInvisible()
    {
        // Half the gate's job. TryUpdate clears the layer's Dirty flag even when Regenerate returns
        // early, so a mesh baked before the toggle would otherwise stay in subMeshes describing a
        // roofline that has since been rebuilt, ready to be drawn the moment the feature came back.
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
    public void DiscardMesh_DoesNotReuploadTheMeshToTheGpu()
    {
        // Why `disabled = true` rather than Clear + FinalizeMesh: the re-upload is a large part of the
        // cost the gate exists to avoid, so a "tidier" discard that cleared the colours would give
        // back most of the saving while still passing every other test in this file.
        MethodDefinition discard = Method(LayerTypeName, "DiscardMesh");
        List<string> calls = Calls(discard).Select(c => c.Name).ToList();

        Assert.That(calls.Any(c => c == "FinalizeMesh" || c == "Clear"), Is.False,
            "DiscardMesh clears or finalizes the mesh — that re-uploads it to the GPU, which is most of what the gate saves");
    }

    [Test]
    public void Visible_ReadsTheFeatureToggleItself()
    {
        // Pins the gate to the user-facing setting rather than to some cached copy of it, which is what
        // makes an off-toggle actually stop the work. EaveShade, not EaveShadows: the two are separate
        // flags (the caster and the shade), and this layer is only the shade.
        MethodDefinition visible = Method(LayerTypeName, "get_Visible");
        bool readsFlag = visible.Body.Instructions.Any(i =>
            i.OpCode == OpCodes.Ldsfld && (i.Operand as FieldReference)?.Name == "EaveShade");

        Assert.That(readsFlag, Is.True,
            "SectionLayer_EaveShade.Visible no longer reads CelestialLightingFeatures.EaveShade");
    }

    // --- Toggling the feature back on mid-game ---

    [Test]
    public void ApplyToRuntime_RebuildsTheShadeMeshesWhenTheToggleMoves()
    {
        // The other half of the mid-game toggle, and it only became load-bearing for this layer once
        // the gate landed. Because the bake is skipped while the feature is off AND TryUpdate clears
        // Dirty regardless, nothing on the map is marked dirty by the time a player ticks the box back
        // on — without this call the shade stays missing until the next roof or wall edit, which reads
        // as "the setting did nothing".
        MethodDefinition apply = Method("CelestialLighting.CelestialLightingSettings", "ApplyToRuntime");
        bool syncs = Calls(apply).Any(c =>
            c.Name == "SyncTo" && c.DeclaringType.Name == "EaveShadowRedraw");

        Assert.That(syncs, Is.True,
            "ApplyToRuntime no longer calls EaveShadowRedraw.SyncTo — toggling the eave feature back on leaves the map unshaded");
    }

    [Test]
    public void TheRebuildStillReachesThisLayersSubscription()
    {
        // EaveShadowRedraw dirties Buildings, which is the flag SectionLayer_SunShadows subscribes to.
        // It reaches this layer only because this layer subscribes to Buildings too. That was a free
        // side effect before the gate; now it is the delivery mechanism, so pin both ends — a future
        // narrowing of either would strand the shade off.
        MethodDefinition rebuild = Method("CelestialLighting.EaveShadowRedraw", "RebuildShadowMeshes");
        MethodDefinition constructor = Method(LayerTypeName, ".ctor");

        Assert.Multiple(() =>
        {
            Assert.That(Calls(rebuild).Any(c => c.Name == "WholeMapChanged"), Is.True,
                "the rebuild no longer dirties the map's sections");
            Assert.That(ReadsFlagDef(rebuild, "Buildings"), Is.True,
                "EaveShadowRedraw no longer dirties Buildings");
            Assert.That(ReadsFlagDef(constructor, "Buildings"), Is.True,
                "SectionLayer_EaveShade dropped its Buildings subscription — EaveShadowRedraw can no longer reach it");
        });
    }

    // --- Helpers ---

    // MapMeshFlagDefOf's fields are static, so a subscription or a dirty call names its flag with a
    // plain ldsfld; that is what these read.
    private static bool ReadsFlagDef(MethodDefinition method, string flagName) =>
        method.Body.Instructions.Any(i =>
            i.OpCode == OpCodes.Ldsfld
            && (i.Operand as FieldReference)?.Name == flagName
            && (i.Operand as FieldReference)?.DeclaringType.Name == "MapMeshFlagDefOf");

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
