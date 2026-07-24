using System;

namespace CelestialLighting.Tests;

/// <summary>
/// Offline edge-case coverage for EclipseMath.cs — the pure geometry behind the eclipse-darkening
/// overlay (DESIGN.md §10). No RimWorld/Unity assembly required, since EclipseMath has no dependency
/// on either. Complements ApiCompatibilityTests.cs (which only checks that the vanilla members the
/// patch touches still exist); these tests check that our own disk-overlap math is correct.
/// </summary>
[TestFixture]
public class EclipseMathTests
{
    private const double Tolerance = 1e-6;
    private const double LooseTolerance = 1e-4;

    // --- CircleIntersectionArea ---

    [Test]
    public void CircleIntersectionArea_FullySeparate_IsZero()
    {
        // Centers farther apart than the sum of radii: no overlap.
        Assert.That(EclipseMath.CircleIntersectionArea(3.0, 1.0, 1.0), Is.EqualTo(0.0).Within(Tolerance));
    }

    [Test]
    public void CircleIntersectionArea_ExternallyTangent_IsZero()
    {
        // Exactly touching (d == r1 + r2): still zero overlap.
        Assert.That(EclipseMath.CircleIntersectionArea(2.0, 1.0, 1.0), Is.EqualTo(0.0).Within(Tolerance));
    }

    [Test]
    public void CircleIntersectionArea_Concentric_IsSmallerDiskArea()
    {
        // One disk entirely inside the other (d == 0): overlap is the smaller disk's full area.
        double expected = Math.PI * 1.0 * 1.0;
        Assert.That(EclipseMath.CircleIntersectionArea(0.0, 2.0, 1.0), Is.EqualTo(expected).Within(Tolerance));
    }

    [Test]
    public void CircleIntersectionArea_OneInsideOther_AtInternalTangent_IsSmallerDiskArea()
    {
        // d == |r1 - r2|: smaller disk internally tangent, still fully contained.
        double expected = Math.PI * 1.0 * 1.0;
        Assert.That(EclipseMath.CircleIntersectionArea(1.0, 2.0, 1.0), Is.EqualTo(expected).Within(Tolerance));
    }

    [Test]
    public void CircleIntersectionArea_UnitCirclesHalfOverlapped_MatchesClosedForm()
    {
        // Two unit circles with centers 1 apart: classic lens area
        // = 2*acos(1/2) - (1/2)*sqrt(3) = 2*pi/3 - sqrt(3)/2 ≈ 1.2283697.
        double expected = 2.0 * Math.PI / 3.0 - Math.Sqrt(3.0) / 2.0;
        Assert.That(EclipseMath.CircleIntersectionArea(1.0, 1.0, 1.0), Is.EqualTo(expected).Within(Tolerance));
    }

    [Test]
    public void CircleIntersectionArea_IsSymmetricInRadii()
    {
        double a = EclipseMath.CircleIntersectionArea(1.3, 1.0, 0.7);
        double b = EclipseMath.CircleIntersectionArea(1.3, 0.7, 1.0);
        Assert.That(a, Is.EqualTo(b).Within(Tolerance));
    }

    // --- CoverageFraction ---

    [TestCase(3.0, 0.0)]   // fully separate -> nothing occulted
    [TestCase(0.0, 1.0)]   // concentric equal disks -> sun fully occulted
    public void CoverageFraction_EqualDisks_Bounds(double distance, double expected)
    {
        Assert.That(EclipseMath.CoverageFraction(distance, 1.0, 1.0), Is.EqualTo(expected).Within(Tolerance));
    }

    [Test]
    public void CoverageFraction_UnitDisks_HalfOverlap()
    {
        // lens area / sun area, sun radius 1 -> (2*pi/3 - sqrt(3)/2) / pi ≈ 0.3910022.
        double expected = (2.0 * Math.PI / 3.0 - Math.Sqrt(3.0) / 2.0) / Math.PI;
        Assert.That(EclipseMath.CoverageFraction(1.0, 1.0, 1.0), Is.EqualTo(expected).Within(Tolerance));
    }

    [Test]
    public void CoverageFraction_AlwaysWithinUnitInterval()
    {
        for (int i = 0; i <= 40; i++)
        {
            double d = i * 0.1; // 0 .. 4.0
            double coverage = EclipseMath.CoverageFraction(d, 1.0, 1.03);
            Assert.That(coverage, Is.InRange(0.0, 1.0), $"coverage out of range at distance {d}");
        }
    }

    // --- CoverageAtProgress ---

