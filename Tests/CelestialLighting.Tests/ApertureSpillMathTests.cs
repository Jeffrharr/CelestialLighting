using System;
using CelestialLighting;
using NUnit.Framework;

namespace CelestialLighting.Tests;

// The doorway wash's arithmetic. The claim under test is a continuity one — the wash is the lamp's
// own falloff curve entered late, not a second curve — so most of what follows is about the two
// places that curve has to join up: the opening itself and the outer rim.
[TestFixture]
public class ApertureSpillMathTests
{
    [TestCase(18f, 0f, 18f)]
    [TestCase(18f, 5.5f, 12.5f)]
    [TestCase(18f, 17.9f, 0.1f)]
    public void ResidualReachIsWhatIsLeftOfTheRadius(float radius, float d0, float expected)
    {
        Assert.That(ApertureSpillMath.ResidualReach(radius, d0), Is.EqualTo(expected).Within(1e-4f));
    }

    // A doorway at or beyond the lamp's radius throws nothing. This is the case that decides whether
    // a second polygon, coverage grid and mesh get built at all, so it is worth pinning rather than
    // trusting to a clamp.
    [TestCase(18f, 18f)]
    [TestCase(18f, 25f)]
    [TestCase(0f, 0f)]
    [TestCase(-1f, 1f)]
    public void NothingSpillsFromAnOpeningTheLightCannotReach(float radius, float d0)
    {
        Assert.That(ApertureSpillMath.ResidualReach(radius, d0), Is.EqualTo(0f));
        Assert.That(ApertureSpillMath.Spills(radius, d0), Is.False);
    }

    [TestCase(18f, 5.5f)]
    [TestCase(18f, 0.5f)]
    [TestCase(4f, 3.99f)]
    public void SomethingSpillsFromAnOpeningInsideTheRadius(float radius, float d0)
    {
        Assert.That(ApertureSpillMath.Spills(radius, d0), Is.True);
    }

    // THE SEAM AT THE OPENING. The spill's apex has to carry the same texture coordinate the lamp's
    // own fan carries at the doorway, or the wash starts at a different brightness from the beam it
    // continues and draws a bright or dark ring around the opening.
    [Test]
    public void TheSpillStartsWhereTheLampHasAlreadyGot()
    {
        const float radius = 18f;
        const float d0 = 5.5f;

        Assert.That(
            ApertureSpillMath.SpillU(radius, d0, 0f),
            Is.EqualTo(ApertureSpillMath.ApertureU(radius, d0)).Within(1e-6f));
    }

    // THE RIM. A spill vertex at the residual reach must land exactly on U = 1, where the gradient is
    // zero — so the wash fades out at the LAMP's radius rather than at a radius of its own. Getting
    // this wrong is what a separately-scaled second emitter would do, and it shows as the wash
    // stopping short of, or running past, the light it belongs to.
    [TestCase(18f, 5.5f)]
    [TestCase(18f, 0f)]
    [TestCase(7.25f, 3f)]
    public void TheSpillEndsAtTheLampsOwnRadius(float radius, float d0)
    {
        float reach = ApertureSpillMath.ResidualReach(radius, d0);
        Assert.That(ApertureSpillMath.SpillU(radius, d0, reach), Is.EqualTo(1f).Within(1e-5f));
    }

    // The whole model in one property: the two distances add, and the sum is read off the lamp's own
    // curve. Asserted against the arithmetic spelled out independently rather than against the
    // function's own expression, so this is an oracle and not a restatement.
    [TestCase(18f, 4f, 3f, 7f / 18f)]
    [TestCase(18f, 0f, 9f, 0.5f)]
    [TestCase(12f, 6f, 3f, 0.75f)]
    public void TheTwoDistancesAdd(float radius, float d0, float d1, float expected)
    {
        Assert.That(ApertureSpillMath.SpillU(radius, d0, d1), Is.EqualTo(expected).Within(1e-5f));
    }

    // Past the radius the coordinate saturates rather than running off the end of the gradient. A
    // texture lookup clamps anyway, so this is about the value being meaningful to read back.
    [Test]
    public void TheCoordinateNeverRunsPastTheGradient()
    {
        Assert.That(ApertureSpillMath.SpillU(18f, 10f, 40f), Is.EqualTo(1f));
        Assert.That(ApertureSpillMath.ApertureU(18f, 40f), Is.EqualTo(1f));
    }

