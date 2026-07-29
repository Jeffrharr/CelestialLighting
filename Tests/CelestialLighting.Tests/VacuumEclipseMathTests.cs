namespace CelestialLighting.Tests;

/// <summary>
/// Offline coverage for VacuumEclipseMath.cs (§18e, how an eclipse responds on an Odyssey vacuum
/// map).
///
/// Two claims carry the subsystem, and they are the first two fixtures below:
///
///   1. <see cref="VacuumUmbralGlow_IsExactlyTheSharedNightFloor"/> — totality in orbit bottoms out
///      at #30's night floor itself, not at a fraction of it and not at a second constant of our own.
///   2. <see cref="VacuumBrightness_TracksCoveredFraction_MoreCloselyThanSeaLevel"/> — the hardening
///      claim, as a measured comparison rather than an adjective.
///
/// Everything else exists to stop those two passing for the wrong reason. Per the §18 convention in
/// Vacuum.cs, every behavioural case pins the vacuum value and its sea-level twin together, so a
/// regression in either shows up as a diverging pair rather than as one number quietly matching a
/// stale expectation.
/// </summary>
[TestFixture]
public class VacuumEclipseMathTests
{
    private const float Tolerance = 0.0001f;

    private const float SeaLevelStarlight = NightRadianceMath.DefaultStarlightGlow;   // 0.02
    private const float SeaLevelAirglow = NightRadianceMath.DefaultAirglowGlow;       // 0.02
    private const float MaxMoonlight = NightRadianceMath.DefaultMaxMoonlightGlow;     // 0.15

    // A solar eclipse happens at NEW MOON by definition — the moon is between us and the sun, showing
    // us its unlit face — so the moonlight term of the night floor is 0 during any eclipse. Every
    // umbral figure below is therefore evaluated against the new-moon floor, which is the physically
    // reachable one rather than a convenient minimum.
    private static float NightFloor(bool inVacuum) =>
        NightRadianceMath.NightFloorGlow(
            SeaLevelStarlight, SeaLevelAirglow, moonlightGlow: 0f, MaxMoonlight, inVacuum);

    // Vanilla's umbral sky colour, RimWorld.GameCondition_NoSunlight.EclipseSkyColors.sky =
    // (0.482, 0.603, 0.682), as Rec. 709 luma: 0.2126*0.482 + 0.7152*0.603 + 0.0722*0.682 = 0.583.
    //
    // This is the sea-level anchor of the whole subsystem and it is VANILLA'S number, not ours —
    // which is the point. We are not asserting how bright a sea-level total eclipse should be; we are
    // taking RimWorld's own answer (a wan grey at 58% of full sky brightness, i.e. the unshadowed
    // atmosphere scattering light into the umbra) and asking what changes when the atmosphere is
    // removed. ApiCompatibilityTests pins that the field still exists to be read.
    private const float VanillaUmbralSkyBrightness = 0.583f;

    // Vanilla's umbral GLOW target: SkyTarget(0f, EclipseSkyColors, 1f, 0f). A flat zero.
    private const float VanillaUmbralGlow = 0f;

    // A clear daytime sky, i.e. the (1,1,1) `skyColorsDay` both Core's Clear and Odyssey's Orbit
    // ship. The eclipse response is measured as a fraction of whatever the sky was, so this is only
    // the normalisation.
    private const float DaySkyBrightness = 1f;

    private static float UmbralSkyBrightness(bool inVacuum) =>
        VacuumEclipseMath.UmbralSkyBrightness(
            VanillaUmbralSkyBrightness, NightFloor(inVacuum), minNightBrightness: 0f, inVacuum);

    // --- CLAIM 1: the umbral minimum IS the shared night floor ---

