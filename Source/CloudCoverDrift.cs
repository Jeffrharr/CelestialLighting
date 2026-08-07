namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file, same discipline and same
// reason as AerosolDrift.cs: linked into the offline test project via <Compile Include>, so the
// shipped code is the tested code. The live half that reads the tick, the tile id, and the day's
// SeasonalWetFraction is CloudCoverClock.cs, exactly the split FrameStamp.cs/GeometryMemo.cs already
// establishes.
//
// WHAT THIS MODELS (DESIGN.md §22). "Clear" in vanilla is a single fixed sky palette for its entire
// duration — see WeatherDecider's own discrete weighted-random state machine, which has no notion of
// a fractional cloud amount anywhere in it. This file invents that missing axis: while the map is
// actually in Clear weather, how overcast does the sky look right now, as a fraction in [0, 1].
//
// TWO SEPARATE QUESTIONS, TWO SEPARATE FILES. "How cloudy is a typical Clear day on this tile, this
// time of year" is SeasonalWetFraction.cs's question, answered from the same ingredients vanilla's
// own WeatherDecider.CurrentWeatherCommonality uses for its rain term (biome commonality x rainfall
// curve, temperature-gated) — that value barely moves within a single day. "How cloudy is it THIS
// HOUR, given that average" is this file's question, answered by wobbling around the seasonal mean
// with coherent noise. Composing the two happens here (Fraction/FractionWithAmplitude) rather than in
// SeasonalWetFraction.cs, because the noise engine and the composition rule are what actually differ
// from AerosolDrift's — the wet-fraction classifier stays a pure function of biome/season data with no
// noise mixed into it, testable on its own.
//
// WHY THE CORRELATION TIME IS HOURS, NOT DAYS. AerosolDrift's 3-day lattice cell is deliberately built
// so nothing changes inside one evening — any visible wobble there reads as flicker fighting §8's
// smooth elevation ramp. Cloud cover is the opposite brief: real skies visibly change over a single
// afternoon, and are supposed to. An 8-hour base cell puts roughly three "regimes" across a day (a
// morning value, an afternoon value, a night value) with smootherstep interpolation carrying it
// smoothly between them — no hour-to-hour jump — while summing three octaves (fastest layer ~2 hours)
// lets two octaves occasionally align and produce a faster net swing than any single layer alone. That
// is the "clouds CAN change drastically some hours, but usually drift" character asked for, and it
// falls out of the noise shape rather than needing a special case for it.
//
// WHY ADDITIVE AROUND THE SEASONAL MEAN RATHER THAN MULTIPLICATIVE AROUND 1. AerosolDrift multiplies
// because it is modelling a LOADING (more or less particulate in air that already has some) —
// multiplying by 0 keeps an unpolluted tile provably at 0 with no gate anywhere. Cloud cover is a
// FRACTION/probability, not a loading: a tile with wetFraction 0 should still be ABLE to have a
// passing cloud drift over it, not be structurally pinned to zero. So the noise wobbles around the
// target additively and the sum is clamped, rather than the target being scaled by the noise.
public static class CloudCoverDrift
{
    // Width of one noise lattice cell, in HOURS — the base correlation time. This is the number
    // DESIGN.md's live A/B is expected to retune first if the drift reads too choppy or too syrupy;
    // everything below it (SamplesPerCell, LatticeCells) is derived so only this needs to change.
    public const int LatticeCellHours = 8;

    // Three octaves, not AerosolDrift's two. Each extra octave halves the fastest layer's cell width
    // (LatticeCellHours / 2^(Octaves-1)), so three octaves here put the fastest layer at 2 hours —
    // comfortably inside a single day, which is the point: AerosolDrift's whole design is keeping the
    // fastest layer OUTSIDE a day, and this file's whole design is putting it inside one.
    public const int Octaves = 3;

    // Samples per base lattice cell. One sample is one in-game hour (LatticeDriftNoise.TicksPerSample),
    // so this is just the cell width restated in samples rather than an independent number — unlike
    // AerosolDrift, where LatticeCellDays and SamplesPerDay are different units and have to be
    // multiplied together.
    public const int SamplesPerCell = LatticeCellHours;

