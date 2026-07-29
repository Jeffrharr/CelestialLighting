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

    // Peak strength of a moon-cast shadow's alpha, relative to a full daytime sun shadow: what a full
    // moon high in a fully dark sky casts. Everything dimmer than that scales down from here via the
    // contrast ratio in MoonShadowStrength.
    //
    // Kept as an artistic ceiling rather than derived from the lux model, deliberately. Physically a
    // full moon in true darkness is the overwhelmingly dominant light source and its shadow contrast
    // approaches 1.0, the same as the sun's at noon — the reason a real moon shadow still reads as
    // faint is that the eye is dark-adapted and the whole scene is dim, which a monitor showing a
    // brightened night scene cannot reproduce. So the ratio decides the SHAPE of the curve (when the
    // shadow appears, how phase and twilight move it) and this constant sets its amplitude.
    //
    // Before §6b this number was doing a second, hidden job: standing in for the daylight washout the
    // model could not express. It no longer is — washout is now the ratio's business — so this is a
    // pure look knob and moving it changes only how strong the deepest-night shadow is.
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

    // The day-of-year at which the SUN would carry the moon's current declination.
    //
    // Same fact as MoonDeclinationDegrees above, expressed so it survives a change of seasonal
    // model. The moon rides the same ecliptic as the sun, just elongation degrees along it, and an
    // offset in ecliptic angle is an offset in day-of-year — a full 360 degrees of elongation is one
    // year. So rather than rebuild the moon's declination from a tilt and a phase we assume, callers
    // evaluate whatever declination function the SUN is currently using, at this shifted day.
    //
    // This matters because the sun's declination is no longer always ours. With Realistic Axial Tilt
    // installed it comes from their model, whose seasonal phase sits a quarter-year off vanilla's
    // (see AxialTiltCompat). Rebuilding the moon from our own -cos while the sun ran on their sin
    // would put the two bodies a season apart: MoonDeclinationDegrees_EqualsSunDeclination_AtNewMoon
    // is exactly the invariant that would break, and it would break silently — as a moon riding high
    // on the wrong nights, months into a save.
    //
    // Deliberately returns an unwrapped day (may exceed DaysPerYear or go negative): every
    // declination model we feed it is periodic in the year, so wrapping would add a branch that
    // changes no result.
    public static float MoonEquivalentSunDayOfYear(float dayOfYear, float cyclePosition) =>
        dayOfYear + ElongationDegrees(cyclePosition) / 360f * Formulas.DaysPerYear;

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

    // Alpha of the moon-cast shadow, from the ratio of moonlight to ambient skylight (DESIGN.md §6b).
    //
    // WHAT CHANGED AND WHY (this replaced a phase-times-altitude ramp that never looked at the sun).
    // The old model gated moon shadows on the SUN being below the refraction horizon and then scaled
    // by illuminated fraction and moon altitude. Both halves were wrong in the same way: they treated
    // "is there a moon shadow" as a fact about the moon alone, when it is entirely a question of how
    // the moon's light compares to everything else landing on the ground.
    //
    // The visible consequence was a moon shadow that switched on at sun elevation -0.83 degrees, in a
    // ~400 lux sky — about 1600x brighter than the full moon casting it. It was rendered at full
    // strength in a sky where a real moon shadow is four orders of magnitude below the eye's contrast
    // threshold, and it popped in over a 3-degree window instead of fading.
    //
    // Now IlluminanceMath does the comparison in absolute lux and this is just the contrast it finds,
    // scaled to our peak alpha. The sun's elevation is an input rather than a gate, which removes the
    // pop entirely: at sunset the contrast is ~0.0007 (invisible, as it should be), it crosses the
    // perceptible threshold around sun -6.5 degrees for a full moon, and reaches full strength around
    // -11. A quarter moon, being ~11x dimmer, does not appear until about -9 and is not at full
    // strength until roughly -15 — deep in nautical twilight, which is exactly right.
    //
    // NOTE, because it is a deliberate behaviour change and not an oversight: phase no longer scales
    // the shadow linearly once the moon dominates the sky. In full darkness a half-lit moon now reads
    // at ~0.98 of a full moon's contrast rather than 0.50. That is the physically correct answer —
    // shadow contrast is a RATIO, and once any caster is well above the starlight floor it casts a
    // near-full-contrast shadow; what a half moon actually costs you is overall scene brightness,
    // which is §7's night radiance to own, not this. Phase still matters enormously at the twilight
    // end (when the shadow appears at all) and at the faint end (a 10%-lit crescent is dimmer than
    // starlight itself and casts essentially nothing).
    //
    // Returns 0 below the horizon, and 0 whenever the result would render below the perceptible
    // floor — see MoonShadowIsPerceptible for why that is a real gate rather than an optimization.
    public static float MoonShadowStrength(
        float illuminatedFraction, float moonElevationDegrees, float sunElevationDegrees)
    {
        if (moonElevationDegrees <= MoonHorizonElevationDegrees)
            return 0f;

        float moonLux = IlluminanceMath.MoonLux(illuminatedFraction, moonElevationDegrees);
        float ambientLux = IlluminanceMath.AmbientSkyLux(sunElevationDegrees);
        float strength = IlluminanceMath.ShadowContrast(moonLux, ambientLux) * MoonShadowMaxStrength;

        return MoonShadowIsPerceptible(strength) ? strength : 0f;
    }

    // How much darker than the lit ground a moon shadow of this alpha actually renders, in [0,1].
    //
    // Inverts SkyManager's lerp the same way MoonShadowColorValue does, and against the colour that
    // function returns: the shader draws Color.Lerp(Color.white, colors.shadow, alpha), so the
    // darkening is alpha * (1 - shadowColorValue). Stated in rendered units rather than alpha because
    // "is this shadow visible" is a claim about what reaches the screen, and §6a is the standing proof
    // that the two are different questions.
    public static float MoonShadowDarkening(float strength) =>
        Clamp01(strength) * (1f - MoonShadowColorValue(MoonShadowPeakDarkening, MoonShadowMaxStrength));

    // Whether a moon shadow at this alpha is worth rendering at all.
    //
    // With a contrast ratio there is no longer any elevation at which the moon shadow is exactly zero
    // — in full daylight it is 0.0002, not 0. Left ungated, MoonPosition.ShadowForMap would report a
    // shadow at every moment of every day, which would in turn have Patch_MoonShadowColor darkening
    // the night's shadow colour for a shadow nobody can see. So the model's honest "always present,
    // usually invisible" answer gets truncated exactly where invisibility begins.
    //
    // Reuses §13a's PerceptibleDarkening (about 5 values out of 255, below which a cast shadow stops
    // reading on mid-tone ground) rather than inventing a second threshold: it is a fact about human
    // vision, not about weather, and having two numbers for it is how they drift apart. Measured
    // before the player's "Shadow strength" slider, which is applied later by the patches — so the
    // gate is very slightly permissive on a lowered slider, which is the right direction to err.
    public static bool MoonShadowIsPerceptible(float strength) =>
        MoonShadowDarkening(strength) >= WeatherDimmingMath.PerceptibleDarkening;

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
