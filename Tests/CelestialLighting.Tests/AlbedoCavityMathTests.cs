namespace CelestialLighting.Tests;

/// <summary>
/// Offline coverage for AlbedoCavityMath.cs — §21, the surface-cloud light cavity. No RimWorld/Unity
/// assembly required, since the file is dependency-free. Complements ApiCompatibilityTests.cs, which
/// only checks that Verse.SnowGrid still exposes the members SurfaceBuildup reads; these tests check
/// that the cavity model itself is right, and in particular that it makes the counterintuitive claim
/// the subsystem exists to make.
/// </summary>
[TestFixture]
public class AlbedoCavityMathTests
{
    // The reference values below were computed in double precision from A = 1/(1 - a_s*a_c); MathF
    // runs the same arithmetic in single precision, so a few 1e-5 of drift is expected and fine.
    private const float Tolerance = 1e-4f;

    // --- The series itself, and its vacuum twin ---

    // Every case pins the sea-level value AND its vacuum counterpart in one sweep, per the
    // convention Source/Vacuum.cs sets out: a regression in either shows up as a diverging pair
    // rather than as a single number quietly matching a stale expectation. The vacuum answer is
    // exactly 1 — no atmosphere, no cloud base, no second wall, no cavity — so it is asserted as an
    // exact equality rather than within a tolerance.
    [TestCase(0.85f, 0.75f, 2.758621f)]   // fresh snow under a thick overcast: the headline number
    [TestCase(0.50f, 0.75f, 1.600000f)]   // settled snow under the same deck
    [TestCase(0.35f, 0.75f, 1.355932f)]   // sand under the same deck — the 1.6 buildup generalization
    [TestCase(0.20f, 0.75f, 1.176471f)]   // bare ground under the same deck: the baseline
    [TestCase(0.85f, 0.10f, 1.092896f)]   // fresh snow under a CLEAR sky: almost nothing
    [TestCase(0.20f, 0.10f, 1.020408f)]   // bare ground under a clear sky
    public void Amplification_MatchesTheGeometricSeries_AndCollapsesToOneInVacuum(
        float surfaceAlbedo, float cloudAlbedo, float expected)
    {
        Assert.That(
            AlbedoCavityMath.Amplification(surfaceAlbedo, cloudAlbedo, false),
            Is.EqualTo(expected).Within(Tolerance),
            "A = 1/(1 - a_surface * a_cloud) at sea level");

        Assert.That(
            AlbedoCavityMath.Amplification(surfaceAlbedo, cloudAlbedo, true),
            Is.EqualTo(1f),
            "vacuum has no cloud base to close the cavity, so A is exactly 1");
    }

    [Test]
    public void Amplification_StaysFinite_AtThePhysicallyUnreachableLimit()
    {
        // Two perfect mirrors facing each other diverge. Nothing shipped can reach it (our worst
        // case is 0.85 * 0.75 = 0.6375), but a modded WeatherCloudDeck.opacity of 1 next to some
        // future albedo of 1 must produce a large number rather than an infinity that would
        // propagate into SkyTarget.glow as a NaN. What is asserted is that it is bounded, not what
        // the bound's value is — MaxCavityProduct is a guard, not a tuning.
        float amplification = AlbedoCavityMath.Amplification(1f, 1f, false);

        Assert.That(float.IsFinite(amplification), Is.True);
        Assert.That(amplification, Is.EqualTo(1f / (1f - AlbedoCavityMath.MaxCavityProduct)).Within(Tolerance));
    }

    [Test]
    public void Amplification_ShippedWorstCase_NeverReachesTheGuard()
    {
        // The guard above is only honest if it never binds in practice, because a binding clamp
        // would silently flatten the top of the ramp. Fresh snow under a full overcast is the
        // brightest pair the shipped constants can produce.
        float worstCase = AlbedoCavityMath.FreshSnowAlbedo * AlbedoCavityMath.OvercastCloudAlbedo;

        Assert.That(worstCase, Is.LessThan(AlbedoCavityMath.MaxCavityProduct));
    }

    // --- The regression pin: no buildup changes nothing ---

