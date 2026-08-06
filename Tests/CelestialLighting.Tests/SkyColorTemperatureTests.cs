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
        SkyColorTemperature.Rgb direct = SkyColorTemperature.SkyColorForElevation(20f, SeaLevel, inVacuum: false);
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
        // The reason the scale height is a parameter rather than baked in (issue #83 adds an aerosol
        // species at 1500 m to this same file): at 3000 m a near-surface aerosol layer has almost
        // entirely dropped below the observer while two thirds of the bulk air is still overhead.
        // If these two ever came out equal, the shared model has collapsed into a Rayleigh-only one.
        float bulkAir = AtmosphericColumn.ColumnFraction(3000f, AtmosphericColumn.RayleighScaleHeightMetres);
        float nearSurface = AtmosphericColumn.ColumnFraction(3000f, 1500f);
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

    [TestCase(1f, SkyColorTemperature.HorizonKelvin)] // sea level: unchanged from before §20
    [TestCase(0.9883f, 2044.1f)] // 100 m
    [TestCase(0.8382f, 2610.2f)] // 1500 m
    [TestCase(0.6246f, 3415.9f)] // 4000 m: a markedly whiter horizon sun
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
            "OzoneTwilightMath grew a site-altitude/pressure parameter — Chappuis absorption is at "
            + "20-30 km, above every mountain, so §19 must not scale with where the map sits");
    }
}
