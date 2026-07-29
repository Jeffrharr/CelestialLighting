using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// Recolours the night sky crimson while a third-party blood-moon condition is active (DESIGN.md
// §12). A blood moon is a lunar eclipse — a full moon turned coppery-red — so instead of rendering
// as an ordinary silver-blue moonlit night, we shift the sky's own colours toward crimson.
//
// Postfixes WeatherWorker.CurSkyTarget, the same low-risk seam Patch_TwilightColor uses: we touch
// only SkyColorSet's *colours*, never its .glow, so we don't disturb the brightness value other
// mods read (e.g. Dub's Skylights reading SkyManager.CurSkyGlow) — this stays purely a recolour,
// keeping the night "bright enough to still be a moonlit night, not darkness". At deep night
// Patch_TwilightColor's own nudge has already faded to ~0 (its band centres on dusk glow ≈ 0.35),
// so the two postfixes don't fight over the sky.
[HarmonyPatch(typeof(WeatherWorker), nameof(WeatherWorker.CurSkyTarget))]
public static class Patch_BloodMoon
{
    static void Postfix(Map map, ref SkyTarget __result)
    {
        // Feature gate (default on): when off, leave the sky exactly as vanilla/§2 composed it — the
        // faithful pre-feature baseline. Sits before the condition lookup so "off" is a true no-op.
        if (!CelestialLightingFeatures.BloodMoon)
            return;

        // Enclosed map (a Biomes! Caverns cavern, vanilla's undercave): the moon this recolours
        // the night around is not visible from inside a cave, and VRE-Sanguophage's condition is
        // map-wide rather than sky-aware. See MapSkyMath.
        if (MapSky.IsEnclosed(map))
            return;

        float strength = BloodMoon.TintStrengthForMap(map);
        if (strength <= 0f)
            return;

        __result.colors.sky = TintTowardCrimson(__result.colors.sky, strength);
        __result.colors.overlay = TintTowardCrimson(__result.colors.overlay, strength);
    }

    // Thin conversion boundary: unpack Unity's Color to primitives, let the pure BloodMoonMath core
    // do the luma-preserving crimson blend, repack. Alpha is left untouched — the recolour only
    // changes hue, not how the layer composites.
    private static Color TintTowardCrimson(Color color, float strength)
    {
        BloodMoonMath.Rgb tinted = BloodMoonMath.CrimsonTint(color.r, color.g, color.b, strength);
        return new Color(tinted.R, tinted.G, tinted.B, color.a);
    }
}
