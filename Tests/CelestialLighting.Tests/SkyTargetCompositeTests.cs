using System.Runtime.CompilerServices;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CelestialLighting.Tests;

/// <summary>
/// Structural tests for <c>Patch_SkyTargetComposite</c>, the mod's single Postfix on
/// <c>WeatherWorker.CurSkyTarget</c>.
/// </summary>
/// <remarks>
/// <para>
/// Folding fourteen separate <c>[HarmonyPatch]</c> registrations into one composite removed a
/// failure mode (an accidental, filename-derived composition order) and introduced a different one:
/// a subsystem can now define a perfectly good <c>Apply</c> stage that <em>nothing calls</em>. Under
/// the old arrangement the <c>[HarmonyPatch]</c> attribute was the wiring, so a stage that existed
/// necessarily ran; now the wiring is one line in one file, and forgetting it is silent. The effect
/// simply never appears, every offline test still passes because the pure core is untouched, and the
/// feature flag toggles nothing. That is precisely the class of bug this repo's verification bar
/// exists to catch, so it gets a test rather than a comment.
/// </para>
/// <para>
/// These read the IL of the shipped <c>CelestialLighting.dll</c> rather than calling anything,
/// because the composite takes a <c>Verse.Map</c> and a <c>SkyTarget</c> and cannot run offline.
/// They run against the last <c>./build.sh</c> output, so <c>./build.sh</c> before <c>./test.sh</c>
/// or they are inspecting a stale assembly. They self-ignore rather than fail when it is absent,
/// matching <c>NightDesaturationGateTests</c> and <c>ApiCompatibilityTests</c>.
/// </para>
/// </remarks>
[TestFixture]
[Category("RequiresModDll")]
public class SkyTargetCompositeTests
{
    private const string CompositeTypeName = "CelestialLighting.Patch_SkyTargetComposite";

    // The stage signature, as the composite calls it. Anything matching this in the shipped assembly
    // is a sky stage and is expected to be wired in.
    private const string StageMethodName = "Apply";

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

    // --- The wiring ---

    [Test]
    public void EverySkyStageInTheAssemblyIsCalledByTheComposite()
    {
        // The whole point. A stage that exists but is never called is invisible to every other test
        // in this project, because the pure core it wraps is still perfectly correct.
        List<string> defined = SkyStageTypes().Select(t => t.FullName).OrderBy(n => n).ToList();
        List<string> called = StagesCalledByComposite().OrderBy(n => n).ToList();

        Assert.That(defined, Is.Not.Empty, "no sky stages found at all — has the Apply signature changed?");
        Assert.That(called, Is.EquivalentTo(defined),
            "a sky stage is defined but never wired into Patch_SkyTargetComposite (or vice versa). "
            + $"Defined: {string.Join(", ", defined)}. Called: {string.Join(", ", called)}");
    }

    [Test]
    public void CompositeCallsEachStageExactlyOnce()
    {
        // A stage run twice is not a no-op: every one of them either lerps toward a colour or scales
        // one, so a duplicated call silently doubles that subsystem's strength. Cheap to do with a
        // copy-paste when adding a stage to the list, and invisible on any frame where the effect is
        // near zero anyway — see the repo's own "shadow darkening is idempotent" trap for how this
        // kind of double-count reads when it finally shows up.
        List<string> called = StagesCalledByComposite().ToList();
        IEnumerable<string> duplicated = called.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key);

