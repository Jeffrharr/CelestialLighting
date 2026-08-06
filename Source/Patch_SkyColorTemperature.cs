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

    // How much further the blend opens up under a FULL sea-level aerosol column (§20b). At
    // aerosolFraction 1 the sky blend runs to 0.35 * 1.7 = 0.60 and the overlay to 0.43.
    //
    // This is an OPTICAL-THICKNESS argument, and it is a different claim from the one
    // SkyColorTemperature.TintStrength deliberately refuses to make. TintStrength is the geometric
    // factor — how low the sun is — and letting aerosol push THAT up would say haze makes a sunset
    // more vivid, which is backwards for Mie scattering. This instead says that as the haze layer
    // thickens, a larger FRACTION of the light reaching the ground has been scattered by that layer
    // rather than arriving unscattered, so the sky colour approaches the layer's own colour more
    // completely. That fraction genuinely does climb toward 1 with optical depth.
    //
    // It does not re-vivify anything, because of where it is pointed: at the reference exponent the
    // target at full pollution is the colour §20b's 1000 K endpoint shipped, which is dark and brown
    // (green 0.266, blue ~0). Blending MORE completely toward a dark brown makes the sky duller and
    // browner — which is exactly what Mie scattering does. The direction the structural test protects
    // is preserved; only the magnitude changes.
    //
    // Why it was needed: at §20b's original 1500 K endpoint and a fixed 0.35 blend, pollution 1.0
    // measured a median CIELAB deltaE of 1.31 against clean air on rendered frames — below the ~2.0
    // "visible at a glance" threshold. A maximally poisoned sky is not supposed to be something you
    // have to A/B two screenshots to notice.
    //
    // §20d SURVIVES THIS UNTOUCHED AND DOES NOT DOUBLE-COUNT IT, which is worth stating because the
    // two constants look like they are doing the same job. They are not the same quantity:
    // AerosolSpectrum.HorizonOpticalDepth decides WHAT COLOUR the haze layer is, and this decides HOW
    // COMPLETELY the sky takes that colour. §20d changed only the first, and it changed it by exactly
    // the amount that keeps the reference-exponent target identical to the colour this constant was
    // measured against — that is what the refit to CalibrationAnchorKelvin buys. If the boost were
    // instead folded into the optical depth, the aerosol would redden twice.
    //
    // §20d ALSO KEYS IT ON PARTICLE SIZE, and that is a bug fix rather than a refinement. As §20b
    // shipped it, this was keyed on aerosol AMOUNT alone, so at alpha ~ 0 it opened the blend toward
    // the CLEAN-air colour — there being no other colour for a grey layer to have. Measured on
    // rendered frames, a full grey-dust column came out with mean red RISING 103.6 -> 109.1 against
    // the clean control: visibly warmer, when the entire claim about large particles is that they dim
    // WITHOUT colouring. That is the same "haze makes sunsets more vivid" error the note above says
    // this constant must not commit; it was simply committing it through the blend instead of through
    // TintStrength.
    //
    // So the amount term is multiplied by AerosolSpectrum.ChromaticFraction, which is exactly 0 at
    // alpha 0 and exactly 1 at and above the reference exponent. Every tile at temperate rainfall or
    // wetter — which is where §20b's own live measurement was taken — gets this boost bit-for-bit
    // unchanged. Only the dry half of the range tapers, and that half is a range §20b had no way to
    // express in the first place.
    private const float AerosolBlendBoost = 0.7f;

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

        // And what else is in that air (DESIGN.md §20b): the tile's Biotech pollution as a
        // boundary-layer aerosol column. Read through the same boundary helper and pointing the
        // opposite way — pollution warms the horizon endpoint where altitude cools it — but with a
        // 1500 m scale height against Rayleigh's 8500 m, so it fades out with altitude nearly six
        // times faster. A high enough map is simply above the haze, with no threshold anywhere.
        //
        // Zero on every tile in a game without Biotech, which makes this line a no-op there rather
        // than something that needs a ModsConfig gate around it.
        float aerosolFraction = SiteAltitude.AerosolFractionForMap(map);

        // And what SIZE that aerosol's particles are (DESIGN.md §20d): the tile's Angstrom exponent,
        // keyed on its rainfall. This is the input that takes the subsystem off the Planckian locus —
        // everything above it only moves the sky further along one fixed hue path, while this changes
        // which path it is on. A dry tile's coarse dust extinguishes all three channels nearly alike
        // (a pale, dimmed sun), a wet tile's fine haze strips blue hard (a deep red one), and the
        // temperate middle most colonies sit in lands on the orange §20b already shipped.
        float angstromExponent = SiteAltitude.AngstromExponentForMap(map);

        // Note aerosolFraction is deliberately not passed to TintStrength — see the long note there.
        // §8's lane is colour-only, and aerosol's real non-colour effect is to MUTE a sunset, which
        // is §9's saturation lane and blocked behind #78. Strengthening the tint here would model it
        // backwards.
        float tint = SkyColorTemperature.TintStrength(elevation, pressureFraction, inVacuum);
        if (tint <= 0f)
            return;

        SkyColorTemperature.Rgb rgb = SkyColorTemperature.SkyColorForElevation(
            elevation, pressureFraction, aerosolFraction, angstromExponent, inVacuum);
        Color target = new Color(rgb.R, rgb.G, rgb.B);

        // Aerosol opens the blend up (see AerosolBlendBoost), keyed on BOTH halves of what the haze is:
        // how much of it there is, and whether it has a colour of its own to approach.
        //
        // At aerosolFraction 0 — every tile without Biotech, and every clean tile with it — this is
        // exactly 1.0, so the pre-§20b behaviour is reproduced bit-for-bit rather than approximately.
        // At ChromaticFraction 0 (alpha 0, grey extinction) it is also exactly 1.0, which is the fix
        // for a real defect found by measuring: a full grey column used to open the blend to 0.60 and
        // make the arid frame VISIBLY WARMER than the control, when the whole claim about alpha ~ 0 is
        // that it dims without colouring. See AerosolSpectrum.ChromaticFraction for why the chromatic
        // spread is the right thing to scale by and why it cannot double-count the amount term.
        float aerosolBlend =
            1f + AerosolBlendBoost * aerosolFraction * AerosolSpectrum.ChromaticFraction(angstromExponent);

        __result.colors.sky = Color.Lerp(__result.colors.sky, target, tint * SkyBlend * aerosolBlend);
        __result.colors.overlay = Color.Lerp(__result.colors.overlay, target, tint * OverlayBlend * aerosolBlend);
        // Deliberately leave __result.colors.saturation and __result.glow untouched: saturation
        // shaping is Patch_TwilightColor's job, and glow is off-limits for the whole colour-only lane.
    }
}
