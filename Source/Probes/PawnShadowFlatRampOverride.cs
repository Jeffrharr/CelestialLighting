using HarmonyLib;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead, the
// same boundary CloudCoverFractionOverride draws and for the same reason: this must never reach a
// player's game.
//
// WHY THIS EXISTS. Turning the pawn-shadow fade on changes two things at once. The shadow gains a
// curve along its length, which is the feature — and the draw moves from `Map/SolidColor` to
// `Map/Transparent`, which is a SHADER SWAP. A shader swap is its own risk: a different queue
// composites against the lighting overlay at a different moment, and the symptom of getting that
// wrong is the whole shadow changing darkness, which is indistinguishable from the curve being
// mis-derived. That exact confusion has cost this repo a live run before, on the beam's max, where a
// ΔE of 5.58 looked like a composition change and was a shader landing in the wrong queue.
//
// So the A/B needs a third arm: the NEW material, with the curve flattened. It must reproduce the
// old flat frame. If it does, the shader swap is neutral and everything the feature arm measures is
// the curve. If it does not, the difference it shows is the amount the shader swap is worth, and it
// has to be subtracted from the headline number rather than credited to the fade.
//
// A postfix on the pure fade function is the smallest way to say that. `VectorLightMath
// .PawnShadowFade` already returns exactly 1 for a tip opacity of 1 — this forces the *argument*
// path rather than reimplementing the flattening, so the control arm exercises the real function.
//
// PATCHED MANUALLY, NOT VIA [HarmonyPatch]/PatchAll(). The only PatchAll() in this codebase scans the
// SHIPPED assembly, which never sees this file. Every probe that patches something does it this way.
public static class PawnShadowFlatRampOverride
{
    public const string FeatureKey = "vector_light_shadow_feather_flat";

    private static bool active;

    public static void Install()
    {
        Harmony harmony = new Harmony("celestiallighting.probes.pawnshadowflatrampoverride");

        harmony.Patch(
            AccessTools.Method(typeof(VectorLightMath), nameof(VectorLightMath.PawnShadowFade)),
            postfix: new HarmonyMethod(AccessTools.Method(
                typeof(PawnShadowFlatRampOverride), nameof(Flatten))));
    }

    public static void Set(bool enabled) => active = enabled;

    // Forces the ramp flat without touching the shipped constant, so the feathered material is built
    // and drawn exactly as it would ship and only its texture contents differ.
    //
    // The texture is rebuilt on demand because VectorLightPawnShadows keys its cached ramp row on the
    // tip opacity it was built for — flipping this key mid-scenario therefore invalidates the row on
    // the next draw rather than leaving the previous arm's gradient on screen, which is the failure
    // an in-run A/B would otherwise photograph as "the flag did nothing".
    public static void Flatten(ref float __result)
    {
        if (active)
            __result = 1f;
    }
}
