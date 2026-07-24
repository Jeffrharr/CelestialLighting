namespace CelestialLighting.Tests;

/// <summary>
/// Offline edge-case coverage for Formulas.cs — no RimWorld/Unity assembly required, since
/// Formulas.cs has no dependency on either. Complements ApiCompatibilityTests.cs, which only
/// checks that vanilla members still exist; these tests check that our own math is correct.
/// </summary>
[TestFixture]
public class FormulasTests
{
    private const float Tolerance = 0.0001f;

    // --- LatitudeStrength ---

    [TestCase(0f, 0f)]
    [TestCase(30f, 0.5f)]
    [TestCase(60f, 1f)]
    [TestCase(90f, 1f)] // clamps past full-strength latitude, doesn't overshoot
    [TestCase(-30f, 0.5f)] // symmetric by |latitude|
    [TestCase(-60f, 1f)]
    public void LatitudeStrength_MatchesExpected(float latitude, float expected)
    {
        Assert.That(Formulas.LatitudeStrength(latitude), Is.EqualTo(expected).Within(Tolerance));
    }

    // --- DeclinationSign ---

    [TestCase(0f, -1f)] // start of year: -cos(0) = -1
    [TestCase(15f, 0f)] // quarter-year: -cos(pi/2) = 0
    [TestCase(30f, 1f)] // half-year (solstice): -cos(pi) = 1
    [TestCase(45f, 0f)] // three-quarter-year: -cos(3pi/2) = 0
    [TestCase(60f, -1f)] // full cycle back to start
    public void DeclinationSign_MatchesExpected(float dayOfYear, float expected)
    {
        Assert.That(Formulas.DeclinationSign(dayOfYear), Is.EqualTo(expected).Within(Tolerance));
    }

    // --- ApplyShadowLean (the equinox-flattening regression this formula exists to fix) ---

    [TestCase(5f, 0f, 5f)] // lean == 0 must be a true no-op, at every y, not just y == 0
    [TestCase(-5f, 0f, -5f)]
    [TestCase(0f, 0f, 0f)]
    [TestCase(-10f, 1f, 10f)] // full positive lean flips a negative y to fully positive
    [TestCase(10f, -1f, -10f)] // full negative lean flips a positive y to fully negative
    [TestCase(10f, 1f, 10f)] // already the target sign: full lean is a no-op
    [TestCase(-10f, 0.5f, 0f)] // halfway through a flip: lerp(-10, 10, 0.5) == 0
    public void ApplyShadowLean_MatchesExpected(float y, float lean, float expected)
    {
        Assert.That(Formulas.ApplyShadowLean(y, lean), Is.EqualTo(expected).Within(Tolerance));
    }

    [Test]
    public void ApplyShadowLean_NeverFlattensAtLeanZero_AcrossManyYValues()
    {
        // Regression guard for the original bug: Lerp(y, -y, InverseLerp(-1,1,0)) == 0 for any y.
        // The sign-blend replacement must leave y untouched at lean == 0 for every y, not just 0.
        for (float y = -20f; y <= 20f; y += 1f)
        {
            Assert.That(Formulas.ApplyShadowLean(y, 0f), Is.EqualTo(y).Within(Tolerance),
                $"lean == 0 flattened y == {y}");
        }
    }

    // --- TwilightBandWidth ---

    [TestCase(0f, 0.12f)]
    [TestCase(1f, 0.35f)]
    [TestCase(0.5f, 0.235f)]
    public void TwilightBandWidth_MatchesExpected(float strength, float expected)
    {
        Assert.That(Formulas.TwilightBandWidth(strength), Is.EqualTo(expected).Within(Tolerance));
    }

    // --- TwilightFactor ---

    [Test]
    public void TwilightFactor_PeaksAtPeakGlow_FullStrength()
    {
        float factor = Formulas.TwilightFactor(Formulas.TwilightPeakGlow, strength: 1f);
        Assert.That(factor, Is.EqualTo(0.55f).Within(Tolerance));
    }

    [Test]
    public void TwilightFactor_HasNonzeroFloorAtPeakGlow_EvenAtZeroStrength()
    {
        // Documents an intentional (not accidental) property of the formula: even at strength ==
        // 0 (the equator), sunGlow exactly at the peak still yields a small nonzero nudge —
        // Lerp(0.15, 0.55, 0) == 0.15, not 0. Patch_TwilightColor separately early-returns when
        // ctx.Strength <= 0, which is what actually keeps the equator untouched in practice.
        float factor = Formulas.TwilightFactor(Formulas.TwilightPeakGlow, strength: 0f);
        Assert.That(factor, Is.EqualTo(0.15f).Within(Tolerance));
    }

