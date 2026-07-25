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
// colors.shadow as the colour directly — stays correct too. It also keeps one owner per field:
// nothing else in this mod writes colors.shadow (§2 twilight, §8 colour-temperature, §9 desaturation
// and §11/§12 all work on colors.sky/overlay/saturation), so there is no composition order to argue
// about.
//
// Night only. Above the refraction horizon the sun is the caster and vanilla's daytime shadow colours
// are already correct and well-tuned; touching them there would change every daylight shadow in the
// game, which is not what this is for.
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

        // Sun up: it owns the shadow. Same shared simulator and same horizon constant both shadow
        // patches use, so all three can never disagree about when night has started.
        float sunElevation = SolarPosition.ElevationForMap(map);
        if (sunElevation > Formulas.AtmosphericRefractionDegrees)
            return;

        // No moon shadow being cast (moon down, or new) => leave the night's shadow colour alone.
        // Asking the same adapter the two shadow patches ask means this can never darken the ground for
        // a shadow that isn't there.
        if (!MoonPosition.ShadowForMap(map).HasValue)
            return;

        float value = MoonMath.MoonShadowColorValue(
            MoonMath.MoonShadowPeakDarkening, MoonMath.MoonShadowMaxStrength);

        // Neutral grey rather than a blue-tinted "moonlight shadow": §9's low-light desaturation
        // already owns the night's colour cast, and a tint here would fight it for the same look.
        __result.colors.shadow = new Color(value, value, value, __result.colors.shadow.a);
    }
}
