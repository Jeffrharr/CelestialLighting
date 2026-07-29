using System.Collections.Generic;
using RimWorld;
using Verse;

namespace CelestialLighting;

// Thin adapter over MapSkyMath (which carries the full "why" for the skyless gate, including why
// shadows deliberately use a different flag). This file only pulls the primitives off live
// BiomeDef state.
//
// The three questions every map-kind-sensitive patch in this mod asks, and the ONLY three:
//
//   HasSky(map)       — can weather roll overhead? §13's question, and only §13's.
//   IsEnclosed(map)   — is there a ceiling between this map and the sky? Gates the sky-sourced and
//                       sky-tinting effects.
//   DrawsShadows(map) — does this map render shadows at all? Gates the shadow subsystems.
//
// They are separate on purpose, and orbit is why. Orbit answers false / false / true: no weather,
// no ceiling, harsh unfiltered sunlight. Collapsing the first two would silently strip orbit of
// every sky effect; collapsing in the third would delete shadows from the one place sunlight is
// completely unobstructed.
//
// This lived as a private WeatherDimming.HasSky until Biomes! Caverns showed the question was
// general rather than a §13 detail; WeatherDimming now delegates here, so §13's behaviour and its
// live scenario are unchanged by the move.
//
// NOT CACHED, deliberately, and this survives the promotion from one caller to nine. Every call
// site is per-map-per-frame at worst — once per weather inside SkyManagerUpdate, once per
// CurShadowStrength, once per section regenerate — never per cell, and the list walked is a dozen
// entries at most. A cache keyed on BiomeDef would buy nothing measurable, would add a write to be
// reasoned about against RimWorld 1.6's worker threads, and would have to be invalidated against
// the harness's own SetBiome step, which mutates map.Biome at runtime specifically so scenarios
// like weather_dimming_skyless.json can sweep biomes inside a single run.
public static class MapSky
{
    // Whether weather can roll overhead — see MapSkyMath.HasSky. This is §13's question. If you are
    // reaching for it to decide whether a SKY effect applies, you want IsEnclosed below instead:
    // this one is false in orbit, which has no weather but a perfect view of the sky.
    public static bool HasSky(Map map)
    {
        BiomeDef biome = map?.Biome;

        return MapSkyMath.HasSky(
            biome != null,
            biome != null && biome.disableSkyLighting,
            biome == null ? 0 : WeatherChoiceCount(biome));
    }

    // Whether there is a ceiling between this map and the sky — see MapSkyMath.IsEnclosed for why
    // this is a different question from HasSky and why orbit must answer false to it.
    //
    // `inVacuum` is vanilla's own field, set by Odyssey's Space/Orbit biomes and by no cave biome, so
    // separating "no atmosphere" from "no ceiling" costs us no def-name list.
    public static bool IsEnclosed(Map map)
    {
        BiomeDef biome = map?.Biome;
        return MapSkyMath.IsEnclosed(HasSky(map), biome != null && biome.inVacuum);
    }

    // Whether this map renders shadows at all.
    //
    // Vanilla's own flag, honoured by vanilla itself at SectionLayer_SunShadows.Visible
    // (`Map?.Biome?.disableShadows != true`). Reading the same field means our shadow subsystems can
    // never disagree with vanilla about which maps are shadowless — the same discipline EaveCells
    // applies by borrowing Room.UsesOutdoorTemperature rather than inventing an "indoors" test.
    //
    // Biomes! Caverns sets this on all three of its enclosed cavern biomes, so honouring it is what
    // makes our shadow work stop there rather than being computed and then silently discarded by
    // that mod's own DrawLayer skip.
    //
    // Null map or null biome answers TRUE, not false: an unknown map should keep rendering what
    // vanilla would render. That is the opposite default to HasSky above, and deliberately so —
    // each defaults to "leave vanilla alone", which for a gate that SUPPRESSES our effect means
    // false, and for a gate that ENABLES our effect means true.
    public static bool DrawsShadows(Map map)
    {
        BiomeDef biome = map?.Biome;
        return biome == null || !biome.disableShadows;
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
}