    // THE most important test in the file. The mod's brightness anchors (IlluminanceMath's lux
    // table, §7's starlight/airglow floors) were read off measurements taken over ordinary ground,
    // so the bare-ground cavity is already inside them; §21 must therefore be a ratio against bare
    // ground and not raw A, or it would double-count and brighten a mud-and-grass map that nothing
    // had changed.
    //
    // Asserted as an EXACT equality, not within a tolerance, and across the whole cloud range: the
    // numerator and denominator are literally the same expression when the surface is bare, so this
    // is a structural property rather than a number that happens to land near 1. If it ever needs a
    // tolerance, the ratio has been replaced by something else and the pin has stopped meaning what
    // it was written to mean.
    [TestCase(0.00f)]
    [TestCase(0.25f)]
    [TestCase(0.50f)]
    [TestCase(0.75f)]
    [TestCase(1.00f)]
    public void CavityGain_IsExactlyOne_OverBareGround_UnderEveryDeck(float cloudOpacity)
    {
        float cloudAlbedo = AlbedoCavityMath.CloudBaseAlbedo(cloudOpacity);

        Assert.That(
            AlbedoCavityMath.CavityGain(
                AlbedoCavityMath.BareGroundAlbedo, AlbedoCavityMath.BareGroundAlbedo,
                cloudAlbedo, false),
            Is.EqualTo(1f),
            "bare ground must reproduce pre-§21 behaviour bit-identically at sea level");

        Assert.That(
            AlbedoCavityMath.CavityGain(
                AlbedoCavityMath.BareGroundAlbedo, AlbedoCavityMath.BareGroundAlbedo,
                cloudAlbedo, true),
            Is.EqualTo(1f),
            "and in vacuum, where there is no cavity to gain from in the first place");
    }

    [Test]
    public void CavityGain_ZeroBuildupDepth_IsExactlyOne_ThroughTheWholeChain()
    {
        // The pin above starts from an albedo; this one starts where the live adapter starts, from a
        // buildup depth of 0, so the depth->albedo ramp is inside the regression rather than beside
        // it. A ramp whose zero end drifted off BareGroundAlbedo would brighten every bare map in
        // the game and no test that began at the albedo would notice.
        float surfaceAlbedo = AlbedoCavityMath.BuildupSurfaceAlbedo(
            0f,
            AlbedoCavityMath.BareGroundAlbedo,
            AlbedoCavityMath.SettledSnowAlbedo,
            AlbedoCavityMath.FreshSnowAlbedo);

        Assert.That(surfaceAlbedo, Is.EqualTo(AlbedoCavityMath.BareGroundAlbedo));

        float gain = AlbedoCavityMath.CavityGain(
            surfaceAlbedo, AlbedoCavityMath.BareGroundAlbedo,
            AlbedoCavityMath.CloudBaseAlbedo(1f), false);

        Assert.That(gain, Is.EqualTo(1f));

        // And the consumer that actually touches SkyTarget.glow leaves it untouched, which is the
        // form the regression takes on screen. Uses §7's shipped default floor (starlight + airglow
        // + a full moon) so this is the real quantity, not an arbitrary one.
        float floor = NightRadianceMath.DefaultStarlightGlow
                      + NightRadianceMath.DefaultAirglowGlow
                      + NightRadianceMath.DefaultMaxMoonlightGlow;

        Assert.That(AlbedoCavityMath.AmplifiedGlow(floor, gain), Is.EqualTo(floor));
    }

    // --- Monotonicity in both walls ---

    [Test]
    public void CavityGain_IsMonotonicallyNonDecreasing_InSurfaceAlbedo()
    {
        // Swept under a full overcast, where the effect is largest and a non-monotonic segment would
        // be most visible. Non-DECREASING rather than strictly increasing, so the statement still
        // holds at the bare-ground end where the gain is pinned at exactly 1.
        float cloudAlbedo = AlbedoCavityMath.CloudBaseAlbedo(1f);
        float previous = 0f;

        for (int step = 0; step <= 100; step++)
        {
            float surfaceAlbedo = step / 100f;
            float gain = AlbedoCavityMath.CavityGain(
                surfaceAlbedo, AlbedoCavityMath.BareGroundAlbedo, cloudAlbedo, false);

            Assert.That(gain, Is.GreaterThanOrEqualTo(previous),
                $"gain fell as surface albedo rose to {surfaceAlbedo}");
            previous = gain;
        }
    }

