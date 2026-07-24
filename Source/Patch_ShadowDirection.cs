using HarmonyLib;
using RimWorld;
using Verse;

namespace CelestialLighting;

// Vanilla's GenCelestial.GetLightSourceInfo, for LightType.Shadow, computes its result vector
// with zero latitude dependence: the y-component (which SkyManager.SetSunShadowVector feeds into
// the world north-south axis) is always `num2 - 2.5f * (num4*num4/100f)`, where num2 is always
// -1.5f or -0.9f — i.e. always negative. Real shadows flip which way they lean depending on
// hemisphere and the sun's seasonal declination; vanilla never models that for shadow rendering
// (even though it does model an analogous latitude effect for glow-percent-by-latitude, via
// SunOffsetFractionFromLatitudeCurve).
[HarmonyPatch(typeof(GenCelestial), nameof(GenCelestial.GetLightSourceInfo))]
public static class Patch_ShadowDirection
{
    static void Postfix(Map map, GenCelestial.LightType type, ref GenCelestial.LightInfo __result)
    {
        if (type != GenCelestial.LightType.Shadow)
            return;

        Formulas.LatitudeContext ctx = LatitudeEffect.ForMap(map);

        // The sign-interpolation itself (including why it's a sign-blend rather than a lerp
        // toward the literal negation) lives in Formulas.ApplyShadowLean, with edge-case unit
        // tests covering lean == 0 (must be a true no-op, the equinox-flattening regression) and
        // |lean| == 1 (must reach a full flip).
        __result.vector.y = Formulas.ApplyShadowLean(__result.vector.y, ctx.Lean);
    }
}
