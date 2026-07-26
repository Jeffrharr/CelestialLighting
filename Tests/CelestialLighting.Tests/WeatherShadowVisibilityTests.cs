namespace CelestialLighting.Tests;

/// <summary>
/// §13a: cloudy-day shadows have to be *visible*, not merely computed.
///
/// These assert the composition, not any single function. Every part of the daylight shadow path was
/// already individually correct when the bug was found in play — direction, length, penumbra and
/// §13's own weather attenuation all produced right answers, and the shadow still could not be seen,
/// because vanilla's colors.shadow caps how dark the shader can draw and every non-Clear weather
/// flattens it to 0.92. So the assertions here are written in rendered darkening (what a player
/// perceives) rather than in alpha, which is the units the bug hid in.
///
/// Same shape as §6a's night case, which is why RenderedShadowDarkening lives in the pure core rather
/// than in a patch: it is the only composition that tells "the alpha is right" apart from "you can
/// see a shadow".
/// </summary>
[TestFixture]
public class WeatherShadowVisibilityTests
{
    // Cinematic ships 0.20; Realistic runs §13 at its full default. Both are exercised, because the
    // regression was worse on Realistic and a fix that only helped the shipped preset would be a
    // false pass for exactly the players most likely to notice.
    private const float CinematicMaxDimming = 0.20f;
    private const float RealisticMaxDimming = WeatherDimmingMath.DefaultMaxDimming;

    // Full sun: ShadowIntensityFromElevation has saturated, so alpha is governed by weather alone.
    private static float DaylightAlpha(float cloudOpacity, float maxDimming) =>
        WeatherDimmingMath.ShadowContrastFactor(cloudOpacity, maxDimming);

    private static float DarkeningBefore(float cloudOpacity, float maxDimming) =>
        WeatherDimmingMath.RenderedShadowDarkening(
            DaylightAlpha(cloudOpacity, maxDimming), WeatherDimmingMath.WeatherShadowValue);

    private static float DarkeningAfter(float cloudOpacity, float maxDimming) =>
        WeatherDimmingMath.RenderedShadowDarkening(
            DaylightAlpha(cloudOpacity, maxDimming), WeatherDimmingMath.ClearDayShadowValue);

    // Measured full-deck darkening on vanilla's flat 0.92: 3.5% on Cinematic (9/255, marginal) and
    // 1.2% on Realistic (3/255, gone). Only the Realistic case is truly sub-perceptible, so the bound
    // is per preset rather than one flattering number applied to both.
    [TestCase(CinematicMaxDimming, 0.04f)]
    [TestCase(RealisticMaxDimming, WeatherDimmingMath.PerceptibleDarkening)]
    public void Before_TheFix_AFullDeckRenderedFaintlyOrWorse(float maxDimming, float bound)
    {
        Assert.That(
            DarkeningBefore(1f, maxDimming),
            Is.LessThan(bound),
            "vanilla's flat weather shadow colour was expected to render faint or invisible");
    }

    [TestCase(CinematicMaxDimming)]
    [TestCase(RealisticMaxDimming)]
    public void Before_TheFix_EvenACloudlessNonClearSkyWasCappedFarBelowClearDay(float maxDimming)
    {
        // The structural defect, and the sharper statement of it. Vanilla's 0.92 is a *ceiling*, not
        // an attenuation: with zero cloud attenuation at all, the best a non-Clear sky could ever
        // render was 8% — under a third of Clear's own 28.2% daylight shadow. No amount of correct
        // alpha could reach past it, which is why every part of the daylight path measured right
        // while the screen showed nothing.
        float bestPossible = DarkeningBefore(0f, maxDimming);
        float clearDay = 1f - WeatherDimmingMath.ClearDayShadowValue;

        Assert.That(bestPossible, Is.EqualTo(1f - WeatherDimmingMath.WeatherShadowValue).Within(1e-5f));
        Assert.That(bestPossible, Is.LessThan(clearDay / 3f));
    }

