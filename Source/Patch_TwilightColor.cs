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
//
// The warm nudge is driven by Formulas.TwilightWarmthFactor, which combines two pieces:
//   1. the original glow-keyed band (above the horizon, where CurCelestialSunGlow varies), and
//   2. a civil-twilight persistence term keyed on true solar elevation (below the horizon).
// Vanilla glow pins to exactly 0 the moment the sun geometrically sets, so keying purely on glow
// snapped the warm tint off at sunset; piece 2 recovers the "how far below the horizon" the glow
// value threw away (from our own solar-position simulator) and lets the warmth linger and fade
// through civil twilight (sun 0 to -6 degrees) the way real dusk does. Both pieces are colour-only
// — this patch writes SkyTarget.colors and never SkyManager's glow, so night stays exactly as
// dark as vanilla makes it (glow-reading mods such as Dub's Skylights see an unmodified value).
[HarmonyPatch(typeof(WeatherWorker), nameof(WeatherWorker.CurSkyTarget))]
public static class Patch_TwilightColor
{
    private static readonly Color WarmTwilight = new Color(1f, 0.45f, 0.15f);

    static void Postfix(Map map, ref SkyTarget __result)
    {
        // Enclosed map (a Biomes! Caverns cavern, vanilla's undercave): there is no sky to
        // catch a sunset, so leave the def's palette exactly as its author wrote it. Sits before the
        // latitude lookup so it is a true no-op. See MapSkyMath for the rule, and for the two
        // neighbouring questions it deliberately refuses to answer.
        if (MapSky.IsEnclosed(map))
            return;

        float strength = LatitudeEffect.StrengthForMap(map);
        if (strength <= 0f)
            return;

        // Deliberately re-derive sun glow from GenCelestial.CurCelestialSunGlow rather than reading
        // __result.glow, so twilight timing is anchored to where the sun actually is rather than to
        // what the sky currently looks like. Recomputing is cheap (trig only, no allocation).
        //
        // The original reason given here — that __result.glow "may already be clamped by the active
        // WeatherDef's maxGlow (fog, rain, etc.)" — was wrong: maxGlow defaults to 1.0 and is set
        // exactly once across all vanilla XML (Odyssey's Overcast, 0.95), so weather essentially
        // never clamps it (DESIGN.md §13). The decision still stands, and for a stronger reason than
        // the one it was made for: §7 rewrites __result.glow below the horizon with its night floor,
        // so reading it here would make the twilight band track moonlight rather than the sun.
        float sunGlow = GenCelestial.CurCelestialSunGlow(map);

        // Solar elevation from the same shared simulator the shadow patches use
        // (SolarPosition.ElevationForMap), so twilight timing and shadow timing can never derive a
        // different sun position from each other. This is what drives the below-horizon
        // civil-twilight persistence: glow (above) has already clamped to 0 by this point at night,
        // but elevation keeps going negative, telling us how deep into twilight we are.
        float elevation = SolarPosition.ElevationForMap(map);

        // Band width, peak position, the civil-twilight persistence band, and the factor curve
        // itself all live in Formulas, with edge-case unit tests covering the band edges, its peak,
        // the persistence pulse's boundaries, and how each scales with latitude strength.
        // The §18 vacuum gate — one read of map.Biome.inVacuum, handed to the pure layer as a
        // primitive like every other input here. Deliberately NOT an early-out in this file: the
        // "twilight is zero without air" decision belongs next to the twilight math (and its unit
        // tests) rather than in an adapter, so the shipped behaviour and the pinned behaviour are
        // literally the same code. See Vacuum.cs for the convention #30-#33 follow.
        bool inVacuum = Vacuum.InVacuumForMap(map);

        float twilightFactor = WarmthFactor(sunGlow, elevation, strength, inVacuum);

        if (twilightFactor <= 0f)
            return;

        __result.colors.sky = Color.Lerp(__result.colors.sky, WarmTwilight, twilightFactor * 0.35f);
        __result.colors.overlay = Color.Lerp(__result.colors.overlay, WarmTwilight, twilightFactor * 0.25f);
        __result.colors.saturation = Mathf.Lerp(__result.colors.saturation, __result.colors.saturation * 1.4f, twilightFactor);
    }

    // The warm-tint factor, honouring the CivilTwilightPersistence feature switch. On (the shipped
    // default) folds in the below-horizon civil-twilight linger via TwilightWarmthFactor; off falls
    // back to the pre-feature glow-keyed-only TwilightFactor, so the warm tint snaps off at
    // geometric sunset exactly as it did before this feature — a faithful "before" the test harness
    // can screenshot for an A/B against the "after". See CelestialLightingFeatures for why off is
    // the old behaviour rather than zero.
    // Both branches take the vacuum gate, so turning the civil-twilight feature off cannot smuggle
    // ground twilight back onto a space map through the legacy path.
    private static float WarmthFactor(float sunGlow, float elevation, float strength, bool inVacuum) =>
        CelestialLightingFeatures.CivilTwilightPersistence
            ? Formulas.TwilightWarmthFactor(sunGlow, elevation, strength, inVacuum)
            : Formulas.TwilightFactor(sunGlow, strength, inVacuum);
}
