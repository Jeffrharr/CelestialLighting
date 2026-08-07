using CelestialLighting;
using NUnit.Framework;

namespace CelestialLighting.Tests;

// Offline coverage for §22's sky-tint multipliers. Small and mostly about the endpoints, because the
// interesting design decision here — reusing WeatherDimmingMath's own Clear/Overcast anchors instead
// of inventing new ones — is exactly what "cloudCover 1 matches vanilla's own overcast ratio" pins.
[TestFixture]
public class CloudCoverSkyTests
{
    private const float Tolerance = 1e-5f;

    [Test]
    public void SkyTintFactor_IsIdentityAtZeroCloudCover()
    {
        Assert.That(CloudCoverSky.SkyTintFactor(0f), Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void SkyTintFactor_MatchesVanillasOwnOvercastRatioAtFullCloudCover()
    {
        // 0.8 / 1.0 — WeatherDimmingMath's own Clear/Overcast luminance anchors. Pinned as the literal
        // ratio, not as WeatherDimmingMath.OvercastSkyLuminance alone, so a future change to
        // ClearSkyLuminance (currently 1.0 and easy to assume fixed) would still be caught.
        Assert.That(CloudCoverSky.SkyTintFactor(1f), Is.EqualTo(0.8f).Within(Tolerance));
    }

    [TestCase(0.25f)]
    [TestCase(0.5f)]
    [TestCase(0.75f)]
    public void SkyTintFactor_IsLinearBetweenTheEndpoints(float cloudCover)
    {
        float expected = 1f + (0.8f - 1f) * cloudCover;
        Assert.That(CloudCoverSky.SkyTintFactor(cloudCover), Is.EqualTo(expected).Within(Tolerance));
    }

    [TestCase(-1f)]
    [TestCase(2f)]
    public void SkyTintFactor_ClampsOutOfRangeCloudCover(float cloudCover)
    {
        Assert.That(CloudCoverSky.SkyTintFactor(cloudCover), Is.InRange(0.8f, 1f));
    }

    [Test]
    public void SaturationTintFactor_IsIdentityAtZeroCloudCover()
    {
        Assert.That(CloudCoverSky.SaturationTintFactor(0f), Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void SaturationTintFactor_MatchesVanillasOwnOvercastRatioAtFullCloudCover()
    {
        // 0.9 / 1.25 — WeatherDimmingMath's own Clear/Overcast saturation anchors.
        Assert.That(CloudCoverSky.SaturationTintFactor(1f), Is.EqualTo(0.9f / 1.25f).Within(Tolerance));
    }

    [TestCase(-1f)]
    [TestCase(2f)]
    public void SaturationTintFactor_ClampsOutOfRangeCloudCover(float cloudCover)
    {
        Assert.That(CloudCoverSky.SaturationTintFactor(cloudCover), Is.InRange(0.9f / 1.25f, 1f));
    }
}