    // NOTE ON THESE OPACITIES. No steady-state vanilla weather produces them: all seven non-Clear
    // weathers ship an identical skyColorsDay, so CloudOpacity is exactly 1.0 for every one of them
    // and 0.0 for Clear. Intermediate values occur only mid-transition (BlendOpacity across
    // WeatherManager's 4000 ticks) and for modded weathers with their own palettes. They are asserted
    // because those are real paths — not because vanilla fog sits at 0.35.
    [TestCase(0.35f, CinematicMaxDimming)]
    [TestCase(0.35f, RealisticMaxDimming)]
    [TestCase(0.6f, CinematicMaxDimming)]
    [TestCase(0.6f, RealisticMaxDimming)]
    public void After_TheFix_PartialCloudKeepsAVisibleShadow(float cloudOpacity, float maxDimming)
    {
        Assert.That(
            DarkeningAfter(cloudOpacity, maxDimming),
            Is.GreaterThan(WeatherDimmingMath.PerceptibleDarkening * 4f),
            $"cloud opacity {cloudOpacity} left the shadow invisible at maxDimming {maxDimming}");
    }

    // The case that actually ships: every non-Clear vanilla weather, which is opacity 1.0.
    [TestCase(CinematicMaxDimming, 0.12f)]
    [TestCase(RealisticMaxDimming, 0.04f)]
    public void After_TheFix_AVanillaCloudyDayIsVisibleAgain(float maxDimming, float expectedAtLeast)
    {
        // Measured 12.2% on Cinematic and 4.2% on Realistic, against 3.5% / 1.2% before. Realistic
        // stays deliberately soft — it runs §13 at full strength — but clears the perceptible floor
        // by more than double, where before it sat below it.
        float darkening = DarkeningAfter(1f, maxDimming);

        Assert.That(darkening, Is.GreaterThan(expectedAtLeast));
        Assert.That(darkening, Is.GreaterThan(WeatherDimmingMath.PerceptibleDarkening * 2f));
        Assert.That(
            darkening,
            Is.GreaterThan(DarkeningBefore(1f, maxDimming) * 3f),
            "the fix should more than triple a vanilla cloudy day's rendered shadow");
    }

    [TestCase(CinematicMaxDimming)]
    [TestCase(RealisticMaxDimming)]
    public void HeavyOvercast_StillAllButErasesShadows(float maxDimming)
    {
        // The fix must NOT turn a blizzard into a sunny day. Under a full deck direct sun really is
        // replaced by diffuse skylight, which is what §13's MaxShadowSoftening was tuned for — so the
        // full-deck case stays far below an unclouded one even after the colour is corrected.
        float fullDeck = DarkeningAfter(1f, maxDimming);
        float unclouded = DarkeningAfter(0f, maxDimming);

        Assert.That(fullDeck, Is.LessThan(unclouded * 0.5f), "a full deck should still gut the shadow");
        Assert.That(unclouded, Is.GreaterThan(0.25f), "an unclouded sun should cast a strong shadow");
    }

    [Test]
    public void TheFix_IsANoOpUnderClear()
    {
        // Substituting Clear's own daytime colour has to leave Clear itself untouched — the patch is
        // defined as "use what vanilla uses on Clear", so on Clear it must change nothing at all.
        Assert.That(
            DarkeningAfter(0f, CinematicMaxDimming),
            Is.EqualTo(1f - WeatherDimmingMath.ClearDayShadowValue).Within(1e-5f));
    }

    [TestCase(CinematicMaxDimming)]
    [TestCase(RealisticMaxDimming)]
    public void ShadowContrast_FallsMonotonicallyWithCloud(float maxDimming)
    {
        // The property vanilla's binary colour made impossible to express: more cloud, less shadow,
        // smoothly. Sampled across the range rather than at the endpoints, since the whole complaint
        // was that intermediate cloud behaved identically to total cloud.
        float previous = float.MaxValue;
        for (int i = 0; i <= 20; i++)
        {
            float darkening = DarkeningAfter(i / 20f, maxDimming);
            Assert.That(darkening, Is.LessThanOrEqualTo(previous), $"darkening rose at cloud {i / 20f}");
            previous = darkening;
        }
    }

    [Test]
    public void RenderedShadowDarkening_InvertsTheShaderLerpAtBothEnds()
    {
        // Anchors for the helper itself: alpha 0 is no shadow whatever the colour, and a black shadow
        // colour at alpha 1 is a total blackout.
        Assert.That(WeatherDimmingMath.RenderedShadowDarkening(0f, 0f), Is.EqualTo(0f).Within(1e-6f));
        Assert.That(WeatherDimmingMath.RenderedShadowDarkening(1f, 0f), Is.EqualTo(1f).Within(1e-6f));
        Assert.That(WeatherDimmingMath.RenderedShadowDarkening(1f, 1f), Is.EqualTo(0f).Within(1e-6f));
    }
}
