namespace CelestialLighting.Tests;

/// <summary>
/// Offline edge-case coverage for OzoneTwilightMath.cs (subsystem 19, polar night blue) — no
/// RimWorld/Unity assembly required, since the file has no dependency on either. Complements
/// ApiCompatibilityTests.cs (which only checks vanilla members still exist); these check that our
/// own absorption math is correct, that it is actually VISIBLE through the multiply overlay, and
/// that the reachability gate is correctness-preserving rather than merely plausible.
/// </summary>
[TestFixture]
public class OzoneTwilightMathTests
{
    private const float Tolerance = 0.0005f;

    // Vanilla's Clear night sky colour, quoted from NightDesaturationMath's dead-end note. Used by
    // the visibility tests below to reproduce what the multiply overlay actually does to the ground.
    private const float VanillaNightR = 0.482f;
    private const float VanillaNightG = 0.603f;
    private const float VanillaNightB = 0.682f;

    // The adapter's sky blend (Patch_PolarNightBlue.SkyBlend). Mirrored here rather than referenced
    // because the adapter needs UnityEngine and cannot be linked into this project.
    private const float SkyBlend = 0.45f;

    // --- BandStrength: the trapezoid envelope ---

    [TestCase(45f, 0f)] // broad daylight
    [TestCase(0f, 0f)] // geometric horizon: the warm terms still own this
    [TestCase(-0.83f, 0f)] // refraction horizon, still nothing
    [TestCase(-4f, 0f)] // onset, exclusive
    [TestCase(-5.6f, 0.5f)] // midpoint of the fade-in
    [TestCase(-7.2f, 1f)] // the measurement anchor: full strength
    [TestCase(-9f, 1f)] // plateau
    [TestCase(-12f, 1f)] // plateau end, still full
    [TestCase(-15f, 0.5f)] // midpoint of the fade-out
    [TestCase(-18f, 0f)] // astronomical twilight ends
    [TestCase(-30f, 0f)] // deep night: §7 owns the sky
    public void BandStrength_MatchesExpected(float elevation, float expected)
    {
        Assert.That(OzoneTwilightMath.BandStrength(elevation, inVacuum: false), Is.EqualTo(expected).Within(Tolerance));
    }

    /// <summary>
    /// Guards against the fade-out ramp's reversed endpoints leaking above the horizon — the easiest
    /// way to get this trapezoid wrong is a sign error that makes daylight faintly blue.
    /// </summary>
    [Test]
    public void BandStrength_IsZeroEverywhereAboveOnset()
    {
        for (float elevation = OzoneTwilightMath.BlueOnsetDegrees; elevation <= 90f; elevation += 2.5f)
        {
            Assert.That(OzoneTwilightMath.BandStrength(elevation, inVacuum: false), Is.EqualTo(0f).Within(Tolerance),
                $"expected no blue at elevation {elevation}");
        }
    }

    /// <summary>
    /// The plateau is the dwell-time property: a shallow polar sun oscillating inside the band must
    /// hold a steady blue rather than pulsing. If someone later "simplifies" the trapezoid back to a
    /// triangle peaking at one elevation, this is the test that fails.
    /// </summary>
    [Test]
    public void BandStrength_HasAFlatPlateau()
    {
        for (float elevation = OzoneTwilightMath.BluePeakDegrees; elevation >= OzoneTwilightMath.BluePlateauEndDegrees; elevation -= 0.1f)
        {
            Assert.That(OzoneTwilightMath.BandStrength(elevation, inVacuum: false), Is.EqualTo(1f).Within(Tolerance),
                $"plateau broken at elevation {elevation}");
        }
    }

    /// <summary>
    /// Catches a sign or endpoint error at any of the four anchors: a discontinuity here would show
    /// in game as the sky snapping colour partway through dusk.
    /// </summary>
    [Test]
    public void BandStrength_IsContinuous()
    {
        float previous = OzoneTwilightMath.BandStrength(-20f, inVacuum: false);
        for (float elevation = -20f; elevation <= 2f; elevation += 0.05f)
        {
            float current = OzoneTwilightMath.BandStrength(elevation, inVacuum: false);
            Assert.That(System.MathF.Abs(current - previous), Is.LessThan(0.02f),
                $"jump at elevation {elevation}");
            previous = current;
        }
    }

