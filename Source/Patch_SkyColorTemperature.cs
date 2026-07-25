using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// Subsystem 8 (DESIGN.md §8): a continuous sky colour-temperature curve keyed on sun altitude,
// generalizing §2's single fixed twilight hue. Like Patch_TwilightColor this is a Postfix on
// WeatherWorker.CurSkyTarget that NUDGES (never replaces) the returned colours toward a target —
// here a blackbody colour derived from the current sun elevation (warm ~2000 K near the horizon,
// neutral ~5772 K daylight overhead). Blending rather than overwriting preserves each WeatherDef's
// own palette (rain/fog stay distinct), just warmed by however low the sun is.
//
// COLOUR ONLY, NEVER .glow — this stays in the exact same low-risk lane as §2: we only touch
// __result.colors, so we do not disturb the brightness value (glow) that Dub's Skylights and other
// mods read. See DESIGN.md "Conflict risk".
//
// Composition with §2: both patches run on the same call and both warm the sky at low sun. That is
// intentional (DESIGN.md §8: "§2's dusk/dawn nudge becomes one anchor point on this curve") — §2
// adds its concentrated golden-hour warmth in a narrow band around sunGlow 0.35, while this adds a
// broader altitude-driven tint that also covers, e.g., a high-latitude winter noon whose sun never
// climbs out of the warm part of the curve.
[HarmonyPatch(typeof(WeatherWorker), nameof(WeatherWorker.CurSkyTarget))]
public static class Patch_SkyColorTemperature
{
    // Per-channel maximum blend strengths, mirroring Patch_TwilightColor's (sky 0.35 / overlay
    // 0.25) so the two subsystems stack to a coherent — not overpowering — amount of warmth. The
    // pure SkyColorTemperature.TintStrength (0..1) scales these down away from the horizon.
    private const float SkyBlend = 0.35f;
    private const float OverlayBlend = 0.25f;

    static void Postfix(Map map, ref SkyTarget __result)
    {
        // Feature gate (default on): when off, leave each WeatherDef's palette untouched — the
        // faithful pre-feature baseline. Sits before the elevation lookup so "off" is a true no-op.
        if (!CelestialLightingFeatures.SkyColorTemperature)
            return;

        // Re-derive sun elevation from our own simulator (via the shared SolarPosition adapter)
        // rather than reading __result.glow, for the same reason Patch_TwilightColor does: glow may
        // already be clamped by the active WeatherDef's maxGlow, which would make the tint track
        // weather-dimmed brightness instead of true sun position. Elevation is the physically
        // correct key for a colour-temperature curve and is unaffected by weather.
        float elevation = SolarPosition.ElevationForMap(map);

        float tint = SkyColorTemperature.TintStrength(elevation);
        if (tint <= 0f)
            return;

        SkyColorTemperature.Rgb rgb = SkyColorTemperature.SkyColorForElevation(elevation);
        Color target = new Color(rgb.R, rgb.G, rgb.B);

        __result.colors.sky = Color.Lerp(__result.colors.sky, target, tint * SkyBlend);
        __result.colors.overlay = Color.Lerp(__result.colors.overlay, target, tint * OverlayBlend);
        // Deliberately leave __result.colors.saturation and __result.glow untouched: saturation
        // shaping is Patch_TwilightColor's job, and glow is off-limits for the whole colour-only lane.
    }
}
