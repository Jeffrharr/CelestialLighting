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
public static class Patch_TwilightColor
{
    private static readonly Color WarmTwilight = new Color(1f, 0.45f, 0.15f);

    internal static void Apply(Map map, ref SkyInputs inputs, ref SkyTarget target)
    {
        // Enclosed map (a Biomes! Caverns cavern, vanilla's undercave): there is no sky to
        // catch a sunset, so leave the def's palette exactly as its author wrote it. Sits before the
        // latitude lookup so it is a true no-op. See MapSkyMath for the rule, and for the two
        // neighbouring questions it deliberately refuses to answer.
        if (inputs.IsEnclosed)
            return;

        // Sky blacked out right now (Glowforest's permanent sulfur overcast, a smoke vent's 3-days-in-7
        // one, Royalty's sun blocker — never an eclipse; see MapSkyMath.ConditionBlacksOutSky). No
        // sunset reaches through an opaque cloud deck.
        //
        // Not cosmetic tidying: vanilla composes a condition's dark SkyTarget with
        // SkyColorSet.LerpDarken, which takes the per-channel MIN, and warming a sky LOWERS its green
        // and blue. So the min keeps almost all of our warmth and only clips the red.
        //
        // A separate term from §18a's vacuum gate inside TwilightWarmth.ForMap below, not a clause on
        // it, because the two say different things: a vacuum map has no atmosphere to scatter a sunset
        // through, while this one has plenty of atmosphere and something opaque under it. They also
        // disagree on every map — orbit is never blacked out, Glowforest is never in vacuum.
        if (inputs.SkyBlackedOut)
            return;

        // Every input to the factor — latitude, sun glow, sun elevation, and the §18 vacuum flag —
        // is lifted off live state by TwilightWarmth.ForMap, which the twilight_warmth live probe
        // also calls. Sharing that one adapter is what stops the patch and the probe from deriving
        // two different answers from the same frame (the discipline SolarPosition.cs enforces
        // between the shadow patches). Band width, peak position, the civil-twilight persistence
        // pulse and the vacuum gate itself all live in Formulas under offline unit tests.
        //
        // The blackout gate deliberately sits OUTSIDE this adapter, so twilight_warmth keeps
        // reporting what the twilight curve says while map_sky_blacked_out reports whether it is
        // being applied — two probes, two questions, neither masking the other.
        float twilightFactor = TwilightWarmth.ForMap(map);

        if (twilightFactor <= 0f)
            return;

        target.colors.sky = Color.Lerp(target.colors.sky, WarmTwilight, twilightFactor * 0.35f);
        target.colors.overlay = Color.Lerp(target.colors.overlay, WarmTwilight, twilightFactor * 0.25f);
        target.colors.saturation = Mathf.Lerp(target.colors.saturation, target.colors.saturation * 1.4f, twilightFactor);
    }
}
