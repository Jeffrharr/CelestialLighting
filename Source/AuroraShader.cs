using UnityEngine;
using Verse;

namespace CelestialLighting;

// Loads the aurora curtain's fragment program and builds the materials that drive it (DESIGN.md
// §11a, issue #196).
//
// WHAT IT REPLACES. The curtain's field used to be baked into a 192-square RGBA texture and stretched
// over sheets 88 cells wide — 2.2 texels per cell, magnified bilinearly, so the rays reached the
// screen as soft vertical smears and the hem as a broad band rather than a line. The shader evaluates
// the same field per fragment, at the resolution of the screen, and the field advances continuously
// rather than one completed sweep at a time.
//
// NOT A PERFORMANCE CHANGE. The bake was cheap and this is not a way to buy frames back; the mod's
// settings screen deliberately offers no toggle for it. See CelestialLightingFeatures.AuroraShaderField.
//
// THE PURE CORE STAYS, IN BOTH OF ITS JOBS. AuroraCurtainHemRays is still the fallback renderer when
// this cannot load, and it is still the reference the port is checked against — see
// AuroraShaderAgreementProbe. That second job is new for this repo: the cloud volume's shader marches
// a density field the CPU baked, so CloudRaymarchMath still governs those pixels, whereas this file
// hands the whole field to HLSL. A silent divergence between the two would be an aurora that is
// simply a different aurora, with every offline test still green, so the agreement probe is part of
// the feature rather than a nicety attached to it.
//
// WHY LOADING IS ALLOWED TO FAIL. Three things can go wrong and none are hypothetical: the bundle can
// be absent (a source checkout that never ran Tools/ShaderBundle/build.sh, or a publish.sh that
// forgot to stage it), it can be present but built for another OS, and it can load fine and not
// compile on the player's hardware. All three land on Available == false and the curtain falls back
// to the bake — a shipped, measured arm rather than an unknown one.
//
// [StaticConstructorOnStartup] is load-bearing rather than tidiness, exactly as on VectorLightShader:
// Shader lookups and Material construction must happen on Unity's main thread after LoadedModManager
// has the bundles open, and the attribute is what guarantees the static initialiser runs there rather
// than on whichever thread first touches the type.
[StaticConstructorOnStartup]
public static class AuroraShader
{
    // The shader's path INSIDE the bundle, minus the extension. ContentFinder builds the full path as
    // Assets/Data/<packageId>/Materials/<this>.shader, and Tools/ShaderBundle/build.sh is what puts
    // it there — the two have to change together, and there is no error if they disagree, only a
    // silent fallback to the bake.
    public const string ShaderPath = "CelestialAurora";

    // The name the .shader file DECLARES. This is the check that matters, and it is stricter than
    // VectorLightShader's identity test against DefaultShader for a reason CloudVolumeShader paid to
    // learn: ShaderDatabase.LoadShader does not return null for a missing shader, it logs and hands
    // back a fallback that is non-null and isSupported. Asserting the name we actually wanted also
    // catches a bundle that loaded some OTHER shader, and it does not depend on which fallback
    // vanilla happens to choose this version.
    public const string ShaderName = "CelestialLighting/Aurora";

    private static readonly int FieldTimeId = Shader.PropertyToID("_FieldTime");

    private static readonly int DriverTintId = Shader.PropertyToID("_DriverTint");

    private static readonly Shader Loaded = Load();

    // Whether the fragment program can be drawn at all on this machine. Read this rather than the
    // feature flag wherever the answer has to be true for the frame to be correct.
    public static bool Available => Loaded != null;

    // Whether the curtain should be drawn by the shader this frame: asked for, and possible.
    //
    // The flag is checked AFTER availability, deliberately, so "on" never means an empty sky on a
    // machine that cannot run the pass — the same ordering CloudSheetOverlay uses for the volume.
    public static bool Active => Available && CelestialLightingFeatures.AuroraShaderField;