    // Cell centres sit at integer + 0.5, so a one-cell doorway radiates from the middle of its cell
    // and a two-cell one from the join between them.
    [TestCase(30, 30, 30.5f)]
    [TestCase(30, 31, 31f)]
    [TestCase(19, 19, 19.5f)]
    [TestCase(-2, -1, -1f)]
    public void TheOpeningRadiatesFromItsOwnCentre(int min, int max, float expected)
    {
        Assert.That(ApertureSpillMath.ApertureCentre(min, max), Is.EqualTo(expected).Within(1e-6f));
    }

    // THE PUSH IS ALONG THE LIGHT'S BEARING, which is what keeps it going THROUGH the opening rather
    // than along the wall. Checked on an axis-aligned case where the answer is obvious by hand, and
    // on a diagonal where a normalisation slip would show up as a push of the wrong length.
    [Test]
    public void ThePushLeavesTheOpeningAlongTheLightsBearing()
    {
        ApertureSpillMath.SpillOrigin(24.5f, 19.5f, 30.5f, 19.5f, 0.5f, out float x, out float z);
        Assert.That(x, Is.EqualTo(31f).Within(1e-5f));
        Assert.That(z, Is.EqualTo(19.5f).Within(1e-5f));
    }

    [Test]
    public void ThePushIsTheStatedLengthOnADiagonal()
    {
        ApertureSpillMath.SpillOrigin(0f, 0f, 3f, 4f, 1f, out float x, out float z);

        // (3,4) is 5 away, so the unit bearing is (0.6, 0.8) and a push of 1 lands at (3.6, 4.8).
        Assert.That(x, Is.EqualTo(3.6f).Within(1e-5f));
        Assert.That(z, Is.EqualTo(4.8f).Within(1e-5f));

        float dx = x - 3f;
        float dz = z - 4f;
        Assert.That(Math.Sqrt(dx * dx + dz * dz), Is.EqualTo(1.0).Within(1e-5));
    }

    // A lamp standing in the doorway has no bearing to push along. Degenerate rather than an error:
    // it lights both rooms directly and has no wash to throw.
    [Test]
    public void ALightOnTheOpeningDoesNotMoveIt()
    {
        ApertureSpillMath.SpillOrigin(30.5f, 19.5f, 30.5f, 19.5f, 0.5f, out float x, out float z);
        Assert.That(x, Is.EqualTo(30.5f));
        Assert.That(z, Is.EqualTo(19.5f));
    }

    [Test]
    public void StrengthIsZeroWhereNothingSpills()
    {
        Assert.That(ApertureSpillMath.ApertureStrength(18f, 20f, 0.5f), Is.EqualTo(0f));
    }

    [Test]
    public void StrengthPassesTheCurveThroughWhereSomethingSpills()
    {
        Assert.That(ApertureSpillMath.ApertureStrength(18f, 5.5f, 0.42f), Is.EqualTo(0.42f));
        Assert.That(ApertureSpillMath.ApertureStrength(18f, 5.5f, -1f), Is.EqualTo(0f));
    }

    // THE CONTINUITY PROPERTY, SWEPT. Walking out from the opening the coordinate must rise
    // monotonically and never exceed one — a wash that brightened with distance, or ran off the
    // gradient, would be a sign error nobody would spot in a still.
    [Test]
    public void TheCoordinateRisesMonotonicallyOutFromTheOpening()
    {
        const float radius = 18f;
        const float d0 = 4.25f;
        float reach = ApertureSpillMath.ResidualReach(radius, d0);
        float previous = -1f;

        for (int step = 0; step <= 100; step++)
        {
            float distance = reach * step / 100f;
            float u = ApertureSpillMath.SpillU(radius, d0, distance);

            Assert.That(u, Is.GreaterThanOrEqualTo(previous), $"at step {step}");
            Assert.That(u, Is.LessThanOrEqualTo(1f), $"at step {step}");
            previous = u;
        }
    }
}