    [TestCase(false, 0f)]        // sea level: vanilla's flat-zero umbral glow, passed through
    [TestCase(true, 0.0317f)]    // vacuum: #30's new-moon night floor, exactly
    public void VacuumUmbralGlow_IsExactlyTheSharedNightFloor(bool inVacuum, float expected)
    {
        float floor = NightFloor(inVacuum);
        float umbra = VacuumEclipseMath.UmbralGlow(VanillaUmbralGlow, floor, inVacuum);

        Assert.That(umbra, Is.EqualTo(expected).Within(0.0005f),
            inVacuum
                ? "vacuum totality must bottom out at §18b's night floor"
                : "a sea-level eclipse must keep vanilla's own umbral glow");

        if (!inVacuum)
            return;

        // The load-bearing half: EXACTLY the floor, not a tuned fraction of it. Asserted as an
        // identity against the shared function rather than against the literal above, so that if #30
        // retunes the vacuum night budget this test follows it instead of pinning a stale copy —
        // which is the entire reason the floor is a shared read. #31 binds to the same function from
        // the shadow side, so the two provably agree.
        Assert.That(umbra, Is.EqualTo(floor).Within(float.Epsilon),
            "the vacuum umbral minimum must BE NightRadianceMath.NightFloorGlow, not a fraction of it");
    }

    [Test]
    public void VacuumUmbra_IsBrighterThanVanillasZero_AndThatIsCorrect()
    {
        // Worth pinning because it looks like a regression at a glance: our vacuum umbra is a touch
        // BRIGHTER than vanilla's, whose target glow is a flat 0. Totality in orbit is starlit, not
        // switched off — vanilla's 0 was never physical, and the near-black look comes from the
        // colour channel (claim 2), not from driving gameplay light to nothing.
        Assert.That(
            VacuumEclipseMath.UmbralGlow(VanillaUmbralGlow, NightFloor(inVacuum: true), inVacuum: true),
            Is.GreaterThan(VanillaUmbralGlow));
    }

    [Test]
    public void VacuumUmbra_IsDarkerThanASurfaceNight()
    {
        // The §18e restatement of §18b's design claim: an eclipse cannot be a way to reach a state
        // darker than orbital night, and orbital night is the darkest state the mod produces. So the
        // vacuum umbra must sit below every surface night floor while still being a real, non-zero
        // starlit sky.
        float orbitalUmbra = VacuumEclipseMath.UmbralGlow(
            VanillaUmbralGlow, NightFloor(inVacuum: true), inVacuum: true);

        Assert.That(orbitalUmbra, Is.LessThan(NightFloor(inVacuum: false)));
        Assert.That(orbitalUmbra, Is.GreaterThan(0f));
    }

    // --- CLAIM 2: ingress and egress harden ---

    [Test]
    public void VacuumBrightness_TracksCoveredFraction_MoreCloselyThanSeaLevel()
    {
        // THE QUANTITATIVE FORM OF THE CLAIM. CoverageTrackingError is the mean absolute deviation,
        // over a whole central transit, between how far the sky has actually dimmed and the fraction
        // of the solar disc that is covered. A response driven by disc overlap and nothing else
        // scores 0; every unit of score is light in the umbra that did not come through the sun.
        //
        // At sea level that light is the unshadowed atmosphere scattering in, and it is the dominant
        // term: vanilla's umbra keeps 58% of full sky brightness, so the curve can never deviate less
        // than that from the covered fraction. In vacuum there is nothing to scatter, so the pedestal
        // collapses to whatever the night sky itself supplies.
        const int samples = 400;
        float seaLevelError = VacuumEclipseMath.CoverageTrackingError(
            DaySkyBrightness, UmbralSkyBrightness(inVacuum: false), magnitude: 1f, samples);
        float vacuumError = VacuumEclipseMath.CoverageTrackingError(
            DaySkyBrightness, UmbralSkyBrightness(inVacuum: true), magnitude: 1f, samples);

        Assert.That(vacuumError, Is.LessThan(seaLevelError),
            "the vacuum response must track covered fraction more closely than the sea-level one");

        // Not merely smaller — smaller by a wide, stated margin, so a future change that erodes the
        // effect to a rounding difference fails here rather than passing on a technicality. The ratio
        // is 1 / UmbralSkyBrightnessScale, i.e. ~6x at the shipped night floor; asserting 4x leaves
        // room for the floor to be retuned without leaving room for the claim to become decorative.
        Assert.That(vacuumError * 4f, Is.LessThan(seaLevelError),
            $"vacuum error {vacuumError} must be at least 4x tighter than sea level {seaLevelError}");
    }

