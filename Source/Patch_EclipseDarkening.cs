using HarmonyLib;
using RimWorld;
using Verse;

namespace CelestialLighting;

// Cosmetic overlay on the vanilla Eclipse event (DESIGN.md §10). Purely visual: we change how the
// sky darkens, never the eclipse's mechanical effects (solar-power loss, mood, sky-gaze joy) — all
// of those read the condition itself, which we leave completely untouched.
//
// Vanilla's eclipse is GameCondition_NoSunlight. It contributes to the sky in SkyManager via two
// overrides: SkyTarget(map) returns a fixed fully-dark, wan blue-grey target, and
// SkyTargetLerpFactor(map) returns how strongly that target is blended in — vanilla ramps it
// linearly over just TransitionTicks (200) at each end and holds it flat at 1 (full black) for the
// entire middle, which reads as an abrupt on/off dimming.
//
// We postfix SkyTargetLerpFactor and instead drive the blend by the geometric occulted-fraction of
// the sun as the moon transits it (EclipseMath), computed from the event's own progress. Because
// this factor controls how much of the dark, wan eclipse target is mixed into the live sky, driving
// it by disk-overlap coverage gives a gradual partial → near-total → partial ramp in both glow and
// colour, anchored to the real event — exactly the visual the issue asks for. The pure ramp already
// reaches 0 at both ends, so it subsumes vanilla's short in/out transition without any pop.
[HarmonyPatch(typeof(GameCondition_NoSunlight), nameof(GameCondition_NoSunlight.SkyTargetLerpFactor))]
public static class Patch_EclipseDarkening
{
    static void Postfix(GameCondition_NoSunlight __instance, ref float __result)
    {
        // Only reshape the actual Eclipse event. GameCondition_NoSunlight is also the condition
        // class behind the Royalty "SunBlocker" machine (a permanent, artificially-caused blackout
        // with no transit geometry), so gating on the Eclipse def keeps that working as vanilla and
        // scopes our change to the celestial event this feature is about.
        if (__instance.def != GameConditionDefOf.Eclipse)
            return;

        float progress = EclipseProgress(__instance);
        __result = (float)EclipseMath.SkyLerpFactorAtProgress(progress);
    }

    // Fraction of the eclipse elapsed, in [0, 1]. TicksPassed + TicksLeft is the full duration; we
    // guard the degenerate zero-duration case (nothing elapsed yet) by reporting 0 so the sky stays
    // at its normal daytime value rather than dividing by zero.
    static float EclipseProgress(GameCondition condition)
    {
        int passed = condition.TicksPassed;
        int total = passed + condition.TicksLeft;
        if (total <= 0)
            return 0f;

        return (float)passed / total;
    }
}