    [Test]
    public void CavityGain_IsMonotonicallyNonDecreasing_InCloudAlbedo()
    {
        // The formal statement of "the cavity needs both walls": raising the cloud albedo raises
        // both the numerator and the denominator, but raises the larger numerator faster, so a
        // snow-covered map keeps gaining as the deck thickens.
        float previous = 0f;

        for (int step = 0; step <= 100; step++)
        {
            float gain = AlbedoCavityMath.CavityGain(
                AlbedoCavityMath.FreshSnowAlbedo,
                AlbedoCavityMath.BareGroundAlbedo,
                AlbedoCavityMath.CloudBaseAlbedo(step / 100f),
                false);

            Assert.That(gain, Is.GreaterThanOrEqualTo(previous),
                $"gain fell as cloud opacity rose to {step / 100f}");
            previous = gain;
        }
    }

    // --- The headline: the inversion ---

    [Test]
    public void SnowyOvercast_IsBrighterThanSnowyClearSky_InDiffuseLight()
    {
        // THE claim §21 exists to make, and the one nothing else in the mod could produce: the same
        // snowfield is brighter in diffuse light under a thick overcast than under a clear sky,
        // because a clear sky has no reflector overhead to close the cavity.
        //
        // Stated in COMPOSED terms rather than in raw gain, deliberately. §21 lifts SkyTarget.glow
        // and §13 dims the colour channel, and it is their product that reaches the screen — so a
        // test on gain alone would assert something the player never sees, and would pass even if
        // §13's dimming were heavy enough to eat the whole effect. Both halves are the shipped
        // functions with shipped defaults; nothing here is a stand-in.
        float snowy = DiffuseBrightness(AlbedoCavityMath.FreshSnowAlbedo, cloudOpacity: 1f, precipitationRate: 0f);
        float snowyClear = DiffuseBrightness(AlbedoCavityMath.FreshSnowAlbedo, cloudOpacity: 0f, precipitationRate: 0f);

        Assert.That(snowy, Is.GreaterThan(snowyClear),
            "fresh snow under a thick overcast must out-brighten fresh snow under a clear sky");

        // And it is not marginal — the deck has to be able to survive §13's own dimming by a wide
        // margin for the effect to read as anything but noise on screen.
        Assert.That(snowy / snowyClear, Is.GreaterThan(1.5f));
    }

    [Test]
    public void SnowyOvercast_StillInverts_UnderAFullBlizzard()
    {
        // The worst case for the inversion: a blizzard is the heaviest §13 dimming the mod applies,
        // so it is the deck most able to cancel the cavity it creates. If the inversion survives
        // here it survives everywhere, and a future retune of MaxDimming that broke it would be
        // caught by this rather than in play.
        float blizzard = DiffuseBrightness(AlbedoCavityMath.FreshSnowAlbedo, cloudOpacity: 1f, precipitationRate: 1f);
        float snowyClear = DiffuseBrightness(AlbedoCavityMath.FreshSnowAlbedo, cloudOpacity: 0f, precipitationRate: 0f);

        Assert.That(blizzard, Is.GreaterThan(snowyClear));
    }

    [Test]
    public void BareGround_DoesNotInvert_TheDeckJustDarkensIt()
    {
        // The control, and the reason the test above is about SNOW rather than about overcasts. With
        // no buildup the cavity gain is exactly 1, so §13's dimming is unopposed and an overcast is
        // simply darker than a clear sky — which is what the mod did before §21 and must keep doing.
        // Without this case the inversion test would pass just as well from a bug that brightened
        // every overcast map.
        float bareOvercast = DiffuseBrightness(AlbedoCavityMath.BareGroundAlbedo, cloudOpacity: 1f, precipitationRate: 0f);
        float bareClear = DiffuseBrightness(AlbedoCavityMath.BareGroundAlbedo, cloudOpacity: 0f, precipitationRate: 0f);

        Assert.That(bareOvercast, Is.LessThan(bareClear));
    }

    // --- The other wall: a clear sky over snow buys almost nothing ---

