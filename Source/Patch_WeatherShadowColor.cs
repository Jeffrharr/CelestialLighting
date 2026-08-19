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
//
// §18c LIVES HERE TOO, and deliberately in this file rather than in a fourth patch of its own. This
// is THE daytime writer of colors.shadow, and what §18c changes is not when it writes but which fill
// it writes: on a vacuum map there is no dome doing the filling, so the umbra falls to §18b's night
// light budget and the sky palette is not consulted at all. Making that one gated call
// (ShadowFillMath.DaytimeUmbraFill, `inVacuum` last per Source/Vacuum.cs's convention) rather than a
// second patch racing this one is what keeps the field single-writer per frame — with two postfixes
// on the same method the winner would be whichever Harmony happened to order last.
//
// So the full ownership table for colors.shadow, all three arms splitting on shared discriminators:
//
//     sun down                -> §6a  Patch_MoonShadowColor   (the moon is the caster)
//     sun up, has atmosphere  -> §13a this file, Clear's skylight fill
//     sun up, in vacuum       -> §18c this file, §18b's night floor
//
// The horizon test is Formulas.AtmosphericRefractionDegrees via the shared SolarPosition adapter;
// the vacuum test is Vacuum.InVacuumForMap. Neither is re-derived anywhere.
public static class Patch_WeatherShadowColor
{
    // Fallback for the window before defs are loaded (and for a modded Clear that somehow lacks the
    // set). Clear's shipped 1.6 skyColorsDay.shadow, so the fallback and the live read agree on
    // vanilla. The three components live in ShadowFillMath so this anchor and §18c's vacuum umbra sit
    // side by side in one pure file and the unit tests can pin them as a diverging pair.
    private static readonly Color ClearDayShadowFallback = new Color(
        ShadowFillMath.SeaLevelUmbraR, ShadowFillMath.SeaLevelUmbraG, ShadowFillMath.SeaLevelUmbraB);

    internal static void Apply(Map map, ref SkyTarget target)
    {
        // §18's shared gate, read exactly once and passed down as a bool (Source/Vacuum.cs's
        // convention). Two flags rather than one because the two halves of this patch are separate
        // features that happen to write the same field: §13a substitutes Clear's skylight fill,
        // §18c substitutes the night budget for it.
        bool inVacuum = CelestialLightingFeatures.VacuumShadowContrast && Vacuum.InVacuumForMap(map);

        // §13a's half is gated on §13, because §13's alpha is what replaces the softening it removes.
        // With weather dimming off, vanilla's flat colour is the ONLY thing making cloudy-day shadows
        // weaker than clear-day ones, and neutralizing it would leave a blizzard casting shadows as
        // hard as noon sun — the opposite of the faithful baseline that flag promises. §18c's half
        // does not ride on that reasoning (there is no weather on a vacuum map to soften shadows in
        // the first place), so it is deliberately not gated on §13 as well.
        if (!inVacuum && !CelestialLightingFeatures.WeatherDimming)
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
        // eclipse, see MapSkyMath.ConditionBlacksOutSky). This patch exists to restore contrast to a
        // shadow the weather had flattened; under an opaque cloud deck there is no cast band at all, so
        // restoring its contrast would be sharpening something that should not be on screen.
        //
        // Gated for the same reason §6a is, not merely by symmetry: a WeatherEvent's OverrideShadowVector
        // makes SkyManager use colors.shadow directly, bypassing the CurShadowStrength lerp that
        // Patch_ShadowStrength's zero works through.
        if (MapSky.SkyBlackedOut(map))
            return;

        // Sun down: the moon is the caster and §6a's Patch_MoonShadowColor owns the colour. Split on
        // the same shared horizon constant every shadow patch uses, so the writers of colors.shadow
        // are exactly complementary and can never both fire on one frame. That holds in vacuum too:
        // vacuum NIGHT lights the shadow and the ground beside it from the same budget, making the
        // contrast between them a moonlight question rather than a skylight-fill one — §6a's, not
        // §18c's.
        float sunElevation = SolarPosition.ElevationForMap(map);
        if (sunElevation <= Formulas.AtmosphericRefractionDegrees)
            return;

        // §18b's published night floor, read through the shared adapter rather than reassembled here:
        // in vacuum a shadow bottoms out at exactly the value §7 uses for how dark the sky can get.
        //
        // GATED ON inVacuum, and the gate is not a micro-optimization. DaytimeUmbraFill consumes
        // nightFloorGlow only in its `inVacuum` arm and returns the plain sky fill otherwise, so on a
        // surface map this value was computed and discarded — and the discard happened on the DAYLIGHT
        // side of the elevation check above, which is the half of the day nothing else asks the moon
        // anything. §7's Patch_NightRadiance returns before its own FloorGlowFor above
        // NightFloorStartElevation, and §6a's Patch_MoonShadowColor returns as soon as the sun is up,
        // so this line alone was keeping the lunar simulation alive through every daylight frame.
        //
        // An earlier comment here argued the branch was not worth it because the lookup is cheap. The
        // memo collapses the two CALLS per frame to one EVALUATION; it cannot collapse that remaining
        // evaluation to none. Same distinction issue #64 drew for Patch_LimbRefraction, applied to the
        // last daylight caller left on this path.
        float nightFloor = inVacuum ? NightRadiance.FloorGlowFor(map) : 0f;

        // target.glow is the lit ground this umbra is a multiply against, and it is vanilla's own
        // daylight here: the only patch of ours that writes .glow is §7's Patch_NightRadiance, which
        // returns before touching it above NightFloorStartElevation, and we are above the refraction
        // horizon, which is higher still.
        Color skyFill = ClearDayShadow();
        UmbraFill fill = ShadowFillMath.DaytimeUmbraFill(
            skyFill.r, skyFill.g, skyFill.b, nightFloor, target.glow, inVacuum);

        target.colors.shadow = new Color(fill.R, fill.G, fill.B, target.colors.shadow.a);
    }

    // Not cached in a static field: WeatherDefOf is populated after defs load, and a game can be
    // restarted into a different modded def set within one process. The lookup is a field read on a
    // resolved DefOf, so there is nothing here worth caching against a once-per-frame call.
    private static Color ClearDayShadow() =>
        WeatherDefOf.Clear == null ? ClearDayShadowFallback : WeatherDefOf.Clear.skyColorsDay.shadow;
}
