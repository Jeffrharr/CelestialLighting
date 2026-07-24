using System;

namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file (System only). Same
// discipline as Formulas.cs: this file is linked into both the shipped mod (net481) and the test
// project (net8.0) via a single <Compile Include>, so the exact moon model that runs inside
// RimWorld is the exact model under test, with no hand-copied reimplementation that could drift.
//
// This is the pure core of subsystem 6 (see DESIGN.md §6): a single representative moon modeled on
// the ecliptic. It deliberately leaves the ~5 degree lunar orbital inclination and lunar parallax
// out — a Moon-on-the-ecliptic approximation is more than accurate enough for a shadow direction
// and a night-brightness scalar, and modeling inclination/nodes belongs to the opt-in eclipse
// trigger (§10), not here (without them a flat-ecliptic moon would transit the sun every new moon).
//
// The design keeps the moon consistent with the sun by reusing Formulas' own solar-position
// simulator: the moon is treated as a second body on the same ecliptic, so its declination and
// altitude/azimuth come from the same equations, just offset by the moon's elongation from the sun.
public static class MoonMath
{
    // Default lunar cycle length, in in-game days. One RimWorld quadrum (GenDate.DaysPerQuadrum ==
    // 15, a quarter of the 60-day year) gives a tidy four cycles per year. Exposed as a plain
    // constant (not a live setting) because there is no ModSettings screen yet; GameComponent_
    // MoonPhase reads this as its default and is the single place a future settings slider will
    // override. Multiply by GenDate.TicksPerDay in the adapter to get a tick count — kept in days
    // here so this file needs no Verse constant.
    public const float DefaultSynodicMonthDays = 15f;

    // Reused from Formulas so the moon's declination amplitude matches the sun's exactly — the moon
    // rides the same ecliptic, tilted by the same real axial tilt, so "moon high in the winter sky"
    // is a geometric consequence rather than a separate tuned curve.
    public const float AxialTiltDegrees = Formulas.AxialTiltDegrees;

    // Below this moon elevation (the same refraction-adjusted horizon Formulas uses for the sun) the
    // moon is effectively down: it casts no shadow and contributes no moonlight. Shared so the moon
    // and sun agree on where "the horizon" is.
    public const float MoonHorizonElevationDegrees = Formulas.AtmosphericRefractionDegrees;

    // Peak strength of a moon-cast shadow's alpha, relative to a full daytime sun shadow. A full
    // moon overhead is dramatically dimmer than the sun, so even the strongest moon shadow is a
    // faint hint, not a hard-edged daytime shadow. Illuminated fraction and moon altitude scale down
    // from here (a new moon casts nothing; a low full moon casts a whisper).
    public const float MoonShadowMaxStrength = 0.28f;

    // The eight canonical named phases, in cycle order starting from new. Waxing (illuminated
    // fraction growing) runs New -> First Quarter -> Full; waning runs Full -> Last Quarter -> New.
    public enum MoonPhase
    {
        New,
        WaxingCrescent,
        FirstQuarter,
        WaxingGibbous,
        Full,
        WaningGibbous,
        LastQuarter,
        WaningCrescent,
    }

    // Fraction through the synodic cycle in [0, 1): 0 == new moon (moon lined up with the sun),
    // 0.5 == full moon (moon opposite the sun). Derived purely from the absolute tick count so every
    // map/tile shares one moon with no stored state to drift. Uses a floored (positive) modulo so a
    // negative or zero-based epoch still lands in [0, 1); returns 0 for a non-positive period rather
    // than dividing by zero.
    public static float SynodicCyclePosition(long ticksAbs, long synodicPeriodTicks)
    {
        if (synodicPeriodTicks <= 0L)
            return 0f;

        long wrapped = ((ticksAbs % synodicPeriodTicks) + synodicPeriodTicks) % synodicPeriodTicks;
        return (float)((double)wrapped / synodicPeriodTicks);
    }

    // Sun-moon elongation angle in degrees, 0 at new and 180 at full. It is just the cycle position
    // swept around a full circle — the moon pulls one full 360 degrees ahead of the sun over one
    // synodic month.
    public static float ElongationDegrees(float cyclePosition) => cyclePosition * 360f;

    // Illuminated fraction of the moon's disc, 0 at new and 1 at full. Standard half-cosine of the
    // elongation: (1 - cos(elongation)) / 2. This drives both how strong a moon shadow is and how
    // much moonlight the night gets.
    public static float IlluminatedFraction(float cyclePosition)
    {
        float elongationRad = ToRadians(ElongationDegrees(cyclePosition));
        return (1f - MathF.Cos(elongationRad)) * 0.5f;
    }

