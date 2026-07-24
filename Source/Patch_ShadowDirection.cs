using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// Vanilla's GenCelestial.GetLightSourceInfo, for LightType.Shadow, doesn't derive its result from
// real sun position at all: length is a plain linear lerp across the day fraction, and the only
// public signal for "how far below the horizon is the sun" (CurCelestialSunGlow) clamps to exactly
// 0 the instant the sun dips under — throwing away how far below it actually is. That makes it
// impossible to tell "just set" from "polar midnight" using vanilla's own numbers, and its
// near-pole handling (SunOffsetFractionFromLatitudeCurve) is a two-point curve tuned only for
// 70-75 degrees latitude, not a real declination model.
//
// This Postfix replaces the Shadow-type result outright with a small solar-position simulator
// (see Formulas.cs "Solar-position shadow simulator"): real elevation/azimuth from
// latitude+declination+hour-angle, shadow length from cot(elevation) so shadows get naturally
// dramatic near the horizon instead of vanilla's shrink-to-nothing-at-dusk curve, and existence
// gated on Formulas.AtmosphericRefractionDegrees (not a bare 0) so the shadow doesn't vanish a few
// minutes before real-world atmospheric refraction would have let it linger.
//
// NOTE: __result.intensity here does NOT control whether a shadow is actually visible in-game —
// the shadow shader's alpha comes from a separate call to GenCelestial.CurShadowStrength(map),
// patched independently by Patch_ShadowStrength using the same SolarPosition.ElevationForMap so
// the two never disagree. This field is still set for Patch_ShadowTilt, which reads it back from
// this same patched call rather than calling CurShadowStrength a second time itself.
[HarmonyPatch(typeof(GenCelestial), nameof(GenCelestial.GetLightSourceInfo))]
public static class Patch_ShadowDirection
{
    static void Postfix(Map map, GenCelestial.LightType type, ref GenCelestial.LightInfo __result)
    {
        if (type != GenCelestial.LightType.Shadow)
            return;

        SolarPosition.Inputs inputs = SolarPosition.InputsForMap(map);
        float elevation = Formulas.SolarElevationDegrees(inputs.Latitude, inputs.Declination, inputs.DayPercent);

        if (elevation <= Formulas.AtmosphericRefractionDegrees)
        {
            // Sun is physically below the horizon (past atmospheric refraction). Vanilla's "night"
            // branch here (the folded, num2 == -0.9f curve) renders a second shadow meant to
            // represent moonlight, but it isn't tied to any real moon position — until
            // GameComponent_MoonPhase exists to drive an actual moon-cast shadow, suppress it
            // entirely rather than show a fake one.
            __result.intensity = 0f;
            return;
        }

        float azimuth = Formulas.SolarAzimuthDegrees(inputs.Latitude, inputs.Declination, elevation, inputs.DayPercent);
        Formulas.ShadowVector shadow = Formulas.ShadowVectorFromSunPosition(elevation, azimuth);

        __result.vector = new Vector2(shadow.X, shadow.Y);
        __result.intensity = Formulas.ShadowIntensityFromElevation(elevation);
    }
}
