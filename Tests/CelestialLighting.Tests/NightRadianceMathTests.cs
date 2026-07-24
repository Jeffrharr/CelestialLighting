namespace CelestialLighting.Tests;

/// <summary>
/// Offline edge-case coverage for NightRadianceMath.cs (§7 night-sky radiance) — no RimWorld/Unity
/// assembly required, since the pure math has no dependency on either. These check that our own
/// star/airglow/moonlight sum and the night-floor blend behave correctly at the boundaries;
/// ApiCompatibilityTests only checks that the vanilla members the adapter touches still exist.
/// </summary>
[TestFixture]
public class NightRadianceMathTests
{
    private const float Tolerance = 0.0001f;

    // --- MoonAltitudeFactor ---

    [TestCase(90f, 1f)]     // zenith: sin(90) == 1
    [TestCase(30f, 0.5f)]   // sin(30) == 0.5
    [TestCase(0f, 0f)]      // exactly on the horizon
    [TestCase(-10f, 0f)]    // below the horizon: clamps to 0, a set moon casts no light
    [TestCase(-90f, 0f)]    // straight down
    public void MoonAltitudeFactor_MatchesExpected(float moonElevationDegrees, float expected)
    {
        Assert.That(NightRadianceMath.MoonAltitudeFactor(moonElevationDegrees), Is.EqualTo(expected).Within(Tolerance));
    }

    // --- MoonlightGlow ---

    [Test]
    public void MoonlightGlow_IsMax_AtFullMoonZenith()
    {
        float glow = NightRadianceMath.MoonlightGlow(illuminatedFraction: 1f, moonElevationDegrees: 90f, maxMoonlightGlow: 0.15f);
        Assert.That(glow, Is.EqualTo(0.15f).Within(Tolerance));
    }