    [Test]
    public void ClearSkyOverFreshSnow_GainsUnderTenPercent()
    {
        // The negative half of the claim, and the half that stops §21 from degenerating into "snowy
        // maps are brighter". A clear sky backscatters ~0.10, so even snow's 0.85 has nothing to
        // bounce against: the gain is ~7%, under the 10% bound the issue states.
        float gain = AlbedoCavityMath.CavityGain(
            AlbedoCavityMath.FreshSnowAlbedo,
            AlbedoCavityMath.BareGroundAlbedo,
            AlbedoCavityMath.CloudBaseAlbedo(0f),
            false);

        Assert.That(gain, Is.EqualTo(1.071038f).Within(Tolerance));
        Assert.That(gain, Is.LessThan(1.10f));
        Assert.That(gain, Is.GreaterThan(1f), "it is small, but it is not nothing");
    }

    [Test]
    public void FreshSnowUnderOvercast_BuysAboutTwoAndAThirdTimes()
    {
        // The positive half, pinned against the issue's own table (2.76 / 1.18 == ~2.3x). Kept
        // beside the clear-sky case above so the two ends of the claim are read together.
        float gain = AlbedoCavityMath.CavityGain(
            AlbedoCavityMath.FreshSnowAlbedo,
            AlbedoCavityMath.BareGroundAlbedo,
            AlbedoCavityMath.CloudBaseAlbedo(1f),
            false);

        Assert.That(gain, Is.EqualTo(2.344828f).Within(Tolerance));
    }

    // --- Cloud base albedo ---

    [TestCase(0.00f, 0.100f)]
    [TestCase(0.25f, 0.2625f)]
    [TestCase(0.50f, 0.425f)]
    [TestCase(1.00f, 0.750f)]
    [TestCase(-1.0f, 0.100f)]   // clamped: a negative opacity is still a clear sky
    [TestCase(2.00f, 0.750f)]   // clamped: nothing is more overcast than overcast
    public void CloudBaseAlbedo_RampsBetweenRayleighAndStratus(float cloudOpacity, float expected)
    {
        Assert.That(
            AlbedoCavityMath.CloudBaseAlbedo(cloudOpacity),
            Is.EqualTo(expected).Within(Tolerance));
    }

    [Test]
    public void CloudBaseAlbedo_AtZero_IsBackscatterAndNotZero()
    {
        // Worth its own case because getting this wrong is silent: an opacity of 0 means a CLEAR
        // sky, which still backscatters, not "no sky at all". Zeroing it here would make the clear
        // arm of the inversion test exactly 1.0 and quietly turn the "under 10%" bound into "under
        // 0%" — the claim would still pass while meaning something else.
        Assert.That(AlbedoCavityMath.CloudBaseAlbedo(0f), Is.EqualTo(AlbedoCavityMath.ClearSkyAlbedo));
        Assert.That(AlbedoCavityMath.ClearSkyAlbedo, Is.GreaterThan(0f));
    }

    // --- Effective cloud opacity: §22's continuous fraction vs §13's discrete classifier (issue #100) ---

    [TestCase(0.00f)]
    [TestCase(0.27f)]   // mid-range: the value that would have been invisible to §21 before #100
    [TestCase(1.00f)]
    public void EffectiveCloudOpacity_OnClearWeather_ReadsTheCloudCoverFraction(float cloudCoverFraction)
    {
        // The headline case the issue is about: on Clear, §13's own opacity is irrelevant (it is
        // always exactly 0 for Clear by construction — see WeatherDimmingMath's census table) and
        // §22's continuous fraction is what the cavity should see instead, whatever §13 happens to
        // report.
        Assert.That(
            AlbedoCavityMath.EffectiveCloudOpacity(0f, weatherIsClear: true, cloudCoverFraction),
            Is.EqualTo(cloudCoverFraction));
    }

    [TestCase(0.00f)]
    [TestCase(0.55f)]
    [TestCase(1.00f)]
    public void EffectiveCloudOpacity_OnNonClearWeather_IgnoresCloudCoverAndReadsTheWeatherOpacity(
        float weatherOpacity)
    {
        // Any real weather (Overcast, Rain, a modded palette) must keep reading §13's classifier
        // exactly as before #100 — the substitution only ever applies to the one weather §13
        // abstains on. A stale or nonsensical cloud-cover fraction here (0.9, deliberately far from
        // weatherOpacity) must be completely ignored, not blended in.
        Assert.That(
            AlbedoCavityMath.EffectiveCloudOpacity(weatherOpacity, weatherIsClear: false, cloudCoverFraction: 0.9f),
            Is.EqualTo(weatherOpacity));
    }

