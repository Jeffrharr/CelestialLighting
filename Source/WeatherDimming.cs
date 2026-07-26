using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// Thin adapter for §13: pulls the primitives WeatherDimmingMath needs off live Map/WeatherManager
// state. Shared by Patch_WeatherDimming (sky tint), Patch_ShadowStrength (shadow softening),
// Patch_LowLightDesaturation (§9's apparent brightness) and WeatherDimmingProbe, so all four always
// agree about how heavy the weather is instead of risking four independently-derived values
// disagreeing — the same discipline SolarPosition enforces for sun elevation.
//
// This is also where the two live-state questions the pure classifier cannot answer are asked: does
// this map have a sky at all (HasSky), and has the def declared its own answer (WeatherCloudDeck).
// Both exist because §13's original palette-only classifier was tuned against a vanilla-only census
// and misread modded cave environments as overcast — see issue #31 and DESIGN.md §13.
//
// WHY map.weatherManager AND NOT WeatherWorker's own `def`. Patch_WeatherDimming postfixes
// WeatherWorker.CurSkyTarget, so the "obvious" read is the def belonging to the worker being
// patched. That field is private (pinned by ApiCompatibilityTests.WeatherWorker_DefFieldIsNotPublic),
// so it would need FieldRefAccess — and it would buy nothing, because reading the manager is exactly
// equivalent, not merely close. SkyManager.CurrentSkyTarget calls CurSkyTarget on BOTH the current
// and the last weather's worker and lerps the two results by TransitionLerpFactor. A uniform
// map-level multiply therefore factors straight back out of that lerp:
//
//     Lerp(a*k, b*k, t) == k * Lerp(a, b, t)
//
// so blending the two defs' opacities here and applying one scalar gives bit-identical output to
// applying each def's own scalar inside its own worker call — with no reflection and no fragile
// private-field binding.
public static class WeatherDimming
{
    // How much of a cloud deck is overhead right now, in [0,1], blended across any in-flight weather
    // transition. 0 when the feature is off, when there is no weather manager (pocket maps during
    // generation), when the map has no sky over it, or under any clear / non-weather weather.
    public static float CloudOpacityFor(Map map)
    {
        if (!CelestialLightingFeatures.WeatherDimming)
            return 0f;

        WeatherManager weather = map?.weatherManager;
        if (weather == null)
            return 0f;

        if (!HasSky(map))
            return 0f;

        return WeatherDimmingMath.BlendOpacity(
            OpacityOf(weather.lastWeather),
            OpacityOf(weather.curWeather),
            weather.TransitionLerpFactor);
    }

    // The 0..1 fraction by which the rendered sky is currently darkened. 0 whenever CloudOpacityFor
    // is 0, so the feature gate and the clear-sky fast path are both inherited from it.
    public static float DimmingFor(Map map)
    {
        float opacity = CloudOpacityFor(map);
        if (opacity <= 0f)
            return 0f;

        // Vanilla already lerps all three rates across the weather transition (WeatherManager.RainRate
        // and friends are Mathf.Lerp(last, cur, TransitionLerpFactor)), so we deliberately do not lerp
        // them again here — only the palette-derived opacity needs our own blend. SandRate returns 0
        // without Odyssey, so reading it unconditionally is safe.
        WeatherManager weather = map.weatherManager;
        return WeatherDimmingMath.DimmingFraction(
            opacity,
            weather.RainRate,
            weather.SnowRate,
            weather.SandRate,
            WeatherDimmingSettings.MaxDimming);
    }

    // Whether this map has a sky that weather can be overhead in.
    //
    // §13's STRUCTURAL GUARD, and the half of issue #31 the pure classifier cannot reach. "Is this
    // palette a cloud deck?" is a question about a WeatherDef; "is there any sky here?" is a question
    // about the map, and asking it here rather than trying to infer it from a palette is what closes
    // the entire cave / pocket-map / orbit class in one cheap check. §13 shipped without this on the
    // strength of a vanilla-only census; Biomes! Caverns and MultiFloors both ship cave environments
    // with overcast-shaped palettes, which the palette rule alone rates 1.00 and 0.71.
    //
    // Two independent conditions, either of which means no sky:
    //
    //   - disableSkyLighting: the biome's own declaration that there is nothing overhead. This is the
    //     same field SkyManagerUpdate itself branches on to stop writing colors.sky to
    //     MatBases.LightOverlay at all, so it is vanilla's own notion of "skyless", not ours.
    //   - a biome that offers fewer than two possible weathers can never change weather, so its one
    //     palette is the map's fixed environment rather than a deck. WeatherDimmingMath's
    //     BiomeHasChangingWeather documents exactly what that covers — including the biomes it does
    //     NOT catch and why they turn out to be harmless anyway.
    //
    // NOT cached, deliberately. This runs a handful of times per frame — once per weather per
    // SkyManagerUpdate, plus CurShadowStrength — never per cell, and the list it walks is a dozen
    // entries at most. A cache keyed on BiomeDef would buy nothing measurable and would add a write
    // that has to be reasoned about against RimWorld 1.6's worker threads.
    private static bool HasSky(Map map)
    {
        BiomeDef biome = map.Biome;

        // A map with no biome at all is a degenerate case (pocket maps mid-generation). Treat it as
        // skyless: declining to dim leaves vanilla's own rendering untouched, which is the safe
        // direction to be wrong in.
        if (biome == null)
            return false;

        if (biome.disableSkyLighting)
            return false;

        return WeatherDimmingMath.BiomeHasChangingWeather(WeatherChoiceCount(biome));
    }

    // How many weathers this biome can actually roll, i.e. how many it lists at a commonality above
    // zero. Counting *possibilities* rather than entries matters: Biomes! Caverns lists vanilla's
    // Rain, FoggyRain and DryThunderstorm on its cavern biomes at commonality 0 precisely to suppress
    // them, so an entry count would read those caverns as having a climate when their author has said
    // the opposite.
    //
    // Reading the live def is also what makes this correct where reading the XML would not be: the
    // list here is already the INHERITED one, so `Undercave` arrives with both its own weather and the
    // `Underground` it inherits from `Biome_Underground`. Any offline reasoning about this rule has to
    // reproduce that merge or it will undercount — see WeatherDimmingMath.BiomeHasChangingWeather.
    private static int WeatherChoiceCount(BiomeDef biome)
    {
        List<WeatherCommonalityRecord> records = biome.baseWeatherCommonalities;
        if (records == null)
            return 0;

        int count = 0;
        foreach (WeatherCommonalityRecord record in records)
        {
            if (record != null && record.commonality > 0f)
                count++;
        }

        return count;
    }

    // Classifies a single WeatherDef from data it already ships — its day palette and its
    // precipitation rates. No def-name list and no registration step; see
    // WeatherDimmingMath.CloudOpacity for what each line of evidence buys and why neither alone is
    // enough.
    private static float OpacityOf(WeatherDef def)
    {
        if (def == null)
            return 0f;

        // An explicit statement by the def beats anything we can infer from it. Checked first so the
        // escape hatch is unconditional: a mod author (or an XML patch) who says "this is not a cloud
        // deck" is not overruled by a palette that happens to look like one.
        WeatherCloudDeck declared = def.GetModExtension<WeatherCloudDeck>();
        if (declared != null && declared.OverridesOpacity)
            return Mathf.Clamp01(declared.opacity);

        SkyColorSet colors = def.skyColorsDay;
        return WeatherDimmingMath.CloudOpacity(
            colors.sky.r, colors.sky.g, colors.sky.b, colors.saturation,
            def.rainRate, def.snowRate, def.sandRate);
    }
}
