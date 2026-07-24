using System;

namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file (only System), for the same
// reason as Formulas.cs: it is compiled into both the shipped mod (net481, inside RimWorld) and the
// offline test project (net8.0, via a linked <Compile Include>), so the exact code that ships is the
// exact code under test. Anything that needs Mathf/Map/Find belongs in the patch/adapter
// (Patch_EclipseDarkening / EclipseIntegration), which passes primitives in from live game state.
//
// This is the geometric core shared by BOTH eclipse concepts in DESIGN.md §10. Vanilla's Eclipse
// (GameCondition_NoSunlight) drives the sky to full darkness with a short linear ramp at each end and
// a flat black middle — an on/off dim over a physically-impossible day-long duration. We replace that
// dim with a coverage ramp: the occulted fraction of the sun's disk as a moon disc passes over it,
// following circle-intersection geometry. That occulted fraction is exactly the amount by which the
// sky should be pulled toward the dark, wan eclipse target, so it maps straight onto the condition's
// sky-lerp factor.
//
// The two concepts differ only in what moves the discs together and how long they stay (see DESIGN.md
// §10a/§10b); both read the SAME CircleIntersectionArea/CoverageFraction primitives below:
//   - Natural (§10a, opt-in): the modeled moon crosses the sun in a straight line at constant speed —
//     NaturalCoverageAtProgress — over the correct SHORT real-eclipse duration (NaturalEclipseDurationTicks).
//   - Unnatural (§10b, default): a scripted disc quickly slides in, PARKS fully over the sun for the
//     whole (long, vanilla) duration, then slides out — UnnaturalCoverageAtProgress. Deliberately
//     un-physical motion, honest to the fact that the vanilla event's duration was never real.
public static class EclipseMath
{
    // Apparent moon/sun angular-radius ratio, chosen slightly greater than 1 so a central eclipse
    // has a brief "totality" plateau (the sun sits fully behind the larger moon disk for a short
    // window around maximum) instead of touching full darkness for a single instant. Real total
    // eclipses look like this because the Moon's apparent size is a touch larger than the Sun's when
    // a total eclipse is possible at all. Cosmetic-only value; not derived from any external source.
    public const double DefaultMoonSunRadiusRatio = 1.03;

    // Fraction of the (long, vanilla) unnatural-eclipse duration spent sliding the disc IN, and again
    // sliding it OUT — the rest of the event is the "parked" plateau at full coverage. Small so the
    // fly-in/fly-out reads as a quick, deliberately un-physical dart across the sun rather than a slow
    // natural transit. Cosmetic-only value (see DESIGN.md §10b "quickly slides in ... parks ... slides
    // away").
    public const double DefaultSlideFraction = 0.12;

    /// <summary>
    /// Area of the lens-shaped intersection of two circles with radii <paramref name="r1"/> and
    /// <paramref name="r2"/> whose centers are <paramref name="distance"/> apart. Standard
    /// circle-circle intersection formula; returns 0 when the disks are fully separate and the
    /// smaller disk's full area when one is entirely inside the other.
    /// </summary>
    public static double CircleIntersectionArea(double distance, double r1, double r2)
    {
        double d = Math.Abs(distance);

        // Disks fully separate (or externally tangent): no overlap at all.
        if (d >= r1 + r2)
            return 0.0;

        // One disk lies entirely inside the other: the overlap is just the smaller disk.
        if (d <= Math.Abs(r1 - r2))
        {
            double rMin = Math.Min(r1, r2);
            return Math.PI * rMin * rMin;
        }

        // Partial overlap: sum of the two circular segments that make up the lens.
        double r1Sq = r1 * r1;
        double r2Sq = r2 * r2;
        double alpha = Math.Acos((d * d + r1Sq - r2Sq) / (2.0 * d * r1));
        double beta = Math.Acos((d * d + r2Sq - r1Sq) / (2.0 * d * r2));
        // Heron-style term for the two triangles; Max(0,..) guards against tiny negative values
        // from floating-point rounding right at the tangent boundaries.
        double triangleTerm =
            0.5 * Math.Sqrt(Math.Max(0.0, (-d + r1 + r2) * (d + r1 - r2) * (d - r1 + r2) * (d + r1 + r2)));
        return r1Sq * alpha + r2Sq * beta - triangleTerm;
    }

