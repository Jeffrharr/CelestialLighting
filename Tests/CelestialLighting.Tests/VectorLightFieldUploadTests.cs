using System.Runtime.CompilerServices;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CelestialLighting.Tests;

/// <summary>
/// Structural tests for §27 phase 6's per-emitter field upload — the texture the fragment shader
/// samples vanilla's delivered glow from.
/// </summary>
/// <remarks>
/// <para>
/// These read the IL of the shipped <c>CelestialLighting.dll</c>, in the same style and for the same
/// reason as <see cref="NightDesaturationGateTests"/>: the thing that broke is not arithmetic, so no
/// pure core can hold it. It is a Unity API contract — <c>Texture2D.SetPixels32(Color32[])</c>
/// requires an array of EXACTLY width*height — and the only place that contract is visible offline is
/// the call site.
/// </para>
/// <para>
/// What it caught: <c>CopyField</c> uploaded through a shared, grow-only <c>Color32[]</c> scratch
/// buffer (`if (FieldPixels.Length &lt; count) FieldPixels = new Color32[count]`). Once any emitter
/// grew it, the next SMALLER emitter handed SetPixels32 an oversized array and Unity threw
/// "the size of data to be written would result in writing outside the target buffer bounds" — every
/// frame, from a Postfix on GameConditionManagerDraw, which takes the rest of that draw chain
/// (§11a's aurora, §23b's cloud underlight, §24's snow glare) down with it. A radius-3 torch next to
/// a radius-14 sun lamp is all it took, so it survived every scenario whose fixture lit one radius.
/// </para>
/// </remarks>
[TestFixture]
public class VectorLightFieldUploadTests
{
    private const string OverlayTypeName = "CelestialLighting.VectorLightOverlay";

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

    [Test]
    public void CopyField_DoesNotUploadThroughTheExactLengthOverload()
    {
        // SetPixels32(Color32[]) is not wrong in general — a gradient or a fixed-size field can use it
        // safely, because one texture size means one array size. It is wrong HERE, where the texture is
        // re-created per emitter at that emitter's diameter and the source is whatever the glow grid
        // handed over. Any future edit that reaches back for the array overload is reaching for the
        // exact bug this test exists to remember.
        MethodDefinition copyField = Method(OverlayTypeName, "CopyField");

        Assert.That(Calls(copyField).Any(c => c.Name is "SetPixels32" or "SetPixels" or "LoadRawTextureData"),
            Is.False,
            "CopyField uploads through an overload that demands an exactly-sized array — a smaller "
            + "emitter than the last one will throw inside GameConditionManagerDraw");
    }

    [Test]
    public void CopyField_WritesIntoTheTexturesOwnBuffer()
    {
        // The positive half: GetRawTextureData<Color32>() hands back a NativeArray view OF the texture,
        // already exactly diameter*diameter long, so there is no second buffer whose length can drift
        // from the texture's. It also removes the reason the shared buffer existed — writing in place
        // allocates nothing per emitter, where an exactly-sized scratch array would have to be
        // reallocated every time two different radii alternated on screen.
        MethodDefinition copyField = Method(OverlayTypeName, "CopyField");

        Assert.That(Calls(copyField).Any(c => c.Name == "GetRawTextureData"), Is.True,
            "CopyField no longer writes through the texture's own raw buffer");
    }

    [Test]
    public void Overlay_KeepsNoSharedPixelScratchBuffer()
    {
        // The general form of the failure, rather than the one call site. A static array shared between
        // emitters of different sizes is the trap; sizing it correctly on Tuesday does not stop somebody
        // reintroducing a grow-only `if (Length < count)` on Friday.
        IEnumerable<string> arrayFields = Type(OverlayTypeName).Fields
            .Where(f => f.IsStatic && f.FieldType.IsArray)
            .Select(f => f.Name);

        Assert.That(arrayFields, Is.Empty,
            "VectorLightOverlay holds a static array shared across emitters of different diameters — "
            + $"({string.Join(", ", arrayFields)}) — which is how the SetPixels32 overflow got in");
    }

    [Test]
    public void CopyField_StillForcesAlphaOpaque()
    {
        // Guards the fix's blast radius, not the bug. ComputeGlowGridsJob writes accumulated DISTANCE
        // into the alpha of these Color32s, so the upload has to overwrite it; moving from a marshalled
        // Color32[] to a raw texel view is exactly the kind of edit that could drop the one statement
        // doing it and leave a channel that means nothing sitting in the sampler.
        MethodDefinition copyField = Method(OverlayTypeName, "CopyField");
        bool writesOpaqueAlpha = copyField.Body.Instructions.Any(i =>
            i.OpCode == OpCodes.Ldc_I4 && i.Operand is int and 255)
            || copyField.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_I4_S && Convert.ToInt32(i.Operand) == 255);

        Assert.That(writesOpaqueAlpha, Is.True,
            "CopyField no longer forces alpha to 255 — the glow grid's distance channel is reaching the sampler");
    }

    private static IEnumerable<MethodReference> Calls(MethodDefinition method) =>
        method.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            .Select(i => (MethodReference)i.Operand);

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
