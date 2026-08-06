namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file, same discipline and same
// reason as AtmosphericColumn.cs and AuroraNoise.cs: it is linked into the offline test project via
// <Compile Include> so the exact code that ships is the exact code under test. The live half — the
// one that reads Find.TickManager.TicksAbs and the map's tile id, and memoises the result — lives in
// AerosolDriftClock.cs, exactly the split FrameStamp.cs/GeometryMemo.cs already uses.
//
// WHAT THIS MODELS (DESIGN.md §20c). After §20b, a map's aerosol column is a FIXED function of its
// tile: pollution x exp(-siteAltitude/1500). Two consecutive evenings on the same map therefore
// produce a pixel-identical sunset, which is exactly the monotony players notice even when any one
// sunset looks good in a screenshot.
//
// Real sunsets differ night to night for a reason that has nothing to do with geometry — the sun's
// path is nearly identical two evenings running. What changes is the AIR MASS overhead. Maritime air
// arrives clean, continental air arrives loaded, a front brings a different size distribution, smoke
// drifts in from somewhere else entirely. So this file drives §20b's column with a slow noise that
// wanders around the tile's baseline instead of sitting on it.
//
// THE PRIMARY FAILURE MODE IS FLICKER, NOT MONOTONY. An aerosol column that wobbles inside a single
// evening does not read as weather; it reads as a bug, and it would fight §8's elevation ramp, which
// is smooth by construction. Everything below is shaped around a correlation time of a couple of
// DAYS: the lattice cell is three days wide, only two octaves are summed (so the fastest layer still
// spans a day and a half), and the tests pin the ratio between within-evening and across-day change
// rather than trusting those numbers to stay right.
//
// WHY MULTIPLICATIVE AROUND 1 RATHER THAN ADDITIVE. An air mass carries more or less of whatever the
// tile's sources put into it; it does not manufacture haze over a pristine tile. So the physical
// quantity that varies is the LOADING, and loading scales the column. That also gives the property
// worth having most: a zero-pollution tile — every tile in a game without Biotech — multiplies zero
// by something and stays exactly zero, so this whole subsystem is provably inert wherever §20b was.
public static class AerosolDrift
{
    // How far the column is allowed to wander, as a fraction of the tile's baseline. 0.35 means an
    // air mass can carry the column anywhere in [0.65x, 1.35x] of what §20b alone would have given.
    //
    // Sized against what it does to the thing a player actually sees. §20b's warm endpoint runs from
    // 2000 K (clean) to 1500 K (fully loaded) at sea level, so on a pollution-0.5 tile the baseline
    // aerosolFraction of 0.5 lands the endpoint at 1750 K, and +-35% moves it across roughly
    // 1837-1662 K. That is a visible difference between two evenings without either of them reading
    // as a different biome — which is the whole brief. Larger amplitudes start producing evenings
    // that look like a wildfire moved in, on a tile with no wildfire.
    public const float DriftAmplitude = 0.35f;

    // Hard ceiling on any amplitude this file will honour, whatever a caller passes. The multiplier
    // is 1 + amplitude * u with u in [-1, 1], so an amplitude below 1 keeps the multiplier strictly
    // positive BY CONSTRUCTION rather than by a clamp that could be forgotten. 0.9 leaves that
    // guarantee with margin (worst case 0.1x) and is far above anything the aesthetics want.
    //
    // This exists because "no configuration of the noise can produce a negative or absurd optical
    // depth" is a property the subsystem has to have, not a property of the one amplitude we happen
    // to ship. A future settings slider, a dev override, or a test sweeping silly values all land
    // here.
    public const float MaxDriftAmplitude = 0.9f;