    /// <summary>
    /// §18's vacuum gate. Asserted alongside the sea-level value in the same sweep so a regression
    /// shows up as a diverging pair rather than as two independently-passing tests.
    /// </summary>
    [TestCase(0f)]
    [TestCase(-4f)]
    [TestCase(-7.2f)]
    [TestCase(-12f)]
    [TestCase(-18f)]
    [TestCase(-40f)]
    public void BandStrength_IsZeroInVacuum_AtEveryElevation(float elevation)
    {
        Assert.That(OzoneTwilightMath.BandStrength(elevation, inVacuum: true), Is.EqualTo(0f).Within(Tolerance));
        Assert.That(OzoneTwilightMath.BandStrength(elevation, inVacuum: false), Is.GreaterThanOrEqualTo(0f));
    }

    // --- SlantAirmass and ChappuisTransmission: the hue ---

    [TestCase(10f, 0f)] // sun up: no long slant path
    [TestCase(0f, 0f)] // horizon
    [TestCase(-6f, 22.5f)] // halfway to the plateau
    [TestCase(-12f, OzoneTwilightMath.MaxSlantAirmass)] // plateau
    [TestCase(-30f, OzoneTwilightMath.MaxSlantAirmass)] // clamped below
    public void SlantAirmass_MatchesExpected(float elevation, float expected)
    {
        Assert.That(OzoneTwilightMath.SlantAirmass(elevation), Is.EqualTo(expected).Within(0.01f));
    }

    /// <summary>
    /// The whole hue rests on the cross-section ordering R > G >> B. This is the exact inverse of
    /// §8's BlackbodyToRgb_StaysWarm_AcrossOurWholeRange (which asserts R >= G >= B), and that
    /// inversion is precisely why the two subsystems cannot share a file.
    /// </summary>
    [TestCase(-4f)]
    [TestCase(-7.2f)]
    [TestCase(-12f)]
    [TestCase(-18f)]
    public void ChappuisTransmission_IsBlue(float elevation)
    {
        SkyColorTemperature.Rgb rgb = OzoneTwilightMath.ChappuisTransmission(elevation);
        Assert.That(rgb.B, Is.GreaterThan(rgb.G), "blue must survive the notch better than green");
        Assert.That(rgb.G, Is.GreaterThan(rgb.R), "green must survive better than red (603 nm is the band peak)");
    }

    /// <summary>
    /// Pins the multiply-channel correction: the transmission must carry HUE ONLY. If the maximum
    /// channel ever drifts below 1 the adapter would be smuggling a brightness change into a patch
    /// documented as colour-only, and §7a would multiply it away anyway.
    /// </summary>
    [TestCase(-4f)]
    [TestCase(-7.2f)]
    [TestCase(-12f)]
    [TestCase(-18f)]
    public void ChappuisTransmission_IsLuminanceNormalised(float elevation)
    {
        SkyColorTemperature.Rgb rgb = OzoneTwilightMath.ChappuisTransmission(elevation);
        float brightest = System.MathF.Max(rgb.R, System.MathF.Max(rgb.G, rgb.B));
        Assert.That(brightest, Is.EqualTo(1f).Within(Tolerance));
    }

    /// <summary>
    /// The notch deepens as the slant path lengthens — the progression a fixed blackbody colour
    /// cannot express at all, and the reason the hue is a function of elevation rather than a
    /// constant. Strict below the horizon down to the plateau; flat below that, by design.
    /// </summary>
    [Test]
    public void ChappuisTransmission_DeepensMonotonically_DownToThePlateau()
    {
        float previousRedOverBlue = 2f;
        for (float elevation = 0f; elevation >= OzoneTwilightMath.BluePlateauEndDegrees; elevation -= 0.25f)
        {
            SkyColorTemperature.Rgb rgb = OzoneTwilightMath.ChappuisTransmission(elevation);
            float redOverBlue = rgb.R / rgb.B;
            Assert.That(redOverBlue, Is.LessThan(previousRedOverBlue),
                $"hue stopped deepening at elevation {elevation}");
            previousRedOverBlue = redOverBlue;
        }
    }

    /// <summary>
    /// THE test this subsystem exists to pass, and the one that would have caught the rejected
    /// blackbody design before a line of adapter code was written.
    ///
    /// MatBases.LightOverlay.color multiplies the scene, so what the player sees is the per-channel
    /// RATIO change against an unmodified night. §9 measured its own first two attempts at 0.001 and
    /// recorded them as dead ends — "the effect was, measurably, not there". A full-strength
    /// Planckian 20,000 K tint manages only ~5% red attenuation here for the same reason: vanilla's
    /// night sky is already almost exactly that blue. The absorption model must clear that bar by a
    /// wide margin at the band's peak, at the shipped blend, or it is not worth shipping.
    /// </summary>
    [Test]
    public void GroundRedAttenuation_ExceedsVisibleThreshold()
    {
        float attenuation = 1f - GroundChannelFactor(OzoneTwilightMath.BluePeakDegrees, VanillaNightR, 0);
        Assert.That(attenuation, Is.GreaterThan(0.20f),
            "the blue must be visible through the multiply overlay, not another §9-style dead end");
    }

