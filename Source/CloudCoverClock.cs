using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace CelestialLighting;

// The impure half of DESIGN.md §22, and deliberately nothing more: it reads the live values the two
// pure models need — the absolute tick, the map's world tile id, its biome's weather list, and its
// seasonal temperature — memoises the answer on CloudCoverDrift's own hourly cadence, and hands a
// primitive back. Every constant, every clamp and every line of arithmetic lives in
// SeasonalWetFraction.cs and CloudCoverDrift.cs, both Verse-free and unit tested offline.
//
// Same split as AerosolDriftClock.cs, for the same reason recorded there: a memo bug and a formula bug
// look identical from inside a running game, so the parts that CAN be tested offline are kept where
// they can be. Same "no MapComponent" reasoning too — see AerosolDriftClock.cs's header — this is a
// pure function of (tileId, TicksAbs, the biome's def data), none of which needs a save-format entry
// of its own.
//
// WHERE THE INGREDIENTS COME FROM (mirroring vanilla, not guessing at it). WeatherDecider's own
// (private) CurrentWeatherCommonality — see that method's decompile — computes each WeatherDef's
// likelihood as commonality * commonalityRainfallFactor.Evaluate(tile.rainfall), gated to 0 if
// !weather.temperatureRange.Includes(currentTemperature). SeasonalWetFractionFor below reads exactly
// those three vanilla fields (WeatherCommonalityRecord.commonality, WeatherDef.commonalityRainfallFactor,
// WeatherDef.temperatureRange) off the same BiomeDef vanilla itself asks (map.Biome.baseWeatherCommonalities),
// including the same null-curve fallback (commonalityRainfallFactor == null means a factor of 1, not 0
// and not "skip this entry" — see CurrentWeatherCommonality's own `if (... != null) num *= ...`).
//
// WHAT IS DELIBERATELY NOT REUSED. CurrentWeatherCommonality also folds in the fire-needs-rain factor,
// GameCondition.WeatherCommonalityFactor, tile-mutator overrides, and gates on map.mapTemperature.OutdoorTemp
// (the LIVE outdoor temperature, which weather itself perturbs). This file uses
// GenTemperature.GetTemperatureFromSeasonAtTile instead — the pure seasonal estimate with no weather
// noise in it — because §22 is answering "what kind of place is this, this time of year", and feeding
// today's actual weather-perturbed temperature back into "how cloudy should a typical day be" would
// make the estimate depend on itself. See SeasonalWetFraction.cs's own header for the same point made
// from the pure-math side.
public static class CloudCoverClock
{
    // Vanilla's own threshold for "this weather meaningfully produces rain", reused here rather than
    // invented: CurrentWeatherCommonality checks `weather.rainRate > 0.1f` twice (the fire-chance
    // factor and the post-fire rain-disable window). No equivalent vanilla threshold exists for snow,
    // so the same number is applied to snowRate for consistency rather than picking a second, unrelated
    // constant.
    private const float WetRateThreshold = 0.1f;

    private readonly struct CachedSample
    {
        public readonly int SampleIndex;
        public readonly float Fraction;

        public CachedSample(int sampleIndex, float fraction)
        {
            SampleIndex = sampleIndex;
            Fraction = fraction;
        }
    }

    // Keyed by TILE id, same reasoning as AerosolDriftClock.Cache: the cached value is a pure function
    // of exactly (tileId, sampleIndex, that tile's biome data), and the biome a tile sits on does not
    // change mid-game, so a hit requires the inputs to match, which means the value is right.
    private static readonly Dictionary<int, CachedSample> Cache = new Dictionary<int, CachedSample>();

    // How cloudy this map's Clear-weather sky should look right now, in [0, 1]. Only meaningful while
    // the map is actually in Clear weather — see this file's callers for that gate; this method has
    // no opinion on what weather the map is currently in. Returns 0 when CelestialLightingFeatures
    // .CloudCover is off, which is what makes "off" a faithful pre-feature baseline for both callers
    // at once — see the flag itself for why "off" must mean this, not "no effect this frame".
    //
    // PER-FRAME COST. Same shape as AerosolDriftClock.MultiplierForMap: on the overwhelming majority of
    // calls this is a dictionary lookup plus one int compare. SeasonalWetFractionFor — the expensive
    // half, since it walks the biome's whole weather list — only runs once per tile per in-game HOUR,
    // the same cadence CloudCoverDrift's own noise is sampled at, so there is no reason to cache it any
    // more finely than the value it feeds.
    public static float FractionForMap(Map map)
    {
        // Gated here rather than in each caller, mirroring WeatherDimming.CloudOpacityFor: this is
        // the one place both Patch_CloudCoverSky and Patch_CloudCoverLabel actually call, so gating
        // it here is the only way to guarantee neither can drift out of sync with the other about
        // whether the feature is on.
        if (!CelestialLightingFeatures.CloudCover)
            return 0f;

        int tileId = map.Tile.tileId;
        int absTick = Find.TickManager.TicksAbs;
        int sampleIndex = CloudCoverDrift.SampleIndex(absTick);

        if (Cache.TryGetValue(tileId, out CachedSample cached) && cached.SampleIndex == sampleIndex)
            return cached.Fraction;

        float wetFraction = SeasonalWetFractionFor(map, absTick);

        // The tile id doubles as the noise seed, same as AerosolDriftClock — stable across save/load,
        // independent between two colonies on one planet.
        float fraction = CloudCoverDrift.Fraction(sampleIndex, tileId, wetFraction);
        Cache[tileId] = new CachedSample(sampleIndex, fraction);
        return fraction;
    }

    private static float SeasonalWetFractionFor(Map map, int absTick)
    {
        Tile tileInfo = map.TileInfo;
        BiomeDef biome = tileInfo.PrimaryBiome;
        List<WeatherCommonalityRecord> records = biome.baseWeatherCommonalities;
        float seasonalTemperature = GenTemperature.GetTemperatureFromSeasonAtTile(absTick, map.Tile);

        List<SeasonalWetFraction.Entry> entries = new List<SeasonalWetFraction.Entry>(records.Count);
        for (int i = 0; i < records.Count; i++)
        {
            WeatherCommonalityRecord record = records[i];
            WeatherDef weather = record.weather;

            // A cross-ref that failed to resolve is not something a live game should ever hand us, but
            // costs nothing to guard against — see SeasonalWetFraction.cs's own defensive posture
            // toward malformed entries.
            if (weather != null)
            {
                bool isWet = weather.rainRate > WetRateThreshold || weather.snowRate > WetRateThreshold;
                bool eligible = weather.temperatureRange.Includes(seasonalTemperature);

                // Mirrors CurrentWeatherCommonality's own null check exactly: no curve means a factor
                // of 1 (no rainfall dependence), not a factor of 0.
                float rainfallFactor = weather.commonalityRainfallFactor != null
                    ? weather.commonalityRainfallFactor.Evaluate(tileInfo.rainfall)
                    : 1f;

                entries.Add(new SeasonalWetFraction.Entry(record.commonality, rainfallFactor, isWet, eligible));
            }
        }

        return SeasonalWetFraction.Fraction(entries);
    }
}
