using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// Vanilla's WeatherWorker.CurSkyTarget blends between four fixed SkyColorSet thresholds purely
// by GenCelestial.CurCelestialSunGlow — no latitude dependence. Real high-latitude twilight is
// both longer and more saturated/warm than the equatorial transition vanilla models uniformly
// everywhere. This nudges (never replaces) the returned colors toward a warm target during a
// latitude-scaled twilight band, so each WeatherDef's own palette (rain/fog/etc.) still reads as
// distinct — it's just warmed while dusk/dawn is happening.
[HarmonyPatch(typeof(WeatherWorker), nameof(WeatherWorker.CurSkyTarget))]
public static class Patch_TwilightColor
{
    private static readonly Color WarmTwilight = new Color(1f, 0.45f, 0.15f);

    static void Postfix(Map map, ref SkyTarget __result)
    {
        float strength = LatitudeEffect.StrengthForMap(map);
        if (strength <= 0f)
            return;

        // Deliberately re-derive sun glow from GenCelestial.CurCelestialSunGlow rather than
        // reading __result.glow: __result.glow may already be clamped down by the active
        // WeatherDef's maxGlow (fog, rain, etc.), which would make twilight timing track
        // weather-dimmed brightness instead of true sun position. Recomputing here is cheap
        // (trig only, no allocation) and keeps the twilight band anchored to where the sun
        // actually is, independent of what the sky currently looks like.
        float sunGlow = GenCelestial.CurCelestialSunGlow(map);

        // Band width, peak position, and the factor curve itself all live in
        // Formulas.TwilightFactor, with edge-case unit tests covering the band's edges, its peak,
        // and how both scale with latitude strength.
        float twilightFactor = Formulas.TwilightFactor(sunGlow, strength);

        if (twilightFactor <= 0f)
            return;

        __result.colors.sky = Color.Lerp(__result.colors.sky, WarmTwilight, twilightFactor * 0.35f);
        __result.colors.overlay = Color.Lerp(__result.colors.overlay, WarmTwilight, twilightFactor * 0.25f);
        __result.colors.saturation = Mathf.Lerp(__result.colors.saturation, __result.colors.saturation * 1.4f, twilightFactor);
    }
}