    // One material per sheet slot, or null when the shader is unavailable.
    //
    // ONE SET, NOT TWO. The CPU path needs a second set because it cross-fades between the last two
    // completed sweeps; the shader evaluates the field at the current tick, so there is nothing to
    // cross-fade and each live display is one draw call rather than two.
    public static Material[] BuildSheetMaterials(int count)
    {
        if (!Available)
            return null;

        Material[] mats = new Material[count];

        for (int i = 0; i < mats.Length; i++)
            mats[i] = NewMaterial();

        return mats;
    }

    // The field's clock, in wrapped ticks. See the shader's _FieldTime for why the wrap has already
    // happened in integer arithmetic by the time it gets here.
    public static void SetFieldTime(Material material, float wrappedTicks)
    {
        material.SetFloat(FieldTimeId, wrappedTicks);
    }

    // The driver condition's colour, and in alpha how far it pulls the palette. Passed per sheet per
    // frame rather than pinned per sweep, because there are no sweeps: the shader samples the tint
    // the driver has right now, which is what removes the vertical colour gradient the bake had to
    // pin `_sweepTint` to avoid.
    public static void SetDriverTint(Material material, Color tint, float weight)
    {
        material.SetColor(DriverTintId, new Color(tint.r, tint.g, tint.b, weight));
    }

    // A material on the loaded shader, for callers that need one outside the sheet pool —
    // AuroraShaderAgreementProbe renders through this.
    //
    // IT HAS TO COME FROM HERE, and the probe learned that the expensive way: Shader.Find cannot see a
    // shader that arrived in an AssetBundle. It searches shaders built into the player and the
    // Resources folder, finds nothing, returns null, and `new Material(null)` throws
    // "Value cannot be null. Parameter name: shader" — which reads as the shader having failed to
    // load when it had in fact loaded perfectly and was sitting in this class's own field.
    public static Material NewFieldMaterial() => Available ? NewMaterial() : null;

    private static Material NewMaterial()
    {
        Material material = new Material(Loaded);

        // COPY MoteGlow'S RENDER QUEUE RATHER THAN TRUSTING THE TAG IN THE BUNDLE. An additive pass
        // is order-independent only against other additive passes: the lighting overlay is a
        // MULTIPLY, so a pass drawn before it is attenuated by it and one drawn after it is not. The
        // vector-light shader's first bundle declared "Queue"="Transparent" (3000) against MoteGlow's
        // 3151, landed on the wrong side of that multiply, and measured a masked ΔE of 5.58 — which
        // did not look like an ordering bug, it looked like the composition being wrong.
        //
        // That trap is worse here than it was there, because this pass exists to glow through
        // pitch-black nights. Under the multiply it would be dimmed by exactly the sky darkness the
        // curtain is meant to be seen against, i.e. it would fail hardest in the conditions it is for.
        //
        // Reading the queue off MoteGlow rather than hardcoding 3151 means we cannot drift from it if
        // Ludeon moves the motes, and changing it needs no rebuild of the three bundles.
        material.renderQueue = ShaderDatabase.MoteGlow.renderQueue;

        return material;
    }

    private static Shader Load()
    {
        Shader shader = ShaderDatabase.LoadShader(ShaderPath);

        if (shader == null || shader.name != ShaderName)
        {
            Log.Warning(
                "[CelestialLighting] Could not load shader '" + ShaderPath + "' from the mod's asset "
                + "bundles (got '" + (shader == null ? "null" : shader.name) + "'). The aurora "
                + "curtain falls back to the CPU-baked field.");
            return null;
        }

        // Supported is a per-machine answer, not a per-build one: the bundle can be perfectly valid
        // and still fail to compile on hardware or a graphics API that cannot run the pass. Vanilla
        // asks the same question of its own shaders — SectionLayer_SunShadows is skipped entirely
        // when MatBases.SunShadow.shader.isSupported is false — so this is the established shape of
        // the check rather than defensiveness.
        if (!shader.isSupported)
        {
            Log.Warning(
                "[CelestialLighting] Shader '" + ShaderPath + "' loaded but is not supported on this "
                + "system. The aurora curtain falls back to the CPU-baked field.");
            return null;
        }

        return shader;
    }
}