    /// <summary>
    /// The blue must also deepen on screen, not just in the model — the payoff of the airmass ramp.
    /// </summary>
    [Test]
    public void GroundRedAttenuation_GrowsAsTheSunSinks()
    {
        float atOnset = 1f - GroundChannelFactor(-5f, VanillaNightR, 0);
        float atPeak = 1f - GroundChannelFactor(OzoneTwilightMath.BluePeakDegrees, VanillaNightR, 0);
        float atPlateau = 1f - GroundChannelFactor(OzoneTwilightMath.BluePlateauEndDegrees, VanillaNightR, 0);

        Assert.That(atPeak, Is.GreaterThan(atOnset));
        Assert.That(atPlateau, Is.GreaterThan(atPeak));
    }

    /// <summary>
    /// Reproduces what the adapter does to one channel of vanilla's night sky, then expresses it as
    /// the factor the multiply overlay applies to the ground. channel: 0 = R, 1 = G, 2 = B.
    /// </summary>
    private static float GroundChannelFactor(float elevation, float vanillaChannel, int channel)
    {
        SkyColorTemperature.Rgb hue = OzoneTwilightMath.ChappuisTransmission(elevation);
        float hueChannel = channel == 0 ? hue.R : (channel == 1 ? hue.G : hue.B);

        // The adapter scales the pure hue against the colour it blends from, so the tint stays
        // luminance-neutral — see Patch_PolarNightBlue.
        float target = hueChannel * VanillaNightB;
        float blended = vanillaChannel + (target - vanillaChannel) * SkyBlend;
        return blended / vanillaChannel;
    }

    // --- CanReachBandToday: the gate ---

    [TestCase(0f, 0f, true)] // equator, equinox
    [TestCase(0f, 23.44f, true)] // equator, solstice
    [TestCase(45f, -23.44f, true)] // mid-latitude winter
    [TestCase(78f, -23.44f, true)] // CIVIL POLAR NIGHT — the money case, must never be skipped
    [TestCase(70f, 23.44f, false)] // midnight sun: never dips to -4
    [TestCase(90f, 23.44f, false)] // pole in summer
    [TestCase(86f, -23.44f, false)] // true polar night: never climbs to -18
    public void CanReachBandToday_MatchesExpected(float latitude, float declination, bool expected)
    {
        Assert.That(OzoneTwilightMath.CanReachBandToday(latitude, declination), Is.EqualTo(expected));
    }

    /// <summary>
    /// The gate is an optimisation, so it must be correctness-preserving, not merely plausible: on
    /// every (latitude, declination) it rejects, the band strength must be zero at every hour of
    /// that day. Cross-checked against Formulas.SolarElevationDegrees — the same elevation function
    /// the live adapter feeds — rather than against the gate's own closed-form extremes, so the two
    /// derivations have to agree independently.
    /// </summary>
    [Test]
    public void CanReachBandToday_NeverSkipsAReachableDay()
    {
        int skippedPairs = 0;

        for (float latitude = -90f; latitude <= 90f; latitude += 1f)
        {
            for (float declination = -23.44f; declination <= 23.44f; declination += 1f)
            {
                bool skipped = !OzoneTwilightMath.CanReachBandToday(latitude, declination);
                if (skipped)
                {
                    skippedPairs++;
                    AssertBandNeverOpensDuringTheDay(latitude, declination);
                }
            }
        }

        // Without this the property above passes vacuously if the gate degenerates to "always run" —
        // which is exactly what a sign error in the closed-form extremes would produce, and it would
        // look like a green test while the optimisation silently did nothing.
        Assert.That(skippedPairs, Is.GreaterThan(0), "the gate never skipped anything, so nothing was proven");
    }

    private static void AssertBandNeverOpensDuringTheDay(float latitude, float declination)
    {
        for (float dayPercent = 0f; dayPercent <= 1f; dayPercent += 0.005f)
        {
            float elevation = Formulas.SolarElevationDegrees(latitude, declination, dayPercent);
            Assert.That(OzoneTwilightMath.BandStrength(elevation, inVacuum: false), Is.EqualTo(0f).Within(Tolerance),
                $"gate skipped lat {latitude} decl {declination}, but the band opens at day-percent {dayPercent} (elevation {elevation})");
        }
    }