    [Test]
    public void EffectiveCloudOpacity_FeatureOff_FallsBackToTheExactPreIssue100Reading()
    {
        // §22's own feature gate (CelestialLightingFeatures.CloudCover) already makes
        // CloudCoverClock.FractionForMap return exactly 0 when off — the adapter passes that 0
        // straight through as `cloudCoverFraction`. On a Clear map that must reproduce precisely
        // what §13 itself already reported for Clear (also exactly 0), so "feature off" is
        // bit-identical to the discrete pre-#100 behaviour with no special case in this function.
        float preIssue100Reading = AlbedoCavityMath.EffectiveCloudOpacity(
            weatherOpacity: 0f, weatherIsClear: false, cloudCoverFraction: 0f);
        float featureOffReading = AlbedoCavityMath.EffectiveCloudOpacity(
            weatherOpacity: 0f, weatherIsClear: true, cloudCoverFraction: 0f);

        Assert.That(featureOffReading, Is.EqualTo(preIssue100Reading));
        Assert.That(featureOffReading, Is.EqualTo(0f));
    }

    // --- Depth to albedo ---

    [TestCase(0.000f, 0.200f)]   // bare
    [TestCase(0.125f, 0.350f)]   // half a dusting: the ground is still showing through
    [TestCase(0.250f, 0.500f)]   // the cover/age knee, at vanilla's own Dusting/Thin boundary
    [TestCase(0.625f, 0.675f)]   // halfway through the settled->fresh segment
    [TestCase(1.000f, 0.850f)]   // just buried
    [TestCase(-1.00f, 0.200f)]   // clamped
    [TestCase(2.000f, 0.850f)]   // clamped: SnowGrid.MaxDepth is 1
    public void BuildupSurfaceAlbedo_RampsThroughCoverThenAge(float meanDepth, float expected)
    {
        Assert.That(
            AlbedoCavityMath.BuildupSurfaceAlbedo(
                meanDepth,
                AlbedoCavityMath.BareGroundAlbedo,
                AlbedoCavityMath.SettledSnowAlbedo,
                AlbedoCavityMath.FreshSnowAlbedo),
            Is.EqualTo(expected).Within(Tolerance));
    }

    [Test]
    public void BuildupSurfaceAlbedo_IsMonotonic_AcrossTheKnee()
    {
        // The two segments meet at ShallowBuildupDepth and a sign error in either would show up as a
        // step or a dip there rather than as a wrong endpoint — both endpoints are pinned above and
        // would still pass.
        float previous = 0f;

        for (int step = 0; step <= 200; step++)
        {
            float albedo = AlbedoCavityMath.BuildupSurfaceAlbedo(
                step / 200f,
                AlbedoCavityMath.BareGroundAlbedo,
                AlbedoCavityMath.SettledSnowAlbedo,
                AlbedoCavityMath.FreshSnowAlbedo);

            Assert.That(albedo, Is.GreaterThanOrEqualTo(previous));
            previous = albedo;
        }
    }

    [Test]
    public void BuildupSurfaceAlbedo_TakesItsAlbedosAsArguments_SoSandNeedsNoSecondRamp()
    {
        // The whole point of keying the core on albedo rather than on snow depth: Odyssey's
        // Map.sandGrid is a SnowGrid-shaped sibling, and wiring it up must be a different constant
        // at the call site, not a different function here. Driving the same ramp with SandAlbedo
        // proves that structurally.
        float sandy = AlbedoCavityMath.BuildupSurfaceAlbedo(
            1f,
            AlbedoCavityMath.BareGroundAlbedo,
            AlbedoCavityMath.BareGroundAlbedo,
            AlbedoCavityMath.SandAlbedo);

        Assert.That(sandy, Is.EqualTo(AlbedoCavityMath.SandAlbedo));

        // And sand does produce a real, much smaller, cavity — the generalization is not a no-op.
        float sandGain = AlbedoCavityMath.CavityGain(
            sandy, AlbedoCavityMath.BareGroundAlbedo, AlbedoCavityMath.CloudBaseAlbedo(1f), false);
        float snowGain = AlbedoCavityMath.CavityGain(
            AlbedoCavityMath.FreshSnowAlbedo, AlbedoCavityMath.BareGroundAlbedo,
            AlbedoCavityMath.CloudBaseAlbedo(1f), false);

        Assert.That(sandGain, Is.GreaterThan(1f));
        Assert.That(sandGain, Is.LessThan(snowGain));
    }

