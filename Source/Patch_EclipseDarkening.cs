using HarmonyLib;
using RimWorld;
using Verse;

namespace CelestialLighting;

// Reshapes how the vanilla Eclipse event darkens the sky (DESIGN.md §10). Purely visual: we change
// only the sky-blend factor, never the eclipse's mechanical effects (solar-power loss, mood, sky-gaze
// joy) — all of those read the condition itself, which we leave completely untouched.
//
// Vanilla's eclipse is GameCondition_NoSunlight. It contributes to the sky in SkyManager via two
// overrides: SkyTarget(map) returns a fixed fully-dark, wan blue-grey target, and
// SkyTargetLerpFactor(map) returns how strongly that target is blended in — vanilla ramps it linearly
// over just TransitionTicks (200) at each end and holds it flat at 1 (full black) for the entire
// middle, which reads as an abrupt on/off dimming over a physically-impossible day-long duration.
//
// We postfix SkyTargetLerpFactor and instead drive the blend by the shared coverage ramp
// (EclipseMath), computed from the event's own progress. Which ramp depends on the mode selected in
// EclipseSettings (DESIGN.md §10a/§10b):
//   - Default (unnatural, §10b): a scripted disc darts in over the sun, PARKS fully over it for the
//     whole vanilla duration, then darts out. The vanilla timing/duration/gameplay are untouched; we
//     only replace the flat dim with this fly-in/park/fly-out darkening.
//   - Opt-in (natural, §10a): a real straight-line transit ramp over the (then-shortened) duration.
// Either way the ramp reaches 0 at both ends, so it subsumes vanilla's short in/out transition with
// no pop, and gives a gradual partial → near-total → partial change in both glow and colour.
[HarmonyPatch(typeof(GameCondition_NoSunlight), nameof(GameCondition_NoSunlight.SkyTargetLerpFactor))]
public static class Patch_EclipseDarkening
{
    static void Postfix(GameCondition_NoSunlight __instance, ref float __result)
    {
        // Feature gate (default on): when off, leave vanilla's own SkyTargetLerpFactor result — the
        // faithful pre-feature baseline. Sits first so "off" is a true no-op.
        if (!CelestialLightingFeatures.EclipseDarkening)
            return;

        // Only reshape the actual Eclipse event. GameCondition_NoSunlight is also the condition class
        // behind the Royalty "SunBlocker" machine (a permanent, artificially-caused blackout with no
        // transit geometry), so gating on the Eclipse def keeps that working as vanilla and scopes our
        // change to the celestial event this feature is about.
        if (__instance.def != GameConditionDefOf.Eclipse)
            return;

        float progress = EclipseIntegration.ProgressOf(__instance);
        // Both the natural and unnatural ramps are the same disk-overlap coverage math; which one this
        // eclipse uses is decided per-condition by EclipseIntegration.RendersNatural — in the single-
        // flavour modes every eclipse renders that flavour, and in Both mode a geometric eclipse we
        // fired renders natural while the storyteller's random one renders unnatural. In the natural
        // case the transit magnitude scales how dark it gets (a grazing partial darkens only partway);
        // the unnatural case ignores it and always parks fully. Routing through the shared EclipseMath
        // selector keeps this live patch and the offline probe in lockstep.
        __result = (float)EclipseMath.SkyLerpFactorAtProgress(
            progress, EclipseIntegration.RendersNatural(__instance), EclipseIntegration.ActiveNaturalMagnitude);
    }
}
