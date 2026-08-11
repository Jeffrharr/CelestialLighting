namespace CelestialLighting.Tests;

// Offline coverage for §23c's daylight cloud shadows (Source/CloudShadowMath.cs), linked into this
// project via <Compile Include> so these exercise the exact code that ships.
[TestFixture]
public class CloudShadowMathTests
{
    private const float Tolerance = 1e-4f;

    // The headline invariant, and the one that pairs this lane with §23b: the two never draw at the
    // same time. Above the horizon a deck is an occluder; below it, there is no direct beam left to
    // occlude and the deck has become §23b's source instead.
    [TestCase(0f, TestName = "NoShadowWithTheSunOnTheHorizon")]
    [TestCase(-1f, TestName = "NoShadowAfterSunset")]
    [TestCase(-20f, TestName = "NoShadowAtNight")]
    public void NoShadowOnceTheSunIsDown(float elevation)
    {
        Assert.That(CloudShadowMath.ShadowAlpha(elevation, 0.5f, inVacuum: false),
            Is.EqualTo(0f).Within(Tolerance));
    }

    // Both ends of the coverage range draw nothing, exactly as §23b's do — and for the same reason,
    // which is what makes the two lanes one design rather than two. A solid deck shades the whole map
    // evenly, and an even shade is precisely what §13's flat dimming already renders.
    [TestCase(0f, TestName = "NothingUnderAClearSky")]
    [TestCase(1f, TestName = "NothingUnderASolidOvercast")]
    public void AUniformSkyCastsNoPatches(float fraction)
    {
        Assert.That(CloudShadowMath.ShadowAlpha(45f, fraction, inVacuum: false),
            Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void ShadowsAreStrongestWithTheSunHigh()
    {
        float noon = CloudShadowMath.ShadowAlpha(75f, 0.5f, inVacuum: false);
        float midMorning = CloudShadowMath.ShadowAlpha(35f, 0.5f, inVacuum: false);
        float lowSun = CloudShadowMath.ShadowAlpha(5f, 0.5f, inVacuum: false);

        Assert.That(noon, Is.GreaterThan(midMorning));
        Assert.That(midMorning, Is.GreaterThan(lowSun));
        Assert.That(lowSun, Is.GreaterThan(0f));
    }

    // The low-sun fade is not decoration. A cloud shadow needs a direct beam to block, and near the
    // horizon most of what reaches the ground is diffuse skylight the deck does not occlude sharply —
    // so the shadow has to be nearly gone well before sunset rather than switching off at it.
    [Test]
    public void TheDirectBeamFadesOutWellBeforeSunset()
    {
        float atFade = CloudShadowMath.DirectBeamFraction(CloudShadowMath.DirectBeamFadeDegrees);
        float halfway = CloudShadowMath.DirectBeamFraction(CloudShadowMath.DirectBeamFadeDegrees / 2f);
        float nearHorizon = CloudShadowMath.DirectBeamFraction(1f);

        Assert.That(CloudShadowMath.DirectBeamFraction(0f), Is.EqualTo(0f));
        Assert.That(nearHorizon, Is.LessThan(atFade * 0.05f),
            "one degree up should be a small fraction of the fade point, not a linear share of it");
        Assert.That(halfway, Is.LessThan(atFade * 0.5f), "the fade is quadratic, not linear");
        Assert.That(atFade, Is.GreaterThan(0f));
    }

    // Continuity across the fade boundary: the quadratic ramp below it has to meet the plain sine
    // above it, or the shadow steps as the sun climbs through 10 degrees.
    [Test]
    public void TheFadeMeetsTheGeometricTermWithoutAStep()
    {
        float justBelow = CloudShadowMath.DirectBeamFraction(CloudShadowMath.DirectBeamFadeDegrees - 0.01f);
        float justAbove = CloudShadowMath.DirectBeamFraction(CloudShadowMath.DirectBeamFadeDegrees + 0.01f);

        Assert.That(justBelow, Is.EqualTo(justAbove).Within(0.002f));
    }

    [Test]
    public void ShadowIsZeroInVacuum()
    {
        Assert.That(CloudShadowMath.ShadowAlpha(45f, 0.5f, inVacuum: true), Is.EqualTo(0f));
    }

    [Test]
    public void ShadowScalesLinearlyWithAmplitudeAndIgnoresNonsense()
    {
        float half = CloudShadowMath.ShadowAlphaWithAmplitude(45f, 0.5f, 0.1f, false);
        float whole = CloudShadowMath.ShadowAlphaWithAmplitude(45f, 0.5f, 0.2f, false);

        Assert.That(whole, Is.EqualTo(half * 2f).Within(Tolerance));
        Assert.That(CloudShadowMath.ShadowAlphaWithAmplitude(45f, 0.5f, 0f, false), Is.EqualTo(0f));
        Assert.That(CloudShadowMath.ShadowAlphaWithAmplitude(45f, float.NaN, 0.2f, false), Is.EqualTo(0f));
        Assert.That(CloudShadowMath.ShadowAlphaWithAmplitude(45f, 0.5f, float.NaN, false), Is.EqualTo(0f));
    }
}
