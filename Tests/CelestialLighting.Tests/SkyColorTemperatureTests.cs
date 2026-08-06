using System.Reflection;

namespace CelestialLighting.Tests;

/// <summary>
/// Offline edge-case coverage for SkyColorTemperature.cs (subsystem 8) — no RimWorld/Unity assembly
/// required, since the file has no dependency on either. Complements ApiCompatibilityTests.cs (which
/// only checks vanilla members still exist); these check that our own colour-temperature math is
/// correct.
/// </summary>
[TestFixture]
public class SkyColorTemperatureTests
{
    private const float Tolerance = 0.0005f;

    // Sea level: the identity value of §20's site-altitude input (AtmosphericColumn's column
    // fraction is 1 at 0 m). Every pre-§20 expectation below is passed this, so the whole block
    // stays a regression pin on the original curve rather than being re-baselined against the new
    // parameter — if threading pressureFraction through moved a sea-level number, these fail.
    private const float SeaLevel = 1f;

    // Unpolluted: the identity value of §20b's aerosol input (AtmosphericColumn.AerosolLoadFraction
    // is 0 at pollution 0, which is every tile in a game without Biotech). Every pre-§20b expectation
    // below is passed this, so the whole block stays a regression pin on the §20 curve rather than
    // being re-baselined against the new parameter.
    private const float CleanAir = 0f;

    // --- ColorTemperatureKelvin: warm at the horizon, neutral at the zenith, monotonic between ---

    [TestCase(-5f, SkyColorTemperature.HorizonKelvin)] // below horizon clamps flat to warm
    [TestCase(0f, SkyColorTemperature.HorizonKelvin)] // horizon
    [TestCase(30f, 3886f)] // halfway up the ramp: Lerp(2000, 5772, 0.5)
    [TestCase(60f, SkyColorTemperature.ZenithKelvin)] // full daylight altitude
    [TestCase(90f, SkyColorTemperature.ZenithKelvin)] // zenith clamps flat to neutral
    public void ColorTemperatureKelvin_MatchesExpected(float elevation, float expected)
    {
        Assert.That(SkyColorTemperature.ColorTemperatureKelvin(elevation, SeaLevel, inVacuum: false),
            Is.EqualTo(expected).Within(0.5f));
    }

    [Test]
    public void ColorTemperatureKelvin_IsMonotonicNonDecreasing_AsSunClimbs()
    {
        float previous = SkyColorTemperature.ColorTemperatureKelvin(-10f, SeaLevel, inVacuum: false);
        for (float elevation = -10f; elevation <= 90f; elevation += 2.5f)
        {
            float current = SkyColorTemperature.ColorTemperatureKelvin(elevation, SeaLevel, inVacuum: false);
            Assert.That(current, Is.GreaterThanOrEqualTo(previous - Tolerance),
                $"colour temperature dropped as the sun rose (at elevation {elevation})");
            previous = current;
        }
    }

    // --- TintStrength: strongest at low sun, zero at high sun, zero once well below the horizon ---

    [TestCase(0f, 1f)] // horizon: full strength
    [TestCase(-0.83f, 1f)] // refraction-adjusted horizon: still full strength
    [TestCase(30f, 0.5f)] // halfway to daylight altitude
    [TestCase(10f, 0.8333f)] // low winter-noon sun: still strongly warm
    [TestCase(60f, 0f)] // daylight altitude: no tint
    [TestCase(90f, 0f)] // zenith: no tint
    [TestCase(-6f, 0f)] // end of civil twilight: tint has faded out entirely
    [TestCase(-20f, 0f)] // deep night: no tint
    public void TintStrength_MatchesExpected(float elevation, float expected)
    {
        Assert.That(SkyColorTemperature.TintStrength(elevation, SeaLevel, inVacuum: false),
            Is.EqualTo(expected).Within(0.001f));
    }

    [Test]
    public void TintStrength_FadesSmoothlyBelowHorizon()
    {
        // Midway through the civil-twilight fade band (-6 .. -0.83), the gate is ~0.5.
        float mid = SkyColorTemperature.TintStrength(-3.415f, SeaLevel, inVacuum: false);
        Assert.That(mid, Is.EqualTo(0.5f).Within(0.02f));
    }

    // --- BlackbodyToRgb: anchor points from the Tanner Helland Planckian-locus approximation ---

