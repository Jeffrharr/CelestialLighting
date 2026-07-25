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

    // How much darker than the lit ground the strongest moon shadow renders, as a fraction — the
    // *visible* contrast at MoonShadowMaxStrength.
    //
    // This constant exists because MoonShadowMaxStrength alone could never deliver it. That one is
    // only the alpha handed to SkyManager, which then does `Color.Lerp(Color.white,
    // curSky.colors.shadow, strength)` — and vanilla's night colors.shadow is nearly white
    // ((0.85,0.85,0.85) on Clear, (0.92,…) on every other weather), because vanilla never meant to
    // draw a real night shadow at all. Even at alpha 1.0 that caps a night shadow at a 15% darkening;
    // at our 0.28 it worked out to 4.2% on a clear night and 2.2% otherwise — computed correctly, fed
    // to the shader correctly, and rendered invisibly. Patch_MoonShadowColor replaces that near-white
    // input so the alpha has something to bite on. 25% keeps a moon shadow a faint hint: clearly
    // present, nowhere near a daytime shadow's contrast.
    public const float MoonShadowPeakDarkening = 0.25f;

    // The greyscale value to feed SkyTarget.colors.shadow at night so a shadow at `maxStrength`
    // renders exactly `peakDarkening` darker than the lit ground.
    //
    // Inverts vanilla's own lerp instead of second-guessing it: the rendered value is
    // 1 - strength * (1 - shadowValue), so solving at strength == maxStrength gives
    // 1 - peakDarkening / maxStrength. Because the strength term stays vanilla's, weaker moons scale
    // down proportionally for free — a half-lit moon lands at half the contrast with no second curve
    // to keep in sync. Returns 1 (leave the ground alone) when maxStrength is 0, the only case where
    // no colour could produce the requested darkening.
    public static float MoonShadowColorValue(float peakDarkening, float maxStrength)
    {
        if (maxStrength <= 0f)
            return 1f;

        return Clamp01(1f - Clamp01(peakDarkening) / maxStrength);
    }

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

    // --- Lunar orbital inclination and nodes (opt-in natural-eclipse trigger, DESIGN.md §10a) ---
    //
    // Everything above models the moon flat on the ecliptic, which is exactly why such a moon would
    // transit the sun at *every* new moon. The natural-eclipse trigger needs the ~5° orbital tilt put
    // back: a solar eclipse happens only when a new moon (elongation ~0, moon lined up with the sun in
    // longitude) coincides with the moon being near one of its two orbital nodes — the points where
    // the tilted orbit crosses the ecliptic and the moon's ecliptic *latitude* passes through zero.
    // Away from a node the moon rides above or below the sun and no eclipse occurs. Modeling the node,
    // and its slow retrograde regression, is what converts "every month" into the realistic "an
    // eclipse only every few game years" cadence this feature targets (enforced by a simulation test
    // in MoonMathTests). All System-only and unit-tested; consumed by EclipseIntegration's provider.

    // Tilt of the lunar orbit to the ecliptic. The real Moon's ~5.14°; kept real because it (together
    // with the disc radii in EclipseMath) sets how narrow the near-node window must be for the discs
    // to actually overlap, and thus how rare eclipses are.
    public const float LunarInclinationDegrees = 5.14f;

    // In-game length of one full retrograde regression of the ascending node around the ecliptic, in
    // days. NOT astronomically scaled (the real value is ~18.6 years): it is tuned empirically so that
    // new moons line up with a node roughly once every few game years, which the cadence assertion in
    // MoonMathTests pins. Chosen incommensurate with the 60-day year so eclipses don't lock to fixed
    // calendar dates. GameComponent_MoonPhase reads this as its default nodal period.
    public const float DefaultNodalPeriodDays = 403f;

    // Fraction through the nodal regression cycle in [0, 1), from the absolute tick count, exactly like
    // SynodicCyclePosition — no stored state, one shared node for the whole game. Returns 0 for a
    // non-positive period rather than dividing by zero.
    public static float NodalCyclePosition(long ticksAbs, long nodalPeriodTicks)
    {
        if (nodalPeriodTicks <= 0L)
            return 0f;

        long wrapped = ((ticksAbs % nodalPeriodTicks) + nodalPeriodTicks) % nodalPeriodTicks;
        return (float)((double)wrapped / nodalPeriodTicks);
    }

    // Ecliptic longitude of the ascending node, in degrees. The lunar nodes move retrograde, so this
    // decreases a full 360° over one nodal period. The absolute phase is arbitrary (there is no "true"
    // epoch to anchor to), so nodal position 0 maps to node longitude 0 and it winds negative.
    public static float AscendingNodeLongitudeDegrees(float nodalPosition) => -nodalPosition * 360f;

    // Sun's ecliptic longitude in degrees, sweeping a full 360° over the year with day-of-year. This
    // is the same angle Formulas' declination sinusoid rides on, expressed as a longitude.
    public static float SunEclipticLongitudeDegrees(float dayOfYear) =>
        dayOfYear / Formulas.DaysPerYear * 360f;

    // Moon's ecliptic longitude = the sun's plus the moon's elongation (the moon runs a full 360°
    // ahead of the sun over one synodic month).
    public static float MoonEclipticLongitudeDegrees(float dayOfYear, float cyclePosition) =>
        SunEclipticLongitudeDegrees(dayOfYear) + ElongationDegrees(cyclePosition);

    // Moon's ecliptic latitude in degrees — how far above (+) or below (−) the ecliptic the tilted
    // orbit carries it. Zero at the two nodes, ±inclination a quarter-orbit from them. This is the one
    // term the flat model on the ecliptic drops, and the reason most new moons miss the sun.
    public static float MoonEclipticLatitudeDegrees(float dayOfYear, float cyclePosition, float nodalPosition)
    {
        float argFromNode =
            MoonEclipticLongitudeDegrees(dayOfYear, cyclePosition) - AscendingNodeLongitudeDegrees(nodalPosition);
        return LunarInclinationDegrees * MathF.Sin(ToRadians(argFromNode));
    }

    // Apparent angular separation between the sun and moon discs, in degrees. The sun sits on the
    // ecliptic (latitude 0) at its longitude; the moon sits at its own longitude, offset by the
    // elongation, and its ecliptic latitude. The spherical law of cosines gives the great-circle angle
    // between the two directions. Near a new moon the longitude gap collapses, so the separation tends
    // to the moon's ecliptic latitude — which is why an eclipse needs a new moon *and* a near-node
    // crossing at once. EclipseMath.IsGeometricTransit compares this against the summed disc radii.
    public static float SunMoonSeparationDegrees(float dayOfYear, float cyclePosition, float nodalPosition)
    {
        float deltaLonRad = ToRadians(ElongationDegrees(cyclePosition)); // moon longitude − sun longitude
        float latitudeRad = ToRadians(MoonEclipticLatitudeDegrees(dayOfYear, cyclePosition, nodalPosition));
        float cosSeparation = MathF.Cos(latitudeRad) * MathF.Cos(deltaLonRad);
        return ToDegrees(MathF.Acos(Clamp(cosSeparation, -1f, 1f)));
    }

    private static float ToRadians(float degrees) => degrees * MathF.PI / 180f;
    private static float ToDegrees(float radians) => radians * 180f / MathF.PI;
    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
}
