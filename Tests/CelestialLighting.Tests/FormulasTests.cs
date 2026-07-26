using System;

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

    // --- TwilightPeakHeight ---

    [TestCase(0f, 0.15f)] // equator floor
    [TestCase(1f, 0.55f)] // full-strength peak
    [TestCase(0.5f, 0.35f)]
    public void TwilightPeakHeight_MatchesExpected(float strength, float expected)
    {
        Assert.That(Formulas.TwilightPeakHeight(strength), Is.EqualTo(expected).Within(Tolerance));
    }

    [Test]
    public void TwilightPeakHeight_IsTheGlowBandPeak_AtPeakGlow()
    {
        // The refactor that extracted TwilightPeakHeight must not change TwilightFactor's peak:
        // at TwilightPeakGlow the band factor equals the peak height exactly.
        Assert.That(
            Formulas.TwilightFactor(Formulas.TwilightPeakGlow, strength: 0.5f),
            Is.EqualTo(Formulas.TwilightPeakHeight(0.5f)).Within(Tolerance));
    }

    // --- CivilTwilightPersistence ---

    [TestCase(0f, 0f)] // geometric horizon: pulse starts at 0 so it meets the fading glow band without a jump
    [TestCase(Formulas.CivilTwilightPeakDegrees, 1f)] // peak of the lingering warmth
    [TestCase(Formulas.CivilTwilightEndDegrees, 0f)] // end of civil twilight: faded to nothing
    [TestCase(-1f, 0.5f)] // halfway up the rising limb (0 -> peak at -2)
    [TestCase(-4f, 0.5f)] // halfway down the falling limb (peak at -2 -> end at -6)
    public void CivilTwilightPersistence_MatchesExpected(float elevationDegrees, float expected)
    {
        Assert.That(Formulas.CivilTwilightPersistence(elevationDegrees), Is.EqualTo(expected).Within(Tolerance));
    }

    [TestCase(0.5f)] // just above the horizon: daytime, no lingering-twilight term
    [TestCase(10f)]
    [TestCase(90f)]
    public void CivilTwilightPersistence_IsZero_AboveHorizon(float elevationDegrees)
    {
        Assert.That(Formulas.CivilTwilightPersistence(elevationDegrees), Is.EqualTo(0f).Within(Tolerance));
    }

    [TestCase(-6.5f)] // just past civil twilight
    [TestCase(-20f)]
    [TestCase(-90f)] // solar nadir
    public void CivilTwilightPersistence_IsZero_BelowCivilTwilight(float elevationDegrees)
    {
        Assert.That(Formulas.CivilTwilightPersistence(elevationDegrees), Is.EqualTo(0f).Within(Tolerance));
    }

    // --- TwilightWarmthFactor ---

    [Test]
    public void TwilightWarmthFactor_MatchesGlowBand_AboveHorizon()
    {
        // Above the horizon the persistence term is 0, so the combined factor is exactly the
        // original glow-keyed band — daytime/dusk behaviour is unchanged by this refinement.
        float glowBand = Formulas.TwilightFactor(Formulas.TwilightPeakGlow, strength: 1f);
        float combined = Formulas.TwilightWarmthFactor(Formulas.TwilightPeakGlow, elevationDegrees: 20f, strength: 1f);
        Assert.That(combined, Is.EqualTo(glowBand).Within(Tolerance));
    }

    [Test]
    public void TwilightWarmthFactor_LingersAfterSunset_WhereGlowAloneWouldBeZero()
    {
        // The whole point of the refinement: with the sun below the horizon vanilla glow has pinned
        // to 0 (glow band -> 0), yet the combined factor is still positive during civil twilight.
        float persistencePeak = Formulas.TwilightWarmthFactor(
            sunGlow: 0f, elevationDegrees: Formulas.CivilTwilightPeakDegrees, strength: 1f);
        Assert.That(persistencePeak, Is.EqualTo(Formulas.TwilightPeakHeight(1f)).Within(Tolerance));
    }

    [Test]
    public void TwilightWarmthFactor_IsZero_AtDeepNight()
    {
        // Sun well below civil twilight and glow at 0: no warm tint at all — night is left dark.
        float factor = Formulas.TwilightWarmthFactor(sunGlow: 0f, elevationDegrees: -30f, strength: 1f);
        Assert.That(factor, Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void TwilightWarmthFactor_IsContinuous_AcrossTheHorizon()
    {
        // Both pieces are ~0 at the horizon (the glow band's left flank reaches 0 there at full
        // strength, and the persistence pulse starts at 0), so crossing sunset must not produce a
        // jump in the warm tint. Sample just above and just below and require them to stay close.
        float justAbove = Formulas.TwilightWarmthFactor(sunGlow: 0.001f, elevationDegrees: 0.05f, strength: 1f);
        float justBelow = Formulas.TwilightWarmthFactor(sunGlow: 0f, elevationDegrees: -0.05f, strength: 1f);
        Assert.That(justBelow, Is.EqualTo(justAbove).Within(0.02f));
    }

    // --- SolarDeclinationDegrees ---

    [TestCase(0f, -23.44f)] // start of year: winter-pole-favoring, matches DeclinationSign(0) == -1
    [TestCase(30f, 23.44f)] // half-year (solstice): DeclinationSign(30) == 1
    [TestCase(15f, 0f)] // equinox
    public void SolarDeclinationDegrees_MatchesExpected(float dayOfYear, float expected)
    {
        Assert.That(Formulas.SolarDeclinationDegrees(dayOfYear), Is.EqualTo(expected).Within(Tolerance));
    }

    // --- SolarElevationDegrees ---

    [Test]
    public void SolarElevationDegrees_IsNinety_AtEquatorEquinoxNoon()
    {
        // Sun directly overhead: 0 latitude, 0 declination, solar noon.
        float elevation = Formulas.SolarElevationDegrees(latitudeDegrees: 0f, declinationDegrees: 0f, dayPercent: 0.5f);
        Assert.That(elevation, Is.EqualTo(90f).Within(Tolerance));
    }

    [Test]
    public void SolarElevationDegrees_IsNegativeNinety_AtEquatorEquinoxMidnight()
    {
        float elevation = Formulas.SolarElevationDegrees(latitudeDegrees: 0f, declinationDegrees: 0f, dayPercent: 0f);
        Assert.That(elevation, Is.EqualTo(-90f).Within(Tolerance));
    }

    [TestCase(0.25f)] // 6am
    [TestCase(0.75f)] // 6pm
    public void SolarElevationDegrees_IsZero_AtEquatorEquinoxSunriseSunset(float dayPercent)
    {
        float elevation = Formulas.SolarElevationDegrees(latitudeDegrees: 0f, declinationDegrees: 0f, dayPercent: dayPercent);
        Assert.That(elevation, Is.EqualTo(0f).Within(Tolerance));
    }

    [TestCase(0f)]
    [TestCase(0.25f)]
    [TestCase(0.5f)]
    [TestCase(0.75f)]
    public void SolarElevationDegrees_IsConstant_AtNorthPole_RegardlessOfDayPercent(float dayPercent)
    {
        // cos(latitude) == 0 at the pole, so the hour-angle term drops out entirely: elevation
        // equals declination all "day" long. This is what makes midnight sun/polar night fall out
        // of the formula for free, with no special-casing.
        float elevation = Formulas.SolarElevationDegrees(latitudeDegrees: 90f, declinationDegrees: 23.44f, dayPercent: dayPercent);
        Assert.That(elevation, Is.EqualTo(23.44f).Within(Tolerance));
    }

    [Test]
    public void SolarElevationDegrees_IsNegative_AtNorthPole_DuringSouthernSummer()
    {
        // Polar night: the sun never rises at the north pole while the south pole has its summer.
        float elevation = Formulas.SolarElevationDegrees(latitudeDegrees: 90f, declinationDegrees: -23.44f, dayPercent: 0.5f);
        Assert.That(elevation, Is.EqualTo(-23.44f).Within(Tolerance));
    }

    [Test]
    public void SolarElevationDegrees_IsMirrored_AtSouthPole()
    {
        float elevation = Formulas.SolarElevationDegrees(latitudeDegrees: -90f, declinationDegrees: 23.44f, dayPercent: 0f);
        Assert.That(elevation, Is.EqualTo(-23.44f).Within(Tolerance));
    }

    // --- ShadowLengthFromElevation ---

    [Test]
    public void ShadowLengthFromElevation_IsOne_AtFortyFiveDegrees()
    {
        Assert.That(Formulas.ShadowLengthFromElevation(45f), Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void ShadowLengthFromElevation_IsZero_AtZenith()
    {
        Assert.That(Formulas.ShadowLengthFromElevation(90f), Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void ShadowLengthFromElevation_ClampsToMaxNearHorizon()
    {
        // cot(1 degree) is ~57, far past MaxShadowLength (15) — must clamp, not blow up.
        Assert.That(Formulas.ShadowLengthFromElevation(1f), Is.EqualTo(Formulas.MaxShadowLength).Within(Tolerance));
    }

    [Test]
    public void ShadowLengthFromElevation_FloorsElevationNearZero()
    {
        // Elevations below the internal floor (0.5 degrees) must not diverge further than the
        // floor's own value does — otherwise cot() approaches infinity as elevation approaches 0.
        float atZero = Formulas.ShadowLengthFromElevation(0f);
        float belowFloor = Formulas.ShadowLengthFromElevation(0.01f);
        Assert.That(belowFloor, Is.EqualTo(atZero).Within(Tolerance));
    }

    // --- ShadowIntensityFromElevation ---

    [TestCase(-0.83f, 0f)] // AtmosphericRefractionDegrees: the existence gate, ramp floor
    [TestCase(2.17f, 1f)] // AtmosphericRefractionDegrees + ShadowIntensityRampDegrees: ramp ceiling
    [TestCase(0.67f, 0.5f)] // ramp midpoint
    [TestCase(100f, 1f)] // well past the ramp: stays clamped at full strength
    [TestCase(-5f, 0f)] // well below the refraction-adjusted horizon: clamps to 0, doesn't go negative
    public void ShadowIntensityFromElevation_MatchesExpected(float elevationDegrees, float expected)
    {
        Assert.That(Formulas.ShadowIntensityFromElevation(elevationDegrees), Is.EqualTo(expected).Within(Tolerance));
    }

    // --- ShadowVectorFromSunPosition ---

    [Test]
    public void ShadowVectorFromSunPosition_PointsAwayFromSun_WhenSunDueNorth()
    {
        // Sun at azimuth 0 (due north) casts a shadow due south: positive X stays ~0, Y goes negative.
        Formulas.ShadowVector shadow = Formulas.ShadowVectorFromSunPosition(elevationDegrees: 45f, azimuthDegrees: 0f);
        Assert.That(shadow.X, Is.EqualTo(0f).Within(Tolerance));
        Assert.That(shadow.Y, Is.EqualTo(-1f).Within(Tolerance)); // cot(45) == 1
    }

    [Test]
    public void ShadowVectorFromSunPosition_PointsAwayFromSun_WhenSunDueEast()
    {
        // Sun at azimuth 90 (due east) casts a shadow due west: X goes negative, Y stays ~0.
        Formulas.ShadowVector shadow = Formulas.ShadowVectorFromSunPosition(elevationDegrees: 45f, azimuthDegrees: 90f);
        Assert.That(shadow.X, Is.EqualTo(-1f).Within(Tolerance));
        Assert.That(shadow.Y, Is.EqualTo(0f).Within(Tolerance));
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

    // --- ScaleShadowVector / ScaleShadowStrength: the two user-facing Look sliders ---

    [Test]
    public void ScaleShadowVector_ScalesLengthWithoutTurningTheShadow()
    {
        // The knob is a length knob: direction is the physical model's answer and must survive it,
        // or a preset would silently point shadows somewhere the sun is not.
        Formulas.ShadowVector scaled = Formulas.ScaleShadowVector(3f, 4f, 1.4f);
        Assert.That(MathF.Sqrt(scaled.X * scaled.X + scaled.Y * scaled.Y), Is.EqualTo(7f).Within(Tolerance));
        Assert.That(scaled.X / scaled.Y, Is.EqualTo(3f / 4f).Within(Tolerance));
    }

    [Test]
    public void ScaleShadowVector_IsIdentityAtOne()
    {
        // Realistic's 1.0 must be a true no-op, not "almost the physical model".
        Formulas.ShadowVector scaled = Formulas.ScaleShadowVector(-2.5f, 6f, 1f);
        Assert.That(scaled.X, Is.EqualTo(-2.5f).Within(Tolerance));
        Assert.That(scaled.Y, Is.EqualTo(6f).Within(Tolerance));
    }

    [Test]
    public void ScaleShadowVector_ReClampsToVanillasMaxLength()
    {
        // Vanilla's mesh/shader is tuned for a -15..15 vector range, so scaling an already-clamped
        // near-horizon shadow up must not push geometry past what that renderer expects.
        Formulas.ShadowVector scaled = Formulas.ScaleShadowVector(Formulas.MaxShadowLength, 0f, 2f);
        Assert.That(scaled.X, Is.EqualTo(Formulas.MaxShadowLength).Within(Tolerance));
    }

    [Test]
    public void ScaleShadowVector_DegenerateInputsCollapseToNoShadow()
    {
        // A zero-length vector has no direction to preserve, and a zero/negative scale means "no
        // shadow" — both must produce a vector rather than a NaN the shader would render as garbage.
        Formulas.ShadowVector zeroVector = Formulas.ScaleShadowVector(0f, 0f, 1.4f);
        Assert.That(zeroVector.X, Is.EqualTo(0f).Within(Tolerance));
        Assert.That(zeroVector.Y, Is.EqualTo(0f).Within(Tolerance));

        Formulas.ShadowVector zeroScale = Formulas.ScaleShadowVector(3f, 4f, 0f);
        Assert.That(zeroScale.X, Is.EqualTo(0f).Within(Tolerance));
        Assert.That(zeroScale.Y, Is.EqualTo(0f).Within(Tolerance));
    }

    [TestCase(1f, 1f, 1f)]        // identity
    [TestCase(0.8f, 0.5f, 0.4f)]  // plain multiply
    [TestCase(1f, 0f, 0f)]        // slider at the floor means no shadow at all
    [TestCase(2f, 1f, 1f)]        // an out-of-range intensity still cannot exceed full opacity
    [TestCase(1f, 2f, 1f)]        // nor can an out-of-range slider
    [TestCase(-1f, 1f, 0f)]
    public void ScaleShadowStrength_MultipliesAndClampsBothEnds(float intensity, float scale, float expected)
    {
        Assert.That(Formulas.ScaleShadowStrength(intensity, scale), Is.EqualTo(expected).Within(Tolerance));
    }
}
