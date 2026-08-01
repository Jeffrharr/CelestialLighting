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

    // --- OverlayBrightnessFactor (pitch-black nights, §7a) ---

    [Test]
    public void OverlayBrightnessFactor_KeepsFullBrightness_AtOrAboveReference()
    {
        // A bright (moonlit) night at/above OverlayFullBrightGlow is left at vanilla brightness (1).
        Assert.That(NightRadianceMath.OverlayBrightnessFactor(NightRadianceMath.OverlayFullBrightGlow, 0f),
            Is.EqualTo(1f).Within(Tolerance));
        Assert.That(NightRadianceMath.OverlayBrightnessFactor(1f, 0f), Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void OverlayBrightnessFactor_GoesFullyBlack_AtZeroGlow_WithZeroClamp()
    {
        // True pitch black: floors off + moon down (glow 0) and no playability clamp -> keep 0 (the
        // overlay is pulled fully to black). This is the "make pitch-black APPEAR pitch-black" case.
        Assert.That(NightRadianceMath.OverlayBrightnessFactor(0f, 0f), Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void OverlayBrightnessFactor_RespectsMinBrightnessClamp()
    {
        // The playability clamp: even at glow 0 the night never darkens below the clamp, so it stays
        // navigable. This is the knob the user asked for to keep pitch black from being unplayable.
        Assert.That(NightRadianceMath.OverlayBrightnessFactor(0f, 0.18f), Is.EqualTo(0.18f).Within(Tolerance));
        // A glow whose raw factor is below the clamp is lifted to the clamp...
        Assert.That(NightRadianceMath.OverlayBrightnessFactor(0.015f, 0.18f), Is.EqualTo(0.18f).Within(Tolerance));
        // ...but a glow above the clamp keeps its (brighter) raw factor. Midway between the two anchors
        // is keep 0.5, comfortably above an 0.18 clamp.
        float midway = (NightRadianceMath.OverlayDarkGlow + NightRadianceMath.OverlayFullBrightGlow) / 2f;
        Assert.That(NightRadianceMath.OverlayBrightnessFactor(midway, 0.18f), Is.EqualTo(0.5f).Within(Tolerance));
    }

    [Test]
    public void OverlayBrightnessFactor_IsLinearBetweenTheTwoAnchors()
    {
        // Midway between OverlayDarkGlow and OverlayFullBrightGlow -> keep half the brightness (no clamp).
        float midway = (NightRadianceMath.OverlayDarkGlow + NightRadianceMath.OverlayFullBrightGlow) / 2f;
        Assert.That(NightRadianceMath.OverlayBrightnessFactor(midway, 0f), Is.EqualTo(0.5f).Within(Tolerance));
    }

    [Test]
    public void OverlayBrightnessFactor_KeepsTheFloorsVisible_OnAMoonlessNightWithTheFloorsOn()
    {
        // This assertion is the deliberate inverse of what it used to be. It previously required a
        // moonless floors-ON night to render fully black, which meant the "Atmospheric night glow"
        // toggle produced no visible change at all outdoors — the floors were cancelled by the very
        // curve meant to display them. The floors exist to be seen; pitch-black is what turning them
        // OFF is for, asserted directly below.
        float moonless = NightRadianceMath.NightSourceGlow(
            NightRadianceMath.DefaultStarlightGlow, NightRadianceMath.DefaultAirglowGlow, moonlightGlow: 0f);
        float keep = NightRadianceMath.OverlayBrightnessFactor(moonless, 0f);

        Assert.That(keep, Is.GreaterThan(0f), "the atmospheric floors must be visible outdoors");
        // 0.04 / 0.19 — a faint starlit dark rather than a void.
        Assert.That(keep, Is.EqualTo(0.04f / 0.19f).Within(Tolerance));
        Assert.That(keep, Is.LessThan(0.3f), "but still unmistakably night, not a dimmed day");
    }

    [Test]
    public void OverlayBrightnessFactor_GoesFullyBlack_WithTheFloorsOff()
    {
        // The other half of the toggle's contract, and the route to true pitch-black the design has
        // always documented: drop starlight and airglow to zero and, with no moon, the night glow is
        // exactly 0 so the overlay blacks out. Together with the test above this is what makes the
        // atmospheric-glow switch actually distinguish two states on screen.
        float moonlessFloorsOff = NightRadianceMath.NightSourceGlow(
            starlightGlow: 0f, airglowGlow: 0f, moonlightGlow: 0f);
        Assert.That(NightRadianceMath.OverlayBrightnessFactor(moonlessFloorsOff, 0f),
            Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void OverlayBrightnessFactor_KeepsVanillaBrightness_UnderAFullMoonAtZenith()
    {
        // The other end of the same invariant: a full moon at zenith on top of the floors is the brightest
        // night the model can produce, and it must not be darkened at all.
        float fullMoon = NightRadianceMath.NightSourceGlow(
            NightRadianceMath.DefaultStarlightGlow,
            NightRadianceMath.DefaultAirglowGlow,
            NightRadianceMath.MoonlightGlow(1f, 90f, NightRadianceMath.DefaultMaxMoonlightGlow));
        Assert.That(NightRadianceMath.OverlayBrightnessFactor(fullMoon, 0f), Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void OverlayBrightnessFactor_StillBlacksOut_WithTheAtmosphericFloorsOff()
    {
        // Floors off leaves moonlight only, so a moonless night lands at glow 0 — under the dark anchor,
        // still fully black. (The pre-existing "true pitch-black" path must survive the retune.)
        Assert.That(NightRadianceMath.OverlayBrightnessFactor(0f, 0f), Is.EqualTo(0f).Within(Tolerance));
    }

    // --- RawOverlayBrightnessFactor / EffectiveMinNightBrightness: UnnaturalDarkness carve-out ---

    [Test]
    public void RawOverlayBrightnessFactor_MatchesOverlayBrightnessFactor_AtZeroFloor()
    {
        // The whole point of splitting the ramp out: RawOverlayBrightnessFactor is
        // OverlayBrightnessFactor with no floor applied, so the two must agree wherever the floor
        // itself is 0.
        Assert.That(
            NightRadianceMath.RawOverlayBrightnessFactor(0.1f),
            Is.EqualTo(NightRadianceMath.OverlayBrightnessFactor(0.1f, 0f)).Within(Tolerance));
    }

    [Test]
    public void EffectiveMinNightBrightness_InactiveUnnaturalDarkness_ReturnsConfiguredFloorUnchanged()
    {
        // Every ordinary reason the sky is dark (a real moonless night, DarkenedSkies, weather) keeps
        // the player's own floor untouched, however it compares to raw.
        Assert.That(
            NightRadianceMath.EffectiveMinNightBrightness(
                unnaturalDarknessActive: false, configuredMinBrightness: 0.50f, rawBrightnessFactor: 0f),
            Is.EqualTo(0.50f).Within(Tolerance));
    }

    [Test]
    public void EffectiveMinNightBrightness_UnnaturalDarkness_FloorDarkerThanEvent_FloorApplies()
    {
        // Early in the 300-tick fade-in, the event's own unfloored glow is still close to the
        // pre-event sky (raw is high) — a moderate floor like Cinematic's 0.50 is the darker of the
        // two here, so it applies exactly as it would on an ordinary night.
        Assert.That(
            NightRadianceMath.EffectiveMinNightBrightness(
                unnaturalDarknessActive: true, configuredMinBrightness: 0.50f, rawBrightnessFactor: 0.9f),
            Is.EqualTo(0.50f).Within(Tolerance));
    }

    [Test]
    public void EffectiveMinNightBrightness_UnnaturalDarkness_FloorBrighterThanEvent_EventWins()
    {
        // Once the fade-in completes, raw collapses toward 0 — a configured floor above that would
        // LIFT the screen back above the event's own darkness, which is exactly what must not happen.
        // The min() clamps to raw instead.
        Assert.That(
            NightRadianceMath.EffectiveMinNightBrightness(
                unnaturalDarknessActive: true, configuredMinBrightness: 0.50f, rawBrightnessFactor: 0.1f),
            Is.EqualTo(0.1f).Within(Tolerance));
    }

    [Test]
    public void EffectiveMinNightBrightness_UnnaturalDarkness_FloorEqualsEvent_EitherValue()
    {
        // The boundary: min() of two equal values is that value either way, so this is really just
        // pinning that the comparison is <=/>= inclusive rather than landing on some other constant.
        Assert.That(
            NightRadianceMath.EffectiveMinNightBrightness(
                unnaturalDarknessActive: true, configuredMinBrightness: 0.3f, rawBrightnessFactor: 0.3f),
            Is.EqualTo(0.3f).Within(Tolerance));
    }

}