    // --- Combining more than one cover ---

    [Test]
    public void CombinedSurfaceAlbedo_IsTheIdentity_WhenOnlyOneCoverIsPresent()
    {
        // The common case, and the one every pre-Odyssey map and every desert map without a
        // freak snowfall lands in: exactly one of the two grids is ever nonzero, so combining must
        // reproduce that one ramp exactly rather than pulling it toward bare ground.
        Assert.That(
            AlbedoCavityMath.CombinedSurfaceAlbedo(AlbedoCavityMath.FreshSnowAlbedo, AlbedoCavityMath.BareGroundAlbedo),
            Is.EqualTo(AlbedoCavityMath.FreshSnowAlbedo));

        Assert.That(
            AlbedoCavityMath.CombinedSurfaceAlbedo(AlbedoCavityMath.BareGroundAlbedo, AlbedoCavityMath.SandAlbedo),
            Is.EqualTo(AlbedoCavityMath.SandAlbedo));
    }

    [Test]
    public void CombinedSurfaceAlbedo_TakesTheStrongerCover_NotTheSum()
    {
        // A map reading nonzero on both grids at once (a dusted desert) must not brighten past
        // what its more dominant single cover would produce on its own — the ground is buried
        // under one thing or the other, never both stacked.
        float combined = AlbedoCavityMath.CombinedSurfaceAlbedo(
            AlbedoCavityMath.FreshSnowAlbedo, AlbedoCavityMath.SandAlbedo);

        Assert.That(combined, Is.EqualTo(AlbedoCavityMath.FreshSnowAlbedo));
        Assert.That(combined, Is.LessThan(AlbedoCavityMath.FreshSnowAlbedo + AlbedoCavityMath.SandAlbedo));
    }

    [Test]
    public void CombinedSurfaceAlbedo_IsBareGround_WhenNeitherCoverIsPresent()
    {
        Assert.That(
            AlbedoCavityMath.CombinedSurfaceAlbedo(AlbedoCavityMath.BareGroundAlbedo, AlbedoCavityMath.BareGroundAlbedo),
            Is.EqualTo(AlbedoCavityMath.BareGroundAlbedo));
    }

    // --- The glow consumer ---

    [TestCase(0.00f, 2.344828f, 0.000000f)]   // black stays black: there is nothing to amplify
    [TestCase(0.04f, 2.344828f, 0.093793f)]   // a moonless snowy overcast night: the floors, lifted
    [TestCase(0.19f, 2.344828f, 0.445517f)]   // §7's full default floor under a full moon
    [TestCase(0.60f, 2.344828f, 1.000000f)]   // clamped back into RimWorld's 0..1 glow range
    [TestCase(0.19f, 1.000000f, 0.190000f)]   // gain 1: the input, untouched
    public void AmplifiedGlow_ScalesAndClampsBackIntoRange(float glow, float gain, float expected)
    {
        Assert.That(AlbedoCavityMath.AmplifiedGlow(glow, gain), Is.EqualTo(expected).Within(Tolerance));
    }

    [Test]
    public void AmplifiedGlow_StaysBelowThePlantGrowthThreshold_OnTheDefaultFloor()
    {
        // Scope guard, not physics. §7 writes SkyTarget.glow, which IS gameplay-visible —
        // GlowGrid.GroundGlowAt feeds PlantProperties.growMinGlow (0.51), solar panels and pawn
        // psych-glow — so §21 amplifying that floor could in principle make crops grow at night on a
        // snowy map. It does not: the brightest reachable floor (starlight + airglow + a full moon)
        // amplified by the largest reachable gain lands at 0.446, comfortably under 0.51.
        //
        // This is a bound the shipped CONSTANTS happen to give us rather than one the code enforces,
        // which is exactly why it is pinned: raising MaxMoonlightGlow or the snow albedo far enough
        // would cross it silently, and the symptom in play would be crops growing in the dark rather
        // than anything that looks like a lighting bug.
        float floor = NightRadianceMath.DefaultStarlightGlow
                      + NightRadianceMath.DefaultAirglowGlow
                      + NightRadianceMath.DefaultMaxMoonlightGlow;

        float gain = AlbedoCavityMath.CavityGain(
            AlbedoCavityMath.FreshSnowAlbedo,
            AlbedoCavityMath.BareGroundAlbedo,
            AlbedoCavityMath.CloudBaseAlbedo(1f),
            false);

        Assert.That(AlbedoCavityMath.AmplifiedGlow(floor, gain), Is.LessThan(0.51f));
    }