    // One in-game hour, i.e. RimWorld's GenDate.TicksPerHour. Restated as a literal rather than read
    // from GenDate because this file must not reference Verse — the live adapter is where anything
    // may know what a GenDate is. GenDate.DaysPerYear is pinned in ApiCompatibilityTests for the same
    // class of reason; this one is pinned as TickManager.TicksAbs plus the arithmetic below.
    //
    // WHY HOURLY, AND WHY THE STAIRCASE IS INVISIBLE. Sampling continuously would be smoother and is
    // tempting, but the mod has a real performance history around per-frame work (issues #11, #12,
    // #20, #23, #60; DESIGN.md §16) and this value is read several times per map per frame off
    // WeatherWorker.CurSkyTarget. Hourly quantisation turns that into one noise evaluation per map
    // per in-game hour. The cost of the staircase: the multiplier's fastest hourly step is 0.018
    // (measured over the whole period, and pinned), which on the very worst tile (sea level,
    // pollution 1) is about 9 K of horizon endpoint. Over that same hour the sun itself moves ~15
    // degrees and drags the endpoint by several HUNDRED K continuously, so the step is around one
    // percent of a motion already happening. It is not a compromise anyone can see.
    public const int TicksPerSample = 2500;

    // Samples per in-game day. RimWorld days are 60000 ticks; 60000 / 2500 = 24.
    public const int SamplesPerDay = 24;

    // Width of one noise lattice cell, in days — the coarse correlation time. Three days for the
    // BASE octave; with the second octave at half that, the effective correlation time of the summed
    // field is around two days, which is the "air masses persist" number the design asks for.
    //
    // Stated as the base cell rather than as the effective figure because the base cell is what the
    // arithmetic below actually uses, and a constant whose name does not match what it does is how
    // these things drift.
    public const float LatticeCellDays = 3f;

    // Two octaves, not the three AuroraCurtain uses. Each extra octave halves the timescale of the
    // fastest layer, and a third here would put a component on an 18-hour cell — comfortably inside
    // a single evening, which is precisely the flicker this subsystem must not produce. Two is
    // enough to keep the field from feeling like a metronome hitting a lattice node every three days
    // while leaving the fastest layer at a day and a half.
    public const int Octaves = 2;

    // How many base cells before the noise repeats. AuroraNoise wraps by construction (it is built
    // for a tiled texture), so a period is not optional — it is a parameter we choose. 4096 cells at
    // three days each is 12288 days, about 205 in-game years, which no colony reaches.
    //
    // The wrap is also what keeps this exact in floating point. Wrapping the sample index in INTEGER
    // arithmetic before dividing means the coordinate handed to the noise never exceeds 4096,
    // regardless of how far into the game's absolute tick count we are — so a colony in year 5600
    // gets the same lattice resolution as one in year 5500 rather than slowly losing mantissa bits.
    public const int LatticeCells = 4096;

    // Samples per base lattice cell: 3 days x 24 = 72.
    public const int SamplesPerCell = (int)(LatticeCellDays * SamplesPerDay);

    // Full period of the sequence in samples: 4096 x 72 = 294912 (12288 days).
    public const int SamplesPerPeriod = LatticeCells * SamplesPerCell;

    // Which hourly sample the given absolute tick falls in. This is the ONLY place the tick becomes a
    // sample index, so it is the only place the cadence lives.
    //
    // Floor division rather than C#'s truncating /, so a negative absolute tick (nothing in a real
    // game produces one, but dev tooling and tests reach for them) walks backwards through the
    // sequence one bucket at a time instead of folding two buckets together across zero.
    public static int SampleIndex(int absoluteTicks)
    {
        int quotient = absoluteTicks / TicksPerSample;
        bool roundedTowardZeroFromBelow = absoluteTicks < 0 && quotient * TicksPerSample != absoluteTicks;
        return roundedTowardZeroFromBelow ? quotient - 1 : quotient;
    }

    // The shipped multiplier: what to scale §20b's aerosol column by right now on this tile.
    public static float Multiplier(int sampleIndex, int tileSeed) =>
        MultiplierWithAmplitude(sampleIndex, tileSeed, DriftAmplitude);

