using System;

namespace CelestialLighting.Tests;

/// <summary>
/// Offline edge-case coverage for EclipseMath.cs — the pure geometry shared by both eclipse concepts
/// (DESIGN.md §10a natural / §10b unnatural). No RimWorld/Unity assembly required, since EclipseMath
/// has no dependency on either. Complements ApiCompatibilityTests.cs (which only checks that the
/// vanilla members the patch touches still exist); these tests check that our own disk-overlap math is
/// correct.
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

    // --- NaturalCoverageAtProgress (§10a: a real straight-line transit) ---

    [TestCase(0.0)]
    [TestCase(1.0)]
    public void NaturalCoverageAtProgress_ZeroAtContacts(double progress)
    {
        // First and last contact: the disks are externally tangent, so nothing is occulted.
        double coverage = EclipseMath.NaturalCoverageAtProgress(progress, magnitude: 1.0, moonSunRadiusRatio: 1.03);
        Assert.That(coverage, Is.EqualTo(0.0).Within(LooseTolerance));
    }

    [Test]
    public void NaturalCoverageAtProgress_CentralEclipse_FullyDarkAtMaximum()
    {
        // magnitude 1, moon slightly larger than sun -> sun fully behind the moon at mid-eclipse.
        double coverage = EclipseMath.NaturalCoverageAtProgress(0.5, magnitude: 1.0, moonSunRadiusRatio: 1.03);
        Assert.That(coverage, Is.EqualTo(1.0).Within(Tolerance));
    }

    [Test]
    public void NaturalCoverageAtProgress_IsSymmetricAboutMaximum()
    {
        // The ramp up and the ramp down mirror each other: coverage(p) == coverage(1-p).
        for (int i = 0; i <= 10; i++)
        {
            double p = i / 10.0;
            double a = EclipseMath.NaturalCoverageAtProgress(p, 1.0, 1.03);
            double b = EclipseMath.NaturalCoverageAtProgress(1.0 - p, 1.0, 1.03);
            Assert.That(a, Is.EqualTo(b).Within(Tolerance), $"asymmetry at progress {p}");
        }
    }

    [Test]
    public void NaturalCoverageAtProgress_MonotonicallyRisesToMaximum()
    {
        // Over the first half (first contact -> maximum) the occulted fraction only ever increases.
        double previous = -1.0;
        for (int i = 0; i <= 50; i++)
        {
            double p = 0.5 * (i / 50.0); // 0 .. 0.5
            double coverage = EclipseMath.NaturalCoverageAtProgress(p, 1.0, 1.03);
            Assert.That(coverage, Is.GreaterThanOrEqualTo(previous - LooseTolerance),
                $"coverage decreased before maximum at progress {p}");
            previous = coverage;
        }
    }

    [Test]
    public void NaturalCoverageAtProgress_GrazingMagnitude_StaysNearZero()
    {
        // magnitude 0 -> the moon only grazes the sun's edge; it never actually covers any of it.
        for (int i = 0; i <= 10; i++)
        {
            double p = i / 10.0;
            double coverage = EclipseMath.NaturalCoverageAtProgress(p, magnitude: 0.0, moonSunRadiusRatio: 1.03);
            Assert.That(coverage, Is.EqualTo(0.0).Within(LooseTolerance), $"grazing eclipse occulted sun at progress {p}");
        }
    }

    [Test]
    public void NaturalCoverageAtProgress_ClampsProgressOutsideUnitInterval()
    {
        // Values outside [0,1] clamp to the contact endpoints rather than extrapolating.
        Assert.That(EclipseMath.NaturalCoverageAtProgress(-0.5, 1.0, 1.03), Is.EqualTo(0.0).Within(LooseTolerance));
        Assert.That(EclipseMath.NaturalCoverageAtProgress(1.5, 1.0, 1.03), Is.EqualTo(0.0).Within(LooseTolerance));
    }

    // --- NaturalEclipseDurationTicks (§10a: the corrected short real duration) ---

    [Test]
    public void NaturalEclipseDurationTicks_CentralPass_MatchesChordOverSpeed()
    {
        // Central pass (magnitude 1): the full chord is 2*(sunR + moonR) degrees. With combined radii
        // 0.51° and a relative speed of 0.51°/hr the transit takes 2 hours; at 2500 ticks/hr that is
        // 5000 ticks.
        double ticks = EclipseMath.NaturalEclipseDurationTicks(
            relativeAngularSpeedDegPerHour: 0.51,
            sunAngularRadiusDeg: 0.25,
            moonAngularRadiusDeg: 0.26,
            magnitude: 1.0,
            ticksPerHour: 2500.0);
        Assert.That(ticks, Is.EqualTo(5000.0).Within(1e-3));
    }

    [Test]
    public void NaturalEclipseDurationTicks_GrazingIsShorterThanCentral()
    {
        // A grazing pass crosses a shorter chord, so it lasts less time than a central one.
        double central = EclipseMath.NaturalEclipseDurationTicks(0.5, 0.25, 0.26, magnitude: 1.0, ticksPerHour: 2500.0);
        double grazing = EclipseMath.NaturalEclipseDurationTicks(0.5, 0.25, 0.26, magnitude: 0.2, ticksPerHour: 2500.0);
        Assert.That(grazing, Is.LessThan(central));
        Assert.That(grazing, Is.GreaterThan(0.0));
    }

    [Test]
    public void NaturalEclipseDurationTicks_ScalesInverselyWithSpeed()
    {
        // Twice the relative angular speed -> half the duration.
        double slow = EclipseMath.NaturalEclipseDurationTicks(0.4, 0.25, 0.26, 1.0, 2500.0);
        double fast = EclipseMath.NaturalEclipseDurationTicks(0.8, 0.25, 0.26, 1.0, 2500.0);
        Assert.That(fast, Is.EqualTo(slow / 2.0).Within(1e-6));
    }

    [TestCase(0.0, 2500.0)]  // zero speed -> degenerate
    [TestCase(0.5, 0.0)]     // zero tick rate -> degenerate
    public void NaturalEclipseDurationTicks_DegenerateInputs_ReturnZero(double speed, double ticksPerHour)
    {
        double ticks = EclipseMath.NaturalEclipseDurationTicks(speed, 0.25, 0.26, 1.0, ticksPerHour);
        Assert.That(ticks, Is.EqualTo(0.0));
    }

    // --- UnnaturalCoverageAtProgress (§10b: scripted fly-in / park / fly-out) ---

    [TestCase(0.0)]
    [TestCase(1.0)]
    public void UnnaturalCoverageAtProgress_ZeroAtContacts(double progress)
    {
        // The scripted disc has not yet reached (or has just left) the sun at the very ends.
        double coverage = EclipseMath.UnnaturalCoverageAtProgress(progress, 0.12, magnitude: 1.0, moonSunRadiusRatio: 1.03);
        Assert.That(coverage, Is.EqualTo(0.0).Within(LooseTolerance));
    }

    [TestCase(0.12)]  // just reached the parked plateau
    [TestCase(0.3)]   // middle of the parked plateau
    [TestCase(0.5)]   // maximum
    [TestCase(0.88)]  // about to leave the parked plateau
    public void UnnaturalCoverageAtProgress_ParkedPlateauIsFull(double progress)
    {
        // For the whole middle of the event the disc is parked fully over the (slightly larger) sun.
        double coverage = EclipseMath.UnnaturalCoverageAtProgress(progress, 0.12, magnitude: 1.0, moonSunRadiusRatio: 1.03);
        Assert.That(coverage, Is.EqualTo(1.0).Within(Tolerance));
    }

    [Test]
    public void UnnaturalCoverageAtProgress_ReachesFullFasterThanNatural()
    {
        // The whole point of §10b: the disc darts in. At a small progress the unnatural ramp is well
        // ahead of the natural straight-line transit (which is only just past first contact).
        double natural = EclipseMath.NaturalCoverageAtProgress(0.06, 1.0, 1.03);
        double unnatural = EclipseMath.UnnaturalCoverageAtProgress(0.06, 0.12, 1.0, 1.03);
        Assert.That(unnatural, Is.GreaterThan(natural));
    }

    [Test]
    public void UnnaturalCoverageAtProgress_IsSymmetricAboutMaximum()
    {
        for (int i = 0; i <= 20; i++)
        {
            double p = i / 20.0;
            double a = EclipseMath.UnnaturalCoverageAtProgress(p, 0.12, 1.0, 1.03);
            double b = EclipseMath.UnnaturalCoverageAtProgress(1.0 - p, 0.12, 1.0, 1.03);
            Assert.That(a, Is.EqualTo(b).Within(Tolerance), $"asymmetry at progress {p}");
        }
    }

    [Test]
    public void UnnaturalCoverageAtProgress_MonotonicallyRisesDuringFlyIn()
    {
        // Across the fly-in window [0, slideFraction] the coverage only ever increases.
        double previous = -1.0;
        for (int i = 0; i <= 50; i++)
        {
            double p = 0.12 * (i / 50.0);
            double coverage = EclipseMath.UnnaturalCoverageAtProgress(p, 0.12, 1.0, 1.03);
            Assert.That(coverage, Is.GreaterThanOrEqualTo(previous - LooseTolerance),
                $"coverage decreased during fly-in at progress {p}");
            previous = coverage;
        }
    }

    [Test]
    public void UnnaturalCoverageAtProgress_HandlesDegenerateSlideFraction()
    {
        // slideFraction 0 is clamped to a tiny positive window rather than dividing by zero; the whole
        // event is then effectively one long parked plateau at full coverage.
        double coverage = EclipseMath.UnnaturalCoverageAtProgress(0.5, 0.0, 1.0, 1.03);
        Assert.That(coverage, Is.EqualTo(1.0).Within(Tolerance));
    }

    // --- Sky-lerp-factor selectors ---

    [Test]
    public void NaturalSkyLerpFactorAtProgress_MatchesCentralNaturalCoverage()
    {
        for (int i = 0; i <= 10; i++)
        {
            double p = i / 10.0;
            double expected = EclipseMath.NaturalCoverageAtProgress(p, 1.0, EclipseMath.DefaultMoonSunRadiusRatio);
            Assert.That(EclipseMath.NaturalSkyLerpFactorAtProgress(p), Is.EqualTo(expected).Within(Tolerance));
        }
    }

    [Test]
    public void UnnaturalSkyLerpFactorAtProgress_MatchesDefaultSlideCoverage()
    {
        for (int i = 0; i <= 10; i++)
        {
            double p = i / 10.0;
            double expected = EclipseMath.UnnaturalCoverageAtProgress(
                p, EclipseMath.DefaultSlideFraction, 1.0, EclipseMath.DefaultMoonSunRadiusRatio);
            Assert.That(EclipseMath.UnnaturalSkyLerpFactorAtProgress(p), Is.EqualTo(expected).Within(Tolerance));
        }
    }

    [Test]
    public void SkyLerpFactorAtProgress_SelectsRampByMode()
    {
        // The selector the patch/probe call routes to the natural ramp when natural mode is on, and the
        // unnatural ramp otherwise. The two ramps differ mid-fly-in, which is what pins the selection.
        for (int i = 0; i <= 10; i++)
        {
            double p = i / 10.0;
            Assert.That(EclipseMath.SkyLerpFactorAtProgress(p, naturalMode: true),
                Is.EqualTo(EclipseMath.NaturalSkyLerpFactorAtProgress(p)).Within(Tolerance));
            Assert.That(EclipseMath.SkyLerpFactorAtProgress(p, naturalMode: false),
                Is.EqualTo(EclipseMath.UnnaturalSkyLerpFactorAtProgress(p)).Within(Tolerance));
        }

        // The modes genuinely differ during the fly-in (not just alias to the same numbers).
        Assert.That(EclipseMath.SkyLerpFactorAtProgress(0.06, naturalMode: false),
            Is.Not.EqualTo(EclipseMath.SkyLerpFactorAtProgress(0.06, naturalMode: true)).Within(LooseTolerance));
    }

    // --- IsGeometricTransit (opt-in §10a astronomical trigger geometry) ---

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