    [TestCase(1.0f)]     // dead-central total
    [TestCase(0.99f)]    // just inside the totality plateau
    [TestCase(0.6f)]     // a deep partial
    [TestCase(0.2f)]     // a grazing partial
    public void VacuumTracksCoveredFraction_MoreCloselyAtEveryMagnitude(float magnitude)
    {
        // The hardening is not a property of totality alone — the issue's wording is about ingress
        // and egress, i.e. the partial phases, which are all a grazing eclipse ever has. Swept over
        // magnitude so a change that only fixed the deepest eclipses would fail.
        const int samples = 400;
        float seaLevelError = VacuumEclipseMath.CoverageTrackingError(
            DaySkyBrightness, UmbralSkyBrightness(inVacuum: false), magnitude, samples);
        float vacuumError = VacuumEclipseMath.CoverageTrackingError(
            DaySkyBrightness, UmbralSkyBrightness(inVacuum: true), magnitude, samples);

        Assert.That(vacuumError * 4f, Is.LessThan(seaLevelError),
            $"magnitude {magnitude}: vacuum {vacuumError} vs sea level {seaLevelError}");
    }

    [Test]
    public void CoverageTrackingError_IsZeroForAPerfectDiscOverlapResponse()
    {
        // Calibrates the metric itself: a hypothetical umbra with no light in it at all scores
        // exactly 0, which is what makes every other score readable as "how much light was in the
        // umbra". Without this the comparison above would only be an ordering with no zero point.
        Assert.That(
            VacuumEclipseMath.CoverageTrackingError(
                DaySkyBrightness, umbralSkyBrightness: 0f, magnitude: 1f, samples: 400),
            Is.EqualTo(0f).Within(Tolerance));
    }

    // --- The colour scale, both arms ---

    [TestCase(false, 1f)]          // sea level: an exact identity, so the postfix is a proven no-op
    [TestCase(true, 0.1668f)]      // vacuum: §7a's own glow->screen curve at the night floor
    public void UmbralSkyBrightnessScale_PairsVacuumAgainstSeaLevel(bool inVacuum, float expected)
    {
        Assert.That(
            VacuumEclipseMath.UmbralSkyBrightnessScale(
                NightFloor(inVacuum), minNightBrightness: 0f, inVacuum),
            Is.EqualTo(expected).Within(0.001f));
    }

    [Test]
    public void SeaLevelScale_IsExactlyOne_SoTheAdapterCannotDriftOnSurfaceMaps()
    {
        // Stronger than the Within() above and deliberately separate: the adapter multiplies live
        // Color channels by this, so anything other than a bit-exact 1 would silently retint every
        // eclipse on every planet-surface map in the game. This is the assertion that makes
        // "Patch_EclipseVacuumSky is a no-op outside vacuum" a fact rather than an intention.
        Assert.That(
            VacuumEclipseMath.UmbralSkyBrightnessScale(
                NightFloor(inVacuum: false), minNightBrightness: 0f, inVacuum: false),
            Is.EqualTo(1f));
    }

    [Test]
    public void RaisingMinNightBrightness_LiftsTheVacuumUmbraToo()
    {
        // The player's playability clamp has to reach inside an eclipse, or someone who raised the
        // night floor because true black is hard to navigate would still be blacked out during
        // totality — the one time they cannot wait it out by walking somewhere lit. Riding on
        // OverlayBrightnessFactor gets this for free; the test is here so it stays free.
        float floor = NightFloor(inVacuum: true);
        float unclamped = VacuumEclipseMath.UmbralSkyBrightnessScale(floor, 0f, inVacuum: true);
        float clamped = VacuumEclipseMath.UmbralSkyBrightnessScale(floor, 0.4f, inVacuum: true);

        Assert.That(clamped, Is.GreaterThan(unclamped));
        Assert.That(clamped, Is.EqualTo(0.4f).Within(Tolerance));
    }

