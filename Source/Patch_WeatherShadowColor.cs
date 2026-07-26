using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// Makes daytime shadows survive weather (DESIGN.md §13a). The daylight twin of §6a's
// Patch_MoonShadowColor, fixing the same class of bug at the other end of the day.
//
// THE PROBLEM. SkyManager renders a cast shadow as
//
//     color = Color.Lerp(Color.white, curSky.colors.shadow, GenCelestial.CurShadowStrength(map));
//
// so colors.shadow is a hard ceiling on how dark any shadow can ever draw, and the alpha only scales
// toward it. Vanilla tunes that colour for Clear and nothing else. Straight out of
// Core/Defs/WeatherDefs/Weathers.xml:
//
//     Clear                       skyColorsDay.shadow = (0.718, 0.745, 0.757)   -> 28% darkening
//     Fog / Rain / SnowGentle /
//     SnowHard / thunderstorms /
//     FoggyRain                   ALL FOUR sky-colour sets = (0.92, 0.92, 0.92) ->  8% darkening
//
// Every non-Clear weather is that one flat value, at every sky-glow anchor. So the moment any weather
// rolls in, a full-sun shadow is capped at 8% before this mod touches anything — and then §13's
// ShadowContrastFactor scales the alpha down on top of it. Measured at high sun under a full deck:
//
//     preset      alpha   colors.shadow   rendered   darkening
//     Cinematic   0.433   0.92            0.965       3.5%   (9 / 255)   marginal
//     Realistic   0.150   0.92            0.988       1.2%   (3 / 255)   gone
//
// The sharper way to put it: 0.92 is a CEILING, not an attenuation. Even with no cloud attenuation
// at all, the best a non-Clear sky can render is 8% — under a third of Clear's own 28.2% — so no
// amount of correct alpha can reach past it. The shadow direction, length, penumbra and weather
// attenuation were all being computed correctly and rendered at best faintly — exactly what §6a
// found at night, and what a live session found by noticing shadows had simply gone on a cloudy day.
//
// WHY IT IS A DOUBLE ATTENUATION. Vanilla's flat 0.92 *is* vanilla's weather-softening mechanism: it
// has no cloud model, so it hard-codes "weather => almost no shadow" into the colour. §13 has its own
// — CloudOpacityFor feeds ShadowContrastFactor, which attenuates the alpha. Running both means the
// weather penalty is applied twice, once as a ceiling we cannot see past and once as a scale.
//
// THE FIX. While the sun is up, substitute the daytime shadow colour vanilla itself considers
// correct — Clear's — and let §13's alpha be the single weather lever:
//
//     preset      cloudy day, before -> after      Clear day (unchanged)
//     Cinematic       3.5%  ->  12.2%                  28.2%
//     Realistic       1.2%  ->   4.2%                  28.2%
//
// A cloudy day still reads as markedly softer than a clear one, which is physically right and what
// §13's MaxShadowSoftening was tuned for. What changes is that it is visible at all.
//
// WHAT THIS DOES NOT FIX, so nobody re-derives it hopefully later: fog does not differ from a
// blizzard, and this change does not make it. All seven non-Clear vanilla weathers ship an IDENTICAL
// skyColorsDay — sky (0.8,0.8,0.8), saturation 0.9 — so WeatherDimmingMath.CloudOpacity returns
// exactly 1.0 for every one of them and 0.0 for Clear. §13's cloud model is therefore just as binary
// as vanilla's colour on vanilla data; it was never the smooth half of the pair. Intermediate
// opacities arise only across a weather transition (BlendOpacity, over WeatherManager's 4000 ticks)
// and for modded weathers carrying their own palettes. Those cases now ramp through a range a player
// can actually see instead of the old 0-8% band — which is the real benefit of keeping the alpha as
// the single lever, rather than any change to steady-state vanilla weather.
//
// Read live off WeatherDefOf.Clear rather than hard-coded, the same discipline SunClock uses for
// vanilla's day length: if Ludeon retunes Clear's daylight shadow, we follow instead of silently
// drifting away from the value this is defined as matching.
[HarmonyPatch(typeof(WeatherWorker), nameof(WeatherWorker.CurSkyTarget))]
public static class Patch_WeatherShadowColor
{
    // Fallback for the window before defs are loaded (and for a modded Clear that somehow lacks the
    // set). Clear's shipped 1.6 skyColorsDay.shadow, so the fallback and the live read agree on
    // vanilla.
    private static readonly Color ClearDayShadowFallback = new Color(0.718f, 0.745f, 0.757f);

    static void Postfix(Map map, ref SkyTarget __result)
    {
        // Gated on §13, because §13's alpha is what replaces the softening we are removing. With
        // weather dimming off, vanilla's flat colour is the ONLY thing making cloudy-day shadows
        // weaker than clear-day ones, and neutralizing it would leave a blizzard casting shadows as
        // hard as noon sun — the opposite of the faithful baseline that flag promises.
        if (!CelestialLightingFeatures.WeatherDimming)
            return;

        // Sun down: the moon is the caster and §6a's Patch_MoonShadowColor owns the colour. Split on
        // the same shared horizon constant both shadow patches use, so the two writers of
        // colors.shadow are exactly complementary and can never both fire on one frame.
        float sunElevation = SolarPosition.ElevationForMap(map);
        if (sunElevation <= Formulas.AtmosphericRefractionDegrees)
            return;

        Color clearDayShadow = ClearDayShadow();
        __result.colors.shadow = new Color(
            clearDayShadow.r, clearDayShadow.g, clearDayShadow.b, __result.colors.shadow.a);
    }

    // Not cached in a static field: WeatherDefOf is populated after defs load, and a game can be
    // restarted into a different modded def set within one process. The lookup is a field read on a
    // resolved DefOf, so there is nothing here worth caching against a once-per-frame call.
    private static Color ClearDayShadow() =>
        WeatherDefOf.Clear == null ? ClearDayShadowFallback : WeatherDefOf.Clear.skyColorsDay.shadow;
}