    /// <summary>
    /// Fraction of the sun's disk occulted by the moon's disk when their centers are
    /// <paramref name="centerDistance"/> apart. This is the intersection area divided by the sun's
    /// area, clamped to [0, 1]. Shared by both the natural and the unnatural eclipse.
    /// </summary>
    public static double CoverageFraction(double centerDistance, double sunRadius, double moonRadius)
    {
        double sunArea = Math.PI * sunRadius * sunRadius;
        if (sunArea <= 0.0)
            return 0.0;

        return Clamp01(CircleIntersectionArea(centerDistance, sunRadius, moonRadius) / sunArea);
    }

    // --- Natural eclipse (§10a): a real straight-line transit at constant speed ---

    /// <summary>
    /// Occulted fraction of the sun as a NATURAL eclipse progresses. <paramref name="progress"/> runs
    /// 0 → 1 over the whole event (0 = first contact, 0.5 = maximum, 1 = last contact).
    /// <paramref name="magnitude"/> in [0, 1] picks how central the eclipse is: 1 = central (the moon
    /// passes straight over the sun's center), 0 = grazing (the disks barely touch). The moon is
    /// modeled as moving in a straight line across the sun at constant speed, which is the standard
    /// first-order picture of a real solar eclipse.
    /// </summary>
    public static double NaturalCoverageAtProgress(double progress, double magnitude, double moonSunRadiusRatio)
    {
        double p = Clamp01(progress);
        const double sunRadius = 1.0; // normalize on the sun's disk
        double moonRadius = moonSunRadiusRatio * sunRadius;
        double m = Clamp01(magnitude);

        // Center-to-center distance at first/last contact (disks externally tangent).
        double contactDistance = sunRadius + moonRadius;

        // Impact parameter: the perpendicular offset of the moon's straight-line path from the sun's
        // center at closest approach. magnitude 1 → 0 (dead-center), magnitude 0 → full contact
        // distance (a grazing pass that never actually covers any of the sun).
        double impactParameter = (1.0 - m) * contactDistance;

        // Half the along-track distance the moon's center travels between first and last contact,
        // for this impact parameter. (At contact, along² + impact² = contactDistance².)
        double halfChord = Math.Sqrt(Math.Max(0.0, contactDistance * contactDistance - impactParameter * impactParameter));

        // Map progress onto the along-track coordinate: -halfChord at p=0, 0 at p=0.5, +halfChord at p=1.
        double alongTrack = halfChord * (2.0 * p - 1.0);
        double centerDistance = Math.Sqrt(alongTrack * alongTrack + impactParameter * impactParameter);

        return CoverageFraction(centerDistance, sunRadius, moonRadius);
    }

    /// <summary>
    /// The correct SHORT real-eclipse duration, in game ticks, for the natural (§10a) eclipse: the
    /// time from first to last contact as the moon crosses the sun at a given relative angular speed.
    /// This is what replaces the vanilla event's physically-impossible ~day-long duration when the
    /// astronomical trigger is on. Pure geometry — the along-track chord across the "contact circle"
    /// (radius = sum of the apparent angular radii) divided by the moon's relative angular speed.
    /// Returns 0 for a degenerate speed/tick rate so a caller falls back to leaving the event alone.
    /// </summary>
    public static double NaturalEclipseDurationTicks(
        double relativeAngularSpeedDegPerHour,
        double sunAngularRadiusDeg,
        double moonAngularRadiusDeg,
        double magnitude,
        double ticksPerHour)
    {
        if (relativeAngularSpeedDegPerHour <= 0.0 || ticksPerHour <= 0.0)
            return 0.0;

        double contactDistance = sunAngularRadiusDeg + moonAngularRadiusDeg;
        double impactParameter = (1.0 - Clamp01(magnitude)) * contactDistance;
        // Full along-track chord (first → last contact) for this impact parameter.
        double chordDegrees =
            2.0 * Math.Sqrt(Math.Max(0.0, contactDistance * contactDistance - impactParameter * impactParameter));
        double hours = chordDegrees / relativeAngularSpeedDegPerHour;
        return hours * ticksPerHour;
    }

    // --- Unnatural eclipse (§10b): a scripted fly-in / park / fly-out disc ---

