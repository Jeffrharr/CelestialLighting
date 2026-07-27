namespace CelestialLighting.Tests;

// Subsystem 6b: absolute ground illuminance in lux, and the shadow contrast that follows from it.
//
// These tests are deliberately stated against published real-world photometry (the standard twilight
// definitions, the moon's apparent magnitudes) rather than against whatever the implementation
// currently returns. That is the whole claim of §6b — that moon-shadow visibility falls out of real
// numbers instead of a tuned curve — so a test that only pinned the current output would verify
// nothing about it.
[TestFixture]
public class IlluminanceMathTests
{
    // Generous by the standards of this suite, and intentionally so: these are physical anchors known
    // to about a significant figure, not exact constants. A test tighter than the data would be
    // claiming precision the model does not have.
    private const float RelativeTolerance = 0.02f;

    private static void AssertLuxWithin(float actual, float expected, string what)
    {
        Assert.That(actual, Is.EqualTo(expected).Within(expected * RelativeTolerance),
            $"{what}: expected ~{expected} lux, got {actual}");
    }

    // --- The anchor table reproduces the standard lighting conditions ---

    [Test]
    public void AmbientSkyLux_MatchesTheStandardTwilightDefinitions()
    {
        AssertLuxWithin(IlluminanceMath.AmbientSkyLux(0f), 400f, "sun at the horizon");
        AssertLuxWithin(IlluminanceMath.AmbientSkyLux(-6f), 3.4f, "end of civil twilight");
        AssertLuxWithin(IlluminanceMath.AmbientSkyLux(-12f), 0.008f, "end of nautical twilight");
        AssertLuxWithin(IlluminanceMath.AmbientSkyLux(-18f), 0.001f, "end of astronomical twilight");
    }

    [Test]
    public void AmbientSkyLux_MiddayIsAboutAHundredThousandLux()
    {
        // The single most-quoted figure in the whole model, and the one that makes the daylight
        // washout four orders of magnitude rather than two.
        Assert.That(IlluminanceMath.AmbientSkyLux(90f), Is.GreaterThan(100000f));
        Assert.That(IlluminanceMath.AmbientSkyLux(90f), Is.LessThan(150000f));
    }

    [Test]
    public void AmbientSkyLux_ClampsToTheNightFloorBelowAstronomicalTwilight()
    {
        // The sky stops darkening once the sun's contribution is gone — below -18 there is only
        // starlight and airglow, and how far below makes no difference.
        Assert.That(IlluminanceMath.AmbientSkyLux(-25f),
            Is.EqualTo(IlluminanceMath.NightSkyFloorLux));
        Assert.That(IlluminanceMath.AmbientSkyLux(-90f),
            Is.EqualTo(IlluminanceMath.NightSkyFloorLux));
    }

    [Test]
    public void AmbientSkyLux_IsMonotonic()
    {
        float previous = 0f;
        for (float elevation = -90f; elevation <= 90f; elevation += 0.5f)
        {
            float lux = IlluminanceMath.AmbientSkyLux(elevation);
            Assert.That(lux, Is.GreaterThanOrEqualTo(previous),
                $"sky got darker as the sun rose, at elevation {elevation:0.0}");
            previous = lux;
        }
    }

    [Test]
    public void AmbientSkyLux_InterpolatesInLogSpace_NotLinearly()
    {
        // Twilight falls roughly a decade per 3-4 degrees, so the midpoint of a segment must be the
        // GEOMETRIC mean of its endpoints, not the arithmetic one. Getting this wrong is subtle and
        // would push the moon shadow's fade-in several degrees late: linear interpolation would put
        // the -3 degree sky at ~200 lux instead of ~37.
        float atMinusThree = IlluminanceMath.AmbientSkyLux(-3f);
        float geometricMean = MathF.Sqrt(400f * 3.4f); // ~36.9

        Assert.That(atMinusThree, Is.EqualTo(geometricMean).Within(geometricMean * RelativeTolerance));
        Assert.That(atMinusThree, Is.LessThan(100f), "linear interpolation would land near 200 lux here");
    }

    // --- Moonlight ---

    [Test]
    public void MoonLux_FullMoonAtZenith_IsAboutAQuarterLux()
    {
        AssertLuxWithin(IlluminanceMath.MoonLux(1f, 90f),
            IlluminanceMath.FullMoonZenithLux, "full moon at zenith");
    }