    // --- The daytime consumer: giving §13's dimming back over snow ---

    [Test]
    public void RecoveredDimming_LeavesBareGroundUntouched_UnderEveryWeather()
    {
        // The daytime regression pin, and the counterpart to the night-floor one above. §21's
        // daytime arm runs inside WeatherDimming.DimmingFor, which every non-snowy map on every save
        // goes through — so "bare ground is bit-identical" has to hold for the dimming itself, not
        // just for the gain that feeds it. A gain of exactly 1 makes the composition the identity
        // algebraically: (1 - d) * 1 = (1 - d).
        foreach (float rate in new[] { 0f, 0.5f, 1f, 1.6f })
        {
            float dimming = WeatherDimmingMath.DimmingFraction(
                1f, 0f, rate, 0f, WeatherDimmingMath.DefaultMaxDimming);

            Assert.That(
                AlbedoCavityMath.RecoveredDimming(dimming, 1f),
                Is.EqualTo(dimming),
                $"bare ground must not change §13's dimming at snowRate {rate}");
        }
    }

    [TestCase(0.18f, 1.000000f, 0.180000f)]   // bare ground, dry overcast: §13 unopposed
    [TestCase(0.18f, 1.152542f, 0.054915f)]   // sand: a real but partial recovery
    [TestCase(0.18f, 1.360000f, 0.000000f)]   // settled snow: fully recovered, clamped at the ceiling
    [TestCase(0.18f, 2.344828f, 0.000000f)]   // fresh snow, dry overcast
    [TestCase(0.255f, 2.344828f, 0.000000f)]  // fresh snow, full blizzard: still fully recovered
    [TestCase(0.00f, 2.344828f, 0.000000f)]   // clear sky: nothing to recover, and none is invented
    public void RecoveredDimming_ComposesExactly_AndNeverGoesNegative(
        float dimming, float cavityGain, float expected)
    {
        Assert.That(
            AlbedoCavityMath.RecoveredDimming(dimming, cavityGain),
            Is.EqualTo(expected).Within(Tolerance));
    }

    [Test]
    public void RecoveredDimming_NeverAsksTheOverlayToBrighten()
    {
        // The clamp is the RENDERER's ceiling, not a taste call: SkyColorSet.sky is assigned straight
        // to MatBases.LightOverlay.color, a multiply whose brightest vanilla value is Clear's (1,1,1)
        // == "do not darken at all". A negative dimming would ask that material to brighten, which it
        // cannot express — SkyTintFactor would return a value above 1 and Patch_WeatherDimming would
        // scale the palette past its own maximum.
        //
        // Swept across the whole reachable space rather than spot-checked, because the failure mode
        // is an overbright sky at one corner of it rather than a wrong number everywhere.
        for (int d = 0; d <= 20; d++)
        {
            for (int g = 0; g <= 20; g++)
            {
                float recovered = AlbedoCavityMath.RecoveredDimming(d / 20f, 1f + g * 0.15f);

                Assert.That(recovered, Is.InRange(0f, 1f));
                Assert.That(WeatherDimmingMath.SkyTintFactor(recovered), Is.LessThanOrEqualTo(1f),
                    "a recovered dimming must never scale the sky palette above vanilla's maximum");
            }
        }
    }

    [Test]
    public void RecoveredDimming_IsMonotonicallyNonIncreasing_InCavityGain()
    {
        // More snow can only ever give more light back, never less. A sign error inside the
        // composition would show up here as a ramp running the wrong way while every endpoint above
        // still passed, because both endpoints happen to sit on clamps.
        float dimming = WeatherDimmingMath.DimmingFraction(
            1f, 0f, 0f, 0f, WeatherDimmingMath.DefaultMaxDimming);
        float previous = 1f;

        for (int step = 0; step <= 100; step++)
        {
            float recovered = AlbedoCavityMath.RecoveredDimming(dimming, 1f + step / 100f);

            Assert.That(recovered, Is.LessThanOrEqualTo(previous));
            previous = recovered;
        }
    }

