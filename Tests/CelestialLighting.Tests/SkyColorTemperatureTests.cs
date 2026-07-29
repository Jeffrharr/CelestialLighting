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

    // --- ColorTemperatureKelvin: warm at the horizon, neutral at the zenith, monotonic between ---

    [TestCase(-5f, SkyColorTemperature.HorizonKelvin)] // below horizon clamps flat to warm
    [TestCase(0f, SkyColorTemperature.HorizonKelvin)] // horizon
    [TestCase(30f, 3886f)] // halfway up the ramp: Lerp(2000, 5772, 0.5)
    [TestCase(60f, SkyColorTemperature.ZenithKelvin)] // full daylight altitude
    [TestCase(90f, SkyColorTemperature.ZenithKelvin)] // zenith clamps flat to neutral
    public void ColorTemperatureKelvin_MatchesExpected(float elevation, float expected)
    {
        Assert.That(SkyColorTemperature.ColorTemperatureKelvin(elevation, inVacuum: false), Is.EqualTo(expected).Within(0.5f));
    }

    [Test]
    public void ColorTemperatureKelvin_IsMonotonicNonDecreasing_AsSunClimbs()
    {
        float previous = SkyColorTemperature.ColorTemperatureKelvin(-10f, inVacuum: false);
        for (float elevation = -10f; elevation <= 90f; elevation += 2.5f)
        {
            float current = SkyColorTemperature.ColorTemperatureKelvin(elevation, inVacuum: false);
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
        Assert.That(SkyColorTemperature.TintStrength(elevation, inVacuum: false), Is.EqualTo(expected).Within(0.001f));
    }

    [Test]
    public void TintStrength_FadesSmoothlyBelowHorizon()
    {
        // Midway through the civil-twilight fade band (-6 .. -0.83), the gate is ~0.5.
        float mid = SkyColorTemperature.TintStrength(-3.415f, inVacuum: false);
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
        SkyColorTemperature.Rgb direct = SkyColorTemperature.SkyColorForElevation(20f, inVacuum: false);
        SkyColorTemperature.Rgb composed =
            SkyColorTemperature.BlackbodyToRgb(SkyColorTemperature.ColorTemperatureKelvin(20f, inVacuum: false));
        Assert.That(direct.R, Is.EqualTo(composed.R).Within(Tolerance));
        Assert.That(direct.G, Is.EqualTo(composed.G).Within(Tolerance));
        Assert.That(direct.B, Is.EqualTo(composed.B).Within(Tolerance));
    }
}