    [Test]
    public void TwilightFactor_IsZero_FarOutsideBand()
    {
        float factor = Formulas.TwilightFactor(sunGlow: 0.99f, strength: 1f);
        Assert.That(factor, Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void TwilightFactor_IsZero_AtExactBandEdge()
    {
        float bandWidth = Formulas.TwilightBandWidth(1f);
        float factor = Formulas.TwilightFactor(Formulas.TwilightPeakGlow + bandWidth, strength: 1f);
        Assert.That(factor, Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void TwilightFactor_IsSymmetric_AroundPeakGlow()
    {
        float below = Formulas.TwilightFactor(Formulas.TwilightPeakGlow - 0.05f, strength: 1f);
        float above = Formulas.TwilightFactor(Formulas.TwilightPeakGlow + 0.05f, strength: 1f);
        Assert.That(below, Is.EqualTo(above).Within(Tolerance));
    }

    // --- ShadowLengthPositionFraction ---

    [Test]
    public void ShadowLengthPositionFraction_IsZero_WhenShadowDirIsDegenerate()
    {
        float fraction = Formulas.ShadowLengthPositionFraction(
            offsetX: 50f, offsetZ: 50f, shadowDirX: 0f, shadowDirZ: 0f, mapSizeX: 250f, mapSizeZ: 250f);
        Assert.That(fraction, Is.EqualTo(0f));
    }

    [Test]
    public void ShadowLengthPositionFraction_IsZero_WhenMapSizeIsDegenerate()
    {
        float fraction = Formulas.ShadowLengthPositionFraction(
            offsetX: 50f, offsetZ: 50f, shadowDirX: 1f, shadowDirZ: 0f, mapSizeX: 0f, mapSizeZ: 0f);
        Assert.That(fraction, Is.EqualTo(0f));
    }

    [Test]
    public void ShadowLengthPositionFraction_IsOne_AtFarEdgeAlongShadowAxis()
    {
        // 250x250 map, shadow pointing purely along +X: the section at the far +X edge
        // (offset = +125 from center) should land exactly at fraction == 1.
        float fraction = Formulas.ShadowLengthPositionFraction(
            offsetX: 125f, offsetZ: 0f, shadowDirX: 1f, shadowDirZ: 0f, mapSizeX: 250f, mapSizeZ: 250f);
        Assert.That(fraction, Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void ShadowLengthPositionFraction_IsNegativeOne_AtOppositeEdge()
    {
        float fraction = Formulas.ShadowLengthPositionFraction(
            offsetX: -125f, offsetZ: 0f, shadowDirX: 1f, shadowDirZ: 0f, mapSizeX: 250f, mapSizeZ: 250f);
        Assert.That(fraction, Is.EqualTo(-1f).Within(Tolerance));
    }

    [Test]
    public void ShadowLengthPositionFraction_IsZero_AtMapCenter()
    {
        float fraction = Formulas.ShadowLengthPositionFraction(
            offsetX: 0f, offsetZ: 0f, shadowDirX: 1f, shadowDirZ: 1f, mapSizeX: 250f, mapSizeZ: 250f);
        Assert.That(fraction, Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void ShadowLengthPositionFraction_ClampsBeyondMapEdge()
    {
        // A position further than the map's own half-extent (shouldn't normally happen for a
        // section actually on the map, but the function must not return outside [-1, 1]).
        float fraction = Formulas.ShadowLengthPositionFraction(
            offsetX: 1000f, offsetZ: 0f, shadowDirX: 1f, shadowDirZ: 0f, mapSizeX: 250f, mapSizeZ: 250f);
        Assert.That(fraction, Is.EqualTo(1f).Within(Tolerance));
    }

    // --- ShadowLengthScale ---

    [TestCase(0f, 0.15f, 1f)]
    [TestCase(1f, 0.15f, 1.15f)]
    [TestCase(-1f, 0.15f, 0.85f)]
    [TestCase(0.5f, 0.15f, 1.075f)]
    public void ShadowLengthScale_MatchesExpected(float positionFraction, float maxVariation, float expected)
    {
        Assert.That(Formulas.ShadowLengthScale(positionFraction, maxVariation), Is.EqualTo(expected).Within(Tolerance));
    }

    [Test]
    public void ShadowLengthScale_ClampsUnclampedInput()
    {
        // Defensive: even if a caller passes a positionFraction outside [-1, 1] directly, the
        // result must stay within [1 - maxVariation, 1 + maxVariation].
        Assert.That(Formulas.ShadowLengthScale(5f, 0.15f), Is.EqualTo(1.15f).Within(Tolerance));
        Assert.That(Formulas.ShadowLengthScale(-5f, 0.15f), Is.EqualTo(0.85f).Within(Tolerance));
    }
}
