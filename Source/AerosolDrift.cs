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

    // Hourly cadence and its floor-division-to-bucket arithmetic now live in LatticeDriftNoise.cs,
    // shared with §22's CloudCoverDrift (see that file's header for why sampling is hourly at all —
    // the reasoning is unchanged, only its location). Restated here as forwarding constants rather than
    // deleted, because AerosolDriftTests.cs — and the rest of this file — is pinned against
    // AerosolDrift.TicksPerSample and AerosolDrift.SampleIndex by name. This is a relocation of the
    // arithmetic, not a change to it: the public surface stays exactly what it was.
    public const int TicksPerSample = LatticeDriftNoise.TicksPerSample;

    // Samples per in-game day. RimWorld days are 60000 ticks; 60000 / 2500 = 24.
    public const int SamplesPerDay = LatticeDriftNoise.SamplesPerDay;

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

    // Which hourly sample the given absolute tick falls in. Forwards to LatticeDriftNoise — see that
    // file for the floor-division reasoning — kept as a named method here because AerosolDriftTests.cs
    // and the rest of this file call it as AerosolDrift.SampleIndex.
    public static int SampleIndex(int absoluteTicks) => LatticeDriftNoise.SampleIndex(absoluteTicks);

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

    // The raw noise field in [0, 1]. Forwards to LatticeDriftNoise.Field with this file's own cell
    // width/period/octave constants — see that file's header for why the wrapping and the y=0
    // AuroraNoise collapse live there now, and DESIGN.md §20c for why AerosolDrift's own correlation
    // time is days rather than the hours §22's CloudCoverDrift uses for the same engine.
    private static float Field(int sampleIndex, int tileSeed) =>
        LatticeDriftNoise.Field(sampleIndex, tileSeed, SamplesPerCell, LatticeCells, Octaves);

    private static float ClampAmplitude(float amplitude)
    {
        if (amplitude < 0f || float.IsNaN(amplitude))
            return 0f;

        return amplitude > MaxDriftAmplitude ? MaxDriftAmplitude : amplitude;
    }
}
