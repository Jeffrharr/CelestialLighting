using HarmonyLib;
using RimWorld;
using Verse;

namespace CelestialLighting;

// §18e: what an eclipse looks like when it happens 200 km up, where there is no atmosphere to
// scatter light into the umbra. Purely visual, like the rest of §10 — the eclipse's mechanical
// effects (solar-power loss, mood, sky-gaze joy) all read the condition itself, which is untouched.
//
// The companion to Patch_EclipseDarkening, and deliberately a separate patch on a separate method.
// The two answer different halves of the same event:
//
//   Patch_EclipseDarkening  postfixes SkyTargetLerpFactor — HOW FAST the sky gets to the umbra
//                           (§10's disc-overlap coverage ramp). Unchanged in vacuum: that ramp is
//                           pure geometry and the geometry is just as valid in orbit.
//   this patch              postfixes SkyTarget           — WHAT the umbra actually IS. This is the
//                           half that is wrong in vacuum.
//
// Splitting them this way is what keeps §10a's cadence, duration and magnitude untouched by the whole
// of §18e: nothing here can change when an eclipse fires or how long it lasts, only how it looks.
//
// NOTE FOR EPIC #8: this is the patch that makes eclipses on vacuum maps a supported case rather than
// a suppressed one, reversing the epic's "should not fire on vacuum maps" line. RimWorld's orbits are
// stationary, so an orbital tile has a fixed lat/long and sees the same transits as the surface tile
// below it — see VacuumEclipseMath's header for the full argument.
[HarmonyPatch(typeof(GameCondition_NoSunlight), nameof(GameCondition_NoSunlight.SkyTarget))]
public static class Patch_EclipseVacuumSky
{
    static void Postfix(GameCondition_NoSunlight __instance, Map map, ref SkyTarget? __result)
    {
        // Same master gate as Patch_EclipseDarkening: with "Eclipse effects" off the mod leaves
        // eclipses entirely alone, vacuum or not. Sits first so "off" is a true no-op.
        if (!CelestialLightingFeatures.EclipseDarkening)
            return;

        // Only the real Eclipse event. GameCondition_NoSunlight is also the class behind the Royalty
        // "SunBlocker" machine — an artificial, permanent blackout with no transit geometry and no
        // claim to being lit by the night sky — so gating on the def keeps that vanilla.
        if (__instance.def != GameConditionDefOf.Eclipse)
            return;

        // Vanilla always returns a value here, but the contract is Nullable and another mod's prefix
        // could legitimately return null to opt the condition out of the sky entirely. Respect that.
        if (!__result.HasValue)
            return;

        // A map with no biome is a degenerate case (pocket maps mid-generation) and Vacuum.InVacuumForMap
        // would dereference it. Declining to touch the target leaves vanilla's rendering exactly as it
        // was, which is the safe direction to be wrong in.
        if (map?.Biome == null)
            return;

        __result = EclipseVacuum.UmbralTargetFor(map, __result.Value);
    }
}