    // Waxing (getting brighter) for the first half of the cycle, waning for the second. Exactly at
    // full (0.5) this reports not-waxing, which is fine — Full is its own labeled phase.
    public static bool IsWaxing(float cyclePosition) => cyclePosition < 0.5f;

    // Index 0..7 into MoonPhase, each named phase centered on its exact cycle point (New on 0,
    // First Quarter on 0.25, Full on 0.5, Last Quarter on 0.75) by rounding to the nearest eighth.
    // Uses floor(x + 0.5) rather than Math.Round to avoid banker's (round-half-to-even) surprises
    // right on the eighth boundaries.
    public static int PhaseIndex(float cyclePosition)
    {
        float wrapped = cyclePosition - MathF.Floor(cyclePosition); // fold any stray value into [0, 1)
        int octant = (int)MathF.Floor(wrapped * 8f + 0.5f);
        return octant % 8; // floor(0.9375*8 + 0.5) == 8 wraps back to New
    }

    public static MoonPhase PhaseFor(float cyclePosition) => (MoonPhase)PhaseIndex(cyclePosition);

    // --- Moon position on the ecliptic ---
    //
    // The moon is treated as a second body on the same ecliptic as the sun, offset by its elongation.
    // sunEclipticAngle advances 2*pi per year (via day-of-year); the moon sits elongation degrees
    // ahead of it. Feeding the moon's own declination and hour angle back through Formulas' solar
    // equations then gives the moon's altitude/azimuth for the current tile and tick — the same
    // trigonometry the sun uses, so the two can never derive inconsistent sky geometry.

    // Moon declination in degrees. Mirrors Formulas.DeclinationSign's one-line sinusoid but evaluates
    // it at the moon's ecliptic angle (sun angle + elongation) instead of the sun's. At elongation 0
    // (new moon) this equals the sun's declination exactly; at 180 (full moon) it is the sun's
    // declination reflected across the equator, which is why a winter full moon rides high.
    public static float MoonDeclinationDegrees(float dayOfYear, float cyclePosition)
    {
        float sunAngle = dayOfYear / Formulas.DaysPerYear * MathF.PI * 2f;
        float moonAngle = sunAngle + ToRadians(ElongationDegrees(cyclePosition));
        return AxialTiltDegrees * -MathF.Cos(moonAngle);
    }

    // The moon's effective "day percent" for reuse with Formulas.SolarElevationDegrees /
    // SolarAzimuthDegrees. Those functions convert dayPercent to an hour angle via (dayPercent -
    // 0.5) * 360; the moon's hour angle lags the sun's by exactly the elongation (the moon rises
    // ~elongation degrees of rotation after the sun), so subtracting the cycle position from the
    // day percent reproduces moonHourAngle == sunHourAngle - elongation. Normalized into [0, 1) so
    // any consumer assuming that range is safe; the trig itself is wrap-invariant either way.
    public static float MoonDayPercent(float dayPercent, float cyclePosition)
    {
        float shifted = dayPercent - cyclePosition;
        return shifted - MathF.Floor(shifted);
    }

    // Normalized 0..1 night-brightness contribution from the moon, for the night-radiance subsystem
    // (§7) to sum with its starlight/airglow floors and scale by its own moonlight slider. Zero when
    // the moon is below the horizon; otherwise the illuminated fraction scaled by how high the moon
    // is (sin of elevation), so a full moon at zenith reads 1 and a low crescent reads near zero.
    // This is intentionally a bare 0..1 scalar with no absolute lux baked in — §7 owns the weighting.
    public static float MoonlightBrightness(float illuminatedFraction, float moonElevationDegrees)
    {
        if (moonElevationDegrees <= MoonHorizonElevationDegrees)
            return 0f;

        float altitudeFactor = Clamp01(MathF.Sin(ToRadians(moonElevationDegrees)));
        return Clamp01(illuminatedFraction) * altitudeFactor;
    }

    // Alpha of the moon-cast shadow: the sun's own elevation->intensity ramp evaluated at the moon's
    // elevation (so the shadow fades in as the moon rises, exactly like a sun shadow at dawn), scaled
    // down by illuminated fraction and by MoonShadowMaxStrength. A new moon (illuminated 0) casts
    // nothing; a full moon high overhead casts the strongest, still-faint, shadow. Returns 0 when the
    // moon is below the horizon.
    public static float MoonShadowStrength(float illuminatedFraction, float moonElevationDegrees)
    {
        if (moonElevationDegrees <= MoonHorizonElevationDegrees)
            return 0f;

        float elevationRamp = Formulas.ShadowIntensityFromElevation(moonElevationDegrees);
        return elevationRamp * Clamp01(illuminatedFraction) * MoonShadowMaxStrength;
    }

    private static float ToRadians(float degrees) => degrees * MathF.PI / 180f;
    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
}
