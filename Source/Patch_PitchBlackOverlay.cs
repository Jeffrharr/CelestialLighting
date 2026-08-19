using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// §7a "pitch-black nights" — the VISUAL arm of night radiance (§7).
//
// §7's Patch_NightRadiance writes the night floor into SkyTarget.glow, which correctly drives
// gameplay light (GlowGrid, and everything that reads SkyManager.CurSkyGlow, e.g. Dub's Skylights)
// — but it does NOT darken the screen. On-screen darkness comes from the MatBases.LightOverlay
// material, whose colour vanilla sets from SkyTarget.colors.sky inside SkyManagerUpdate; the terrain
// sprites are always drawn and merely dimmed by that overlay. So a glow floor of 0.04 and a floor of
// 0 render nearly identically — a "pitch-black" moonless night still looks dim-grey, never black.
//
// This Postfix closes that gap. It runs after SkyManagerUpdate has composed MatBases.LightOverlay
// (and after §9's low-light desaturation has tinted colours), reads the already-night-floored
// CurSkyGlow, and pulls the overlay toward opaque black in proportion to how far below full
// brightness the night sits (NightRadianceMath.OverlayBrightnessFactor). A bright moonlit night keeps
// vanilla brightness; a moonless / floors-off night blacks out — down to the
// NightRadianceSettings.MinNightBrightness clamp, which stops it going darker than the player can
// navigate by (0 = truly pitch black, the default is a moody-but-playable floor).
//
// Injecting here rather than in Patch_NightRadiance's SkyTarget postfix is deliberate: it acts on the
// final composed overlay, so it never has to fight §2 (twilight) or §9 (desaturation) for ownership
// of colors.sky — whatever hue they land on, this darkens it last.
[HarmonyPatch(typeof(SkyManager), nameof(SkyManager.SkyManagerUpdate))]
public static class Patch_PitchBlackOverlay
{
    private static readonly Color OpaqueBlack = new Color(0f, 0f, 0f, 1f);

    static void Postfix(SkyManager __instance)
    {
        // Gated on BOTH flags: the darkening is only meaningful relative to §7's glow floor, so if the
        // floor isn't being written (NightRadiance off) CurSkyGlow is vanilla's and "how dark should it
        // be" is undefined for us. Either flag off => leave the overlay exactly as vanilla/§9 left it.
        if (!CelestialLightingFeatures.NightRadiance || !CelestialLightingFeatures.PitchBlackNights)
            return;

        // MatBases.LightOverlay is a single global material, but SkyManagerUpdate runs for EVERY loaded
        // map each frame — vanilla only writes that material when `map == Find.CurrentMap` and leaves it
        // alone otherwise. Without the same guard, a second colony / quest map / caravan map darkens the
        // material again on top of the visible map's own pass, stacking extra blackness in proportion to
        // how many maps happen to be loaded. Comparing skyManager identity keeps this on public API
        // (SkyManager.map is private).
        Map current = Find.CurrentMap;
        if (current == null || current.skyManager != __instance)
            return;

        // Biomes that declare disableSkyLighting are the one case where vanilla
        // deliberately switches the overlay OFF — it writes (1,1,1,0), alpha zero. Lerping that toward
        // opaque black raises the alpha again and re-enables an overlay vanilla just disabled, veiling a
        // map that is already black away from artificial light. Leave those maps alone.
        if (current.Biome != null && current.Biome.disableSkyLighting)
            return;

        // The floor on the night screen, built up in two passes before it reaches
        // OverlayBrightnessFactor (which clamps it into [0,1] itself, so there is nothing left to
        // reconcile once both passes are done).
        //
        // Pass 1: §19 may RAISE it while the sun sits in the ozone band, because real polar twilight
        // is dim but emphatically not black — snow reads blue because scattered skylight still lands
        // on it, and without this the blue §19 just painted would be multiplied into darkness right
        // here. OverlayFloorFor only ever raises, and returns this value untouched whenever §19 is
        // inactive, so this stays a pure addition.
        //
        // THIS IS THE MOD'S ONLY SANCTIONED VISUAL-ONLY BRIGHTNESS FLOOR, and it is visual-only by
        // construction rather than by care: OverlayBrightnessFactor feeds nothing but
        // MatBases.LightOverlay.color and FogOfWar.color below, so GlowGrid, plant growth, solar
        // output and Dub's Skylights never see it. That is exactly why §19 expresses its floor here
        // instead of writing .glow or brightening its own target colour — this postfix runs last on
        // the composed material and lerps toward opaque black, so it has the final word on screen
        // brightness and anything added upstream would be multiplied away.
        //
        // Note also that the feature gate above (NightRadiance && PitchBlackNights) is already
        // correct for §19's purposes: if either is off nothing darkens the overlay at all, so there
        // is nothing to floor. Do not "fix" this by hoisting the floor out of the gate.
        float configuredMinBrightness = OzoneTwilight.OverlayFloorFor(current, NightRadianceSettings.Current.MinNightBrightness);

        // Pass 2: whatever pass 1 landed on, during Anomaly's UnnaturalDarkness it may only ever be
        // pulled DARKER, never lifted above what the event's own (unfloored) glow already renders —
        // see NightRadianceMath.EffectiveMinNightBrightness and MapSky.UnnaturalDarknessActive for
        // why. This runs even on top of §19's raise: a polar-latitude UnnaturalDarkness event still
        // must not be washed back out. Every other reason the sky is dark keeps pass 1's value
        // unchanged.
        // NOT CurSkyGlow directly. An eclipse drives that field to a flat 0 at any hour, so reading it
        // raw let the event darken a night sky the sun had already left — measured at a median CIELAB
        // ΔE of 8.61, i.e. obvious. NightRadiance.VisualGlowFor floors it at the night floor for
        // visual purposes only; SkyTarget.glow itself is untouched, so nothing gameplay-facing moves.
        // §9's Patch_NightDesaturationStrength reads the same function for the same reason.
        float visualGlow = NightRadiance.VisualGlowFor(current, __instance.CurSkyGlow);

        float rawBrightness = NightRadianceMath.RawOverlayBrightnessFactor(visualGlow);
        float minBrightness = NightRadianceMath.EffectiveMinNightBrightness(
            MapSky.UnnaturalDarknessActive(current), configuredMinBrightness, rawBrightness);

        float keep = NightRadianceMath.OverlayBrightnessFactor(visualGlow, minBrightness);

        // keep == 1 is the daytime / bright-moon common case: nothing to darken, and it also skips the
        // day branch where the overlay is transparent white (1,1,1,0) and must be left alone.
        if (keep >= 1f)
            return;

        float t = 1f - keep;
        MatBases.LightOverlay.color = Color.Lerp(MatBases.LightOverlay.color, OpaqueBlack, t);
        MatBases.FogOfWar.color = Color.Lerp(MatBases.FogOfWar.color, OpaqueBlack, t);
    }
}
