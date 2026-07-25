namespace CelestialLighting.Tests;

// Offline coverage for the pure weather-dimming core (Source/WeatherDimmingMath.cs), linked into
// this project via <Compile Include> so these exercise the exact code that ships.
//
// The census rows below carry the LITERAL values from vanilla's XML
// (Data/{Core,Odyssey,Anomaly}/Defs/WeatherDefs/Weathers.xml), which makes this fixture do double
// duty: it tests the formula AND serves as the executable record of what vanilla actually ships.
// If a RimWorld update repalettes a weather, the API-compat tests will still pass (the fields all
// exist) but these rows will not — which is exactly the signal we want.
[TestFixture]
public class WeatherDimmingMathTests
{
    private const float Tolerance = 1e-4f;

    // --- Luminance ---

    [Test]
    public void Luminance_OfWhiteIsOne()
    {
        Assert.That(WeatherDimmingMath.Luminance(1f, 1f, 1f), Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void Luminance_WeightsGreenMostAndBlueLeast()
    {
        // Rec. 709: a pure-green palette must read far brighter than a pure-blue one. This is what
        // makes a chromatic modded palette classify the way an eye would judge it.
        float green = WeatherDimmingMath.Luminance(0f, 1f, 0f);
        float red = WeatherDimmingMath.Luminance(1f, 0f, 0f);
        float blue = WeatherDimmingMath.Luminance(0f, 0f, 1f);
        Assert.That(green, Is.GreaterThan(red));
        Assert.That(red, Is.GreaterThan(blue));
    }

    // --- CloudOpacity: the zero set ---
    //
    // Every one of these is a weather we must NOT dim. Clear/Windy because they are clear; Orbit,
    // Underground, Undercave and MetalHell because they are not weather at all; UnnaturalDarkness
    // because its darkness is owned by GameCondition_UnnaturalDarkness's LerpDarken and stacking a
    // second multiply on a gameplay-critical Anomaly event would be wrong (DESIGN.md §13, R5).

    [TestCase(1f, 1f, 1f, 1.25f, TestName = "CloudOpacity_Zero_Clear")]
    [TestCase(1f, 1f, 1f, 1.25f, TestName = "CloudOpacity_Zero_Windy")]
    [TestCase(1f, 1f, 1f, 1.25f, TestName = "CloudOpacity_Zero_Orbit")]
    [TestCase(0.3f, 0.4f, 0.4f, 1.25f, TestName = "CloudOpacity_Zero_Underground")]
    [TestCase(0.3f, 0.4f, 0.4f, 1.25f, TestName = "CloudOpacity_Zero_Undercave")]
    [TestCase(0.4f, 0.5f, 0.5f, 1.25f, TestName = "CloudOpacity_Zero_MetalHell")]
    [TestCase(0.482f, 0.603f, 0.682f, 1.25f, TestName = "CloudOpacity_Zero_UnnaturalDarkness")]
    public void CloudOpacity_IsZeroForClearAndNonWeather(float r, float g, float b, float saturation)
    {
        Assert.That(WeatherDimmingMath.CloudOpacity(r, g, b, saturation),
            Is.EqualTo(0f).Within(Tolerance));
    }

    // The guard property, stated machine-checkably. Four of the seven rows above have a FULL
    // luminance deficit — a luminance-only rule would dim caves and the metal hell into blackness.
    // Only the product spares them, and it does so purely on the saturation term.
    [TestCase(0.3f, 0.4f, 0.4f, 1.25f, TestName = "ProductGuard_Underground")]
    [TestCase(0.4f, 0.5f, 0.5f, 1.25f, TestName = "ProductGuard_MetalHell")]
    [TestCase(0.482f, 0.603f, 0.682f, 1.25f, TestName = "ProductGuard_UnnaturalDarkness")]
    public void CloudOpacity_ProductIsWhatSparesDarkNonWeatherPalettes(
        float r, float g, float b, float saturation)
    {
        // Dark enough that luminance alone would call it fully overcast...
        Assert.That(WeatherDimmingMath.LuminanceDeficit(WeatherDimmingMath.Luminance(r, g, b)),
            Is.EqualTo(1f).Within(Tolerance));
        // ...but it keeps the clear family's saturation, so the product is 0.
        Assert.That(WeatherDimmingMath.SaturationDeficit(saturation), Is.EqualTo(0f).Within(Tolerance));
        Assert.That(WeatherDimmingMath.CloudOpacity(r, g, b, saturation),
            Is.EqualTo(0f).Within(Tolerance));
    }

    // And it must hold even with precipitation forced on — proving the AND, not just that one
    // factor happens to be zero in isolation. A modded cave weather that somehow reported rain
    // must still not dim.
    [TestCase(0.3f, 0.4f, 0.4f, 1.25f)]
    [TestCase(0.4f, 0.5f, 0.5f, 1.25f)]
    [TestCase(1f, 1f, 1f, 1.25f)]
    public void DimmingFraction_IsZeroForZeroOpacityEvenWithHeavyPrecipitation(
        float r, float g, float b, float saturation)
    {
        float opacity = WeatherDimmingMath.CloudOpacity(r, g, b, saturation);
        float dimming = WeatherDimmingMath.DimmingFraction(
            opacity, rainRate: 1f, snowRate: 1.5f, sandRate: 1.6f,
            maxDimming: WeatherDimmingMath.DefaultMaxDimming);
        Assert.That(dimming, Is.EqualTo(0f).Within(Tolerance));
    }

    // --- CloudOpacity: the full set ---
    //
    // The twelve palette-B weathers all share sky (0.8,0.8,0.8) / saturation 0.9, plus Anomaly's
    // two gloom weathers which are darker and flatter still and must saturate at 1, not overshoot.

    [TestCase(0.8f, 0.8f, 0.8f, 0.9f, TestName = "CloudOpacity_Full_OvercastWetPalette")]
    [TestCase(0.482f, 0.603f, 0.682f, 0.75f, TestName = "CloudOpacity_Full_GrayPall")]
    [TestCase(0.482f, 0.603f, 0.682f, 0.5f, TestName = "CloudOpacity_Full_UnnaturalFog")]
    [TestCase(0f, 0f, 0f, 0f, TestName = "CloudOpacity_Full_BeyondVanillaClampsNotOvershoots")]
    public void CloudOpacity_IsOneForCloudDecks(float r, float g, float b, float saturation)
    {
        Assert.That(WeatherDimmingMath.CloudOpacity(r, g, b, saturation),
            Is.EqualTo(1f).Within(Tolerance));
    }

    // --- The intensity ladder ---
    //
    // Expected values at DefaultMaxDimming (0.30) with a full deck:
    //   dry (rate 0)     -> 0.6 * 0.30                                  = 0.180
    //   rain      (1.0)  -> lerp(0.18, 0.30, 1.0/1.6 = 0.625)           = 0.255
    //   snowHard  (1.2)  -> lerp(0.18, 0.30, 0.75)                      = 0.270
    //   blizzard  (1.5)  -> lerp(0.18, 0.30, 0.9375)                    = 0.2925
    //   sandstorm (1.6)  -> lerp(0.18, 0.30, 1.0)                       = 0.300

    [TestCase(0f, 0f, 0f, 0.180f, TestName = "Ladder_DryDeck_FogOvercastDryThunderstorm")]
    [TestCase(1f, 0f, 0f, 0.255f, TestName = "Ladder_Rain")]
    [TestCase(1f, 1.2f, 0f, 0.270f, TestName = "Ladder_SnowHard")]
    [TestCase(1f, 1.5f, 0f, 0.2925f, TestName = "Ladder_Blizzard")]
    [TestCase(0f, 0f, 1.6f, 0.300f, TestName = "Ladder_Sandstorm")]
    public void DimmingFraction_MatchesTheVanillaIntensityLadder(
        float rainRate, float snowRate, float sandRate, float expected)
    {
        float dimming = WeatherDimmingMath.DimmingFraction(
            cloudOpacity: 1f, rainRate, snowRate, sandRate,
            maxDimming: WeatherDimmingMath.DefaultMaxDimming);
        Assert.That(dimming, Is.EqualTo(expected).Within(Tolerance));
    }

    [Test]
    public void DimmingFraction_RisesStrictlyWithPrecipitationIntensity()
    {
        float[] rates = { 0f, 0.5f, 1f, 1.2f, 1.5f, 1.6f };
        float previous = -1f;
        foreach (float rate in rates)
        {
            float dimming = WeatherDimmingMath.DimmingFraction(
                cloudOpacity: 1f, rainRate: rate, snowRate: 0f, sandRate: 0f,
                maxDimming: WeatherDimmingMath.DefaultMaxDimming);
            Assert.That(dimming, Is.GreaterThan(previous), $"not strictly increasing at rate={rate}");
            previous = dimming;
        }
    }

    [Test]
    public void DimmingFraction_NeverExceedsMaxDimming()
    {
        // Even with every rate pushed far past anything vanilla ships.
        float dimming = WeatherDimmingMath.DimmingFraction(
            cloudOpacity: 1f, rainRate: 99f, snowRate: 99f, sandRate: 99f,
            maxDimming: WeatherDimmingMath.DefaultMaxDimming);
        Assert.That(dimming, Is.EqualTo(WeatherDimmingMath.DefaultMaxDimming).Within(Tolerance));
    }

    [Test]
    public void ObscurationIntensity_TakesTheHeaviestRateNotTheSum()
    {
        // A hypothetical modded weather that both rains and sandstorms must read as one heavy
        // storm, not a double-strength one.
        float both = WeatherDimmingMath.ObscurationIntensity(1f, 0f, 1.6f);
        float sandOnly = WeatherDimmingMath.ObscurationIntensity(0f, 0f, 1.6f);
        Assert.That(both, Is.EqualTo(sandOnly).Within(Tolerance));
        Assert.That(both, Is.EqualTo(1f).Within(Tolerance));
    }

    // --- The slider-at-zero no-op contract ---

    [TestCase(0f, 0f, 0f)]
    [TestCase(1f, 0f, 0f)]
    [TestCase(0f, 0f, 1.6f)]
    public void DimmingFraction_IsZeroWhenMaxDimmingIsZero(float rainRate, float snowRate, float sandRate)
    {
        float dimming = WeatherDimmingMath.DimmingFraction(
            cloudOpacity: 1f, rainRate, snowRate, sandRate, maxDimming: 0f);
        Assert.That(dimming, Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void ShadowContrastFactor_IsOneWhenMaxDimmingIsZero()
    {
        // The whole subsystem, shadows included, must vanish at slider 0.
        Assert.That(WeatherDimmingMath.ShadowContrastFactor(1f, 0f), Is.EqualTo(1f).Within(Tolerance));
    }

    // --- BlendOpacity across weather transitions ---

    [TestCase(0f, 1f, 0f, 0f, TestName = "Blend_AtStartIsOutgoingWeather")]
    [TestCase(0f, 1f, 1f, 1f, TestName = "Blend_AtEndIsIncomingWeather")]
    [TestCase(0f, 1f, 0.5f, 0.5f, TestName = "Blend_AtMidpoint")]
    [TestCase(1f, 0f, 0.25f, 0.75f, TestName = "Blend_ClearingUp")]
    [TestCase(0f, 1f, -1f, 0f, TestName = "Blend_ClampsBelowZero")]
    [TestCase(0f, 1f, 2f, 1f, TestName = "Blend_ClampsAboveOne")]
    public void BlendOpacity_LerpsAcrossTheTransition(float last, float cur, float t, float expected)
    {
        Assert.That(WeatherDimmingMath.BlendOpacity(last, cur, t), Is.EqualTo(expected).Within(Tolerance));
    }

    // --- SkyTintFactor / ApparentGlow ---

    [Test]
    public void SkyTintFactor_IsIdentityWithNoDimming()
    {
        Assert.That(WeatherDimmingMath.SkyTintFactor(0f), Is.EqualTo(1f).Within(Tolerance));
    }

    [TestCase(0.255f, 0.745f)]
    [TestCase(0.30f, 0.70f)]
    [TestCase(1f, 0f)]
    [TestCase(2f, 0f)]    // defensive: clamps rather than going negative
    [TestCase(-1f, 1f)]   // defensive: clamps rather than brightening
    public void SkyTintFactor_IsOneMinusDimmingClamped(float dimming, float expected)
    {
        Assert.That(WeatherDimmingMath.SkyTintFactor(dimming), Is.EqualTo(expected).Within(Tolerance));
    }

    [Test]
    public void ApparentGlow_IsUnchangedWithNoDimming()
    {
        Assert.That(WeatherDimmingMath.ApparentGlow(0.4f, 0f), Is.EqualTo(0.4f).Within(Tolerance));
    }

    [Test]
    public void ApparentGlow_PreservesBlack()
    {
        // A pitch-black night cannot be made darker; §9 must not divide by or drift below zero.
        Assert.That(WeatherDimmingMath.ApparentGlow(0f, 0.3f), Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void ApparentGlow_IsDarkerUnderWeatherWhichIsTheWholePointOfSubsystem9()
    {
        // The assertion that §9's original promise now holds: the same physical night reads dimmer
        // under a rainy sky than a clear one, so it lands further up the Purkinje ramp.
        const float nightGlow = 0.12f;
        float clear = WeatherDimmingMath.ApparentGlow(nightGlow, 0f);
        float rainy = WeatherDimmingMath.ApparentGlow(nightGlow, 0.255f);
        Assert.That(rainy, Is.LessThan(clear));
        Assert.That(PurkinjeMath.PurkinjeFactor(rainy),
            Is.GreaterThan(PurkinjeMath.PurkinjeFactor(clear)));
    }

    [Test]
    public void ApparentGlow_StaysInRangeAcrossASweep()
    {
        for (int i = 0; i <= 20; i++)
        {
            float glow = i * 0.05f;
            float apparent = WeatherDimmingMath.ApparentGlow(glow, 0.3f);
            Assert.That(apparent, Is.InRange(0f, 1f));
            Assert.That(apparent, Is.LessThanOrEqualTo(glow + Tolerance),
                "dimming must never brighten");
        }
    }

    // --- ShadowContrastFactor ---

    [Test]
    public void ShadowContrastFactor_IsIdentityUnderAClearSky()
    {
        Assert.That(WeatherDimmingMath.ShadowContrastFactor(0f, WeatherDimmingMath.DefaultMaxDimming),
            Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void ShadowContrastFactor_LeavesOnlyAFaintShadowUnderAFullDeck()
    {
        Assert.That(WeatherDimmingMath.ShadowContrastFactor(1f, WeatherDimmingMath.DefaultMaxDimming),
            Is.EqualTo(1f - WeatherDimmingMath.MaxShadowSoftening).Within(Tolerance));
    }

    [Test]
    public void ShadowContrastFactor_FallsMonotonicallyAsTheDeckThickens()
    {
        float previous = float.MaxValue;
        for (int i = 0; i <= 10; i++)
        {
            float opacity = i * 0.1f;
            float factor = WeatherDimmingMath.ShadowContrastFactor(
                opacity, WeatherDimmingMath.DefaultMaxDimming);
            Assert.That(factor, Is.LessThanOrEqualTo(previous + Tolerance));
            Assert.That(factor, Is.InRange(0f, 1f));
            previous = factor;
        }
    }

    [Test]
    public void ShadowContrastFactor_NeverGoesNegativeAtHighSliderValues()
    {
        // The slider tops out at 0.5, well above the 0.30 default; the internal clamp is what stops
        // that from driving shadow alpha below zero.
        Assert.That(WeatherDimmingMath.ShadowContrastFactor(1f, 0.5f),
            Is.EqualTo(1f - WeatherDimmingMath.MaxShadowSoftening).Within(Tolerance));
    }
}