    // --- The composed response curve ---

    [TestCase(0f)]
    [TestCase(0.25f)]
    [TestCase(0.5f)]
    [TestCase(1f)]
    public void EclipsedSkyBrightness_MirrorsVanillaLerpDarken(float coverage)
    {
        // Pins that our offline model of the composed result really is vanilla's
        // SkyColorSet.LerpDarken — Lerp(A, Min(A, B), t) — because every comparison above is only
        // meaningful if this reproduces what SkyManager.CurrentSkyTarget will actually do.
        float umbra = UmbralSkyBrightness(inVacuum: true);
        float expected = DaySkyBrightness + (umbra - DaySkyBrightness) * coverage;

        Assert.That(
            VacuumEclipseMath.EclipsedSkyBrightness(coverage, DaySkyBrightness, umbra),
            Is.EqualTo(expected).Within(Tolerance));
    }

    [TestCase(false, 0.583f)]      // sea level: totality still reads as a wan grey
    [TestCase(true, 0.0973f)]      // vacuum: near-black, 0.583 * 0.1668
    public void TotalityBrightness_PairsVacuumAgainstSeaLevel(bool inVacuum, float expected)
    {
        // "Totality goes near-black", as one number per atmosphere. Full coverage, so the composed
        // brightness is the umbral brightness itself.
        Assert.That(
            VacuumEclipseMath.EclipsedSkyBrightness(
                coverage: 1f, DaySkyBrightness, UmbralSkyBrightness(inVacuum)),
            Is.EqualTo(expected).Within(0.002f));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void UncoveredSky_IsUntouchedInBothAtmospheres(bool inVacuum)
    {
        // The response must vanish at zero coverage in both arms, or the mere presence of the
        // condition would dim a sky nothing is in front of. This is what lets §10's ramp subsume
        // vanilla's own in/out transition with no pop, in orbit as on the ground.
        Assert.That(
            VacuumEclipseMath.EclipsedSkyBrightness(
                coverage: 0f, DaySkyBrightness, UmbralSkyBrightness(inVacuum)),
            Is.EqualTo(DaySkyBrightness).Within(Tolerance));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DimmingIsMonotonicInCoverage(bool inVacuum)
    {
        // More of the sun covered can never mean a brighter sky, in either atmosphere. Cheap, and it
        // is the property a sign error in the scale would break first.
        float umbra = UmbralSkyBrightness(inVacuum);
        float previous = -1f;
        for (int i = 0; i <= 100; i++)
        {
            float dimming = VacuumEclipseMath.NormalisedDimming(i / 100f, DaySkyBrightness, umbra);
            Assert.That(dimming, Is.GreaterThanOrEqualTo(previous));
            previous = dimming;
        }
    }

    // --- Guards ---

    [Test]
    public void UmbralGlow_NeverExceedsGlowRange()
    {
        // The floor is a sum of independent sources, so a settings screen with generous sliders could
        // in principle hand us something above 1. Clamped rather than trusted.
        Assert.That(VacuumEclipseMath.UmbralGlow(0f, 5f, inVacuum: true), Is.EqualTo(1f));
        Assert.That(VacuumEclipseMath.UmbralGlow(0f, -1f, inVacuum: true), Is.EqualTo(0f));
    }

    [Test]
    public void NormalisedDimming_HandlesABlackSky()
    {
        // An eclipse whose sky is already at zero brightness has nothing left to dim; report 0 rather
        // than dividing by it. Reachable on a map whose sky the mod has already blacked out.
        Assert.That(
            VacuumEclipseMath.NormalisedDimming(coverage: 1f, ambientSkyBrightness: 0f, umbralSkyBrightness: 0f),
            Is.EqualTo(0f));
    }

    [Test]
    public void CoverageTrackingError_HandlesDegenerateSampleCounts()
    {
        Assert.That(
            VacuumEclipseMath.CoverageTrackingError(DaySkyBrightness, 0.5f, magnitude: 1f, samples: 0),
            Is.EqualTo(0f));
    }
}