    [TestCase(0.0)]
    [TestCase(1.0)]
    public void CoverageAtProgress_ZeroAtContacts(double progress)
    {
        // First and last contact: the disks are externally tangent, so nothing is occulted.
        double coverage = EclipseMath.CoverageAtProgress(progress, magnitude: 1.0, moonSunRadiusRatio: 1.03);
        Assert.That(coverage, Is.EqualTo(0.0).Within(LooseTolerance));
    }

    [Test]
    public void CoverageAtProgress_CentralEclipse_FullyDarkAtMaximum()
    {
        // magnitude 1, moon slightly larger than sun -> sun fully behind the moon at mid-eclipse.
        double coverage = EclipseMath.CoverageAtProgress(0.5, magnitude: 1.0, moonSunRadiusRatio: 1.03);
        Assert.That(coverage, Is.EqualTo(1.0).Within(Tolerance));
    }

    [Test]
    public void CoverageAtProgress_IsSymmetricAboutMaximum()
    {
        // The ramp up and the ramp down mirror each other: coverage(p) == coverage(1-p).
        for (int i = 0; i <= 10; i++)
        {
            double p = i / 10.0;
            double a = EclipseMath.CoverageAtProgress(p, 1.0, 1.03);
            double b = EclipseMath.CoverageAtProgress(1.0 - p, 1.0, 1.03);
            Assert.That(a, Is.EqualTo(b).Within(Tolerance), $"asymmetry at progress {p}");
        }
    }

    [Test]
    public void CoverageAtProgress_MonotonicallyRisesToMaximum()
    {
        // Over the first half (first contact -> maximum) the occulted fraction only ever increases.
        double previous = -1.0;
        for (int i = 0; i <= 50; i++)
        {
            double p = 0.5 * (i / 50.0); // 0 .. 0.5
            double coverage = EclipseMath.CoverageAtProgress(p, 1.0, 1.03);
            Assert.That(coverage, Is.GreaterThanOrEqualTo(previous - LooseTolerance),
                $"coverage decreased before maximum at progress {p}");
            previous = coverage;
        }
    }

    [Test]
    public void CoverageAtProgress_GrazingMagnitude_StaysNearZero()
    {
        // magnitude 0 -> the moon only grazes the sun's edge; it never actually covers any of it.
        for (int i = 0; i <= 10; i++)
        {
            double p = i / 10.0;
            double coverage = EclipseMath.CoverageAtProgress(p, magnitude: 0.0, moonSunRadiusRatio: 1.03);
            Assert.That(coverage, Is.EqualTo(0.0).Within(LooseTolerance), $"grazing eclipse occulted sun at progress {p}");
        }
    }

    [Test]
    public void CoverageAtProgress_ClampsProgressOutsideUnitInterval()
    {
        // Values outside [0,1] clamp to the contact endpoints rather than extrapolating.
        Assert.That(EclipseMath.CoverageAtProgress(-0.5, 1.0, 1.03), Is.EqualTo(0.0).Within(LooseTolerance));
        Assert.That(EclipseMath.CoverageAtProgress(1.5, 1.0, 1.03), Is.EqualTo(0.0).Within(LooseTolerance));
    }

    [Test]
    public void SkyLerpFactorAtProgress_MatchesDefaultCoverage()
    {
        // The shipped helper is just CoverageAtProgress with the default magnitude/ratio.
        for (int i = 0; i <= 10; i++)
        {
            double p = i / 10.0;
            double expected = EclipseMath.CoverageAtProgress(p, 1.0, EclipseMath.DefaultMoonSunRadiusRatio);
            Assert.That(EclipseMath.SkyLerpFactorAtProgress(p), Is.EqualTo(expected).Within(Tolerance));
        }
    }

    // --- IsGeometricTransit (opt-in astronomical trigger geometry) ---

    [TestCase(0.0, true)]    // dead-on: overlapping
    [TestCase(0.5, true)]    // separation < sum of radii (0.25 + 0.26 = 0.51)
    [TestCase(0.51, false)]  // exactly at the sum: disks tangent, not overlapping
    [TestCase(2.0, false)]   // far apart: no transit
    public void IsGeometricTransit_ThresholdsOnSumOfRadii(double separation, bool expected)
    {
        bool transit = EclipseMath.IsGeometricTransit(separation, sunAngularRadiusDegrees: 0.25, moonAngularRadiusDegrees: 0.26);
        Assert.That(transit, Is.EqualTo(expected));
    }

    [Test]
    public void IsGeometricTransit_UsesAbsoluteSeparation()
    {
        // Sign of the separation (which side of the sun the moon is on) does not matter.
        Assert.That(EclipseMath.IsGeometricTransit(-0.3, 0.25, 0.26), Is.True);
    }
}
