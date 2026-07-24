namespace CelestialLighting.Tests;

/// <summary>
/// Offline edge-case coverage for PenumbraMath.cs — no RimWorld/Unity assembly required, since the
/// file is System-only. Complements ApiCompatibilityTests.cs (which only checks vanilla members
/// still exist); these tests check that the angular-size penumbra math is correct, especially its
/// boundary behavior at the zenith and across the horizon.
/// </summary>
[TestFixture]
public class PenumbraMathTests
{
    // Looser than FormulasTests' 1e-4: these reference values were computed in double precision, and
    // MathF runs the same trig in single precision, so a few 1e-4 of drift is expected and fine.
    private const float Tolerance = 0.001f;

    // --- PenumbraWidthPerHeight ---

    [Test]
    public void PenumbraWidthPerHeight_IsTinyButPositive_AtZenith()
    {
        // Straight overhead the two solar limbs straddle the zenith symmetrically, leaving only the
        // Sun's own angular size as penumbra: width == 2*tan(alpha) ~= 0.0093, small but nonzero.
        float width = PenumbraMath.PenumbraWidthPerHeight(90f);
        Assert.That(width, Is.EqualTo(0.009303f).Within(Tolerance));
        Assert.That(width, Is.GreaterThan(0f));
    }

    [Test]
    public void PenumbraWidthPerHeight_GrowsMonotonically_AsSunDescends()
    {
        // The whole point of the effect: the penumbra widens as the Sun nears the horizon.
        float high = PenumbraMath.PenumbraWidthPerHeight(60f);
        float mid = PenumbraMath.PenumbraWidthPerHeight(20f);
        float low = PenumbraMath.PenumbraWidthPerHeight(5f);
        Assert.That(mid, Is.GreaterThan(high));
        Assert.That(low, Is.GreaterThan(mid));
    }

    [Test]
    public void PenumbraWidthPerHeight_Saturates_BelowHorizon_NeverNegative()
    {
        // Below the horizon the lower-limb edge is undefined/infinite; the limb floor caps the width
        // at a large finite value instead of diverging or flipping sign. Must never return negative.
        float belowHorizon = PenumbraMath.PenumbraWidthPerHeight(-5f);
        Assert.That(belowHorizon, Is.GreaterThan(100f)); // large (saturated), not tiny
        Assert.That(belowHorizon, Is.LessThan(2000f)); // but finite (floored), not infinite
    }

    // --- PenumbraSoftness ---

    [Test]
    public void PenumbraSoftness_IsHalf_AtHalfWidthElevation()
    {
        // At ~5.54 degrees the penumbra width equals SoftnessHalfWidth (1.0), so softness == 0.5 by
        // construction of the w/(w+k) saturating map.
        float softness = PenumbraMath.PenumbraSoftness(5.541195f);
        Assert.That(softness, Is.EqualTo(0.5f).Within(Tolerance));
    }

    [TestCase(90f, 0.009217f)] // near zero at the zenith: essentially a hard edge
    [TestCase(10f, 0.235897f)]
    [TestCase(5f, 0.551194f)]
    public void PenumbraSoftness_MatchesExpected(float elevationDegrees, float expected)
    {
        Assert.That(PenumbraMath.PenumbraSoftness(elevationDegrees), Is.EqualTo(expected).Within(Tolerance));
    }

    [Test]
    public void PenumbraSoftness_StaysInUnitRange_AcrossTheSky()
    {
        // Sweep zenith -> below horizon; softness is a probability-like fraction and must never leave
        // [0, 1] for a shader uniform / contrast term to consume it safely.
        for (float elevation = 90f; elevation >= -10f; elevation -= 2.5f)
        {
            float softness = PenumbraMath.PenumbraSoftness(elevation);
            Assert.That(softness, Is.InRange(0f, 1f), $"softness out of range at elevation {elevation}");
        }
    }

    [Test]
    public void PenumbraSoftness_ApproachesOne_NearHorizon()
    {
        // As the lower limb reaches the floor the penumbra saturates, driving softness toward 1.
        float softness = PenumbraMath.PenumbraSoftness(0f);
        Assert.That(softness, Is.GreaterThan(0.99f));
    }

    // --- PenumbraContrastFactor ---

    [Test]
    public void PenumbraContrastFactor_IsNearOne_AtZenith()
    {
        // A high Sun barely softens: opacity is left essentially untouched (hard-edged shadow).
        Assert.That(PenumbraMath.PenumbraContrastFactor(90f), Is.EqualTo(0.994470f).Within(Tolerance));
    }

    [Test]
    public void PenumbraContrastFactor_IsSeventyPercent_AtHalfSoftness()
    {
        // softness == 0.5 -> 1 - MaxContrastLoss*0.5 == 1 - 0.3 == 0.7.
        Assert.That(PenumbraMath.PenumbraContrastFactor(5.541195f), Is.EqualTo(0.7f).Within(Tolerance));
    }

    [Test]
    public void PenumbraContrastFactor_FloorsAtOneMinusMaxLoss_NearHorizon()
    {
        // Even a maximally-soft horizon shadow keeps 1 - MaxContrastLoss of its opacity; the shadow
        // dimming to nothing at the horizon is ShadowIntensityFromElevation's job, not this term's.
        float floorValue = 1f - PenumbraMath.MaxContrastLoss;
        float contrast = PenumbraMath.PenumbraContrastFactor(0f);
        Assert.That(contrast, Is.GreaterThanOrEqualTo(floorValue));
        Assert.That(contrast, Is.EqualTo(floorValue).Within(0.01f)); // essentially at the floor
    }

    [Test]
    public void PenumbraContrastFactor_StaysInRange_AcrossTheSky()
    {
        float floorValue = 1f - PenumbraMath.MaxContrastLoss;
        for (float elevation = 90f; elevation >= -10f; elevation -= 2.5f)
        {
            float contrast = PenumbraMath.PenumbraContrastFactor(elevation);
            Assert.That(contrast, Is.InRange(floorValue, 1f), $"contrast out of range at elevation {elevation}");
        }
    }
}