    /// <summary>
    /// Occulted fraction of the sun for the UNNATURAL (§10b) eclipse: a scripted disc that darts in
    /// over the first <paramref name="slideFraction"/> of the event, PARKS fully over the sun for the
    /// whole middle, then darts out over the last <paramref name="slideFraction"/>. The fly-in and
    /// fly-out reuse the natural straight-line coverage shape (first-contact → maximum) compressed
    /// into the slide window, so the ramp is smooth; only the parked plateau is un-physical.
    /// <paramref name="progress"/> runs 0 → 1 over the whole (long, vanilla) duration.
    /// </summary>
    public static double UnnaturalCoverageAtProgress(
        double progress, double slideFraction, double magnitude, double moonSunRadiusRatio)
    {
        double p = Clamp01(progress);
        double slide = ClampSlideFraction(slideFraction);

        if (IsInFlyIn(p, slide))
            return SlideCoverage(p / slide, magnitude, moonSunRadiusRatio);

        if (IsInFlyOut(p, slide))
            return SlideCoverage((1.0 - p) / slide, magnitude, moonSunRadiusRatio);

        // Parked: disc held fully over the sun for the whole middle of the event.
        return SlideCoverage(1.0, magnitude, moonSunRadiusRatio);
    }

    // True while the disc is sliding in (before the parked plateau begins).
    private static bool IsInFlyIn(double progress, double slideFraction) => progress < slideFraction;

    // True while the disc is sliding out (after the parked plateau ends).
    private static bool IsInFlyOut(double progress, double slideFraction) => progress > 1.0 - slideFraction;

    // Coverage for a slide that is <paramref name="slideProgress"/> (0..1) of the way from first
    // contact to maximum. Maps onto the first half [0, 0.5] of the natural transit shape, so at
    // slideProgress 0 nothing is covered and at 1 the sun is fully behind the (slightly larger) disc.
    private static double SlideCoverage(double slideProgress, double magnitude, double moonSunRadiusRatio) =>
        NaturalCoverageAtProgress(0.5 * Clamp01(slideProgress), magnitude, moonSunRadiusRatio);

    // --- Sky-lerp-factor selectors (what the darkening patch and the probe actually write/read) ---

    /// <summary>
    /// Sky-lerp factor for the natural (§10a) eclipse: the central-eclipse coverage ramp over event
    /// progress. 0 at the ends (normal sky), a brief 1 (full darkness) at maximum.
    /// </summary>
    public static double NaturalSkyLerpFactorAtProgress(double progress) =>
        NaturalCoverageAtProgress(progress, magnitude: 1.0, moonSunRadiusRatio: DefaultMoonSunRadiusRatio);

    /// <summary>
    /// Sky-lerp factor for the unnatural (§10b) eclipse: the scripted fly-in / park / fly-out ramp.
    /// 0 at the ends, a quick rise to a held 1 across the whole parked middle, then a quick fall.
    /// </summary>
    public static double UnnaturalSkyLerpFactorAtProgress(double progress) =>
        UnnaturalCoverageAtProgress(progress, DefaultSlideFraction, magnitude: 1.0, moonSunRadiusRatio: DefaultMoonSunRadiusRatio);

    /// <summary>
    /// The factor the eclipse-darkening patch writes back onto GameCondition_NoSunlight
    /// .SkyTargetLerpFactor (and the dev probe reads back), selecting the natural or unnatural ramp by
    /// which mode is active. This single selector keeps the live patch and the offline probe in
    /// lockstep so they can never derive different darkness for the same state.
    /// </summary>
    public static double SkyLerpFactorAtProgress(double progress, bool naturalMode) =>
        naturalMode ? NaturalSkyLerpFactorAtProgress(progress) : UnnaturalSkyLerpFactorAtProgress(progress);

    // --- Astronomical-trigger geometry (opt-in §10a path; see EclipseIntegration) ---

    /// <summary>
    /// True when the moon's disk overlaps the sun's disk at all — i.e. a geometric solar transit is
    /// occurring — which happens exactly when their angular separation is less than the sum of the
    /// two apparent angular radii. Used by the opt-in astronomical-trigger path to decide whether a
    /// real eclipse should be firing right now. Pure geometry; carries no gameplay effect by itself.
    /// </summary>
    public static bool IsGeometricTransit(
        double angularSeparationDegrees, double sunAngularRadiusDegrees, double moonAngularRadiusDegrees)
    {
        return Math.Abs(angularSeparationDegrees) < (sunAngularRadiusDegrees + moonAngularRadiusDegrees);
    }

    // Keep the slide window strictly inside (0, 0.5]: 0 would divide by zero in the fly-in map, and
    // anything past 0.5 would make the fly-in and fly-out windows overlap (no parked plateau at all).
    private static double ClampSlideFraction(double slideFraction)
    {
        const double min = 1e-6;
        const double max = 0.5;
        if (slideFraction < min)
            return min;

        if (slideFraction > max)
            return max;

        return slideFraction;
    }

    private static double Clamp01(double value)
    {
        if (value < 0.0)
            return 0.0;

        if (value > 1.0)
            return 1.0;

        return value;
    }
}