    [Test]
    public void MoonLux_FirstQuarterIsAboutATwelfthOfFull_NotAHalf()
    {
        // The published magnitude difference between full (-12.74) and first quarter (-10.0) is 2.74,
        // a factor of ~12.4. Modeling phase linearly — the single most common way to get moonlight
        // wrong, and what this mod did before §6b — would make this 2.0.
        float ratio = IlluminanceMath.MoonLux(1f, 90f) / IlluminanceMath.MoonLux(0.5f, 90f);

        Assert.That(ratio, Is.GreaterThan(9f).And.LessThan(14f),
            $"first quarter came out {ratio:0.0}x fainter than full; published photometry says ~12x");
    }

    [Test]
    public void MoonLux_ScalesWithAltitudeBySineOfElevation()
    {
        // Standard cosine-of-incidence for light landing on a horizontal surface: a moon 30 degrees up
        // delivers exactly half what the same moon delivers overhead.
        Assert.That(IlluminanceMath.MoonLux(1f, 30f),
            Is.EqualTo(IlluminanceMath.MoonLux(1f, 90f) * 0.5f).Within(1e-4f));
    }

    [Test]
    public void MoonLux_IsZero_BelowTheRefractionHorizon()
    {
        // Shares Formulas' horizon with the sun, so the two bodies can never disagree about where the
        // horizon is.
        Assert.That(IlluminanceMath.MoonLux(1f, Formulas.AtmosphericRefractionDegrees), Is.EqualTo(0f));
        Assert.That(IlluminanceMath.MoonLux(1f, -10f), Is.EqualTo(0f));
    }

    [Test]
    public void MoonLux_NewMoonEmitsNothing()
    {
        Assert.That(IlluminanceMath.MoonLux(0f, 90f), Is.EqualTo(0f));
    }

    // --- Contrast ---

    [Test]
    public void ShadowContrast_ApproachesOne_WhenTheCasterDominates()
    {
        Assert.That(IlluminanceMath.ShadowContrast(casterLux: 100f, ambientLux: 0.001f),
            Is.GreaterThan(0.99f));
    }

    [Test]
    public void ShadowContrast_ApproachesZero_WhenTheCasterIsDrowned()
    {
        Assert.That(IlluminanceMath.ShadowContrast(casterLux: 0.267f, ambientLux: 100000f),
            Is.LessThan(0.0001f));
    }

    [Test]
    public void ShadowContrast_IsAHalf_WhenCasterAndAmbientMatch()
    {
        Assert.That(IlluminanceMath.ShadowContrast(5f, 5f), Is.EqualTo(0.5f).Within(1e-6f));
    }

    [Test]
    public void ShadowContrast_IsZero_InTotalDarkness()
    {
        // You cannot see a shadow in a room with the lights off.
        Assert.That(IlluminanceMath.ShadowContrast(0f, 0f), Is.EqualTo(0f));
    }

    // --- The composed claim §6b exists to make ---

    [Test]
    public void FullMoonShadow_IsFourOrdersOfMagnitudeBelowVisible_AtMidday()
    {
        // The finding that motivated the whole subsystem: sunlight does not prevent the moon from
        // casting a shadow, it drowns it. A ~1% contrast is roughly what the eye can resolve, and this
        // is ~0.0003%.
        float contrast = IlluminanceMath.ShadowContrast(
            IlluminanceMath.MoonLux(1f, 90f), IlluminanceMath.AmbientSkyLux(60f));

        Assert.That(contrast, Is.LessThan(0.0001f),
            $"a midday full-moon shadow came out at {contrast:0.000000} contrast");
    }

    [Test]
    public void FullMoonShadow_CrossesIntoVisibility_BetweenCivilAndNauticalTwilight()
    {
        // Where the fade-in actually happens, expressed as the physical claim rather than a pinned
        // number: a full moon cannot compete with the 3.4 lux civil-twilight sky, and comfortably
        // dominates the 0.008 lux nautical one.
        float atCivil = IlluminanceMath.ShadowContrast(
            IlluminanceMath.MoonLux(1f, 60f), IlluminanceMath.AmbientSkyLux(-6f));
        float atNautical = IlluminanceMath.ShadowContrast(
            IlluminanceMath.MoonLux(1f, 60f), IlluminanceMath.AmbientSkyLux(-12f));

        Assert.That(atCivil, Is.LessThan(0.1f));
        Assert.That(atNautical, Is.GreaterThan(0.9f));
    }
}
