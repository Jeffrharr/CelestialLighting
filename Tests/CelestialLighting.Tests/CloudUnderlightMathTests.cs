namespace CelestialLighting.Tests;

// Offline coverage for the pure §23 cloud-base underlighting core (Source/CloudUnderlightMath.cs,
// issue #88 option 1), linked into this project via <Compile Include> so these exercise the exact
// code that ships. See that file's header for the geometry derivation this pins.
[TestFixture]
public class CloudUnderlightMathTests
{
    private const float Tolerance = 1e-3f;

    // --- ShadowEntryDepressionDegrees: the geometry itself ---
    //
    // Values below are independently computed from theta = arccos(R / (R + h)), R = 6371 km, and
    // double as the executable record of issue #88's own worked table: a 1 km low stratus enters
    // shadow barely past the horizon, a 4 km altocumulus a little past that, and a 10 km cirrus
    // lingers noticeably longer — the ordering the whole subsystem exists to reproduce.

    [TestCase(0f, 0f, TestName = "ShadowEntry_GroundDeck_IsZero")]
    [TestCase(1000f, 1.01509f, TestName = "ShadowEntry_LowStratus_1km")]
    [TestCase(4000f, 2.02979f, TestName = "ShadowEntry_MidAltocumulus_4km")]
    [TestCase(10000f, 3.20812f, TestName = "ShadowEntry_HighCirrus_10km")]
    public void ShadowEntryDepressionDegrees_MatchesSecantGeometry(float altitudeMetres, float expectedDegrees)
    {
        Assert.That(CloudUnderlightMath.ShadowEntryDepressionDegrees(altitudeMetres),
            Is.EqualTo(expectedDegrees).Within(Tolerance));
    }

    [Test]
    public void ShadowEntryDepressionDegrees_NegativeAltitudeAlsoZero()
    {
        // A def could hand in a negative override only via a bug; the classifier's own escape hatch
        // (WeatherCloudDeck.OverridesAltitude) guards against sending a negative sentinel through in
        // the first place, but the geometry itself should not misbehave if one arrives anyway.
        Assert.That(CloudUnderlightMath.ShadowEntryDepressionDegrees(-500f), Is.EqualTo(0f));
    }

    [Test]
    public void ShadowEntryDepressionDegrees_IsMonotonicIncreasingInAltitude()
    {
        float low = CloudUnderlightMath.ShadowEntryDepressionDegrees(1000f);
        float mid = CloudUnderlightMath.ShadowEntryDepressionDegrees(4000f);
        float high = CloudUnderlightMath.ShadowEntryDepressionDegrees(10000f);
        Assert.That(mid, Is.GreaterThan(low));
        Assert.That(high, Is.GreaterThan(mid));
    }

    // --- GlowPhase ---

