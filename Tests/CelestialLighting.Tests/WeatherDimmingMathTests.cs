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

    // --- PaletteOpacity: the zero set ---
    //
    // Every one of these is a weather we must NOT dim. Clear/Windy because they are clear; Orbit,
    // Underground, Undercave and MetalHell because they are not weather at all; UnnaturalDarkness
    // because its darkness is owned by GameCondition_UnnaturalDarkness's LerpDarken and stacking a
    // second multiply on a gameplay-critical Anomaly event would be wrong (DESIGN.md §13, R5).

    [TestCase(1f, 1f, 1f, 1.25f, TestName = "PaletteOpacity_Zero_Clear")]
    [TestCase(1f, 1f, 1f, 1.25f, TestName = "PaletteOpacity_Zero_Windy")]
    [TestCase(1f, 1f, 1f, 1.25f, TestName = "PaletteOpacity_Zero_Orbit")]
    [TestCase(0.3f, 0.4f, 0.4f, 1.25f, TestName = "PaletteOpacity_Zero_Underground")]
    [TestCase(0.3f, 0.4f, 0.4f, 1.25f, TestName = "PaletteOpacity_Zero_Undercave")]
    [TestCase(0.4f, 0.5f, 0.5f, 1.25f, TestName = "PaletteOpacity_Zero_MetalHell")]
    [TestCase(0.482f, 0.603f, 0.682f, 1.25f, TestName = "PaletteOpacity_Zero_UnnaturalDarkness")]
    public void PaletteOpacity_IsZeroForClearAndNonWeather(float r, float g, float b, float saturation)
    {
        Assert.That(WeatherDimmingMath.PaletteOpacity(r, g, b, saturation),
            Is.EqualTo(0f).Within(Tolerance));
    }

    // The guard property, stated machine-checkably. Four of the seven rows above have a FULL
    // luminance deficit — a luminance-only rule would dim caves and the metal hell into blackness.
    // Only the product spares them, and it does so purely on the saturation term.
    [TestCase(0.3f, 0.4f, 0.4f, 1.25f, TestName = "ProductGuard_Underground")]
    [TestCase(0.4f, 0.5f, 0.5f, 1.25f, TestName = "ProductGuard_MetalHell")]
    [TestCase(0.482f, 0.603f, 0.682f, 1.25f, TestName = "ProductGuard_UnnaturalDarkness")]
    public void PaletteOpacity_ProductIsWhatSparesDarkNonWeatherPalettes(
        float r, float g, float b, float saturation)
    {
        // Dark enough that luminance alone would call it fully overcast...
        Assert.That(WeatherDimmingMath.LuminanceDeficit(WeatherDimmingMath.Luminance(r, g, b)),
            Is.EqualTo(1f).Within(Tolerance));
        // ...but it keeps the clear family's saturation, so the product is 0.
        Assert.That(WeatherDimmingMath.SaturationDeficit(saturation), Is.EqualTo(0f).Within(Tolerance));
        Assert.That(WeatherDimmingMath.PaletteOpacity(r, g, b, saturation),
            Is.EqualTo(0f).Within(Tolerance));
    }

    // A zero-opacity weather stays a no-op no matter how the rates are set, so the clear-sky fast
    // path really is a fast path and DimmingFraction cannot manufacture dimming from precipitation
    // on its own.
    //
    // NOTE what this deliberately no longer claims. It used to read "a modded cave weather that
    // somehow reported rain must still not dim", asserting an AND between palette and precipitation.
    // The modded-weather audit reversed that: precipitation is now independent evidence of a deck (see
    // CloudOpacity_PrecipitationOverridesAnUnconvincingPalette), because a real modded rainstorm
    // shipping a half-clear palette was the more common failure by far. Cave weathers are handled by
    // the map-level guard instead — BiomeHasChangingWeather — which is where the question belongs.
    [TestCase(0.3f, 0.4f, 0.4f, 1.25f)]
    [TestCase(0.4f, 0.5f, 0.5f, 1.25f)]
    [TestCase(1f, 1f, 1f, 1.25f)]
    public void DimmingFraction_IsZeroForZeroOpacityEvenWithHeavyPrecipitation(
        float r, float g, float b, float saturation)
    {
        float opacity = WeatherDimmingMath.PaletteOpacity(r, g, b, saturation);
        float dimming = WeatherDimmingMath.DimmingFraction(
            opacity, rainRate: 1f, snowRate: 1.5f, sandRate: 1.6f,
            maxDimming: WeatherDimmingMath.DefaultMaxDimming);
        Assert.That(dimming, Is.EqualTo(0f).Within(Tolerance));
    }

    // --- PaletteOpacity: the full set ---
    //
    // The twelve palette-B weathers all share sky (0.8,0.8,0.8) / saturation 0.9, plus Anomaly's
    // two gloom weathers which are darker and flatter still and must saturate at 1, not overshoot.

    [TestCase(0.8f, 0.8f, 0.8f, 0.9f, TestName = "PaletteOpacity_Full_OvercastWetPalette")]
    [TestCase(0.482f, 0.603f, 0.682f, 0.75f, TestName = "PaletteOpacity_Full_GrayPall")]
    [TestCase(0.482f, 0.603f, 0.682f, 0.5f, TestName = "PaletteOpacity_Full_UnnaturalFog")]
    [TestCase(0f, 0f, 0f, 0f, TestName = "PaletteOpacity_Full_BeyondVanillaClampsNotOvershoots")]
    public void PaletteOpacity_IsOneForCloudDecks(float r, float g, float b, float saturation)
    {
        Assert.That(WeatherDimmingMath.PaletteOpacity(r, g, b, saturation),
            Is.EqualTo(1f).Within(Tolerance));
    }

    // --- PrecipitationEvidence ---

    [TestCase(0f, 0f, 0f, TestName = "Precipitation_None_DryWeather")]
    public void PrecipitationEvidence_IsZeroWhenNothingFalls(float rain, float snow, float sand)
    {
        Assert.That(WeatherDimmingMath.PrecipitationEvidence(rain, snow, sand),
            Is.EqualTo(0f).Within(Tolerance));
    }

    // Categorical, not proportional: a drizzle and a monsoon both settle the question of WHETHER
    // there is a deck. The 0.05 row is Anomalies Expected's blood fog, the lightest rate in the whole
    // installed census, and it must count exactly as much as vanilla Blizzard's 1.5.
    [TestCase(1f, 0f, 0f, TestName = "Precipitation_Rain")]
    [TestCase(0.05f, 0f, 0f, TestName = "Precipitation_LightestRateInTheCensus")]
    [TestCase(0f, 1.5f, 0f, TestName = "Precipitation_Blizzard")]
    [TestCase(0f, 0f, 1.6f, TestName = "Precipitation_Sandstorm")]
    [TestCase(0f, 0f, 5f, TestName = "Precipitation_BeyondVanillaDoesNotOvershoot")]
    public void PrecipitationEvidence_IsOneForAnyNonzeroRate(float rain, float snow, float sand)
    {
        Assert.That(WeatherDimmingMath.PrecipitationEvidence(rain, snow, sand),
            Is.EqualTo(1f).Within(Tolerance));
    }

    // --- CloudOpacity: the two lines of evidence composed ---

    [Test]
    public void CloudOpacity_PrecipitationOverridesAnUnconvincingPalette()
    {
        // Alpha Biomes' AB_ForsakenRainyNight_Alternate: a rainstorm (rainRate 1.0) whose day palette
        // is only partway to overcast — sky (0.65,0.70,0.75), saturation 1.1. The palette rule alone
        // rates it 0.43, so before the precipitation override a visibly wet sky dimmed as though half-clear.
        float palette = WeatherDimmingMath.PaletteOpacity(0.65f, 0.70f, 0.75f, 1.1f);
        Assert.That(palette, Is.EqualTo(0.4286f).Within(1e-3f), "census value for this def changed");

        float opacity = WeatherDimmingMath.CloudOpacity(
            0.65f, 0.70f, 0.75f, 1.1f, rainRate: 1f, snowRate: 0f, sandRate: 0f);
        Assert.That(opacity, Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void CloudOpacity_PaletteAloneStillCarriesADryDeck()
    {
        // The other direction: fog, overcast and dry thunderstorms have no precipitation at all, so
        // the palette must remain sufficient on its own.
        float opacity = WeatherDimmingMath.CloudOpacity(
            0.8f, 0.8f, 0.8f, 0.9f, rainRate: 0f, snowRate: 0f, sandRate: 0f);
        Assert.That(opacity, Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void CloudOpacity_IsStillZeroForAClearDrySky()
    {
        float opacity = WeatherDimmingMath.CloudOpacity(
            1f, 1f, 1f, 1.25f, rainRate: 0f, snowRate: 0f, sandRate: 0f);
        Assert.That(opacity, Is.EqualTo(0f).Within(Tolerance));
    }

    // Every precipitating VANILLA weather already scores a full 1.00 on the palette rule, which is
    // why adding precipitation as independent evidence is provably a no-op on vanilla content and
    // only ever reaches modded defs. Rows are the literal vanilla palette-B values plus each
    // weather's rates.
    [TestCase(1f, 0f, 0f, TestName = "VanillaNoOp_Rain")]
    [TestCase(1f, 1.2f, 0f, TestName = "VanillaNoOp_SnowHard")]
    [TestCase(1f, 1.5f, 0f, TestName = "VanillaNoOp_Blizzard")]
    [TestCase(0f, 0f, 1.6f, TestName = "VanillaNoOp_Sandstorm")]
    [TestCase(0f, 0f, 0f, TestName = "VanillaNoOp_FogOvercastDryThunderstorm")]
    public void CloudOpacity_MatchesPaletteOpacityForEveryVanillaWeather(
        float rainRate, float snowRate, float sandRate)
    {
        float palette = WeatherDimmingMath.PaletteOpacity(0.8f, 0.8f, 0.8f, 0.9f);
        float opacity = WeatherDimmingMath.CloudOpacity(
            0.8f, 0.8f, 0.8f, 0.9f, rainRate, snowRate, sandRate);
        Assert.That(opacity, Is.EqualTo(palette).Within(Tolerance));
    }

    // --- BiomeHasChangingWeather: the structural guard ---
    //
    // Measured across all 65 biomes in vanilla + 24 installed workshop mods (Tools/WeatherAudit).
    // Read the member's own comment for what this rule does and does not claim: it is NOT a clean
    // partition of skyless from open-air — vanilla's Undercave offers two weathers once inheritance is
    // applied, the same as the open-air Duskwood — it is a rule that covers every biome which could
    // actually dim wrongly.

    [TestCase(0, TestName = "NoClimate_OceanAndLakeListNoWeatherAtAll")]
    [TestCase(1, TestName = "NoClimate_EveryBiomeThatCouldHaveDimmedWronglySitsHere")]
    public void BiomeHasChangingWeather_IsFalseForSkylessBiomes(int weatherChoices)
    {
        Assert.That(WeatherDimmingMath.BiomeHasChangingWeather(weatherChoices), Is.False);
    }

    [TestCase(2, TestName = "Climate_DuskwoodAndAlsoUndercave_seeTheMemberComment")]
    [TestCase(3, TestName = "Climate_AB_RockyCrags_and_DMSE_ImpactCraterBiome")]
    [TestCase(8, TestName = "Climate_Desert")]
    [TestCase(12, TestName = "Climate_BorealForest")]
    [TestCase(15, TestName = "Climate_RG_TemperateGrassland")]
    public void BiomeHasChangingWeather_IsTrueForOpenAirBiomes(int weatherChoices)
    {
        Assert.That(WeatherDimmingMath.BiomeHasChangingWeather(weatherChoices), Is.True);
    }

    // The property that actually makes the guard safe at its boundary, pinned as a formula rather than
    // left in prose: the two skyless biomes that slip past the count rule (Undercave, UV_SpaceUndercave)
    // can only roll `Underground` and `Undercave`, and BOTH classify to exactly 0. If a RimWorld update
    // repalettes either of those weathers, this fails and the guard needs a second condition.
    [TestCase(0.3f, 0.4f, 0.4f, 1.25f, TestName = "BoundarySafe_UndergroundPalette")]
    [TestCase(0.3f, 0.4f, 0.4f, 1.25f, TestName = "BoundarySafe_UndercavePalette")]
    public void CloudOpacity_IsZeroForEveryWeatherASkylessBoundaryBiomeCanRoll(
        float r, float g, float b, float saturation)
    {
        float opacity = WeatherDimmingMath.CloudOpacity(
            r, g, b, saturation, rainRate: 0f, snowRate: 0f, sandRate: 0f);
        Assert.That(opacity, Is.EqualTo(0f).Within(Tolerance),
            "a skyless biome above the weather-count threshold would now dim wrongly");
    }

    // --- The modded census, as a regression fixture ---
    //
    // The defs that motivated the modded-weather audit, with the opacity each must now classify to. These are the
    // literal skyColorsDay values from the mods' own XML; the two that used to misfire are the ones
    // this row set exists to hold down. Cave environments (BMT_Calm, MF_UndergroundWeather) are NOT
    // here, because they are not fixed at this layer at all — their palettes still read as overcast
    // and it is HasSky that spares them, which is the point of splitting the two questions.

    [TestCase(0.65f, 0.70f, 0.75f, 1.1f, 1f, 1f,
        TestName = "Census_AB_ForsakenRainyNight_Alternate_wasWronglyHalfClearAt0_43")]
    [TestCase(0.482f, 0.603f, 0.682f, 0.9f, 1f, 1f,
        TestName = "Census_VEE_PsychicRain")]
    // Note the palette here. This def's XML says sky (255,0,0), and Verse.ParseHelper.ParseColor
    // treats any triple with a component above 1 as 0-255 bytes — so the value §13 actually sees is
    // (1,0,0), pure red, whose Rec.709 luma of 0.213 reads as a full luminance deficit. Worth pinning:
    // the original audit script read the raw XML floats and concluded this storm dimmed 0%.
    [TestCase(1f, 0f, 0f, 0.9f, 1f, 1f,
        TestName = "Census_VPEH_Bloodstorm_byteColourParsesToPureRedNotSuperWhite")]
    [TestCase(0.95f, 0.90f, 0.80f, 1.0f, 0f, 0.34497f,
        TestName = "Census_VEE_Inferno_heatHazeStaysMildNotFull")]
    [TestCase(1f, 1f, 1f, 1.25f, 0f, 0f,
        TestName = "Census_AB_PetalStorms_clearSkyWithAnOverlayStaysClear")]
    public void CloudOpacity_HoldsTheModdedCensus(
        float r, float g, float b, float saturation, float rainRate, float expected)
    {
        float opacity = WeatherDimmingMath.CloudOpacity(
            r, g, b, saturation, rainRate, snowRate: 0f, sandRate: 0f);
        Assert.That(opacity, Is.EqualTo(expected).Within(1e-3f));
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
