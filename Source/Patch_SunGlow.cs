using HarmonyLib;
using RimWorld;
using Verse;

namespace CelestialLighting;

// §14 REALISTIC mode: make vanilla's brightness follow our physical sun, instead of our sun following
// vanilla's clock. Inactive in the default locked mode.
//
// CelestialSunGlowPercent is the single funnel every glow path runs through — CurCelestialSunGlow ->
// CelestialSunGlow(map) -> CelestialSunGlow(tile) -> here, plus AverageGlow calling it directly — and
// it takes primitives and returns a float, which is exactly the shape our pure core already has. It is
// private, which Harmony patches fine; ApiCompatibilityTests pins its existence and signature.
//
// This CHANGES GAMEPLAY and that is why it is opt-in: glow drives plant growth, solar output and
// pawn-visible darkness, so day length moving by ~1.5 h on average (worst 2.75 h within +/-50 degrees)
// shifts growing hours with it. In exchange the poles behave correctly — real arctic summers, real
// equinoxes, and a southern hemisphere that is not a clamped copy of the tropics.
[HarmonyPatch(typeof(GenCelestial), "CelestialSunGlowPercent")]
public static class Patch_SunGlow
{
    // Reentrancy guard. SunClock measures vanilla's day by CALLING CelestialSunGlow, so if this postfix
    // ever ran while that measurement was in flight it would recurse forever. The two modes are
    // mutually exclusive so it cannot happen today, but the guard makes that structural rather than a
    // property of the enum nobody remembers.
    [System.ThreadStatic] private static bool reentering;

    static void Postfix(float latitude, int dayOfYear, float dayPercent, ref float __result)
    {
        if (CelestialLightingFeatures.SunClock != SunClockMode.Realistic || reentering)
            return;

        reentering = true;
        try
        {
            // Through the seam rather than straight to Formulas. This patch decides how BRIGHT the
            // sky is while SolarPosition decides where the sun IS, and the two reading different
            // obliquities would light a Planetsmith or RAT world's daylight hours on Earth's tilt
            // while its shadows ran on the planet's. That is not a subtle mismatch in this mode
            // specifically: its whole premise is that vanilla's glow follows our physical sun, which
            // it cannot do from a declination our physical sun is not using.
            float declination = AxialTiltCompat.SolarDeclinationDegrees(dayOfYear);
            float elevation = Formulas.SolarElevationDegrees(latitude, declination, dayPercent);
            __result = SunClockMath.GlowFromElevation(elevation);
        }
        finally
        {
            reentering = false;
        }
    }
}