    [Test]
    public void GlowPhase_ZeroAtHorizonAndAtShadowEntry()
    {
        float shadowEntry = CloudUnderlightMath.ShadowEntryDepressionDegrees(10000f);
        Assert.That(CloudUnderlightMath.GlowPhase(0f, shadowEntry), Is.EqualTo(0f).Within(Tolerance));
        Assert.That(CloudUnderlightMath.GlowPhase(-shadowEntry, shadowEntry),
            Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void GlowPhase_PeaksNearWindowMidpoint()
    {
        float shadowEntry = CloudUnderlightMath.ShadowEntryDepressionDegrees(10000f);
        float atMidpoint = CloudUnderlightMath.GlowPhase(-shadowEntry / 2f, shadowEntry);
        Assert.That(atMidpoint, Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void GlowPhase_AboveHorizonIsZero()
    {
        float shadowEntry = CloudUnderlightMath.ShadowEntryDepressionDegrees(10000f);
        Assert.That(CloudUnderlightMath.GlowPhase(5f, shadowEntry), Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void GlowPhase_GroundDeckHasNoWindowAtAll()
    {
        // shadowEntryDegrees <= 0 (a ground-hugging deck) has nothing to glow in — the degenerate
        // zero-width-window guard, not a coincidental zero.
        Assert.That(CloudUnderlightMath.GlowPhase(-1f, 0f), Is.EqualTo(0f));
    }

    // --- ShadowSuppressionPhase: monotonicity is the whole claim ---

    [Test]
    public void ShadowSuppressionPhase_IsMonotonicNonDecreasingBelowHorizon()
    {
        float shadowEntry = CloudUnderlightMath.ShadowEntryDepressionDegrees(1000f);
        float previous = -1f;
        for (float elevation = 0f; elevation >= -7f; elevation -= 0.25f)
        {
            float suppression = CloudUnderlightMath.ShadowSuppressionPhase(elevation, shadowEntry);
            Assert.That(suppression, Is.GreaterThanOrEqualTo(previous));
            previous = suppression;
        }
    }

    [Test]
    public void ShadowSuppressionPhase_ReachesFullAtNightFadeFloor()
    {
        float shadowEntry = CloudUnderlightMath.ShadowEntryDepressionDegrees(1000f);
        float atFadeFloor = CloudUnderlightMath.ShadowSuppressionPhase(
            SkyColorTemperature.NightFadeFloorDegrees, shadowEntry);
        Assert.That(atFadeFloor, Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void ShadowSuppressionPhase_DegenerateWindowReturnsZeroNotNaN()
    {
        // Reached when WarmthMultiplier's fade-floor cap pulls an extreme override altitude down to
        // exactly -NightFadeFloorDegrees: suppressionStart and the fade-floor bound coincide, and the
        // plain (v - a) / (b - a) division would otherwise be a 0/0 NaN.
        float degenerate = -SkyColorTemperature.NightFadeFloorDegrees;
        Assert.That(CloudUnderlightMath.ShadowSuppressionPhase(-3f, degenerate), Is.EqualTo(0f));
    }

    // --- WarmthMultiplier: the composed, consumer-facing result ---

    [TestCase(true, TestName = "WarmthMultiplier_Vacuum_IsNoOp")]
    public void WarmthMultiplier_VacuumIsAlwaysOne(bool inVacuum)
    {
        Assert.That(CloudUnderlightMath.WarmthMultiplier(-3f, 4000f, 1f, inVacuum), Is.EqualTo(1f));
    }

    [TestCase(0f, TestName = "WarmthMultiplier_ZeroOpacity_IsOne")]
    [TestCase(-1f, TestName = "WarmthMultiplier_NegativeOpacity_IsOne")]
    public void WarmthMultiplier_NoCloudDeckIsRegressionPin(float opacity)
    {
        // Issue #88's own invariant: zero cloud deck must be bit-identical to §8's clear-sky
        // behaviour, at any elevation or altitude.
        Assert.That(CloudUnderlightMath.WarmthMultiplier(-3f, 4000f, opacity, inVacuum: false),
            Is.EqualTo(1f));
    }

    [TestCase(0f, TestName = "WarmthMultiplier_SunAtHorizon_IsOne")]
    [TestCase(10f, TestName = "WarmthMultiplier_SunWellAboveHorizon_IsOne")]
    public void WarmthMultiplier_AboveHorizonIsAlwaysOne(float elevationDegrees)
    {
        // The whole mechanism is clouds staying lit after the GROUND stops being lit — nothing here
        // should ever move while the sun itself is still up.
        Assert.That(
            CloudUnderlightMath.WarmthMultiplier(elevationDegrees, 10000f, 1f, inVacuum: false),
            Is.EqualTo(1f));
    }

    [Test]
    public void WarmthMultiplier_OppositeSignsFromSameElevationAndOpacity()
    {
        // The headline behaviour issue #88 asks to pin: at a fixed depression below the horizon and a
        // fixed (full) opacity, a high deck must READ WARMER than baseline and a low deck must read
        // COOLER than baseline. Values independently derived from the same theta = arccos(R/(R+h))
        // geometry (see this file's header).
        float highDeck = CloudUnderlightMath.WarmthMultiplier(-2f, 10000f, 1f, inVacuum: false);
        float lowDeck = CloudUnderlightMath.WarmthMultiplier(-2f, 1000f, 1f, inVacuum: false);

        Assert.That(highDeck, Is.GreaterThan(1f), "a thin high deck should still be glowing here");
        Assert.That(lowDeck, Is.LessThan(1f), "a thick low deck should already be suppressing here");
        Assert.That(highDeck, Is.EqualTo(1.5634f).Within(Tolerance));
        Assert.That(lowDeck, Is.EqualTo(0.8024f).Within(Tolerance));
    }

    [Test]
    public void WarmthMultiplier_ThickLowDeckMonotonicallyReducesWarmthAcrossItsSuppressionTail()
    {
        // "A thick low deck monotonically reduces sunset warmth" — issue #88's ruined-sunset case.
        // Sampled from the deck's own shadow-entry point onward: everything ABOVE that point is the
        // (brief, physically honest) glow phase this same low deck still gets right at the horizon,
        // which is not the claim under test here — see ShadowSuppressionPhase's own monotonicity test
        // for that half stated structurally rather than by sampling.
        float shadowEntry = CloudUnderlightMath.ShadowEntryDepressionDegrees(1000f);
        float previous = 1f;
        for (float belowHorizon = shadowEntry; belowHorizon <= 6f; belowHorizon += 0.25f)
        {
            float multiplier = CloudUnderlightMath.WarmthMultiplier(-belowHorizon, 1000f, 1f, inVacuum: false);
            Assert.That(multiplier, Is.LessThanOrEqualTo(previous));
            previous = multiplier;
        }
    }

    [Test]
    public void WarmthMultiplier_GroundDeckSuppressesSmoothlyWithNoWindow()
    {
        // altitudeMetres 0 has no glow phase at all (ShadowEntryDepressionDegrees is 0), so the
        // multiplier should fall smoothly from 1 at the horizon to 0 at the night fade floor with no
        // discontinuity — this is also the test that would catch the degenerate-range NaN this file's
        // InverseLerpClamped guard exists for.
        Assert.That(CloudUnderlightMath.WarmthMultiplier(0f, 0f, 1f, inVacuum: false),
            Is.EqualTo(1f).Within(Tolerance));
        Assert.That(CloudUnderlightMath.WarmthMultiplier(-3f, 0f, 1f, inVacuum: false),
            Is.EqualTo(0.5f).Within(Tolerance));
        Assert.That(
            CloudUnderlightMath.WarmthMultiplier(
                SkyColorTemperature.NightFadeFloorDegrees, 0f, 1f, inVacuum: false),
            Is.EqualTo(0f).Within(Tolerance));
        Assert.That(CloudUnderlightMath.WarmthMultiplier(-3f, 0f, 1f, inVacuum: false),
            Is.Not.NaN);
    }

    [Test]
    public void WarmthMultiplier_ExtremeAltitudeOverrideCapsAtNightFadeFloorWithNoSuppressionTail()
    {
        // A pathologically high override (50 km — no real troposphere cloud) pushes the raw geometry
        // past §8's own fade floor. WarmthMultiplier caps the window there, which means the ENTIRE
        // below-horizon tail is glow phase and there is no suppression tail left at all — the
        // degenerate-window case ShadowSuppressionPhase's own test pins directly.
        float atMidpoint = CloudUnderlightMath.WarmthMultiplier(-3f, 50000f, 1f, inVacuum: false);
        Assert.That(atMidpoint, Is.EqualTo(1.6f).Within(Tolerance));

        float atFadeFloor = CloudUnderlightMath.WarmthMultiplier(
            SkyColorTemperature.NightFadeFloorDegrees, 50000f, 1f, inVacuum: false);
        Assert.That(atFadeFloor, Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void WarmthMultiplier_ScalesLinearlyWithOpacity()
    {
        float fullOpacity = CloudUnderlightMath.WarmthMultiplier(-2f, 1000f, 1f, inVacuum: false);
        float halfOpacity = CloudUnderlightMath.WarmthMultiplier(-2f, 1000f, 0.5f, inVacuum: false);
        // multiplier = 1 + opacity * deviation, so halving opacity must halve the deviation from 1.
        Assert.That(halfOpacity - 1f, Is.EqualTo((fullOpacity - 1f) / 2f).Within(Tolerance));
    }

    [Test]
    public void WarmthMultiplier_NeverGoesNegative()
    {
        for (float elevation = 0f; elevation >= -8f; elevation -= 0.5f)
        {
            float multiplier = CloudUnderlightMath.WarmthMultiplier(elevation, 500f, 1f, inVacuum: false);
            Assert.That(multiplier, Is.GreaterThanOrEqualTo(0f));
        }
    }

    // --- LayerStrength: §23b's additive lane (issue #88 option 2) ---

    // Issue #88's headline invariant for the spatial lane, and the reason it shares GlowPhase with the
    // flat one: the warm contribution has to PEAK BELOW THE HORIZON. Above it the ground is still lit
    // directly and there is no underlighting to draw; below shadow entry the deck has gone dark too.
    [Test]
    public void LayerStrength_IsZeroWithTheSunUpAndZeroOnceTheDeckIsInShadow()
    {
        float entry = CloudUnderlightMath.ShadowEntryDepressionDegrees(4000f);

        Assert.That(CloudUnderlightMath.LayerStrength(5f, 4000f, 0.5f, inVacuum: false),
            Is.EqualTo(0f).Within(Tolerance));
        Assert.That(CloudUnderlightMath.LayerStrength(0f, 4000f, 0.5f, inVacuum: false),
            Is.EqualTo(0f).Within(Tolerance));
        Assert.That(CloudUnderlightMath.LayerStrength(-entry, 4000f, 0.5f, inVacuum: false),
            Is.EqualTo(0f).Within(Tolerance));
        Assert.That(CloudUnderlightMath.LayerStrength(-entry * 0.5f, 4000f, 0.5f, inVacuum: false),
            Is.EqualTo(CloudUnderlightMath.LayerAmplitude).Within(Tolerance));
    }

    // A high deck stays lit longer, so at any given depression past a low deck's shadow entry the
    // high one is still drawing and the low one is not. This is issue #88's altitude table restated
    // for the additive lane — and it is what makes "a low overcast kills the sunset" fall out here as
    // silence rather than as a special case.
    [Test]
    public void LayerStrength_AHighDeckStillDrawsWhereALowOneHasGoneDark()
    {
        float lowDeck = CloudUnderlightMath.LayerStrength(-2.5f, 1000f, 0.5f, inVacuum: false);
        float highDeck = CloudUnderlightMath.LayerStrength(-2.5f, 10000f, 0.5f, inVacuum: false);

        Assert.That(lowDeck, Is.EqualTo(0f).Within(Tolerance));
        Assert.That(highDeck, Is.GreaterThan(0f));
    }

    // Coverage enters this lane through the FIELD, not through the strength — see LayerStrength's own
    // header. Anything above zero must therefore give the same strength, or a solid overcast would
    // come out as the strongest spatial case when it is precisely the one with no structure in it.
    [Test]
    public void LayerStrength_DoesNotScaleWithCoverage()
    {
        float quarter = CloudUnderlightMath.LayerStrength(-1f, 4000f, 0.25f, inVacuum: false);
        float full = CloudUnderlightMath.LayerStrength(-1f, 4000f, 1f, inVacuum: false);

        Assert.That(quarter, Is.EqualTo(full).Within(Tolerance));
        Assert.That(quarter, Is.GreaterThan(0f));

        // Zero coverage is still a hard stop, so "off" and "no cloud" both mean no draw call at all.
        Assert.That(CloudUnderlightMath.LayerStrength(-1f, 4000f, 0f, inVacuum: false),
            Is.EqualTo(0f).Within(Tolerance));
    }

    // Vacuum.cs's convention: last parameter, required, and a no-op return before any geometry runs.
    // There is no cloud deck to underlight without air, and no air to redden the light that lit it.
    [Test]
    public void LayerStrength_IsZeroInVacuum()
    {
        Assert.That(CloudUnderlightMath.LayerStrength(-1f, 4000f, 0.5f, inVacuum: true),
            Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void LayerStrength_ScalesLinearlyWithAmplitudeAndIgnoresNonsense()
    {
        float half = CloudUnderlightMath.LayerStrengthWithAmplitude(-1f, 4000f, 0.5f, 0.1f, false);
        float whole = CloudUnderlightMath.LayerStrengthWithAmplitude(-1f, 4000f, 0.5f, 0.2f, false);

        Assert.That(whole, Is.EqualTo(half * 2f).Within(Tolerance));
        Assert.That(CloudUnderlightMath.LayerStrengthWithAmplitude(-1f, 4000f, 0.5f, 0f, false),
            Is.EqualTo(0f));
        Assert.That(CloudUnderlightMath.LayerStrengthWithAmplitude(-1f, 4000f, 0.5f, -1f, false),
            Is.EqualTo(0f));
        Assert.That(CloudUnderlightMath.LayerStrengthWithAmplitude(-1f, 4000f, 0.5f, float.NaN, false),
            Is.EqualTo(0f));
    }
}