    [Test]
    public void SnowyOvercastDaylight_RendersAsBrightAsAClearDay()
    {
        // What the DAYTIME half actually delivers on screen, stated as the player experiences it:
        // an overcast that refuses to feel dim. This is the reported phenomenon — "brighter with snow
        // on the ground" — and before §21 a snowy overcast rendered at §13's full 18% darkening,
        // indistinguishable from the same overcast over mud.
        //
        // Equality with a clear day rather than excess over it is the renderer's ceiling, not a
        // modelling choice: see RecoveredDimming_NeverAsksTheOverlayToBrighten. The physically larger
        // claim — snowy overcast brighter than snowy CLEAR sky — is expressible only in the unclamped
        // diffuse model (SnowyOvercast_IsBrighterThanSnowyClearSky_InDiffuseLight above) and on §7's
        // night floor, which has headroom where the daytime colour channel does not.
        float snowyOvercastTint = WeatherDimmingMath.SkyTintFactor(
            AlbedoCavityMath.RecoveredDimming(
                WeatherDimmingMath.DimmingFraction(1f, 0f, 0f, 0f, WeatherDimmingMath.DefaultMaxDimming),
                2.344828f));

        float clearDayTint = WeatherDimmingMath.SkyTintFactor(
            WeatherDimmingMath.DimmingFraction(0f, 0f, 0f, 0f, WeatherDimmingMath.DefaultMaxDimming));

        Assert.That(snowyOvercastTint, Is.EqualTo(clearDayTint).Within(Tolerance));

        // And the control: the same overcast over bare ground still darkens by §13's full amount.
        float bareOvercastTint = WeatherDimmingMath.SkyTintFactor(
            AlbedoCavityMath.RecoveredDimming(
                WeatherDimmingMath.DimmingFraction(1f, 0f, 0f, 0f, WeatherDimmingMath.DefaultMaxDimming),
                1f));

        Assert.That(bareOvercastTint, Is.EqualTo(0.82f).Within(Tolerance));
        Assert.That(bareOvercastTint, Is.LessThan(snowyOvercastTint));
    }

    [Test]
    public void SnowDoesNotRestoreShadowContrast_OnlyBrightness()
    {
        // The asymmetry, pinned so it reads as intent rather than as a missed call site. §13's
        // shadow softening is driven by CloudOpacityFor, NOT by the recovered dimming — so a snowy
        // overcast is bright AND flat, which together are the whiteout that makes terrain hard to
        // read in snow. If someone later routes ShadowContrastFactor through the recovered value,
        // this fails and tells them why it was not.
        float softening = WeatherDimmingMath.ShadowContrastFactor(1f, WeatherDimmingMath.DefaultMaxDimming);

        Assert.That(softening, Is.EqualTo(1f - WeatherDimmingMath.MaxShadowSoftening).Within(Tolerance));
        Assert.That(softening, Is.LessThan(0.2f),
            "a full deck must keep flattening shadows however much light the cavity gives back");
    }

    // --- helpers ---

    // What the screen actually shows for the diffuse half: §21's gain on the glow channel times
    // §13's tint on the colour channel, both through the shipped functions with shipped defaults.
    // Expressed relative to bare ground under a clear sky, so 1.0 means "exactly what the mod
    // rendered before §21" and the numbers read as multiples of that.
    private static float DiffuseBrightness(float surfaceAlbedo, float cloudOpacity, float precipitationRate)
    {
        float gain = AlbedoCavityMath.CavityGain(
            surfaceAlbedo,
            AlbedoCavityMath.BareGroundAlbedo,
            AlbedoCavityMath.CloudBaseAlbedo(cloudOpacity),
            false);

        float dimming = WeatherDimmingMath.DimmingFraction(
            cloudOpacity, 0f, precipitationRate, 0f, WeatherDimmingMath.DefaultMaxDimming);

        return gain * WeatherDimmingMath.SkyTintFactor(dimming);
    }
}
