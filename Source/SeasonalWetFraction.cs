using System.Collections.Generic;

namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file, same discipline and same
// reason as AerosolDrift.cs and CloudCoverDrift.cs: linked into the offline test project via
// <Compile Include>, so the shipped code is the tested code. The live half — the one that actually
// reads a BiomeDef's baseWeatherCommonalities and a tile's rainfall/temperature — lives in
// CloudCoverClock.cs, which builds the Entry list below and hands it here.
//
// THE QUESTION THIS FILE ANSWERS (DESIGN.md §22). "How cloudy should a typical Clear day read, on
// this tile, this time of year" — a single number in [0, 1] that CloudCoverDrift.cs then wobbles
// hour to hour. It is deliberately NOT "what is the chance of rain today": vanilla already rolls that
// every WeatherDeciderTick, and this file has no opinion on it. It reuses the same two ingredients
// vanilla's own (private) WeatherDecider.CurrentWeatherCommonality multiplies together for its own
// rain-likelihood term — a biome's baseWeatherCommonalities weight per WeatherDef, and that
// WeatherDef's commonalityRainfallFactor curve evaluated at the tile's rainfall stat — so a biome/
// season vanilla already treats as rain-prone reads as cloud-prone here too, for the same underlying
// reason.
//
// WHAT IS DELIBERATELY LEFT OUT. CurrentWeatherCommonality also multiplies by
// GameCondition.WeatherCommonalityFactor — quests, fire-needs-rain, Anomaly conditions — and by any
// tile-mutator override. Those are reactive modifiers to the actual weather ROLL, not properties of
// the tile's underlying climate. Cloud cover is meant to answer "what kind of place is this, this time
// of year", not "is there a quest condition active right now", so only the climate half is reused
// here: base commonality x rainfall curve, gated by whether the weather could even occur at the
// current seasonal temperature. A caller wanting those reactive modifiers included can fold them into
// the Commonality an Entry is built with — this file does not know or care where that number came
// from.
public static class SeasonalWetFraction
{
    // One WeatherDef's contribution to the tile's climate, as the caller has already read it off
    // BiomeDef/WeatherDef/Tile for the day in question. Plain data, not a reference to a WeatherDef
    // itself, so this file never needs to know what a WeatherDef is.
    public readonly struct Entry
    {
        // BiomeDef.baseWeatherCommonalities' weight for this WeatherDef — vanilla's base likelihood
        // of the weather occurring on this biome at all, before the rainfall curve is applied.
        public readonly float Commonality;

        // WeatherDef.commonalityRainfallFactor evaluated at the tile's rainfall stat — vanilla's own
        // rainfall-dependent scaling of that same likelihood.
        public readonly float RainfallFactor;

        // Whether this WeatherDef counts toward "wet" for cloud-cover purposes (e.g. it rains or
        // snows) as opposed to "dry" (e.g. Clear, Fog). This file does not classify WeatherDefs
        // itself — see this file's header for why that judgement call belongs to the caller, which
        // has the actual WeatherDef in hand.
        public readonly bool IsWet;

        // Whether this WeatherDef can occur at all at the tile's current seasonal temperature (vanilla
        // gates several weather types this way — see WeatherDef.temperatureRange). An ineligible entry
        // contributes to neither wetMass nor totalMass: a weather type that literally cannot happen
        // right now should not move the estimate in either direction, the same way vanilla's own roll
        // would never select it.
        public readonly bool TemperatureEligible;

        public Entry(float commonality, float rainfallFactor, bool isWet, bool temperatureEligible)
        {
            Commonality = commonality;
            RainfallFactor = rainfallFactor;
            IsWet = isWet;
            TemperatureEligible = temperatureEligible;
        }
    }

    // wetMass / totalMass over the biome's weather list, each entry weighted by
    // Commonality x RainfallFactor — the same product vanilla's own weather roll would weight it by.
    // Zero-guarded: a biome/temperature combination where every entry is ineligible (or the list is
    // empty) has no climate signal to read at all, and 0 (nothing to be cloudy about) is a safer
    // default there than a divide-by-zero or a fabricated middle value.
    public static float Fraction(IReadOnlyList<Entry> entries)
    {
        float wetMass = 0f;
        float totalMass = 0f;

        for (int i = 0; i < entries.Count; i++)
        {
            Entry entry = entries[i];
            if (entry.TemperatureEligible)
            {
                // Clamped rather than trusted, because RainfallFactor comes from a caller-supplied
                // AnimationCurve that modded content controls — a curve dipping negative should not be
                // able to make this weather type SUBTRACT from totalMass and skew the ratio in a
                // direction no vanilla roll could ever produce.
                float mass = NonNegative(entry.Commonality * entry.RainfallFactor);
                totalMass += mass;
                if (entry.IsWet)
                    wetMass += mass;
            }
        }

        return totalMass > 0f ? Clamp01(wetMass / totalMass) : 0f;
    }

    private static float NonNegative(float v) => v < 0f ? 0f : v;

    // NaN-guarding clamp — see CloudCoverDrift.Clamp01 for why this matters: a NaN here would
    // otherwise flow straight into CloudCoverDrift's wetFraction argument and on into a sky colour.
    private static float Clamp01(float v)
    {
        if (float.IsNaN(v))
            return 0f;

        return v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
