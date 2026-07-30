using HarmonyLib;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// Makes §6's moon-cast shadows actually visible (DESIGN.md §6a).
//
// The bug this fixes is the same shape as §7a's. Patch_ShadowDirection sets a real,
// moon-position-derived shadow vector at night and Patch_ShadowStrength sets a matching alpha, both
// from the one MoonPosition.ShadowForMap adapter — that half was right. But the alpha is only a lerp
// *factor*: SkyManager does
//
//     color = Color.Lerp(Color.white, curSky.colors.shadow, GenCelestial.CurShadowStrength(map));
//     MatBases.SunShadow.color = color;   // and SunShadowFade
//
// and vanilla's night colors.shadow is nearly white — (0.85,0.85,0.85) on Clear, (0.92,…) on every
// other weather — because vanilla never intended to draw a real night shadow. That caps a night
// shadow at a 15% darkening even at alpha 1.0, and at our MoonShadowMaxStrength of 0.28 it came out
// at 4.2% on a clear night and 2.2% otherwise: below the perceptual floor, and doubly so on ground
// §7a has already pulled toward black. So the moon shadows were being computed and handed to the
// shader correctly the whole time, and rendering invisibly.
//
// Fixing the *input* rather than adding a second writer of MatBases.SunShadow is deliberate. Vanilla
// still owns the lerp, so the moon's strength keeps scaling the result (a half-lit moon reads at half
// the contrast for free), and the weather-event branch that skips the lerp entirely — using
// colors.shadow as the colour directly — stays correct too.
//
// Night only. Above the refraction horizon the sun is the caster, and §13a's
// Patch_WeatherShadowColor owns colors.shadow there. The two split on the same shared horizon
// constant both shadow patches use, so they are exactly complementary — never both writing on one
// frame. Nothing else in this mod touches the field (§2 twilight, §8 colour-temperature, §9
// desaturation and §11/§12 all work on colors.sky/overlay/saturation), so there is still no
// composition order to argue about.
//
// Vanilla's daylight shadow colour is well-tuned only on Clear; §13a exists because every other
// weather flattens it to a near-white 0.92 that caps a cloudy-day shadow at 8% darkening — the same
// bug as this one, found at the other end of the day.
[HarmonyPatch(typeof(WeatherWorker), nameof(WeatherWorker.CurSkyTarget))]
public static class Patch_MoonShadowColor
{
    static void Postfix(Map map, ref SkyTarget __result)
    {
        // Gated on the same flag as the moon shadows themselves: with MoonShadows off there is no moon
        // shadow to make visible, and darkening colors.shadow would only alter vanilla's own (fake,
        // not-moon-derived) night shadow — the opposite of the faithful baseline that flag promises.
        if (!CelestialLightingFeatures.MoonShadows)
            return;

        // Shadowless map (Biomes! Caverns' cavern biomes, and any biome declaring vanilla's
        // disableShadows). Vanilla itself honours this flag at SectionLayer_SunShadows.Visible, so
        // nothing we compute here would ever be drawn — and Biomes! Caverns additionally Prefix-skips
        // that layer's DrawLayer outright. Reading vanilla's own field is what stops our shadow
        // subsystems from disagreeing with vanilla about which maps are shadowless.
        //
        // Deliberately NOT MapSky.IsEnclosed: orbit has no ceiling and the harshest sunlight in the
        // game, and gating shadows on the sky question would delete them from exactly the wrong place.
        if (!MapSky.DrawsShadows(map))
            return;

        // Sky blacked out right now (issue #35 — Glowforest, a smoke vent, a sun blocker; never an
        // eclipse, see MapSkyMath.ConditionBlacksOutSky). No moonlight arrives through an opaque cloud
        // deck, so there is no moon shadow whose contrast needs rescuing.
        //
        // Worth its own gate rather than riding on Patch_ShadowStrength's zero, because colors.shadow is
        // not always consumed through the lerp that zero neutralizes: when a WeatherEvent supplies an
        // OverrideShadowVector, SkyManager skips `Color.Lerp(Color.white, ..., CurShadowStrength)`
        // entirely and uses colors.shadow as the material colour directly. On those frames a darkened
        // night shadow colour would render at full depth under a blacked-out sky.
        if (MapSky.SkyBlackedOut(map))
            return;

        // Sun up: it owns the shadow. Same shared simulator and same horizon constant both shadow
        // patches use, so all three can never disagree about when night has started.
        float sunElevation = SolarPosition.ElevationForMap(map);
        if (sunElevation > Formulas.AtmosphericRefractionDegrees)
            return;

        // No moon shadow being cast (moon down, new, or the sky still too bright for one to register —
        // §6b) => leave the night's shadow colour alone. Asking the same adapter the two shadow patches
        // ask means this can never darken the ground for a shadow that isn't there.
        //
        // §6b made that guarantee load-bearing rather than incidental. A contrast ratio is never
        // exactly zero, so without ShadowForMap's own perceptibility gate this would darken
        // colors.shadow through every twilight for a shadow whose alpha is 0.0009.
        if (!MoonPosition.ShadowForMap(map).HasValue)
            return;

        float value = MoonMath.MoonShadowColorValue(
            MoonMath.MoonShadowPeakDarkening, MoonMath.MoonShadowMaxStrength);

        // Neutral grey rather than a blue-tinted "moonlight shadow": §9's low-light desaturation
        // already owns the night's colour cast, and a tint here would fight it for the same look.
        __result.colors.shadow = new Color(value, value, value, __result.colors.shadow.a);
    }
}
