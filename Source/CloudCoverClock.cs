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

    // WHAT IS CACHED IS THE SEASONAL MEAN, NOT THE FINISHED FRACTION, which is a change from how this
    // file started and worth recording. The memo used to hold the hour's fraction, which made the
    // shipped value a step function of the tick. That was invisible while §22 was only ever LOOKED at,
    // and stopped being invisible when §25 turned the same number into a COUNT of drawn cloud sheets:
    // every step at the top of an hour was a cloud appearing or vanishing in mid-sky (see
    // CloudCoverDrift.FractionAt for the measurements). Caching one step earlier keeps the whole
    // performance argument — SeasonalWetFractionFor is the expensive half and still runs once per tile
    // per in-game hour — while the cheap half, three octaves of lattice noise, is evaluated where the
    // tick actually is.
    private readonly struct CachedSample
    {
        public readonly int SampleIndex;
        public readonly float WetFraction;

        public CachedSample(int sampleIndex, float wetFraction)
        {
            SampleIndex = sampleIndex;
            WetFraction = wetFraction;
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
    // CONTINUOUS IN THE TICK, not stepped hourly — see CachedSample above for why that changed and
    // CloudCoverDrift.FractionAt for what it costs. Values at the top of each hour are unchanged, so
    // this is a refinement of the same curve rather than a different one.
    //
    // PER-FRAME COST. Same shape as AerosolDriftClock.MultiplierForMap: on the overwhelming majority of
    // calls this is a dictionary lookup plus one int compare, and now three octaves of lattice noise
    // on top — tens of flops, on a path that already walks MapSky. SeasonalWetFractionFor — the
    // expensive half, since it walks the biome's whole weather list — still runs only once per tile
    // per in-game HOUR.
    public static float FractionForMap(Map map) => FractionForTick(map, Find.TickManager.TicksAbs);

    // The same value AT AN ARBITRARY TICK, which is what lets §25 decide a cloud's existence from the
    // cover at the moment that cloud entered the map rather than from the cover right now — see
    // CloudSheetLayout.EntryTickFor for why that is the whole of "clouds drift off instead of
    // vanishing", and why it needs no state to do it.
    //
    // THE SEASONAL MEAN IS TODAY'S, NOT THE PAST TICK'S, and that is a deliberate approximation
    // rather than an oversight. The two terms move at wildly different rates: the noise is the fast
    // one (a 2-hour fastest octave, which is exactly what §25 is reading back through), while the
    // seasonal mean is a temperature curve that barely moves within a day. Evaluating the mean at the
    // past tick too would mean SeasonalWetFractionFor — a walk of the biome's whole weather list —
    // running once per SHEET per hour instead of once per tile per hour, to shift a latched cover by
    // a few thousandths. What it costs is that a latched value is not perfectly frozen: it steps by
    // the seasonal drift of one hour when the memo rolls over, which is far below the twelfth of
    // cover that would add or drop a sheet.
    public static float FractionForTick(Map map, int absTick)
    {
        // Gated here rather than in each caller, mirroring WeatherDimming.CloudOpacityFor: this is
        // the one place both Patch_CloudCoverSky and Patch_CloudCoverLabel actually call, so gating
        // it here is the only way to guarantee neither can drift out of sync with the other about
        // whether the feature is on.
        if (!CelestialLightingFeatures.CloudCover)
            return 0f;

        int tileId = map.Tile.tileId;

        // The tile id doubles as the noise seed, same as AerosolDriftClock — stable across save/load,
        // independent between two colonies on one planet.
        return CloudCoverDrift.FractionAt(
            CloudCoverDrift.SampleIndex(absTick),
            CloudCoverDrift.SamplePhase(absTick),
            tileId,
            SeasonalWetFractionNow(map, tileId));
    }

    // Today's seasonal mean for this tile, memoised on §22's own hourly cadence. Always read at the
    // CURRENT tick, whatever tick the caller is asking the noise about — see FractionForTick.
    private static float SeasonalWetFractionNow(Map map, int tileId)
    {
        int absTick = Find.TickManager.TicksAbs;
        int sampleIndex = CloudCoverDrift.SampleIndex(absTick);

        if (Cache.TryGetValue(tileId, out CachedSample cached) && cached.SampleIndex == sampleIndex)
            return cached.WetFraction;

        float wetFraction = SeasonalWetFractionFor(map, absTick);
        Cache[tileId] = new CachedSample(sampleIndex, wetFraction);
        return wetFraction;
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