    [Test]
    public void BlackbodyToRgb_IsWhite_At6600K()
    {
        // 6600 K sits right at the red/blue break points, where all three channels saturate to 1.
        SkyColorTemperature.Rgb rgb = SkyColorTemperature.BlackbodyToRgb(6600f);
        Assert.That(rgb.R, Is.EqualTo(1f).Within(Tolerance));
        Assert.That(rgb.G, Is.EqualTo(1f).Within(Tolerance));
        Assert.That(rgb.B, Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void BlackbodyToRgb_IsWarmOrange_At2000K()
    {
        // Deep sunset: red pinned, green mid, blue nearly gone.
        SkyColorTemperature.Rgb rgb = SkyColorTemperature.BlackbodyToRgb(2000f);
        Assert.That(rgb.R, Is.EqualTo(1f).Within(Tolerance));
        Assert.That(rgb.G, Is.EqualTo(0.5367f).Within(0.001f));
        Assert.That(rgb.B, Is.EqualTo(0.0546f).Within(0.001f));
    }

    [Test]
    public void BlackbodyToRgb_StaysWarm_AcrossOurWholeRange()
    {
        // Over the entire curve (2000..5772 K) the sky is warm: red is always fully saturated and
        // red >= green >= blue, so it can never come out cool/blue.
        for (float kelvin = SkyColorTemperature.HorizonKelvin; kelvin <= SkyColorTemperature.ZenithKelvin; kelvin += 100f)
        {
            SkyColorTemperature.Rgb rgb = SkyColorTemperature.BlackbodyToRgb(kelvin);
            Assert.That(rgb.R, Is.EqualTo(1f).Within(Tolerance), $"red not saturated at {kelvin} K");
            Assert.That(rgb.G, Is.GreaterThanOrEqualTo(rgb.B - Tolerance), $"green < blue at {kelvin} K");
            Assert.That(rgb.R, Is.GreaterThanOrEqualTo(rgb.G - Tolerance), $"red < green at {kelvin} K");
        }
    }

    [Test]
    public void BlackbodyToRgb_GreenAndBlueRise_AsTemperatureRises()
    {
        // Warmer (higher K) means less-warm-looking: green and blue both climb toward white as the
        // sun rises. This is the perceptual monotonicity the whole subsystem leans on.
        SkyColorTemperature.Rgb cool = SkyColorTemperature.BlackbodyToRgb(2000f);
        SkyColorTemperature.Rgb warm = SkyColorTemperature.BlackbodyToRgb(5772f);
        Assert.That(warm.G, Is.GreaterThan(cool.G));
        Assert.That(warm.B, Is.GreaterThan(cool.B));
    }

    // --- SkyColorForElevation: the composition the adapter and the live probe both call ---

    [Test]
    public void SkyColorForElevation_MatchesManualComposition()
    {
        SkyColorTemperature.Rgb direct = SkyColorTemperature.SkyColorForElevation(
            20f, SeaLevel, CleanAir, AerosolSpectrum.ReferenceAngstromExponent, inVacuum: false);
        SkyColorTemperature.Rgb composed = SkyColorTemperature.BlackbodyToRgb(
            SkyColorTemperature.ColorTemperatureKelvin(20f, SeaLevel, inVacuum: false));
        Assert.That(direct.R, Is.EqualTo(composed.R).Within(Tolerance));
        Assert.That(direct.G, Is.EqualTo(composed.G).Within(Tolerance));
        Assert.That(direct.B, Is.EqualTo(composed.B).Within(Tolerance));
    }

    // --- §20 site altitude: the observer's own air column ---
    //
    // Rayleigh optical depth scales with the surface pressure AT THE OBSERVER, because the slant
    // path from a low sun descends out of space and TERMINATES there — it never re-enters the denser
    // air below. So a mountain base genuinely skips the whole dense column beneath it and sees a
    // whiter sun and a subdued sunset, and the sea-level 2000 K endpoint stops being every map's.

    [TestCase(0f, 1f)] // sea level: the identity value the whole pre-§20 curve is pinned against
    [TestCase(100f, 0.9883f)] // vanilla Tile.elevation default — documented as 0.988, imperceptible
    [TestCase(1500f, 0.8382f)] // documented as 0.84
    [TestCase(4000f, 0.6246f)] // documented as 0.62
    // Real-world anchors named in AtmosphericColumn's header. They are what justify using one
    // textbook constant instead of a hand-tuned ramp, so they are pinned rather than left as prose.
    [TestCase(1600f, 0.8284f)] // Denver, measured ~0.83 atm
    [TestCase(3650f, 0.6509f)] // Lhasa, measured ~0.65 atm
    [TestCase(8850f, 0.3531f)] // Everest summit, measured ~0.35 atm
    public void RayleighPressureFraction_MatchesTheBarometricTable(float siteAltitudeMetres, float expected)
    {
        Assert.That(AtmosphericColumn.RayleighPressureFraction(siteAltitudeMetres),
            Is.EqualTo(expected).Within(0.001f));
    }

    [Test]
    public void ColumnFraction_FallsOffFasterForAShallowerScaleHeight()
    {
        // The reason the scale height is a parameter rather than baked in: at 3000 m a near-surface
        // aerosol layer has almost entirely dropped below the observer while two thirds of the bulk
        // air is still overhead. If these two ever came out equal, the shared model has collapsed
        // into a Rayleigh-only one.
        //
        // §20b has since landed the second species, so the shallow height is now read off
        // AerosolScaleHeightMetres rather than written as a literal — which turns this from a
        // hypothetical about a future caller into a pin on the actual shipped pair.
        float bulkAir = AtmosphericColumn.ColumnFraction(3000f, AtmosphericColumn.RayleighScaleHeightMetres);
        float nearSurface = AtmosphericColumn.ColumnFraction(3000f, AtmosphericColumn.AerosolScaleHeightMetres);
        Assert.That(bulkAir, Is.EqualTo(0.7020f).Within(0.001f));
        Assert.That(nearSurface, Is.EqualTo(0.1353f).Within(0.001f));
    }

    [TestCase(-430f)] // Dead Sea shore: genuinely ~1.05 atm, deliberately not modelled
    [TestCase(-5000f)]
    public void ColumnFraction_ClampsSubSeaLevelToOne(float siteAltitudeMetres)
    {
        // Every consumer treats 1 as "the full, unmodified sea-level effect" and is tuned against
        // that ceiling — §8 multiplies it straight into per-channel blend maxima — so the [0, 1]
        // contract is worth more than a 5% over-pressure no RimWorld worldgen produces.
        Assert.That(AtmosphericColumn.RayleighPressureFraction(siteAltitudeMetres),
            Is.EqualTo(1f).Within(Tolerance));
    }

    // Interpolation is LINEAR IN MIREDS (10^6/K), not in Kelvin, because a mired shift is
    // approximately linear in optical depth and optical depth is what pressureFraction scales. Each
    // expectation below is derived rather than recorded — the sea-level shift is
    // 10^6/2000 - 10^6/5772 = 326.75 mired, and a column fraction p walks that fraction of it:
    //
    //     K(p) = 10^6 / (173.25 + p * 326.75)
    //
    // Both endpoints are identical to a Kelvin-space lerp, which is why every invariant that lives
    // at an endpoint (notably §18's p -> 0 == vacuum) is unaffected by the choice. Only the interior
    // moves, and it moves a lot: 4000 m sits at 2650 K here against 3416 K in Kelvin-space, i.e. the
    // Kelvin version walked the warm end back nearly twice as far for no derivable reason.
    [TestCase(1f, SkyColorTemperature.HorizonKelvin)] // sea level: unchanged from before §20
    [TestCase(0.9883f, 2015.4f)] // 100 m
    [TestCase(0.8382f, 2236.5f)] // 1500 m
    [TestCase(0.6246f, 2650.1f)] // 4000 m: a whiter horizon sun, but less so than Kelvin-space said
    [TestCase(0f, SkyColorTemperature.ZenithKelvin)] // the h -> infinity limit
    public void HorizonKelvinForPressure_WalksTheWarmEndpointTowardTheUnreddenedAnchor(
        float pressureFraction, float expected)
    {
        Assert.That(SkyColorTemperature.HorizonKelvinForPressure(pressureFraction),
            Is.EqualTo(expected).Within(0.5f));
    }

    [Test]
    public void Warmth_IsMonotonicallyNonIncreasing_InSiteAltitude()
    {
        // The invariant the subsystem is allowed to be judged on at every sun angle: thinner air can
        // only ever make the sky less warm — never more, and never non-monotonically. Warmth falls
        // two ways at once (a higher colour temperature and a weaker tint), so both are swept.
        for (float elevation = -10f; elevation <= 90f; elevation += 5f)
        {
            float previousKelvin = SkyColorTemperature.ColorTemperatureKelvin(elevation, SeaLevel, inVacuum: false);
            float previousTint = SkyColorTemperature.TintStrength(elevation, SeaLevel, inVacuum: false);
            for (float siteAltitudeMetres = 0f; siteAltitudeMetres <= 9000f; siteAltitudeMetres += 250f)
            {
                float pressureFraction = AtmosphericColumn.RayleighPressureFraction(siteAltitudeMetres);
                float kelvin = SkyColorTemperature.ColorTemperatureKelvin(elevation, pressureFraction, inVacuum: false);
                float tint = SkyColorTemperature.TintStrength(elevation, pressureFraction, inVacuum: false);
                Assert.That(kelvin, Is.GreaterThanOrEqualTo(previousKelvin - Tolerance),
                    $"sky got warmer with altitude at {siteAltitudeMetres} m, elevation {elevation}");
                Assert.That(tint, Is.LessThanOrEqualTo(previousTint + Tolerance),
                    $"tint got stronger with altitude at {siteAltitudeMetres} m, elevation {elevation}");
                previousKelvin = kelvin;
                previousTint = tint;
            }
        }
    }

    [Test]
    public void ZeroPressure_ReproducesTheVacuumValuesExactly()
    {
        // §18's discrete gate and §20's continuous column model are independent code paths reading
        // different data (BiomeDef.inVacuum vs Tile.elevation), and they agree at the endpoint
        // because vacuum IS the h -> infinity limit of this curve. That agreement is a free test:
        // it costs nothing and it fails loudly if either side is retuned on its own. Exact equality,
        // not a tolerance — both paths land on the same constants by construction, and if they ever
        // only nearly agree, one of them has grown arithmetic the other has not.
        for (float elevation = -30f; elevation <= 90f; elevation += 2.5f)
        {
            Assert.That(SkyColorTemperature.ColorTemperatureKelvin(elevation, pressureFraction: 0f, inVacuum: false),
                Is.EqualTo(SkyColorTemperature.ColorTemperatureKelvin(elevation, SeaLevel, inVacuum: true)),
                $"airless-limit colour temperature diverged from the vacuum gate at elevation {elevation}");
            Assert.That(SkyColorTemperature.TintStrength(elevation, pressureFraction: 0f, inVacuum: false),
                Is.EqualTo(SkyColorTemperature.TintStrength(elevation, SeaLevel, inVacuum: true)),
                $"airless-limit tint strength diverged from the vacuum gate at elevation {elevation}");
        }
    }

    [Test]
    public void PressureFraction_IsClampedAboveOne_SoTheTintCannotOvershoot()
    {
        // Defence in depth against a future caller that computes its own fraction rather than going
        // through AtmosphericColumn: the adapter multiplies TintStrength straight into its 0.35/0.25
        // per-channel maxima, so a fraction above 1 would blend past the strength those constants
        // were chosen for. The curve clamps rather than trusting its input.
        Assert.That(SkyColorTemperature.TintStrength(0f, pressureFraction: 1.5f, inVacuum: false),
            Is.EqualTo(SkyColorTemperature.TintStrength(0f, SeaLevel, inVacuum: false)).Within(Tolerance));
        Assert.That(SkyColorTemperature.ColorTemperatureKelvin(0f, pressureFraction: 1.5f, inVacuum: false),
            Is.EqualTo(SkyColorTemperature.HorizonKelvin).Within(0.5f));
    }

    [Test]
    public void OzoneTwilightBlue_IsAltitudeInvariant_WhileTheWarmTintIsNot()
    {
        // The asymmetry between §8 and §19, pinned in one place so the two subsystems cannot drift
        // into each other. §8's reddening happens in the air the observer stands in, so a mountain
        // skips most of it. §19's Chappuis absorption happens in the ozone layer at 20-30 km —
        // entirely above any mountain — so the polar-night blue is the SAME on a 4000 m plateau as
        // at sea level, and threading a site-altitude term into it would be a modelling error, not
        // an improvement.
        float seaLevelWarmth = SkyColorTemperature.TintStrength(-2f, SeaLevel, inVacuum: false);
        float mountainWarmth = SkyColorTemperature.TintStrength(-2f, pressureFraction: 0.6246f, inVacuum: false);
        Assert.That(mountainWarmth, Is.LessThan(seaLevelWarmth - 0.05f),
            "§8's warm tint no longer responds to site altitude at all");

        // §19 expresses its invariance structurally — by having nowhere to put such a term. Asserting
        // it that way is the only mechanical form available, and it is exactly the form that fails
        // when someone adds one.
        string[] offending = typeof(OzoneTwilightMath)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SelectMany(method => method.GetParameters())
            .Where(parameter => parameter.Name!.Contains("pressure", StringComparison.OrdinalIgnoreCase)
                || parameter.Name!.Contains("altitude", StringComparison.OrdinalIgnoreCase))
            .Select(parameter => parameter.Name!)
            .ToArray();
        Assert.That(offending, Is.Empty,
            "OzoneTwilightMath grew a site-altitude/pressure/aerosol parameter — Chappuis absorption "
            + "is at 20-30 km, above every mountain and roughly fifteen boundary layers up, so §19 "
            + "must scale with neither where the map sits nor what is in its lowest 1.5 km");
    }

    // --- §20b pollution aerosol: the boundary-layer species, and why it is not just a warm knob ---
    //
    // Biotech's Tile.pollution is read as aerosol loading and enters the SAME column model §20 uses,
    // at a much shorter scale height (1500 m against 8500 m). Two consequences carry the whole
    // subsystem and are pinned separately below: it points the OPPOSITE way to altitude (warming the
    // horizon endpoint past sea level rather than cooling it toward the anchor), and it falls away
    // with altitude nearly six times faster, so a high enough map is simply above the haze.

    [TestCase(0f, 1f)] // sea level: the full boundary-layer column is overhead
    [TestCase(100f, 0.9355f)] // vanilla Tile.elevation default: essentially all of it
    [TestCase(1039.7f, 0.5f)] // 1500 * ln 2 — the half-column height, and the reason for the note in
                              // TintStrength that aerosol only exists where the tint has saturated
    [TestCase(1500f, 0.3679f)] // one scale height: 1/e, by definition
    [TestCase(4000f, 0.0695f)] // the headline number: a mountain base sits above the smog
    [TestCase(8850f, 0.0027f)] // Everest summit: aerosol has effectively ceased to exist
    public void AerosolColumnFraction_MatchesTheBoundaryLayerTable(float siteAltitudeMetres, float expected)
    {
        Assert.That(AtmosphericColumn.AerosolColumnFraction(siteAltitudeMetres),
            Is.EqualTo(expected).Within(0.001f));
    }

    [TestCase(0f, 0f, 0f)] // unpolluted sea level: the identity case, and every tile without Biotech
    [TestCase(0f, 1f, 1f)] // fully polluted sea level: the model's maximum
    [TestCase(0f, 0.5f, 0.5f)] // loading scales the column's magnitude linearly
    [TestCase(4000f, 1f, 0.0695f)] // full pollution on a 4000 m tile is still almost no aerosol
    [TestCase(4000f, 0.5f, 0.0347f)] // ...and the two factors compose multiplicatively
    [TestCase(0f, -1f, 0f)] // clamped: pollution is a saved float and worldgen mods write it
    [TestCase(0f, 2f, 1f)] // clamped the other way, for the same reason
    public void AerosolLoadFraction_ScalesTheColumnByPollutionAndClampsIt(
        float siteAltitudeMetres, float tilePollution, float expected)
    {
        Assert.That(AtmosphericColumn.AerosolLoadFraction(siteAltitudeMetres, tilePollution),
            Is.EqualTo(expected).Within(0.001f));
    }

    [Test]
    public void AerosolColumn_DecaysAboutFiveTimesFasterWithAltitudeThanTheRayleighColumn()
    {
        // THE HEADLINE INVARIANT. The ratio is not a side effect of the two constants — it IS the
        // feature, and it is the only reason §20b is a separate species rather than "turn §8's warm
        // knob up on polluted tiles". Both columns are exp(-h/H), so the ratio of their decay rates
        // is exactly the inverse ratio of their scale heights, at every altitude:
        //
        //     ln(aerosol) / ln(rayleigh) = (h / 1500) / (h / 8500) = 8500 / 1500 = 5.667
        //
        // Pinned as a sweep rather than at one altitude precisely because it is altitude-independent:
        // if someone retunes either constant, or replaces either accessor with something that is not
        // a pure exponential of the same height, the sweep diverges rather than one number moving.
        float expectedRatio = AtmosphericColumn.RayleighScaleHeightMetres
            / AtmosphericColumn.AerosolScaleHeightMetres;
        Assert.That(expectedRatio, Is.EqualTo(5.6667f).Within(0.001f),
            "the two scale heights no longer stand in the ~5.7x relationship §20b is built on");

        for (float siteAltitudeMetres = 250f; siteAltitudeMetres <= 9000f; siteAltitudeMetres += 250f)
        {
            float aerosol = AtmosphericColumn.AerosolColumnFraction(siteAltitudeMetres);
            float rayleigh = AtmosphericColumn.RayleighPressureFraction(siteAltitudeMetres);
            double ratio = Math.Log(aerosol) / Math.Log(rayleigh);
            Assert.That(ratio, Is.EqualTo(expectedRatio).Within(0.01),
                $"aerosol/Rayleigh decay-rate ratio drifted at {siteAltitudeMetres} m");
        }
    }

    [Test]
    public void AtFourThousandMetres_TheMountainIsAboveTheSmogButStillUnderMostOfTheAir()
    {
        // The same claim stated the way a player would experience it, so the ratio test above cannot
        // pass while the absolute levels have gone somewhere useless. A 4000 m plateau keeps roughly
        // five eighths of its air (a subdued but real sunset, §20) and almost none of its haze.
        Assert.That(AtmosphericColumn.RayleighPressureFraction(4000f), Is.GreaterThan(0.6f));
        Assert.That(AtmosphericColumn.AerosolColumnFraction(4000f), Is.LessThan(0.1f));
    }
    // --- §20b, restated in colour rather than in Kelvin ---
    //
    // §20b originally expressed the aerosol's effect as a SECOND lerp along the Planckian locus, from
    // the clean-air endpoint down to a 1500 K AerosolHorizonKelvin. §20d retired that, because a point
    // on the locus cannot carry a spectral SHAPE and shape is the whole subject of §20d. So the
    // aerosol's colour now comes out of AerosolSpectrum as a per-channel multiplier instead.
    //
    // Everything §20b actually claimed survives that move — pollution warms, altitude cools, the two
    // compose, unpolluted tiles are untouched — but the claims can no longer be stated as Kelvin,
    // because the composed colour is deliberately not a blackbody at any temperature. They are
    // restated below on the red/blue ratio of the composed colour, which is the same "how far along
    // the warm axis is this" question a colour temperature was answering, asked in a way that does not
    // presuppose the answer lies on the locus.

    // The composed horizon colour, which is where every §20b/§20d invariant is measured. Elevation 0
    // because that is where the aerosol path is longest and the effect largest — a test that passed
    // only at high sun would be pinning almost nothing.
    private static SkyColorTemperature.Rgb HorizonColour(
        float pressureFraction, float aerosolFraction, float angstromExponent) =>
        SkyColorTemperature.SkyColorForElevation(
            0f, pressureFraction, aerosolFraction, angstromExponent, inVacuum: false);

    // Warmth as one number. R/B is what "further down the warm axis" means for a colour, it is finite
    // everywhere the curve can now reach (the clean-air endpoint is 2000 K, comfortably above the
    // Helland fit's 1900 K blue cliff, and the aerosol multiplier is strictly positive), and it is the
    // quantity the live sky_red_blue_ratio probe reports so offline and in-game pins agree.
    private static float RedBlueRatio(SkyColorTemperature.Rgb rgb) => rgb.R / rgb.B;

    [TestCase(1f, 0f, 18.340f)] // clean sea level: §8's own 2000 K anchor, untouched by §20b/§20d
    [TestCase(1f, 0.5f, 28.594f)] // half a sea-level aerosol load
    [TestCase(1f, 1f, 44.581f)] // fully polluted sea level: the warmest the model reaches
    [TestCase(0.9883f, 0.9355f, 29.435f)] // 100 m (vanilla default) at pollution 1.0
    [TestCase(0.8382f, 0.3679f, 4.425f)] // 1500 m at pollution 1.0
    [TestCase(0.6246f, 0.0695f, 1.993f)] // 4000 m at pollution 1.0 — barely moved
    [TestCase(0.6246f, 0f, 1.874f)] // ...from the clean 4000 m value it started at
    public void AerosolColour_CarriesTheHorizonPastTheCleanAirEndpointWhenTheAirIsDirty(
        float pressureFraction, float aerosolFraction, float expectedRedBlueRatio)
    {
        // §20b's table, re-measured in the space the model now works in. Read the last two rows
        // together: full pollution on a 4000 m tile moves the ratio by 0.12, against 26.2 at sea
        // level. A MOUNTAIN BASE IS ABOVE THE SMOG, which was §20b's headline and survives intact.
        Assert.That(RedBlueRatio(HorizonColour(
                pressureFraction, aerosolFraction, AerosolSpectrum.ReferenceAngstromExponent)),
            Is.EqualTo(expectedRedBlueRatio).Within(0.01f));
    }

    [Test]
    public void ZeroPollution_IsBitIdenticalToTheSiteAltitudeOnlyCurve()
    {
        // THE REGRESSION PIN, and the one the ticket asks for as exact equality rather than within a
        // tolerance: at aerosol load 0 the spectral model has to be a true no-op, not a nearly-no-op.
        // Every tile in a game without Biotech takes this path, so "almost unchanged" would mean the
        // mod's default behaviour had silently moved for every existing colony.
        //
        // It holds by construction rather than by luck: at load 0 every optical depth is 0, every
        // transmission is exactly 1.0f, the normalising max is exactly 1.0f, and the multiply is by
        // exactly 1.0f in all three channels. That chain is what makes exact equality assertable, and
        // asserting it exactly is what would catch someone adding an epsilon anywhere along it.
        //
        // §20's one-line formula is restated here rather than called, precisely because the function
        // that used to hold it no longer exists in that form. That is what makes this a pin on the
        // BEHAVIOUR rather than a tautology about whatever the current code does. It is restated in
        // MIREDS, matching §20 after the Kelvin -> mired switch: the endpoint is
        // 10^6 / (mired(Zenith) + p * (mired(Horizon) - mired(Zenith))). Getting that wrong is how
        // the pin silently becomes a tautology, so it is written out longhand.
        for (float siteAltitudeMetres = 0f; siteAltitudeMetres <= 9000f; siteAltitudeMetres += 500f)
        {
            float pressureFraction = AtmosphericColumn.RayleighPressureFraction(siteAltitudeMetres);
            float zenithMired = 1e6f / SkyColorTemperature.ZenithKelvin;
            float horizonMired = 1e6f / SkyColorTemperature.HorizonKelvin;
            float preAerosolEndpoint =
                1e6f / (zenithMired + (horizonMired - zenithMired) * pressureFraction);

            Assert.That(SkyColorTemperature.HorizonKelvinForPressure(pressureFraction),
                Is.EqualTo(preAerosolEndpoint),
                $"the unpolluted horizon endpoint moved at {siteAltitudeMetres} m");

            for (float elevation = -30f; elevation <= 90f; elevation += 7.5f)
            {
                float t = (elevation < 0f ? 0f : elevation > 60f ? 60f : elevation) / 60f;
                float preAerosolKelvin = preAerosolEndpoint
                    + (SkyColorTemperature.ZenithKelvin - preAerosolEndpoint) * t;
                Assert.That(
                    SkyColorTemperature.ColorTemperatureKelvin(elevation, pressureFraction, inVacuum: false),
                    Is.EqualTo(preAerosolKelvin),
                    $"the unpolluted curve moved at {siteAltitudeMetres} m, elevation {elevation}");

                // And the same claim on the COLOUR, which is what §20d actually changed. Swept over
                // every named exponent, because an unpolluted tile must be untouched no matter what
                // particle size its rainfall would have implied — there is no aerosol there for the
                // shape to shape.
                SkyColorTemperature.Rgb expected = SkyColorTemperature.BlackbodyToRgb(preAerosolKelvin);
                foreach (float alpha in AllNamedExponents)
                {
                    SkyColorTemperature.Rgb actual = SkyColorTemperature.SkyColorForElevation(
                        elevation, pressureFraction, CleanAir, alpha, inVacuum: false);
                    Assert.That(actual.R, Is.EqualTo(expected.R),
                        $"unpolluted R moved at {siteAltitudeMetres} m, elevation {elevation}, alpha {alpha}");
                    Assert.That(actual.G, Is.EqualTo(expected.G),
                        $"unpolluted G moved at {siteAltitudeMetres} m, elevation {elevation}, alpha {alpha}");
                    Assert.That(actual.B, Is.EqualTo(expected.B),
                        $"unpolluted B moved at {siteAltitudeMetres} m, elevation {elevation}, alpha {alpha}");
                }
            }
        }
    }

    [Test]
    public void Warmth_IsMonotonicallyNonDecreasing_InPollution()
    {
        // Pollution may only ever make the sky warmer (a HIGHER red/blue ratio), never cooler and
        // never non-monotonically — the mirror of the site-altitude invariant below. Swept over sun
        // elevation, site altitude AND exponent, because since §20d the effect is a function of all
        // four and a monotonicity that only held at one particle size would be worth very little.
        foreach (float alpha in AllNamedExponents)
        {
            for (float siteAltitudeMetres = 0f; siteAltitudeMetres <= 6000f; siteAltitudeMetres += 1000f)
            {
                float pressureFraction = AtmosphericColumn.RayleighPressureFraction(siteAltitudeMetres);
                for (float elevation = -10f; elevation <= 90f; elevation += 10f)
                {
                    float previous = 0f;
                    for (float pollution = 0f; pollution <= 1f; pollution += 0.05f)
                    {
                        float aerosolFraction = AtmosphericColumn.AerosolLoadFraction(siteAltitudeMetres, pollution);
                        float ratio = RedBlueRatio(SkyColorTemperature.SkyColorForElevation(
                            elevation, pressureFraction, aerosolFraction, alpha, inVacuum: false));
                        Assert.That(ratio, Is.GreaterThanOrEqualTo(previous - RatioTolerance),
                            $"sky got cooler as pollution rose (pollution {pollution}, alpha {alpha}, "
                            + $"{siteAltitudeMetres} m, elevation {elevation})");
                        previous = ratio;
                    }
                }
            }
        }
    }

    [Test]
    public void Warmth_IsStillMonotonicallyNonIncreasing_InSiteAltitude_AtEveryPollutionLevel()
    {
        // §20's altitude invariant, re-asserted with §20d's term switched on. It is not obvious for
        // free: climbing raises the clean-air endpoint (cooler) AND thins the aerosol column (cooler
        // again), so the two effects happen to agree — and this is the test that says so rather than
        // leaving it as an argument in a comment. Swept over exponent for the same reason as above.
        foreach (float alpha in AllNamedExponents)
        {
            for (float pollution = 0f; pollution <= 1f; pollution += 0.25f)
            {
                for (float elevation = -10f; elevation <= 90f; elevation += 10f)
                {
                    float previous = float.MaxValue;
                    for (float siteAltitudeMetres = 0f; siteAltitudeMetres <= 9000f; siteAltitudeMetres += 500f)
                    {
                        float ratio = RedBlueRatio(SkyColorTemperature.SkyColorForElevation(
                            elevation,
                            AtmosphericColumn.RayleighPressureFraction(siteAltitudeMetres),
                            AtmosphericColumn.AerosolLoadFraction(siteAltitudeMetres, pollution),
                            alpha,
                            inVacuum: false));
                        Assert.That(ratio, Is.LessThanOrEqualTo(previous + RatioTolerance),
                            $"sky got warmer with altitude at {siteAltitudeMetres} m "
                            + $"(pollution {pollution}, alpha {alpha}, elevation {elevation})");
                        previous = ratio;
                    }
                }
            }
        }
    }

    [Test]
    public void PollutionsColourEffectCollapsesWithAltitudeInProportionToTheAerosolColumn()
    {
        // §20b pinned this claim through a mired argument, and the argument was needed because
        // stacking two Kelvin lerps made the collapse look weaker than the column ratio (133 K against
        // 500 K, an apparent ~3.8x, where the columns differ by ~14x). The mired analysis showed the
        // perceptual collapse really was ~14x, and that number is what this test now reproduces —
        // exactly, and without any analysis, because §20d changed the representation to one where the
        // claim is arithmetic rather than an argument.
        //
        // Beer-Lambert transmission composes ADDITIVELY IN LOG SPACE, which is the same property
        // mireds have and the reason §20b's mired reading was the honest one. So the log of the
        // red/blue ratio shifts by exactly (tau_B - tau_R), which is linear in the aerosol column and
        // completely independent of the clean-air colour it is applied to. The collapse ratio is
        // therefore identically the column ratio, 1 / 0.0695 = 14.4 — the number §20b's mired
        // calculation arrived at, now falling out of the model rather than being derived alongside it.
        //
        // That agreement is the best evidence available offline that §20d is a change of
        // representation rather than a retune: two different models, built for different reasons,
        // agreeing to two significant figures on a quantity neither was fitted to.
        float seaLevelShift = LogRedBlueShiftFromAerosol(SeaLevel, 1f);
        float mountainShift = LogRedBlueShiftFromAerosol(0.6246f, 0.0695f);
        Assert.That(seaLevelShift / mountainShift, Is.GreaterThan(10f),
            "pollution's colour effect no longer collapses with altitude");
        Assert.That(seaLevelShift / mountainShift, Is.EqualTo(14.4f).Within(0.2f));
    }

    private static float LogRedBlueShiftFromAerosol(float pressureFraction, float aerosolFraction)
    {
        float clean = RedBlueRatio(HorizonColour(
            pressureFraction, CleanAir, AerosolSpectrum.ReferenceAngstromExponent));
        float dirty = RedBlueRatio(HorizonColour(
            pressureFraction, aerosolFraction, AerosolSpectrum.ReferenceAngstromExponent));
        return MathF.Log(dirty / clean);
    }

    [Test]
    public void BothColumnsReachZeroTogether_SoTheVacuumAgreementSurvivesTheSecondSpecies()
    {
        // The curve cannot enforce that its two fractions are a consistent pair, and the one place it
        // would matter is the h -> infinity limit that §20 cashes in as the vacuum agreement: an
        // aerosol column that outlived the air column would drag the airless endpoint away from
        // ZenithKelvin. It does not, because it is the FASTER-decaying of the two — but "obviously" is
        // not a test, and the guarantee lives in AtmosphericColumn rather than in the curve, so it is
        // asserted where the pair is actually produced.
        for (float siteAltitudeMetres = 0f; siteAltitudeMetres <= 100000f; siteAltitudeMetres += 5000f)
        {
            float aerosol = AtmosphericColumn.AerosolLoadFraction(siteAltitudeMetres, 1f);
            float rayleigh = AtmosphericColumn.RayleighPressureFraction(siteAltitudeMetres);
            Assert.That(aerosol, Is.LessThanOrEqualTo(rayleigh + Tolerance),
                $"the aerosol column outlived the air column at {siteAltitudeMetres} m");
        }
    }

    [Test]
    public void TintStrength_HasNoAerosolTerm_BecauseMieMutesRatherThanIntensifies()
    {
        // §20b is scoped to colour ONLY, and this is that scope asserted structurally rather than left
        // as prose — the same form §19's altitude-invariance test uses, and the same form that fails
        // when someone adds the term back.
        //
        // Two reasons it must not be there, both in SkyColorTemperature's own long note: where aerosol
        // actually exists the strength factor is already saturated at 1, so the term would be clamped
        // away over most of its band; and where it would not be clamped it would push the tint
        // STRONGER, which is backwards — aerosol greys and mutes a sunset rather than intensifying it.
        // The muting belongs to §9's saturation lane and is blocked behind #78; §8 writes neither
        // .saturation nor .glow, and two subsystems independently pulling saturation down is exactly
        // the failure #78 exists to fix.
        //
        // §20d extends the filter to the Angstrom exponent, and that addition is worth having because
        // the temptation is more plausible there: low alpha does mean a weaker colour shift, so
        // scaling strength by alpha looks like the obvious way to say "grey aerosol barely tints". It
        // would double-count what the spectral model already delivers (at alpha 0 its hue multiplier
        // is exactly (1, 1, 1)) and would simultaneously weaken the clean-air Rayleigh tint, which has
        // nothing to do with aerosol particle size at all.
        string[] offending = typeof(SkyColorTemperature)
            .GetMethod(nameof(SkyColorTemperature.TintStrength))!
            .GetParameters()
            .Where(parameter => parameter.Name!.Contains("aerosol", StringComparison.OrdinalIgnoreCase)
                || parameter.Name!.Contains("pollution", StringComparison.OrdinalIgnoreCase)
                || parameter.Name!.Contains("angstrom", StringComparison.OrdinalIgnoreCase)
                || parameter.Name!.Contains("exponent", StringComparison.OrdinalIgnoreCase))
            .Select(parameter => parameter.Name!)
            .ToArray();
        Assert.That(offending, Is.Empty,
            "TintStrength grew an aerosol/pollution/exponent parameter — §8's warm tint is a function "
            + "of sun geometry and air column alone, and every aerosol question is answered in colour; "
            + "the muting is a §9 ticket keyed on the same fraction, to be filed once #78 settles");
    }

    [Test]
    public void OzoneTwilight_IsInvariantToEverythingAerosol_AtEveryElevation()
    {
        // §19's counterpart to the altitude-invariance test above, and it is the stronger of the two
        // claims: the ozone layer sits at 20-30 km, roughly fifteen aerosol scale heights up, so no
        // amount of boundary-layer haze — and no particle size of it — is between the observer and the
        // Chappuis absorption at all. Polar night blue must therefore respond to neither.
        //
        // Asserted structurally for the same reason §20's version is: OzoneTwilightMath expresses the
        // invariance by having nowhere to put such a term, so the only mechanical way to state it is
        // that no such parameter has appeared. The filter covers §20d's vocabulary as well as §20b's,
        // which also guards the subtler mistake: §19 samples the SAME three wavelengths §20d does, and
        // that shared basis is exactly what would make it look reasonable to hand §19 an exponent.
        // Issue #82's latitude-keyed ozone column is an entirely different axis and is deliberately
        // not caught by this filter.
        string[] offending = typeof(OzoneTwilightMath)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SelectMany(method => method.GetParameters())
            .Where(parameter => parameter.Name!.Contains("pollution", StringComparison.OrdinalIgnoreCase)
                || parameter.Name!.Contains("aerosol", StringComparison.OrdinalIgnoreCase)
                || parameter.Name!.Contains("angstrom", StringComparison.OrdinalIgnoreCase)
                || parameter.Name!.Contains("exponent", StringComparison.OrdinalIgnoreCase))
            .Select(parameter => parameter.Name!)
            .ToArray();
        Assert.That(offending, Is.Empty,
            "OzoneTwilightMath grew a pollution/aerosol/exponent parameter — Chappuis absorption "
            + "happens ~15 aerosol scale heights above the boundary layer, so §19 cannot respond to "
            + "haze in any amount or of any particle size");

        // The counterpart half, so "invariant" cannot pass by §19 having gone flat: §8's warm colour
        // does respond to aerosol, and the two subsystems must stay measurably different in this.
        Assert.That(
            RedBlueRatio(HorizonColour(SeaLevel, 1f, AerosolSpectrum.ReferenceAngstromExponent)),
            Is.GreaterThan(RedBlueRatio(HorizonColour(
                SeaLevel, CleanAir, AerosolSpectrum.ReferenceAngstromExponent)) + 10f),
            "§8's warm colour no longer responds to pollution at all");
    }

    // --- §20d: the aerosol's spectral SHAPE (the Angstrom exponent) ---
    //
    // Everything above this line is a ONE-PARAMETER FAMILY: a ramp along the Planckian locus, with
    // altitude and pollution deciding only how far along it the sky travels. §20d adds the exponent
    // alpha in tau(lambda) ∝ lambda^-alpha, which decides the SHAPE of the extinction rather than its
    // depth, and so decides which hue path the sky is on at all.
    //
    // Read the tests in this block as answering two separate questions, because they are separately
    // falsifiable and the second is weaker than the ticket assumed:
    //
    //   1. Did we leave the one-parameter FAMILY? Yes, unambiguously, and that is the headline: at
    //      alpha 0 a FULL aerosol column produces no hue shift whatsoever. Under the locus model that
    //      was structurally impossible — aerosol amount and aerosol colour were the same knob, so a
    //      full column always meant a fixed 500 K of reddening. Decoupling them is the new capability.
    //
    //   2. Did we leave the LOCUS itself? Yes, but modestly. A monotone power-law filter applied to a
    //      blackbody lands close to another blackbody, because both are smooth and monotone in
    //      wavelength — the measured residual against the best-fitting Planckian point is a few
    //      hundredths of one channel, not a different family of colours. What genuinely widens is the
    //      RANGE of effective endpoints: at a full sea-level load the best-fit temperature runs from
    //      2000 K at alpha 0 down past 1300 K at alpha 2, instead of sitting at one fixed 1500 K.
    //
    // What the model provably CANNOT do, recorded here so nobody re-derives it from a failing test:
    // magenta. A monotone lambda^-alpha law orders the channels by wavelength, so green always lies
    // between red and blue and no exponent can lift blue above green. Magenta and purple twilights are
    // real, but they come from a spectral NOTCH rather than a slope — which is precisely §19's
    // Chappuis band, already modelled in this codebase, and is why real purple twilight is
    // ozone-driven rather than aerosol-driven.

    private static readonly float[] AllNamedExponents =
    {
        AerosolSpectrum.GreyExtinctionExponent,
        AerosolSpectrum.ThickDustExponent,
        AerosolSpectrum.CoarseDustExponent,
        AerosolSpectrum.UrbanHazeExponent,
        AerosolSpectrum.FineSmokeExponent,
        AerosolSpectrum.RayleighExponent,
    };

    // The monotonicity sweeps compare red/blue ratios that run from ~1.9 to ~520, so a fixed absolute
    // slack would be meaninglessly tight at one end and meaninglessly loose at the other. This is
    // large enough to absorb float rounding in a division and far smaller than any real step.
    private const float RatioTolerance = 0.001f;

    [Test]
    public void GreyExtinction_AttenuatesEveryChannelEqually_SoTheSunDimsWithoutShiftingHue()
    {
        // THE HEADLINE TEST — the case that proves we left the one-parameter family, because it is the
        // case the family could not contain. Large particles (fog, sea spray, a thick dust storm) are
        // bigger than every visible wavelength and scatter them all alike, so a heavy aerosol load
        // produces a white sun that merely dims. On the Planckian locus that is unsayable: "more
        // aerosol" and "more reddening" were the same statement.
        //
        // Asserted on the RAW transmission first, because that is where "equal attenuation in all
        // three channels" is actually a claim. The normalised form below cannot help but be (1, 1, 1)
        // if the raw form is equal, so asserting only the normalised form would be unfalsifiable.
        SkyColorTemperature.Rgb raw = AerosolSpectrum.SpectralTransmission(
            aerosolLoad: 1f, AerosolSpectrum.GreyExtinctionExponent);
        Assert.That(raw.G, Is.EqualTo(raw.R), "green was attenuated differently from red at alpha 0");
        Assert.That(raw.B, Is.EqualTo(raw.R), "blue was attenuated differently from red at alpha 0");
        Assert.That(raw.R, Is.LessThan(0.2f),
            "alpha 0 stopped attenuating at all — grey extinction still removes light, it just "
            + "removes all of it equally");

        // And the consequence: the hue multiplier is exactly (1, 1, 1), so the composed sky colour at
        // a FULL aerosol load is bit-identical to the clean-air colour. Exact equality, because at
        // alpha 0 all three optical depths are the same float and the normalisation divides a value by
        // itself — there is nothing here that should be approximately true.
        SkyColorTemperature.Rgb hue = AerosolSpectrum.HueMultiplier(
            aerosolLoad: 1f, AerosolSpectrum.GreyExtinctionExponent);
        Assert.That(hue.R, Is.EqualTo(1f));
        Assert.That(hue.G, Is.EqualTo(1f));
        Assert.That(hue.B, Is.EqualTo(1f));

        SkyColorTemperature.Rgb grey = HorizonColour(SeaLevel, 1f, AerosolSpectrum.GreyExtinctionExponent);
        SkyColorTemperature.Rgb clean = HorizonColour(SeaLevel, CleanAir, AerosolSpectrum.GreyExtinctionExponent);
        Assert.That(grey.R, Is.EqualTo(clean.R));
        Assert.That(grey.G, Is.EqualTo(clean.G));
        Assert.That(grey.B, Is.EqualTo(clean.B));

        // The contrast that makes the above mean something: at the reference exponent the SAME full
        // aerosol load moves the sky a long way. So alpha 0 is not "the aerosol term is broken", it is
        // "the aerosol term is being told the particles are large".
        Assert.That(
            RedBlueRatio(HorizonColour(SeaLevel, 1f, AerosolSpectrum.ReferenceAngstromExponent)),
            Is.GreaterThan(RedBlueRatio(grey) * 2f),
            "the reference exponent stopped reddening, so alpha 0 leaving the colour alone proves "
            + "nothing about particle size");
    }

    [Test]
    public void RayleighExponent_ReproducesTheLambdaToTheMinusFourthLaw()
    {
        // alpha = 4 IS Rayleigh's spectral shape, by definition — so the model's per-channel optical
        // depths must stand in exactly the ratios lambda^-4 gives. This is the definitional half of
        // the ticket's "alpha = 4 reproduces Rayleigh" invariant; the cross-check against §8's own
        // curve is the empirical half and is the test below.
        //
        // Stated as ratios rather than absolute depths because the absolute scale is
        // HorizonOpticalDepth's business (a fitted magnitude) while the ratios are physics.
        float red = AerosolSpectrum.OpticalDepth(
            1f, AerosolSpectrum.RayleighExponent, AerosolSpectrum.RedWavelengthNm);
        float green = AerosolSpectrum.OpticalDepth(
            1f, AerosolSpectrum.RayleighExponent, AerosolSpectrum.GreenWavelengthNm);
        float blue = AerosolSpectrum.OpticalDepth(
            1f, AerosolSpectrum.RayleighExponent, AerosolSpectrum.BlueWavelengthNm);

        Assert.That(red / green,
            Is.EqualTo(MathF.Pow(AerosolSpectrum.RedWavelengthNm / AerosolSpectrum.GreenWavelengthNm, -4f))
                .Within(1e-6f),
            "the red/green optical depth ratio is no longer lambda^-4");
        Assert.That(blue / green,
            Is.EqualTo(MathF.Pow(AerosolSpectrum.BlueWavelengthNm / AerosolSpectrum.GreenWavelengthNm, -4f))
                .Within(1e-6f),
            "the blue/green optical depth ratio is no longer lambda^-4");

        // Sanity on the direction, so the ratios above cannot be right while the sign is inverted:
        // Rayleigh removes short wavelengths hardest, which is why the daytime sky is blue and the
        // low sun is red.
        Assert.That(blue, Is.GreaterThan(green));
        Assert.That(green, Is.GreaterThan(red));
    }

    [Test]
    public void RayleighExponent_IndependentlyReproducesTheKelvinRampsOwnSpectralShape()
    {
        // THE CROSS-CHECK the ticket asks for: does alpha = 4 reproduce §8's existing curve? The two
        // models share no code and no constants, so agreement here is real evidence rather than
        // bookkeeping. §8's Kelvin ramp says that one full sea-level Rayleigh column takes the sun
        // from ZenithKelvin (the unreddened photosphere) to HorizonKelvin, via the Helland fit. This
        // model says the same journey is a lambda^-4 filter of some depth.
        //
        // The test fits that depth to ONE channel and then checks the other. Green sets the depth;
        // blue is then a free prediction with nothing tuned to it. Agreeing to ~15% between a
        // curve-fit blackbody locus and a three-sample power-law filter is a strong result — the two
        // are different approximations of the same physics, so exact agreement would be suspicious.
        SkyColorTemperature.Rgb unreddened = SkyColorTemperature.BlackbodyToRgb(SkyColorTemperature.ZenithKelvin);
        SkyColorTemperature.Rgb reddened = SkyColorTemperature.BlackbodyToRgb(SkyColorTemperature.HorizonKelvin);

        float shapeRed = MathF.Pow(
            AerosolSpectrum.RedWavelengthNm / AerosolSpectrum.ReferenceWavelengthNm, -AerosolSpectrum.RayleighExponent);
        float shapeGreen = 1f; // the reference wavelength is green
        float shapeBlue = MathF.Pow(
            AerosolSpectrum.BlueWavelengthNm / AerosolSpectrum.ReferenceWavelengthNm, -AerosolSpectrum.RayleighExponent);

        // Fit on green, relative to red (red is the least-attenuated channel and therefore the one the
        // normalisation is against, exactly as in the shipped model).
        float greenRatioWanted = (reddened.G / unreddened.G) / (reddened.R / unreddened.R);
        float fittedDepth = -MathF.Log(greenRatioWanted) / (shapeGreen - shapeRed);

        float bluePredicted = MathF.Exp(-fittedDepth * (shapeBlue - shapeRed));
        float blueWanted = (reddened.B / unreddened.B) / (reddened.R / unreddened.R);

        Assert.That(bluePredicted / blueWanted, Is.EqualTo(1f).Within(0.2f),
            "a lambda^-4 per-channel filter no longer reproduces the blue travel §8's own Kelvin ramp "
            + "performs — the two representations of Rayleigh reddening have diverged");

        // The fitted depth also has to be a sane number rather than an artefact: one sea-level
        // Rayleigh column at the horizon coming out near 2 optical depths is the same order as the
        // aerosol depth this file ships, which is what "aerosol is comparable to the air itself in a
        // heavily polluted column" should look like.
        Assert.That(fittedDepth, Is.EqualTo(1.94f).Within(0.05f));
    }

    [TestCase(AerosolSpectrum.GreyExtinctionExponent, 1f)] // no selectivity at all: R and B alike
    [TestCase(AerosolSpectrum.ThickDustExponent, 1.136f)]
    [TestCase(AerosolSpectrum.CoarseDustExponent, 1.585f)]
    [TestCase(AerosolSpectrum.UrbanHazeExponent, 2.431f)]
    [TestCase(AerosolSpectrum.FineSmokeExponent, 4.192f)]
    [TestCase(AerosolSpectrum.RayleighExponent, 28.373f)]
    public void RedBlueTransmissionRatio_MatchesTheParticleSizeTable(float alpha, float expectedRatio)
    {
        // The table in AerosolSpectrum's header, pinned as numbers. This is the quantity that IS the
        // hue: how much more of the red channel survives the aerosol than the blue channel. At alpha 0
        // it is exactly 1 (grey) and it grows to nearly 30 at Rayleigh's own exponent.
        SkyColorTemperature.Rgb raw = AerosolSpectrum.SpectralTransmission(aerosolLoad: 1f, alpha);
        Assert.That(raw.R / raw.B, Is.EqualTo(expectedRatio).Within(0.01f));
    }

    [Test]
    public void RedBlueTransmissionRatio_RisesMonotonicallyWithTheAngstromExponent()
    {
        // The invariant the whole keying depends on: a smaller particle (a larger alpha) must always
        // remove more blue relative to red, with no reversals anywhere in the band. If this ever
        // failed, the rainfall ramp would be mapping wetter tiles to arbitrary hues rather than to
        // consistently redder ones, and the biome story would be noise.
        float previous = 0f;
        for (float alpha = 0f; alpha <= AerosolSpectrum.RayleighExponent; alpha += 0.05f)
        {
            SkyColorTemperature.Rgb raw = AerosolSpectrum.SpectralTransmission(aerosolLoad: 1f, alpha);
            float ratio = raw.R / raw.B;
            Assert.That(ratio, Is.GreaterThan(previous), $"red/blue transmission ratio fell at alpha {alpha}");
            previous = ratio;
        }
    }

    [Test]
    public void AtTheReferenceExponent_ReproducesTheColourTwentyBShipped()
    {
        // THE SUBSUMPTION PIN. §20b's AerosolHorizonKelvin and this file's per-channel transmission are
        // two representations of one physical effect, so exactly one of them can be live — applying
        // both would double-count the reddening. §20d makes the spectral model the live one, and this
        // is the test that says the retirement was a change of REPRESENTATION and not a retune: at the
        // exponent §20b was implicitly calibrated at, the new model lands on the colour §20b shipped.
        //
        // Green matches to within float noise because HorizonOpticalDepth was fitted to make it do so.
        // Red matches trivially — the Helland fit saturates it across this whole range. Blue is the
        // free residual and the interesting one: §20b's 1500 K endpoint has blue EXACTLY zero, because
        // the fit pins blue at 0 below 1900 K, while this model leaves 0.022 of it.
        //
        // That residual is the entire reason the subsumption had to go in this direction rather than
        // the other. A zero blue channel is unrecoverable by any downstream multiplication, so a model
        // that composed a spectral correction ON TOP of §20b's endpoint could never produce the pale,
        // blue-retaining sun that a large-particle aerosol actually gives — the headline case above
        // would have been structurally unreachable. Behind a 0.35 blend the 0.022 is under 0.008 of
        // final sky colour, so nothing on screen moves.
        SkyColorTemperature.Rgb shipped = SkyColorTemperature.BlackbodyToRgb(
            AerosolSpectrum.CalibrationAnchorKelvin);
        SkyColorTemperature.Rgb reproduced = HorizonColour(
            SeaLevel, 1f, AerosolSpectrum.ReferenceAngstromExponent);

        Assert.That(reproduced.R, Is.EqualTo(shipped.R).Within(Tolerance),
            "the red channel drifted from §20b's shipped endpoint");
        Assert.That(reproduced.G, Is.EqualTo(shipped.G).Within(Tolerance),
            "HorizonOpticalDepth is no longer the value that reproduces §20b's green channel — the "
            + "calibration and the constant it was fitted to have come apart");
        Assert.That(shipped.B, Is.EqualTo(0f),
            "the Helland fit no longer crushes blue at 1500 K, which was the whole argument for "
            + "subsuming the locus endpoint rather than composing with it");
        Assert.That(reproduced.B, Is.EqualTo(0.0224f).Within(0.002f),
            "the blue residual moved — it is the one channel the calibration does not pin, so it is "
            + "the one that reports a change in the spectral model's shape");
    }

    // alpha 0 is the deliberate exception: it lands back ON the locus, at exactly the clean-air
    // temperature, because grey extinction shifts no hue at all. Its residual floor is therefore 0 and
    // the row is carrying the OTHER claim — that the effective endpoint is 3257 K, i.e. that a full
    // aerosol load moved the colour nowhere. That is the headline case, not a hole in the sweep.
    [TestCase(AerosolSpectrum.GreyExtinctionExponent, 3257f, 0f)]
    [TestCase(AerosolSpectrum.CoarseDustExponent, 2778f, 0.001f)]
    [TestCase(AerosolSpectrum.UrbanHazeExponent, 2496f, 0.001f)]
    [TestCase(AerosolSpectrum.FineSmokeExponent, 2264f, 0.005f)]
    [TestCase(AerosolSpectrum.RayleighExponent, 1916f, 0.02f)]
    public void TheEffectiveEndpointIsNowKeyedToParticleSize_AndSitsSlightlyOffTheLocus(
        float alpha, float expectedBestFitKelvin, float minimumResidual)
    {
        // The honest, quantified version of "we left the locus", measured at 20° rather than at the
        // horizon because that is where the clean-air colour still has blue to lose and the question
        // is therefore worth asking.
        //
        // Two claims, and the first is much the stronger. (1) The effective endpoint is now a function
        // of PARTICLE SIZE: the best-fitting colour temperature at a full sea-level aerosol load runs
        // from 3257 K at alpha 0 (i.e. the aerosol did nothing) down to ~1900 K at Rayleigh's own
        // exponent, where §20b had exactly one endpoint for every map. (2) The composed colour is
        // genuinely not ON the locus — but only just, by a few hundredths of a channel, because a
        // monotone power-law filter of a blackbody lands near another blackbody. Both are pinned, and
        // the second is pinned with its real magnitude rather than an aspirational one.
        SkyColorTemperature.Rgb composed = SkyColorTemperature.SkyColorForElevation(
            20f, SeaLevel, aerosolFraction: 1f, alpha, inVacuum: false);

        float bestKelvin = 0f;
        float bestResidual = float.MaxValue;
        for (float kelvin = 1000f; kelvin <= 9000f; kelvin += 1f)
        {
            SkyColorTemperature.Rgb candidate = SkyColorTemperature.BlackbodyToRgb(kelvin);
            float residual = MathF.Max(
                MathF.Abs(candidate.R - composed.R),
                MathF.Max(MathF.Abs(candidate.G - composed.G), MathF.Abs(candidate.B - composed.B)));
            if (residual < bestResidual)
            {
                bestResidual = residual;
                bestKelvin = kelvin;
            }
        }

        Assert.That(bestKelvin, Is.EqualTo(expectedBestFitKelvin).Within(30f),
            "the effective colour temperature this particle size lands on has moved");
        Assert.That(bestResidual, Is.GreaterThanOrEqualTo(minimumResidual),
            "the composed colour is now exactly reproducible by a blackbody, which would mean the "
            + "spectral model had collapsed back onto the Planckian locus it exists to leave");
    }

    [Test]
    public void NoExponentCanProduceMagenta_BecauseAPowerLawCannotNotchTheSpectrum()
    {
        // Recorded as a test rather than only as a comment, because "salmon and magenta at high alpha"
        // is a plausible-sounding claim that will be made again. It is wrong, and it is wrong
        // structurally rather than by tuning: tau(lambda) ∝ lambda^-alpha is MONOTONE in wavelength,
        // so the three channels are always ordered by wavelength and green can never be attenuated
        // more than both of its neighbours. Magenta requires exactly that — blue lifted relative to
        // green — which needs a spectral NOTCH.
        //
        // The codebase already has the notch that really produces purple twilight: §19's Chappuis
        // ozone band, which absorbs at 450-780 nm peaking at 603 nm, i.e. in the MIDDLE. That is why
        // real purple twilights are ozone-driven. Anyone chasing magenta should be reading
        // OzoneTwilightMath, not raising alpha here.
        for (float alpha = 0f; alpha <= AerosolSpectrum.RayleighExponent; alpha += 0.1f)
        {
            SkyColorTemperature.Rgb raw = AerosolSpectrum.SpectralTransmission(aerosolLoad: 1f, alpha);
            Assert.That(raw.G, Is.LessThanOrEqualTo(raw.R + Tolerance),
                $"green survived better than red at alpha {alpha}");
            Assert.That(raw.G, Is.GreaterThanOrEqualTo(raw.B - Tolerance),
                $"green was attenuated harder than blue at alpha {alpha} — a power law cannot notch, "
                + "so this would mean the wavelength ordering had been broken");
        }
    }

    [TestCase(0f, AerosolSpectrum.ThickDustExponent)] // drier than any vanilla tile: clamped
    [TestCase(340f, AerosolSpectrum.ThickDustExponent)] // vanilla's ExtremeDesert cutoff
    [TestCase(600f, 0.482f)] // vanilla's Desert/Arid cutoff: coarse dust, an ochre sunset
    [TestCase(1000f, 0.916f)]
    [TestCase(1354f, AerosolSpectrum.ReferenceAngstromExponent)] // where the shipped §20b look sits
    [TestCase(2000f, AerosolSpectrum.FineSmokeExponent)] // vanilla's TropicalRainforest cutoff
    [TestCase(5000f, AerosolSpectrum.FineSmokeExponent)] // wetter than any vanilla tile: clamped
    public void AngstromExponentForRainfall_TracksVanillasOwnBiomeBreakpoints(
        float rainfallMillimetres, float expected)
    {
        // The keying, pinned at the breakpoints vanilla's own BiomeWorkers use — 340 mm for
        // ExtremeDesert and 2000 mm for TropicalRainforest — because keying on those is what makes
        // this "biome-derived" rather than a table of magic numbers next to a table of biome names.
        //
        // The 1354 mm row is the one worth watching: it is where the reference exponent lands, it sits
        // inside the temperate/boreal band most colonies are founded in, and it is why §20d ships
        // without moving the sunset most players already have.
        Assert.That(AerosolSpectrum.AngstromExponentForRainfall(rainfallMillimetres),
            Is.EqualTo(expected).Within(0.005f));
    }

    [Test]
    public void AngstromExponentForRainfall_IsMonotonicAndStaysInsideThePhysicalBand()
    {
        // Wetter must never mean coarser. The direction is the physically defensible part of the
        // keying — arid ground lofts coarse mineral dust while wet ground supplies fine secondary and
        // biogenic particles — so a reversal anywhere would mean the mapping had stopped meaning what
        // its comment says it means.
        float previous = 0f;
        for (float rainfall = -500f; rainfall <= 8000f; rainfall += 50f)
        {
            float alpha = AerosolSpectrum.AngstromExponentForRainfall(rainfall);
            Assert.That(alpha, Is.GreaterThanOrEqualTo(previous - Tolerance),
                $"a wetter tile produced a coarser aerosol at {rainfall} mm");
            Assert.That(alpha, Is.InRange(
                AerosolSpectrum.ThickDustExponent, AerosolSpectrum.FineSmokeExponent),
                $"the exponent left its keyed band at {rainfall} mm");
            previous = alpha;
        }
    }

    [TestCase(-1f)] // a negative exponent would attenuate blue LESS than red, inverting the hue shift
    [TestCase(9f)] // more selective than the air molecules themselves, which no aerosol is
    public void AngstromExponent_IsClampedToThePhysicalBand(float alpha)
    {
        // Defence in depth against a caller that computes its own exponent rather than going through
        // AngstromExponentForRainfall. The floor is the one that matters: a negative exponent is not
        // merely out of range, it silently reverses the sign of the whole effect and would show up as
        // a bluer sunset on polluted tiles with nothing anywhere reporting a problem.
        float clamped = alpha < 0f ? AerosolSpectrum.GreyExtinctionExponent : AerosolSpectrum.RayleighExponent;
        SkyColorTemperature.Rgb actual = AerosolSpectrum.SpectralTransmission(aerosolLoad: 1f, alpha);
        SkyColorTemperature.Rgb expected = AerosolSpectrum.SpectralTransmission(aerosolLoad: 1f, clamped);
        Assert.That(actual.R, Is.EqualTo(expected.R).Within(Tolerance));
        Assert.That(actual.G, Is.EqualTo(expected.G).Within(Tolerance));
        Assert.That(actual.B, Is.EqualTo(expected.B).Within(Tolerance));
    }

    [Test]
    public void HueMultiplier_IsNeverAboveOne_SoTheSpectrumCannotBrightenTheSky()
    {
        // §8 is a colour-only lane and the normalisation is what keeps the spectral model inside it: a
        // multiplier above 1 in any channel would be adding light rather than removing it, which is
        // both unphysical for an extinction law and a brightness change smuggled into a patch that
        // promises not to make one. Swept over the whole exponent and load range rather than spot
        // checked, because the failure would be a boundary case.
        for (float alpha = 0f; alpha <= AerosolSpectrum.RayleighExponent; alpha += 0.25f)
        {
            for (float load = 0f; load <= 1f; load += 0.05f)
            {
                SkyColorTemperature.Rgb hue = AerosolSpectrum.HueMultiplier(load, alpha);
                Assert.That(hue.R, Is.InRange(0f, 1f), $"R left [0, 1] at alpha {alpha}, load {load}");
                Assert.That(hue.G, Is.InRange(0f, 1f), $"G left [0, 1] at alpha {alpha}, load {load}");
                Assert.That(hue.B, Is.InRange(0f, 1f), $"B left [0, 1] at alpha {alpha}, load {load}");
            }
        }
    }

    [Test]
    public void TheAerosolColourFadesOutWithSunAltitude_AtExactlyTheRateTwentyBsEndpointDid()
    {
        // Optical depth is a path length, so a high sun barely looks through the boundary layer at all.
        // §20b got that behaviour for free, because its aerosol endpoint was consumed by the same
        // elevation lerp everything else was; §20d has to reproduce it deliberately, by scaling the
        // load with LowSunFraction rather than inventing a second ramp.
        //
        // At and above DaylightAltitudeDegrees the aerosol term must be gone ENTIRELY — exact
        // equality, since LowSunFraction returns exactly 0 there and the multiplier is exactly 1.
        foreach (float elevation in new[] { 60f, 75f, 90f })
        {
            SkyColorTemperature.Rgb dirty = SkyColorTemperature.SkyColorForElevation(
                elevation, SeaLevel, aerosolFraction: 1f, AerosolSpectrum.RayleighExponent, inVacuum: false);
            SkyColorTemperature.Rgb clean = SkyColorTemperature.BlackbodyToRgb(
                SkyColorTemperature.ColorTemperatureKelvin(elevation, SeaLevel, inVacuum: false));
            Assert.That(dirty.R, Is.EqualTo(clean.R), $"aerosol still tinted R at elevation {elevation}");
            Assert.That(dirty.G, Is.EqualTo(clean.G), $"aerosol still tinted G at elevation {elevation}");
            Assert.That(dirty.B, Is.EqualTo(clean.B), $"aerosol still tinted B at elevation {elevation}");
        }

        // And it fades monotonically on the way there rather than switching off at a threshold.
        float previous = float.MaxValue;
        for (float elevation = 0f; elevation <= 60f; elevation += 2.5f)
        {
            float shift = MathF.Log(RedBlueRatio(SkyColorTemperature.SkyColorForElevation(
                    elevation, SeaLevel, aerosolFraction: 1f, AerosolSpectrum.ReferenceAngstromExponent,
                    inVacuum: false)))
                - MathF.Log(RedBlueRatio(SkyColorTemperature.SkyColorForElevation(
                    elevation, SeaLevel, CleanAir, AerosolSpectrum.ReferenceAngstromExponent,
                    inVacuum: false)));
            Assert.That(shift, Is.LessThanOrEqualTo(previous + RatioTolerance),
                $"the aerosol's colour shift grew as the sun climbed, at elevation {elevation}");
            previous = shift;
        }

        Assert.That(previous, Is.EqualTo(0f).Within(Tolerance),
            "the aerosol colour shift did not reach zero by the daylight altitude");
    }
}
