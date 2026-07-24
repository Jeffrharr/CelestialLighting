using System;

namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file (only System), for the same
// reason as Formulas.cs: it is compiled into both the shipped mod (net481, inside RimWorld) and the
// offline test project (net8.0, via a linked <Compile Include>), so the exact code that ships is the
// exact code under test. Anything that needs Mathf/Map/Find belongs in the patch/adapter
// (Patch_EclipseDarkening), which passes primitives in from live game state.
//
// This is the geometric core of the eclipse-darkening overlay (DESIGN.md §10): vanilla's Eclipse
// (GameCondition_NoSunlight) drives the sky to full darkness with a short linear ramp at each end
// and a flat black middle. We instead reshape that ramp so the sky darkens the way a real partial
// eclipse looks — the moon's disk slides across the sun's, so the occulted fraction of the sun grows
// smoothly from first contact to maximum and shrinks again, following circle-intersection geometry
// rather than an on/off switch. The occulted fraction is exactly the amount by which the sky should
// be pulled toward the eclipse target, so it maps straight onto the condition's sky-lerp factor.
public static class EclipseMath
{
    // Apparent moon/sun angular-radius ratio, chosen slightly greater than 1 so a central eclipse
    // has a brief "totality" plateau (the sun sits fully behind the larger moon disk for a short
    // window around maximum) instead of touching full darkness for a single instant. Real total
    // eclipses look like this because the Moon's apparent size is a touch larger than the Sun's when
    // a total eclipse is possible at all. Cosmetic-only value; not derived from any external source.
    public const double DefaultMoonSunRadiusRatio = 1.03;

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
    /// area, clamped to [0, 1].
    /// </summary>
    public static double CoverageFraction(double centerDistance, double sunRadius, double moonRadius)
    {
        double sunArea = Math.PI * sunRadius * sunRadius;
        if (sunArea <= 0.0)
            return 0.0;

        return Clamp01(CircleIntersectionArea(centerDistance, sunRadius, moonRadius) / sunArea);
    }

    /// <summary>
    /// Occulted fraction of the sun as the eclipse progresses. <paramref name="progress"/> runs
    /// 0 → 1 over the whole event (0 = first contact, 0.5 = maximum, 1 = last contact).
    /// <paramref name="magnitude"/> in [0, 1] picks how central the eclipse is: 1 = central
    /// (the moon passes straight over the sun's center), 0 = grazing (the disks barely touch).
    /// The moon is modeled as moving in a straight line across the sun at constant speed, which is
    /// the standard first-order picture of a solar eclipse.
    /// </summary>
    public static double CoverageAtProgress(double progress, double magnitude, double moonSunRadiusRatio)
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
    /// The factor to blend the sky toward the vanilla eclipse target, for a default cosmetic
    /// eclipse, as a function of event progress. This is what the darkening patch writes back onto
    /// GameCondition_NoSunlight.SkyTargetLerpFactor: 0 at the ends (normal sky), rising smoothly to
    /// a brief 1 (full eclipse darkness) at maximum, following the disk-overlap ramp.
    /// </summary>
    public static double SkyLerpFactorAtProgress(double progress) =>
        CoverageAtProgress(progress, magnitude: 1.0, moonSunRadiusRatio: DefaultMoonSunRadiusRatio);

    // --- Astronomical-trigger geometry (opt-in path; see EclipseIntegration) ---

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

    private static double Clamp01(double value)
    {
        if (value < 0.0)
            return 0.0;

        if (value > 1.0)
            return 1.0;

        return value;
    }
}