    // The multiplier with the amplitude spelled out, which is the form the invariants are pinned
    // against — "amplitude 0 is bit-identical to §20b" is only a meaningful regression pin if zero
    // can actually be passed.
    //
    // MEAN 1 IS STRUCTURAL, NOT TUNED. AuroraNoise.Fbm is a convex combination of Hash01 values, and
    // Hash01 is uniform on [0, 1), so the field's mean is 0.5 exactly (to within the 2^-25 offset of
    // a half-open uniform). Mapping it through 2u-1 therefore has mean 0 exactly, and 1 + amplitude *
    // 0 is 1 for every amplitude. The baseline cannot drift as a side effect of retuning the
    // amplitude, the cell width, or the octave count, because none of those touch that argument —
    // which is why the shape is `1 + a * (2u - 1)` rather than anything that would need a corrective
    // constant.
    public static float MultiplierWithAmplitude(int sampleIndex, int tileSeed, float amplitude)
    {
        float clampedAmplitude = ClampAmplitude(amplitude);

        // Exactly 1f, not "1f to within rounding". §20b's behaviour has to be reproducible bit for
        // bit with the drift switched off, and returning early is the only way to promise that
        // without depending on the noise field's own arithmetic landing on a value that multiplies
        // out cleanly.
        if (clampedAmplitude <= 0f)
            return 1f;

        return 1f + clampedAmplitude * (2f * Field(sampleIndex, tileSeed) - 1f);
    }

    // Apply a multiplier to §20b's baseline column, keeping the [0, 1] contract every consumer of an
    // aerosol fraction is written against.
    //
    // THE CLAMP AT 1 IS A REAL ASYMMETRY AND IS DELIBERATE. On a maximally polluted sea-level tile
    // the baseline is already 1 — the most aerosol the model knows how to mean — so the upward half
    // of the excursion has nowhere to go and is clipped, while the downward half is not. That is the
    // correct trade: SkyColorTemperature.HorizonKelvinForColumns lerps toward AerosolHorizonKelvin on
    // this fraction, and a fraction above 1 would EXTRAPOLATE past 1500 K — precisely the "runs off
    // the end of the world" failure §20b's own header argues against. The curve clamps defensively
    // too; this clamp is what keeps the boundary's stated contract true rather than relying on that.
    //
    // The baseline-preservation invariant is therefore pinned on the MULTIPLIER, which is where it is
    // actually a property of the model, and not on the post-clamp fraction, where it is a property of
    // how close a particular tile sits to the ceiling.
    public static float ApplyMultiplier(float baselineAerosolFraction, float multiplier)
    {
        float driven = baselineAerosolFraction * multiplier;
        return driven < 0f ? 0f : (driven > 1f ? 1f : driven);
    }

    // The raw noise field in [0, 1]. One-dimensional in time, obtained from the two-dimensional
    // AuroraNoise by pinning y.
    //
    // WHY REUSE AURORANOISE RATHER THAN WRITE A 1-D VALUE NOISE. It already owns the two decisions
    // that are easy to get subtly wrong — an avalanching integer hash that does not leave the field
    // visibly correlated between neighbouring lattice points, and a smootherstep fade that matches
    // the second derivative at cell boundaries so the field has no creases. A crease in a texture is
    // a visible line; a crease here would be a kink in how fast the haze changes, which is the same
    // artifact in the time axis. A second copy of that would be a second thing to get right.
    //
    // y = 0 with yPeriod = 1 is what collapses it to one dimension, and the two are belt and braces
    // rather than redundancy. y = 0 makes the vertical fade exactly 0 in EVERY octave (the octave
    // scaling multiplies y by the frequency, and 0 times anything is 0), so only the bottom lattice
    // row is ever read. yPeriod = 1 additionally wraps both rows onto index 0, so even a future
    // caller that passed a nonzero y would get the same answer instead of silently sampling a
    // different slice of the field.
    private static float Field(int sampleIndex, int tileSeed)
    {
        // Integer wrap BEFORE the divide — see LatticeCells for why this is the precision-preserving
        // order. Always-positive modulo, because a negative coordinate would index the hash lattice
        // outside [0, LatticeCells) and break the wrap in one direction only.
        int wrapped = sampleIndex % SamplesPerPeriod;
        if (wrapped < 0)
            wrapped += SamplesPerPeriod;

        float x = wrapped / (float)SamplesPerCell;
        return AuroraNoise.Fbm(x, 0f, LatticeCells, 1, tileSeed, Octaves);
    }

    private static float ClampAmplitude(float amplitude)
    {
        if (amplitude < 0f || float.IsNaN(amplitude))
            return 0f;

        return amplitude > MaxDriftAmplitude ? MaxDriftAmplitude : amplitude;
    }
}
