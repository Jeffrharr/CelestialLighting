namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file, same rule and same reason as
// AuroraNoise.cs: linked into the offline test project via <Compile Include>, so the shipped code is
// the tested code.
//
// The hourly-sampled, smootherstep-interpolated multi-octave lattice engine shared by every "slowly
// drifting per-tile value" subsystem in this mod. §20c's AerosolDrift was the first consumer and, per
// its own header, was "deliberately named for its one consumer rather than generalised up front" —
// §22's CloudCoverDrift is the second consumer DESIGN.md §20c named as a plausible one, so this file
// is that generalisation now that there are two.
//
// What is actually shared between the two: the definition of "which hourly bucket does this absolute
// tick fall in" (SampleIndex), and the mechanics of turning a bucket into a wrapped [0,1) field value
// (Field). What is deliberately NOT shared, because it differs per consumer and each consumer's tests
// pin it independently: cell width, octave count, period length, amplitude, and — most importantly —
// how the raw [0,1) field gets composed into the consumer's own quantity (AerosolDrift multiplies
// around 1 because it models a loading; CloudCoverDrift adds around a target fraction because it
// models a probability). Composition stays in each consumer's own file.
public static class LatticeDriftNoise
{
    // One in-game hour, i.e. RimWorld's GenDate.TicksPerHour. Restated as a literal rather than read
    // from GenDate because this file must not reference Verse — the live adapter is where anything may
    // know what GenDate is. Shared across consumers because the performance argument for hourly
    // quantisation (DESIGN.md §20c: this value is read multiple times per map per frame off
    // WeatherWorker.CurSkyTarget, so continuous sampling would be a real per-frame cost) applies to any
    // consumer hung off the same hook, not just aerosol.
    public const int TicksPerSample = 2500;

    // Samples per in-game day. RimWorld days are 60000 ticks; 60000 / 2500 = 24.
    public const int SamplesPerDay = 24;

    // Which hourly sample a given absolute tick falls in. This is the ONLY place a tick becomes a
    // sample index, so it is the only place the cadence lives.
    //
    // Floor division rather than C#'s truncating /, so a negative absolute tick (nothing in real game
    // play produces one, but dev tooling and tests reach for them) walks backwards through the
    // sequence one bucket at a time instead of folding two buckets together across zero.
    public static int SampleIndex(int absoluteTicks)
    {
        int quotient = absoluteTicks / TicksPerSample;
        bool roundedTowardZeroFromBelow = absoluteTicks < 0 && quotient * TicksPerSample != absoluteTicks;
        return roundedTowardZeroFromBelow ? quotient - 1 : quotient;
    }

    // Raw noise field in [0, 1] for a given hourly sample and tile seed, one-dimensional in time
    // (obtained from AuroraNoise's two-dimensional field by pinning y — see AuroraNoise.Fbm's own
    // header for why that collapses cleanly to one axis).
    //
    // `samplesPerCell` is the base lattice cell width in samples (hours); `latticeCells` is how many
    // base cells before the field repeats; `octaves` is how many frequency-doubling layers are summed.
    // The caller owns all three, because they are what sets a consumer's correlation time and period —
    // AerosolDrift wants days, CloudCoverDrift wants hours, and neither should be able to move the
    // other's by editing a shared constant.
    public static float Field(int sampleIndex, int tileSeed, int samplesPerCell, int latticeCells, int octaves)
    {
        // Integer wrap BEFORE divide, so precision is preserved the same way AerosolDrift's original
        // Field did it: a colony many in-game years old gets the same lattice resolution as a fresh
        // one, rather than slowly losing mantissa bits to an ever-growing sample index. Always-positive
        // modulo, because a negative wrapped coordinate would index the hash lattice outside
        // [0, latticeCells) and break the wrap in one direction only.
        int samplesPerPeriod = latticeCells * samplesPerCell;
        int wrapped = sampleIndex % samplesPerPeriod;
        if (wrapped < 0)
            wrapped += samplesPerPeriod;

        float x = wrapped / (float)samplesPerCell;
        return AuroraNoise.Fbm(x, 0f, latticeCells, 1, tileSeed, octaves);
    }
}