        Assert.That(duplicated, Is.Empty, "a sky stage is called more than once by the composite");
    }

    [Test]
    public void CompositeIsTheOnlyPostfixOnCurSkyTarget()
    {
        // The claim the merge makes. If a later change adds a fresh [HarmonyPatch] on CurSkyTarget
        // instead of a stage, the composition order stops being the list in Patch_SkyTargetComposite
        // and goes back to being decided by Harmony's registration order — which is assembly metadata
        // order, i.e. alphabetical by filename. That is the exact fragility this class removed, and it
        // would come back without anything failing.
        List<string> patchers = _module.Types
            .Where(PatchesCurSkyTarget)
            .Select(t => t.FullName)
            .OrderBy(n => n)
            .ToList();

        Assert.That(patchers, Is.EqualTo(new List<string> { CompositeTypeName }),
            "something other than Patch_SkyTargetComposite now patches WeatherWorker.CurSkyTarget — "
            + "it should almost certainly be a stage in the composite's ordered list instead");
    }

    [Test]
    public void StagesDoNotCarryHarmonyPatchAttributes()
    {
        // A leftover [HarmonyPatch] on a stage would register it AND leave it in the composite's list,
        // running that subsystem twice per call — the CompositeCallsEachStageExactlyOnce failure mode
        // arriving by a route that test cannot see.
        IEnumerable<string> attributed = SkyStageTypes()
            .Where(t => t.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPatch"))
            .Select(t => t.FullName);

        Assert.That(attributed, Is.Empty,
            "a sky stage still carries [HarmonyPatch] — it would run twice, once via Harmony and once "
            + "via Patch_SkyTargetComposite");
    }

    [Test]
    public void PostfixKeepsHarmonysMagicResultParameterName()
    {
        // `__result` is matched by STRING at patch time. Renaming it to something more readable — which
        // is a very natural thing to do to a parameter that is then forwarded to fourteen stages taking
        // `target` — makes Harmony look for a real parameter of that name on CurSkyTarget(Map), fail to
        // find one, and throw out of PatchAll. PatchAll runs in CelestialLightingMod's static
        // constructor, so that takes down every patch in the mod, not just this one.
        //
        // The reason this is a test and not a comment is how it presents: RimWorld swallows the static
        // constructor exception into Player.log and carries on, so the game runs, a harness scenario
        // reports pass=True, and the screenshots look like a plausible sky — because they are vanilla's.
        // Only a measured A/B catches it. This catches it in 40 ms instead.
        MethodDefinition postfix = Method(CompositeTypeName, "Postfix");
        IEnumerable<string> names = postfix.Parameters.Select(p => p.Name);

        Assert.That(names, Does.Contain("__result"),
            "the composite's Postfix no longer takes a parameter named __result — Harmony matches that "
            + "name as a string, so PatchAll will throw and every patch in the mod will fail to apply");
    }

    [Test]
    public void CompositeBuildsExactlyOneSkyInputsPerPass()
    {
        // The per-pass cache only pays for itself if the whole pass shares one. Constructing it inside
        // a stage, or once per stage, is the shape a later refactor is most likely to reach for when
        // adding a stage that wants a value — and it would leave every lookup going back through
        // GeometryMemo exactly as before, with nothing else in the repo showing a difference.
        MethodDefinition postfix = Method(CompositeTypeName, "Postfix");
        int constructed = postfix.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Newobj)
            .Select(i => (MethodReference)i.Operand)
            .Count(r => r.Name == ".ctor" && r.DeclaringType.FullName == "CelestialLighting.SkyInputs");

        Assert.That(constructed, Is.EqualTo(1),
            "the composite should build exactly one SkyInputs and pass it to every stage by ref");
    }

    // --- Helpers ---

    // A "sky stage" is any type in the assembly exposing the exact method the composite calls:
    // static void Apply(Verse.Map, ref SkyInputs, ref Verse.SkyTarget). Discovered from the assembly
    // rather than listed here on purpose — a hard-coded list would need updating by the same person
    // who forgot to update the composite, so it would pass in exactly the case the test exists to catch.
    //
    // The `SkyInputs&` clause is load-bearing beyond discovery. SkyInputs is a mutable struct used as
    // a per-pass cache, so a stage taking it BY VALUE still returns every correct answer — it just
    // fills a copy that is discarded, and the repeated lookups the type exists to remove quietly come
    // back. Nothing else in the repo can see that: no pixel moves, no probe shifts, and the only
    // symptom is a call count in a profiler nobody re-reads. Requiring the by-ref form here means such
    // a stage stops being discovered and fails the wiring test above instead.
    private IEnumerable<TypeDefinition> SkyStageTypes() =>
        _module.Types.Where(t => t.Methods.Any(IsSkyStageMethod));

    private static bool IsSkyStageMethod(MethodDefinition m) =>
        m.Name == StageMethodName
        && m.IsStatic
        && m.ReturnType.FullName == "System.Void"
        && m.Parameters.Count == 3
        && m.Parameters[0].ParameterType.FullName == "Verse.Map"
        && m.Parameters[1].ParameterType.FullName == "CelestialLighting.SkyInputs&"
        && m.Parameters[2].ParameterType.FullName == "Verse.SkyTarget&";

    private IEnumerable<string> StagesCalledByComposite()
    {
        MethodDefinition postfix = Method(CompositeTypeName, "Postfix");
        return postfix.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Call)
            .Select(i => (MethodReference)i.Operand)
            .Where(r => r.Name == StageMethodName)
            .Select(r => r.DeclaringType.FullName);
    }

    private static bool PatchesCurSkyTarget(TypeDefinition type) =>
        type.CustomAttributes.Any(a =>
            a.AttributeType.Name == "HarmonyPatch"
            && a.ConstructorArguments.Any(arg => (arg.Value as string) == "CurSkyTarget"));

    private TypeDefinition Type(string typeFullName)
    {
        TypeDefinition? type = _module.GetType(typeFullName);
        Assert.That(type, Is.Not.Null, $"{typeFullName} no longer exists");
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