    [Test]
    public void MoonlightGlow_IsZero_AtNewMoon()
    {
        // No reflected light regardless of how high the (new) moon sits.
        float glow = NightRadianceMath.MoonlightGlow(illuminatedFraction: 0f, moonElevationDegrees: 90f, maxMoonlightGlow: 0.15f);
        Assert.That(glow, Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void MoonlightGlow_IsZero_WhenMoonBelowHorizon()
    {
        // A full moon that has set contributes nothing.
        float glow = NightRadianceMath.MoonlightGlow(illuminatedFraction: 1f, moonElevationDegrees: -5f, maxMoonlightGlow: 0.15f);
        Assert.That(glow, Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void MoonlightGlow_ScalesWithPhaseAndAltitude()
    {
        // Half-lit moon at 30 degrees: 0.5 (phase) * 0.5 (sin 30) * 0.15 == 0.0375.
        float glow = NightRadianceMath.MoonlightGlow(illuminatedFraction: 0.5f, moonElevationDegrees: 30f, maxMoonlightGlow: 0.15f);
        Assert.That(glow, Is.EqualTo(0.0375f).Within(Tolerance));
    }

    [Test]
    public void MoonlightGlow_ClampsIlluminatedFractionAboveOne()
    {
        // Defensive: an out-of-range phase from a future moon model can't overshoot the max.
        float glow = NightRadianceMath.MoonlightGlow(illuminatedFraction: 2f, moonElevationDegrees: 90f, maxMoonlightGlow: 0.15f);
        Assert.That(glow, Is.EqualTo(0.15f).Within(Tolerance));
    }

    // --- NightSourceGlow ---

    [Test]
    public void NightSourceGlow_SumsTheThreeSources()
    {
        float glow = NightRadianceMath.NightSourceGlow(starlightGlow: 0.02f, airglowGlow: 0.02f, moonlightGlow: 0.15f);
        Assert.That(glow, Is.EqualTo(0.19f).Within(Tolerance));
    }

    [Test]
    public void NightSourceGlow_IsZero_WhenAllSourcesZero()
    {
        // True pitch-black: floors off + no moonlight == exactly 0, no special case.
        float glow = NightRadianceMath.NightSourceGlow(starlightGlow: 0f, airglowGlow: 0f, moonlightGlow: 0f);
        Assert.That(glow, Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void NightSourceGlow_FullMoonReadsBrighterThanNewMoon()
    {
        // The central design claim: summing (not max-ing) makes a full-moon night strictly brighter
        // than a new-moon night at the same star/airglow floors.
        float newMoon = NightRadianceMath.NightSourceGlow(0.02f, 0.02f, moonlightGlow: 0f);
        float fullMoon = NightRadianceMath.NightSourceGlow(0.02f, 0.02f, moonlightGlow: 0.15f);
        Assert.That(fullMoon, Is.GreaterThan(newMoon));
    }

    [Test]
    public void NightSourceGlow_ClampsToOne()
    {
        float glow = NightRadianceMath.NightSourceGlow(starlightGlow: 0.6f, airglowGlow: 0.6f, moonlightGlow: 0.6f);
        Assert.That(glow, Is.EqualTo(1f).Within(Tolerance));
    }

    // --- NightFloorWeight ---

    [TestCase(10f, 0f)]     // sun well up: floor contributes nothing
    [TestCase(-0.83f, 0f)]  // NightFloorStartElevation: ramp floor
    [TestCase(-18f, 1f)]    // NightFloorFullElevation: ramp ceiling (full night)
    [TestCase(-30f, 1f)]    // deeper still (polar midnight): clamps at 1, doesn't overshoot
    public void NightFloorWeight_MatchesExpected(float sunElevationDegrees, float expected)
    {
        Assert.That(NightRadianceMath.NightFloorWeight(sunElevationDegrees), Is.EqualTo(expected).Within(Tolerance));
    }

    [Test]
    public void NightFloorWeight_IsHalf_AtBandMidpoint()
    {
        // Midpoint between -0.83 and -18 is about -9.415 degrees.
        float midpoint = (NightRadianceMath.NightFloorStartElevation + NightRadianceMath.NightFloorFullElevation) / 2f;
        Assert.That(NightRadianceMath.NightFloorWeight(midpoint), Is.EqualTo(0.5f).Within(Tolerance));
    }

    // --- ApplyNightFloor ---

    [Test]
    public void ApplyNightFloor_LeavesDaytimeGlowUnchanged()
    {
        // Sun above the start elevation: weight 0, vanilla glow returned untouched — the day is
        // never brightened or dimmed by this subsystem.
        float result = NightRadianceMath.ApplyNightFloor(vanillaGlow: 1f, sunElevationDegrees: 30f, nightGlow: 0.04f);
        Assert.That(result, Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void ApplyNightFloor_BecomesNightGlow_AtDeepNight()
    {
        // Past astronomical twilight: weight 1, glow is exactly our computed floor.
        float result = NightRadianceMath.ApplyNightFloor(vanillaGlow: 0.5f, sunElevationDegrees: -20f, nightGlow: 0.04f);
        Assert.That(result, Is.EqualTo(0.04f).Within(Tolerance));
    }

    [Test]
    public void ApplyNightFloor_CanDarkenBelowVanillaFloor_ForPitchBlack()
    {
        // The pitch-black case: vanilla night glow above zero, our floor at zero, deep night —
        // result must reach 0 (a Max()-based blend never could).
        float result = NightRadianceMath.ApplyNightFloor(vanillaGlow: 0.1f, sunElevationDegrees: -20f, nightGlow: 0f);
        Assert.That(result, Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void ApplyNightFloor_CanBrightenAboveVanillaFloor_ForFullMoon()
    {
        // The full-moon case: our summed floor exceeds vanilla's flat night glow.
        float result = NightRadianceMath.ApplyNightFloor(vanillaGlow: 0.02f, sunElevationDegrees: -20f, nightGlow: 0.19f);
        Assert.That(result, Is.EqualTo(0.19f).Within(Tolerance));
    }
}
