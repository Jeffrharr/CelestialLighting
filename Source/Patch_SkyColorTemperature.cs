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

        // Enclosed map (a Biomes! Caverns cavern, vanilla's undercave): no sky means no
        // atmospheric scattering to shift warm at dawn and cool at noon, so the def's palette stands
        // as authored. Notably this is what keeps BMT_FungalForest's bioluminescent palette intact
        // rather than dragging it toward a blackbody curve it was never meant to sit on. See MapSkyMath.
        if (MapSky.IsEnclosed(map))
            return;

        // Sky blacked out right now (issue #35 — Glowforest, a smoke vent, a sun blocker; never an
        // eclipse, see MapSkyMath.ConditionBlacksOutSky). A blackbody curve models the colour of
        // scattered SUNLIGHT and none is arriving: what is overhead is opaque sulfur cloud, whose colour
        // belongs to the condition rather than to a solar-elevation curve. Leaving it to vanilla's
        // LerpDarken min() was not enough for the reason Patch_TwilightColor spells out.
        if (MapSky.SkyBlackedOut(map))
            return;

        // Re-derive sun elevation from our own simulator (via the shared SolarPosition adapter)
        // rather than reading __result.glow, for the same reason Patch_TwilightColor does: elevation
        // is the physically correct key for a colour-temperature curve and tracks true sun position
        // rather than displayed brightness, which §7 rewrites below the horizon.
        //
        // (The reason originally cited here — that glow "may already be clamped by the active
        // WeatherDef's maxGlow" — was wrong; maxGlow is set exactly once in all of vanilla. See
        // DESIGN.md §13. The choice is unchanged and better justified.)
        float elevation = SolarPosition.ElevationForMap(map);

        // The §18 vacuum gate (Vacuum.cs). Threaded into the pure layer rather than early-returning
        // here for the same reason as Patch_TwilightColor: the "no air, no reddening" decision lives
        // with the curve it flattens. TintStrength returns 0 in vacuum, so the guard below is what
        // actually turns the patch into a no-op there — one exit path, not two.
        bool inVacuum = Vacuum.InVacuumForMap(map);

        // How much air this map actually sits under (DESIGN.md §20). One live tile read, converted
        // to a primitive here at the boundary exactly like LatitudeEffect does with latitude — the
        // pure curve never learns what a Tile is. Note this is SITE altitude in metres, nothing to
        // do with `elevation` above, which is the sun's angle: RimWorld calls its terrain-height
        // field `elevation` too, and letting that name cross into this file would make every line
        // of it ambiguous.
        float pressureFraction = SiteAltitude.PressureFractionForMap(map);

        float tint = SkyColorTemperature.TintStrength(elevation, pressureFraction, inVacuum);
        if (tint <= 0f)
            return;

        SkyColorTemperature.Rgb rgb = SkyColorTemperature.SkyColorForElevation(elevation, pressureFraction, inVacuum);
        Color target = new Color(rgb.R, rgb.G, rgb.B);

        __result.colors.sky = Color.Lerp(__result.colors.sky, target, tint * SkyBlend);
        __result.colors.overlay = Color.Lerp(__result.colors.overlay, target, tint * OverlayBlend);
        // Deliberately leave __result.colors.saturation and __result.glow untouched: saturation
        // shaping is Patch_TwilightColor's job, and glow is off-limits for the whole colour-only lane.
    }
}
