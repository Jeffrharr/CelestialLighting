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
        Assert.That(SkyColorTemperature.ColorTemperatureKelvin(elevation, SeaLevel, CleanAir, inVacuum: false),
            Is.EqualTo(expected).Within(0.5f));
    }

    [Test]
    public void ColorTemperatureKelvin_IsMonotonicNonDecreasing_AsSunClimbs()
    {
        float previous = SkyColorTemperature.ColorTemperatureKelvin(-10f, SeaLevel, CleanAir, inVacuum: false);
        for (float elevation = -10f; elevation <= 90f; elevation += 2.5f)
        {
            float current = SkyColorTemperature.ColorTemperatureKelvin(elevation, SeaLevel, CleanAir, inVacuum: false);
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
        SkyColorTemperature.Rgb direct = SkyColorTemperature.SkyColorForElevation(20f, SeaLevel, CleanAir, inVacuum: false);
        SkyColorTemperature.Rgb composed = SkyColorTemperature.BlackbodyToRgb(
            SkyColorTemperature.ColorTemperatureKelvin(20f, SeaLevel, CleanAir, inVacuum: false));
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
    public void HorizonKelvinForColumns_WalksTheWarmEndpointTowardTheUnreddenedAnchor(
        float pressureFraction, float expected)
    {
        Assert.That(SkyColorTemperature.HorizonKelvinForColumns(pressureFraction, CleanAir),
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
            float previousKelvin = SkyColorTemperature.ColorTemperatureKelvin(elevation, SeaLevel, CleanAir, inVacuum: false);
            float previousTint = SkyColorTemperature.TintStrength(elevation, SeaLevel, inVacuum: false);
            for (float siteAltitudeMetres = 0f; siteAltitudeMetres <= 9000f; siteAltitudeMetres += 250f)
            {
                float pressureFraction = AtmosphericColumn.RayleighPressureFraction(siteAltitudeMetres);
                float kelvin = SkyColorTemperature.ColorTemperatureKelvin(elevation, pressureFraction, CleanAir, inVacuum: false);
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
            Assert.That(SkyColorTemperature.ColorTemperatureKelvin(elevation, pressureFraction: 0f, aerosolFraction: CleanAir, inVacuum: false),
                Is.EqualTo(SkyColorTemperature.ColorTemperatureKelvin(elevation, SeaLevel, CleanAir, inVacuum: true)),
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
        Assert.That(SkyColorTemperature.ColorTemperatureKelvin(0f, pressureFraction: 1.5f, aerosolFraction: CleanAir, inVacuum: false),
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

    [TestCase(1f, 0f, SkyColorTemperature.HorizonKelvin)] // clean sea level: unchanged from §20
    [TestCase(1f, 1f, SkyColorTemperature.AerosolHorizonKelvin)] // fully polluted sea level: the new endpoint
    [TestCase(1f, 0.5f, 1333.3f)] // half a column of haze: linear in MIREDS between them
    [TestCase(0.6246f, 0f, 2650.1f)] // clean 4000 m: §20's value, pinned again as a regression guard
    [TestCase(0.6246f, 0.0695f, 2377.5f)] // 4000 m at pollution 1.0 — only ~134 K of warming left
    [TestCase(0.8382f, 0.3679f, 1537.2f)] // 1500 m at pollution 1.0
    public void HorizonKelvinForColumns_CarriesTheWarmEndpointPastSeaLevelWhenTheAirIsDirty(
        float pressureFraction, float aerosolFraction, float expected)
    {
        Assert.That(SkyColorTemperature.HorizonKelvinForColumns(pressureFraction, aerosolFraction),
            Is.EqualTo(expected).Within(0.5f));
    }

    [Test]
    public void ZeroPollution_IsBitIdenticalToTheSiteAltitudeOnlyCurve()
    {
        // The regression pin, asserted as EXACT equality rather than within a tolerance, in the same
        // spirit as ZeroPressure_ReproducesTheVacuumValuesExactly above: at pollution 0 the second
        // lerp has to be a true no-op, not a nearly-no-op. Every tile in a game without Biotech takes
        // this path, so "almost unchanged" would mean the mod's default behaviour had silently moved
        // for every existing colony.
        //
        // §20's formula is restated here rather than called, precisely because the function that used
        // to hold it no longer exists in that form. That is what makes this a pin on the BEHAVIOUR
        // rather than a tautology about whatever the current code does.
        //
        // Restated in MIREDS, matching §20 after the Kelvin -> mired switch: the endpoint is
        // 10^6 / (mired(Zenith) + p * (mired(Horizon) - mired(Zenith))). Getting this wrong is how
        // the pin silently becomes a tautology, so it is written out longhand.
        for (float siteAltitudeMetres = 0f; siteAltitudeMetres <= 9000f; siteAltitudeMetres += 500f)
        {
            float pressureFraction = AtmosphericColumn.RayleighPressureFraction(siteAltitudeMetres);
            float zenithMired = 1e6f / SkyColorTemperature.ZenithKelvin;
            float horizonMired = 1e6f / SkyColorTemperature.HorizonKelvin;
            float preAerosolEndpoint =
                1e6f / (zenithMired + (horizonMired - zenithMired) * pressureFraction);

            Assert.That(SkyColorTemperature.HorizonKelvinForColumns(pressureFraction, CleanAir),
                Is.EqualTo(preAerosolEndpoint),
                $"the unpolluted horizon endpoint moved at {siteAltitudeMetres} m");

            for (float elevation = -30f; elevation <= 90f; elevation += 7.5f)
            {
                float t = (elevation < 0f ? 0f : elevation > 60f ? 60f : elevation) / 60f;
                float preAerosolKelvin = preAerosolEndpoint
                    + (SkyColorTemperature.ZenithKelvin - preAerosolEndpoint) * t;
                Assert.That(
                    SkyColorTemperature.ColorTemperatureKelvin(elevation, pressureFraction, CleanAir, inVacuum: false),
                    Is.EqualTo(preAerosolKelvin),
                    $"the unpolluted curve moved at {siteAltitudeMetres} m, elevation {elevation}");
            }
        }
    }

    [Test]
    public void Warmth_IsMonotonicallyNonDecreasing_InPollution()
    {
        // Pollution may only ever make the sky warmer (a LOWER colour temperature), never cooler and
        // never non-monotonically — the mirror of the site-altitude invariant above. Swept over both
        // sun elevation and site altitude, because the aerosol term's effect is a function of all
        // three and a monotonicity that only held at sea level would be worth very little.
        for (float siteAltitudeMetres = 0f; siteAltitudeMetres <= 6000f; siteAltitudeMetres += 500f)
        {
            float pressureFraction = AtmosphericColumn.RayleighPressureFraction(siteAltitudeMetres);
            for (float elevation = -10f; elevation <= 90f; elevation += 10f)
            {
                float previous = float.MaxValue;
                for (float pollution = 0f; pollution <= 1f; pollution += 0.05f)
                {
                    float aerosolFraction = AtmosphericColumn.AerosolLoadFraction(siteAltitudeMetres, pollution);
                    float kelvin = SkyColorTemperature.ColorTemperatureKelvin(
                        elevation, pressureFraction, aerosolFraction, inVacuum: false);
                    Assert.That(kelvin, Is.LessThanOrEqualTo(previous + Tolerance),
                        $"sky got cooler as pollution rose (pollution {pollution}, "
                        + $"{siteAltitudeMetres} m, elevation {elevation})");
                    previous = kelvin;
                }
            }
        }
    }

    [Test]
    public void Warmth_IsStillMonotonicallyNonIncreasing_InSiteAltitude_AtEveryPollutionLevel()
    {
        // §20's altitude invariant, re-asserted with the new term switched on. It is not obvious for
        // free: climbing raises the clean-air endpoint (cooler) but ALSO thins the aerosol column
        // (cooler again), so the two effects happen to agree — and this is the test that says so
        // rather than leaving it as an argument in a comment.
        for (float pollution = 0f; pollution <= 1f; pollution += 0.25f)
        {
            for (float elevation = -10f; elevation <= 90f; elevation += 10f)
            {
                float previous = float.MinValue;
                for (float siteAltitudeMetres = 0f; siteAltitudeMetres <= 9000f; siteAltitudeMetres += 250f)
                {
                    float kelvin = SkyColorTemperature.ColorTemperatureKelvin(
                        elevation,
                        AtmosphericColumn.RayleighPressureFraction(siteAltitudeMetres),
                        AtmosphericColumn.AerosolLoadFraction(siteAltitudeMetres, pollution),
                        inVacuum: false);
                    Assert.That(kelvin, Is.GreaterThanOrEqualTo(previous - Tolerance),
                        $"sky got warmer with altitude at {siteAltitudeMetres} m "
                        + $"(pollution {pollution}, elevation {elevation})");
                    previous = kelvin;
                }
            }
        }
    }

    [Test]
    public void PollutionsWarmingCollapsesWithAltitude_ButLessThanTheColumnAlone()
    {
        // The endpoint geometry partially OFFSETS the aerosol column's collapse, and the size of that
        // offset is worth pinning because it is the one place §20b's headline claim could quietly
        // erode.
        //
        // The aerosol fraction itself falls 14.4x between sea level and 4000 m (1.0 -> 0.0695). But
        // the shift is linear in that fraction times the distance from the clean-air endpoint down to
        // AerosolHorizonKelvin, and altitude has MOVED that endpoint up — so the bracket is wider at
        // altitude than at sea level:
        //
        //   sea level  10^6/1500 - 10^6/2000   = 666.67 - 500.00 = 166.67 mired of headroom
        //   4000 m     10^6/1500 - 10^6/2650   = 666.67 - 377.34 = 289.33 mired of headroom
        //
        // so the net suppression is 14.388 * (166.667 / 289.329) = 8.29x, not 14.4x. Thinner air
        // leaves more room to redden into, which gives back a little of what the missing haze took.
        //
        // 8.29x is still the claim holding, not failing: it sits far above the 5.67x ratio of the two
        // scale heights, and close to the 8.99x ratio of the two columns. "A mountain base is above
        // the smog" survives; it is simply worth 8x rather than 14x, and a reader who derived 14 from
        // the fractions alone deserves to find out here rather than from a screenshot.
        float seaLevelMired = MiredShiftFromPollution(SeaLevel, 1f);
        float mountainMired = MiredShiftFromPollution(0.6246f, 0.0695f);

        Assert.That(seaLevelMired / mountainMired, Is.GreaterThan(5.67f),
            "pollution must still collapse faster with altitude than the scale-height ratio alone");
        Assert.That(seaLevelMired / mountainMired, Is.EqualTo(11.55f).Within(0.15f));
    }

    private static float MiredShiftFromPollution(float pressureFraction, float aerosolFraction)
    {
        float clean = SkyColorTemperature.HorizonKelvinForColumns(pressureFraction, CleanAir);
        float dirty = SkyColorTemperature.HorizonKelvinForColumns(pressureFraction, aerosolFraction);
        return 1e6f / dirty - 1e6f / clean;
    }

    [Test]
    public void BothColumnsReachZeroTogether_SoTheVacuumAgreementSurvivesTheSecondSpecies()
    {
        // HorizonKelvinForColumns cannot enforce that its two fractions are a consistent pair, and
        // the one place it would matter is the h -> infinity limit that §20 cashes in as the vacuum
        // agreement: an aerosol column that outlived the air column would drag the airless endpoint
        // away from ZenithKelvin. It does not, because it is the FASTER-decaying of the two — but
        // "obviously" is not a test, and the guarantee lives in AtmosphericColumn rather than in the
        // curve, so it is asserted where the pair is actually produced.
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
        // §20b is scoped to colour temperature ONLY, and this is that scope asserted structurally
        // rather than left as prose — the same form §19's altitude-invariance test uses, and the same
        // form that fails when someone adds the term back.
        //
        // Two reasons it must not be there, both in SkyColorTemperature's own long note: where
        // aerosol actually exists the strength factor is already saturated at 1, so the term would be
        // clamped away over most of its band; and where it would not be clamped it would push the
        // tint STRONGER, which is backwards — Mie scattering is nearly wavelength-flat next to
        // Rayleigh's λ^-4, so heavy aerosol greys and mutes a sunset rather than intensifying it.
        // The muting belongs to §9's saturation lane and is blocked behind #78; §8 writes neither
        // .saturation nor .glow, and two subsystems independently pulling saturation down is exactly
        // the failure #78 exists to fix.
        string[] offending = typeof(SkyColorTemperature)
            .GetMethod(nameof(SkyColorTemperature.TintStrength))!
            .GetParameters()
            .Where(parameter => parameter.Name!.Contains("aerosol", StringComparison.OrdinalIgnoreCase)
                || parameter.Name!.Contains("pollution", StringComparison.OrdinalIgnoreCase))
            .Select(parameter => parameter.Name!)
            .ToArray();
        Assert.That(offending, Is.Empty,
            "TintStrength grew an aerosol/pollution parameter — §20b is colour-temperature only, and "
            + "strengthening the tint models Mie scattering backwards; the muting is a §9 ticket "
            + "keyed on the same fraction, to be filed once #78 settles");
    }

    [Test]
    public void OzoneTwilightBlue_IsPollutionInvariant_AtEveryElevation()
    {
        // §19's counterpart to the altitude-invariance test above, and it is the stronger of the two
        // claims: the ozone layer sits at 20-30 km, roughly fifteen aerosol scale heights up, so no
        // amount of boundary-layer haze is between the observer and the Chappuis absorption at all.
        // Polar night blue must therefore not respond to pollution at any elevation.
        //
        // Asserted structurally for the same reason §20's version is: OzoneTwilightMath expresses the
        // invariance by having nowhere to put such a term, so the only mechanical way to state it is
        // that no such parameter has appeared. Stated here rather than in OzoneTwilightMathTests to
        // keep §20b's whole scope story in one place; issue #82's latitude-keyed ozone column is an
        // entirely different axis and is deliberately not caught by this filter.
        string[] offending = typeof(OzoneTwilightMath)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SelectMany(method => method.GetParameters())
            .Where(parameter => parameter.Name!.Contains("pollution", StringComparison.OrdinalIgnoreCase)
                || parameter.Name!.Contains("aerosol", StringComparison.OrdinalIgnoreCase))
            .Select(parameter => parameter.Name!)
            .ToArray();
        Assert.That(offending, Is.Empty,
            "OzoneTwilightMath grew a pollution/aerosol parameter — Chappuis absorption happens ~15 "
            + "aerosol scale heights above the boundary layer, so §19 cannot respond to haze");

        // The counterpart half, so "invariant" cannot pass by §19 having gone flat: §8's warm tint
        // does respond, and the two subsystems must stay measurably different in this respect.
        float cleanEndpoint = SkyColorTemperature.HorizonKelvinForColumns(SeaLevel, CleanAir);
        float pollutedEndpoint = SkyColorTemperature.HorizonKelvinForColumns(SeaLevel, 1f);
        Assert.That(pollutedEndpoint, Is.LessThan(cleanEndpoint - 100f),
            "§8's warm endpoint no longer responds to pollution at all");
    }
}