    // --- OverlayFloor: the visual-only brightness floor ---

    [TestCase(0f, 1f, 1f, OzoneTwilightMath.DefaultOverlayFloor)] // full band, full strength
    [TestCase(0f, 0.5f, 1f, 0.15f)] // half band: floor fades with the blue
    [TestCase(0f, 1f, 0f, 0f)] // strength 0 is a true no-op — the A/B baseline
    [TestCase(0f, 0f, 1f, 0f)] // outside the band
    [TestCase(0.5f, 1f, 1f, 0.5f)] // Cinematic's 0.50 already higher: inert by construction
    [TestCase(0.2f, 1f, 1f, OzoneTwilightMath.DefaultOverlayFloor)] // raises a low floor
    public void OverlayFloor_MatchesExpected(float baseMin, float band, float strength, float expected)
    {
        Assert.That(OzoneTwilightMath.OverlayFloor(baseMin, band, strength), Is.EqualTo(expected).Within(Tolerance));
    }

    /// <summary>
    /// The safety property the whole floor design rests on: it may brighten a night, never darken
    /// one the player's own setting made brighter.
    /// </summary>
    [Test]
    public void OverlayFloor_NeverLowersTheCallerFloor()
    {
        for (float baseMin = 0f; baseMin <= 1f; baseMin += 0.05f)
        {
            for (float band = 0f; band <= 1f; band += 0.1f)
            {
                for (float strength = 0f; strength <= 1f; strength += 0.25f)
                {
                    Assert.That(OzoneTwilightMath.OverlayFloor(baseMin, band, strength), Is.GreaterThanOrEqualTo(baseMin));
                }
            }
        }
    }

    /// <summary>
    /// The only test that proves the floor changes what the screen actually does. Exercises the real
    /// cross-subsystem seam into §7a: at a deep-twilight glow the raw overlay keeps a functionally
    /// black fraction of vanilla brightness, and our floor is what lifts it to something the blue is
    /// visible against.
    /// </summary>
    [Test]
    public void OverlayFloor_ComposesWithOverlayBrightnessFactor()
    {
        const float DeepTwilightGlow = 0.014f;

        float unfloored = NightRadianceMath.OverlayBrightnessFactor(DeepTwilightGlow, minBrightness: 0f);
        Assert.That(unfloored, Is.LessThan(0.15f), "precondition: an unfloored deep twilight is near-black");

        float floor = OzoneTwilightMath.OverlayFloor(0f, bandStrength: 1f, tintStrength: 1f);
        float floored = NightRadianceMath.OverlayBrightnessFactor(DeepTwilightGlow, floor);

        Assert.That(floored, Is.EqualTo(OzoneTwilightMath.DefaultOverlayFloor).Within(Tolerance));
        Assert.That(floored, Is.GreaterThan(unfloored));
    }

    // --- Composition with the warm subsystems ---

    /// <summary>
    /// Pins the "the bands are disjoint enough that ordering does not matter" claim that justifies
    /// shipping without a HarmonyPriority. §8's warm tint and our blue overlap only in -6..-4, and
    /// only ever weakly. If someone retunes either curve so the product climbs, the no-priority
    /// decision has quietly become load-bearing and this test says so.
    /// </summary>
    [Test]
    public void WarmAndBlue_AreNeverBothAtFullStrength()
    {
        for (float elevation = -20f; elevation <= 10f; elevation += 0.1f)
        {
            float warm = SkyColorTemperature.TintStrength(elevation, inVacuum: false);
            float blue = OzoneTwilightMath.BandStrength(elevation, inVacuum: false);
            Assert.That(warm * blue, Is.LessThan(0.10f), $"warm and blue both strong at elevation {elevation}");
        }
    }

    /// <summary>
    /// §2's civil-twilight warmth dies at -6 and our blue starts at -4, so the two share exactly the
    /// documented 2-degree handover window and nothing else.
    /// </summary>
    [Test]
    public void WarmPersistenceAndBlue_OverlapOnlyInTheDocumentedWindow()
    {
        for (float elevation = -30f; elevation <= 5f; elevation += 0.1f)
        {
            float product = Formulas.CivilTwilightPersistence(elevation) * OzoneTwilightMath.BandStrength(elevation, inVacuum: false);
            bool insideWindow = elevation <= OzoneTwilightMath.BlueOnsetDegrees && elevation >= Formulas.CivilTwilightEndDegrees;
            if (!insideWindow)
                Assert.That(product, Is.EqualTo(0f).Within(Tolerance), $"unexpected overlap at elevation {elevation}");
        }
    }
}