    // How many base cells before the field repeats. Chosen to match AerosolDrift's own period (294912
    // samples = 12288 days, ~205 in-game years) for the same reason that number was picked there: no
    // colony reaches it, and matching it means both drifts can be reasoned about with one period figure
    // rather than two.
    public const int LatticeCells = 36864;

    // Full period sequence in samples: 36864 x 8 = 294912 (12288 days) — see LatticeCells above.
    public const int SamplesPerPeriod = LatticeCells * SamplesPerCell;

    // How far the sampled cloud cover is allowed to wobble from the day's seasonal mean, in absolute
    // fraction. Unlike AerosolDrift.MaxDriftAmplitude, there is no structural ceiling this file MUST
    // enforce below 1 — composition here is additive-then-clamped, so even an amplitude far past 1
    // cannot produce a negative or unbounded result the way an unclamped multiplicative amplitude
    // could. The ceiling below exists only to keep a stray settings value or dev override meaningful
    // rather than to prevent a crash.
    //
    // Starting value, not yet live-tuned. 0.35 mirrors AerosolDrift.DriftAmplitude's own figure only
    // because both were picked the same way — an eyeballed guess pending the live A/B this repo
    // requires for every visual change (DESIGN.md's verification bar) — not because the two quantities
    // are physically comparable.
    public const float WobbleAmplitude = 0.35f;

    // Ceiling described above. 1.0 rather than something tighter because, again, nothing breaks
    // structurally past that point here — it exists so "amplitude 5" and "amplitude 1e9" read the same
    // as "amplitude 1" instead of each being its own untested edge case.
    public const float MaxWobbleAmplitude = 1f;

    // Which hourly sample the given absolute tick falls in. Forwards to LatticeDriftNoise, named here
    // (rather than calling LatticeDriftNoise.SampleIndex directly from CloudCoverClock.cs) purely so
    // this file reads the same shape as AerosolDrift.cs for anyone comparing the two side by side.
    public static int SampleIndex(int absoluteTicks) => LatticeDriftNoise.SampleIndex(absoluteTicks);

    // The shipped fraction: today's seasonal mean, wobbled by right-now's noise on this tile.
    public static float Fraction(int sampleIndex, int tileSeed, float wetFraction) =>
        FractionWithAmplitude(sampleIndex, tileSeed, wetFraction, WobbleAmplitude);

    // Fraction with the amplitude spelled out, so the shape of the invariants pinned against it in
    // CloudCoverDriftTests.cs does not silently stop meaning anything if WobbleAmplitude is retuned —
    // same reason AerosolDrift.MultiplierWithAmplitude exists alongside AerosolDrift.Multiplier.
    public static float FractionWithAmplitude(int sampleIndex, int tileSeed, float wetFraction, float amplitude)
    {
        float clampedAmplitude = ClampAmplitude(amplitude);
        float wobble = clampedAmplitude * (2f * Field(sampleIndex, tileSeed) - 1f);
        return Clamp01(Clamp01(wetFraction) + wobble);
    }

    // The raw noise field in [0, 1]. Forwards to LatticeDriftNoise.Field with this file's own cell
    // width/period/octave constants — see LatticeDriftNoise.cs's header for why the wrapping and the
    // AuroraNoise collapse to one dimension live there now, shared with AerosolDrift.
    private static float Field(int sampleIndex, int tileSeed) =>
        LatticeDriftNoise.Field(sampleIndex, tileSeed, SamplesPerCell, LatticeCells, Octaves);

    private static float ClampAmplitude(float amplitude)
    {
        if (amplitude < 0f || float.IsNaN(amplitude))
            return 0f;

        return amplitude > MaxWobbleAmplitude ? MaxWobbleAmplitude : amplitude;
    }

    // NaN-guarding clamp. A NaN wetFraction handed in by a caller (or a NaN produced somewhere upstream
    // in a modded biome's data) must not propagate into a NaN sky colour the way AerosolDrift's own
    // amplitude guard exists to prevent — see that file's NonsenseAmplitudes test for the precedent.
    private static float Clamp01(float v)
    {
        if (float.IsNaN(v))
            return 0f;

        return v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
