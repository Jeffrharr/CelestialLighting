using System;

namespace CelestialLighting.Tests;

/// <summary>
/// Second-tier offline coverage for Source/Formulas.cs (DESIGN.md §1/§2/§3), complementing
/// FormulasTests.cs rather than repeating it. Two things live here that the first-tier file leaves
/// implicit:
///
///   1. The members it only exercises indirectly — HourAngleDegrees, SolarAzimuthDegrees (including
///      both of its documented degenerate fallbacks) and the magnitude/direction contract of
///      ShadowVectorFromSunPosition.
///   2. Whole-model invariants swept across latitude and season rather than checked at a handful of
///      hand-picked points. These are the properties a correct solar-position simulator must satisfy
///      everywhere — the sun peaks at noon, the day is symmetric about it, declination is bounded by
///      the axial tilt, nothing ever returns NaN — and they are what would catch a sign flip or a
///      degrees/radians slip that happens to leave the equator-at-equinox cases looking right.
/// </summary>
[TestFixture]
public class FormulasSolarGeometryTests
{
    private const float Tolerance = 1e-3f;

    // The sweep the design was validated against (see §14's measured tables): 5-degree latitude steps
    // pole to pole, every 5th day of the 60-day year.
    private static IEnumerable<float> Latitudes()
    {
        for (int lat = -90; lat <= 90; lat += 5)
            yield return lat;
    }

    private static IEnumerable<float> Days()
    {
        for (int day = 0; day < 60; day += 5)
            yield return day;
    }

    // --- HourAngleDegrees ---
    //
    // The convention every other function here (and vanilla's own SunPositionUnmodified) assumes:
    // 0 at solar noon, negative before it, positive after, a full 360 across the day. §14's warp
    // depends on exactly this symmetry, so it is worth pinning by value rather than by usage.

    [TestCase(0f, ExpectedResult = -180f)]
    [TestCase(0.25f, ExpectedResult = -90f)]
    [TestCase(0.5f, ExpectedResult = 0f)]
    [TestCase(0.75f, ExpectedResult = 90f)]
    [TestCase(1f, ExpectedResult = 180f)]
    public float HourAngleDegrees_MatchesTheNoonCenteredConvention(float dayPercent)
    {
        return Formulas.HourAngleDegrees(dayPercent);
    }

    [Test]
    public void HourAngleDegrees_IsAntisymmetricAboutNoon()
    {
        // Morning and afternoon are mirror images, which is what makes the sun's elevation an even
        // function of time-from-noon — the property §14's whole two-line warp is built on.
        for (float offset = 0f; offset <= 0.5f; offset += 0.05f)
        {
            float before = Formulas.HourAngleDegrees(0.5f - offset);
            float after = Formulas.HourAngleDegrees(0.5f + offset);
            Assert.That(before, Is.EqualTo(-after).Within(Tolerance));
        }
    }

    // --- SolarDeclinationDegrees over the year ---

    [Test]
    public void SolarDeclination_NeverExceedsTheAxialTilt()
    {
        // The declination sinusoid is scaled by the real 23.44-degree tilt; anything outside that
        // band would mean the seasonal term had picked up a stray factor.
        for (float day = 0f; day < 60f; day += 0.25f)
        {
            float declination = Formulas.SolarDeclinationDegrees(day);
            Assert.That(Math.Abs(declination), Is.LessThanOrEqualTo(Formulas.AxialTiltDegrees + Tolerance),
                $"day {day}");
        }
    }

    [TestCase(0f, -23.44f)]   // day 0: sun over the southern hemisphere
    [TestCase(15f, 0f)]       // equinox
    [TestCase(30f, 23.44f)]   // northern summer solstice
    [TestCase(45f, 0f)]       // the other equinox
    public void SolarDeclination_HitsSolsticesAndEquinoxesOnTheQuadrumBoundaries(float dayOfYear, float expected)
    {
        // RimWorld's 60-day year splits into four 15-day quadrums, so the solstices and equinoxes land
        // exactly on those boundaries. A phase slip in DeclinationSign would show up here first.
        Assert.That(Formulas.SolarDeclinationDegrees(dayOfYear), Is.EqualTo(expected).Within(Tolerance));
    }

    [Test]
    public void SolarDeclination_RepeatsEveryYear()
    {
        for (float day = 0f; day < 60f; day += 2.5f)
        {
            Assert.That(Formulas.SolarDeclinationDegrees(day + Formulas.DaysPerYear),
                Is.EqualTo(Formulas.SolarDeclinationDegrees(day)).Within(Tolerance), $"day {day}");
        }
    }

    // --- SolarElevationDegrees: whole-model invariants ---

    [Test]
    public void SolarElevation_PeaksAtSolarNoon_AtEveryLatitudeAndSeason()
    {
        // sin(elevation) = sin(lat)sin(decl) + cos(lat)cos(decl)cos(hourAngle), and cos(lat)cos(decl)
        // is never negative for real latitudes/declinations, so the maximum is always at cos(H) == 1.
        // If any sweep point beat noon, the hour-angle term's sign or scale would be wrong.
        foreach (float lat in Latitudes())
        {
            foreach (float day in Days())
            {
                float declination = Formulas.SolarDeclinationDegrees(day);
                float noon = Formulas.SolarElevationDegrees(lat, declination, 0.5f);

                for (float p = 0f; p <= 1f; p += 0.02f)
                {
                    float elevation = Formulas.SolarElevationDegrees(lat, declination, p);
                    Assert.That(elevation, Is.LessThanOrEqualTo(noon + Tolerance),
                        $"lat {lat}, day {day}, dayPercent {p} beat solar noon");
                }
            }
        }
    }

    [Test]
    public void SolarElevation_IsSymmetricAboutNoon()
    {
        // Equal times either side of noon see the sun at the same height. §14's warp assumes this
        // (it maps a whole day through one half-day number); if it ever stopped holding, locked mode
        // would land sunrise and sunset at different elevations.
        foreach (float lat in Latitudes())
        {
            float declination = Formulas.SolarDeclinationDegrees(20f);
            for (float offset = 0f; offset <= 0.5f; offset += 0.05f)
            {
                float morning = Formulas.SolarElevationDegrees(lat, declination, 0.5f - offset);
                float afternoon = Formulas.SolarElevationDegrees(lat, declination, 0.5f + offset);
                Assert.That(morning, Is.EqualTo(afternoon).Within(Tolerance), $"lat {lat}, offset {offset}");
            }
        }
    }

    [Test]
    public void SolarElevation_StaysWithinTheSkyAndIsNeverNaN()
    {
        // Including the exact poles and an extreme declination, where the sin() argument can be pushed
        // marginally outside [-1, 1] by float error — the reason SolarElevationDegrees clamps before
        // calling Asin. Without that clamp this returns NaN and every downstream subsystem silently
        // renders nothing.
        foreach (float lat in new[] { -90f, -89.999f, -45f, 0f, 45f, 89.999f, 90f })
        {
            foreach (float declination in new[] { -90f, -23.44f, 0f, 23.44f, 90f })
            {
                for (float p = 0f; p <= 1f; p += 0.1f)
                {
                    float elevation = Formulas.SolarElevationDegrees(lat, declination, p);
                    Assert.That(float.IsNaN(elevation), Is.False, $"NaN at lat {lat}, decl {declination}, p {p}");
                    Assert.That(elevation, Is.InRange(-90.001f, 90.001f));
                }
            }
        }
    }

    [Test]
    public void SolarElevation_AtThePole_EqualsDeclinationAllDayLong()
    {
        // The midnight-sun/polar-night case falling out for free (DESIGN.md §1): at the pole
        // cos(latitude) == 0 kills the hour-angle term entirely, so elevation is flat across the day.
        foreach (float day in Days())
        {
            float declination = Formulas.SolarDeclinationDegrees(day);
            for (float p = 0f; p <= 1f; p += 0.1f)
            {
                Assert.That(Formulas.SolarElevationDegrees(90f, declination, p),
                    Is.EqualTo(declination).Within(0.01f), $"day {day}, dayPercent {p}");
            }
        }
    }

    // --- SolarAzimuthDegrees ---

    [Test]
    public void SolarAzimuth_SunRisesInTheEast()
    {
        // The handedness anchor. Everything else in this section is symmetric about noon and stays
        // green with the sky mirrored east-west, which is precisely how a dropped minus sign on sinAz
        // shipped a sun that rose in the west — caught by someone looking at a screenshot, not by the
        // suite. Sunrise east / sunset west is the one assertion that can only pass one way round.
        //
        // Equinox at +-45 puts the sun exactly on the horizon at dayPercent 0.25 and 0.75, so these are
        // true sunrise and sunset rather than "some time in the morning".
        foreach (float latitude in new[] { 45f, -45f })
        {
            float sunriseElevation = Formulas.SolarElevationDegrees(latitude, 0f, 0.25f);
            float sunsetElevation = Formulas.SolarElevationDegrees(latitude, 0f, 0.75f);
            Assert.That(sunriseElevation, Is.EqualTo(0f).Within(0.01f), $"precondition: sunrise at lat {latitude}");
            Assert.That(sunsetElevation, Is.EqualTo(0f).Within(0.01f), $"precondition: sunset at lat {latitude}");

            float sunrise = NormalizeAzimuth(Formulas.SolarAzimuthDegrees(latitude, 0f, sunriseElevation, 0.25f));
            float sunset = NormalizeAzimuth(Formulas.SolarAzimuthDegrees(latitude, 0f, sunsetElevation, 0.75f));
            Assert.That(sunrise, Is.EqualTo(90f).Within(0.01f), $"sunrise is due east at lat {latitude}");
            Assert.That(sunset, Is.EqualTo(270f).Within(0.01f), $"sunset is due west at lat {latitude}");
        }
    }

    [Test]
    public void ShadowVector_PointsWestAtSunriseAndEastAtSunset()
    {
        // The same fact one layer down, in the units that actually reach the screen: X is world east,
        // so a sunrise shadow is thrown to negative X. Patch_ShadowDirection hands this straight to
        // info.vector.x with no axis flip, so this sign IS the on-screen sign.
        float sunriseElevation = Formulas.SolarElevationDegrees(45f, 0f, 0.25f);
        float sunsetElevation = Formulas.SolarElevationDegrees(45f, 0f, 0.75f);

        float sunriseAzimuth = Formulas.SolarAzimuthDegrees(45f, 0f, sunriseElevation, 0.25f);
        float sunsetAzimuth = Formulas.SolarAzimuthDegrees(45f, 0f, sunsetElevation, 0.75f);

        Formulas.ShadowVector atSunrise = Formulas.ShadowVectorFromSunPosition(sunriseElevation, sunriseAzimuth);
        Formulas.ShadowVector atSunset = Formulas.ShadowVectorFromSunPosition(sunsetElevation, sunsetAzimuth);

        Assert.That(atSunrise.X, Is.LessThan(0f), "sunrise shadow falls west");
        Assert.That(atSunset.X, Is.GreaterThan(0f), "sunset shadow falls east");
    }

    private static float NormalizeAzimuth(float degrees) => degrees < 0f ? degrees + 360f : degrees;

    [Test]
    public void SolarAzimuth_IsDueSouthAtNoonInTheNorthernHemisphere()
    {
        // Standard north-referenced azimuth: from a northern tile the noon sun sits due south (180).
        float declination = 0f;
        float elevation = Formulas.SolarElevationDegrees(45f, declination, 0.5f);
        float azimuth = Formulas.SolarAzimuthDegrees(45f, declination, elevation, 0.5f);
        Assert.That(Math.Abs(azimuth), Is.EqualTo(180f).Within(0.01f));
    }

    [Test]
    public void SolarAzimuth_IsDueNorthAtNoonInTheSouthernHemisphere()
    {
        // The mirror case, and the one a hemisphere sign bug would break while leaving the north right.
        float declination = 0f;
        float elevation = Formulas.SolarElevationDegrees(-45f, declination, 0.5f);
        float azimuth = Formulas.SolarAzimuthDegrees(-45f, declination, elevation, 0.5f);
        Assert.That(azimuth, Is.EqualTo(0f).Within(0.01f));
    }

    [Test]
    public void SolarAzimuth_MirrorsAcrossNoon()
    {
        // Morning and afternoon azimuths are equal and opposite about the noon meridian, so shadows
        // sweep symmetrically through the day. This asserts symmetry ONLY — it holds just as well with
        // the whole sky mirrored east-west, which is how we shipped a backwards sun past a green suite.
        // SolarAzimuth_SunRisesInTheEast is the test that pins the handedness down.
        float declination = Formulas.SolarDeclinationDegrees(20f);
        for (float offset = 0.05f; offset <= 0.4f; offset += 0.05f)
        {
            float morningElevation = Formulas.SolarElevationDegrees(45f, declination, 0.5f - offset);
            float afternoonElevation = Formulas.SolarElevationDegrees(45f, declination, 0.5f + offset);
            float morning = Formulas.SolarAzimuthDegrees(45f, declination, morningElevation, 0.5f - offset);
            float afternoon = Formulas.SolarAzimuthDegrees(45f, declination, afternoonElevation, 0.5f + offset);
            Assert.That(morning, Is.EqualTo(-afternoon).Within(0.01f), $"offset {offset}");
        }
    }

    [Test]
    public void SolarAzimuth_AtThePole_FallsBackToTrackingTheHourAngle()
    {
        // cos(latitude) == 0 makes every azimuth "away from the pole", so the formula is degenerate.
        // The documented fallback tracks the hour angle directly, normalized into [0, 360) — it keeps
        // sweeping smoothly instead of dividing by zero or freezing the shadow in place.
        // The fallback tracks the NEGATED hour angle, matching the sign on sinAz in the real formula,
        // so the sun keeps circling the same way it does one degree of latitude further out.
        float declination = Formulas.SolarDeclinationDegrees(30f);
        foreach (float p in new[] { 0f, 0.25f, 0.5f, 0.75f })
        {
            float elevation = Formulas.SolarElevationDegrees(90f, declination, p);
            float azimuth = Formulas.SolarAzimuthDegrees(90f, declination, elevation, p);

            float negatedHourAngle = -Formulas.HourAngleDegrees(p);
            float expected = negatedHourAngle < 0f ? negatedHourAngle + 360f : negatedHourAngle;
            Assert.That(azimuth, Is.EqualTo(expected).Within(0.01f), $"dayPercent {p}");
        }
    }

    [Test]
    public void SolarAzimuth_AtZenith_FallsBackRatherThanDividingByZero()
    {
        // The other degenerate case: sun directly overhead (cos(elevation) == 0), where azimuth is
        // genuinely undefined. Equator, equinox, noon puts it exactly there.
        float elevation = Formulas.SolarElevationDegrees(0f, 0f, 0.5f);
        Assert.That(elevation, Is.EqualTo(90f).Within(0.01f), "precondition: sun is at the zenith");

        float azimuth = Formulas.SolarAzimuthDegrees(0f, 0f, elevation, 0.5f);
        Assert.That(float.IsNaN(azimuth), Is.False);
        Assert.That(azimuth, Is.EqualTo(0f).Within(0.01f)); // normalized hour angle at noon
    }

    [Test]
    public void SolarAzimuth_IsNeverNaN_AcrossTheWholeSweep()
    {
        foreach (float lat in Latitudes())
        {
            foreach (float day in Days())
            {
                float declination = Formulas.SolarDeclinationDegrees(day);
                for (float p = 0f; p <= 1f; p += 0.05f)
                {
                    float elevation = Formulas.SolarElevationDegrees(lat, declination, p);
                    float azimuth = Formulas.SolarAzimuthDegrees(lat, declination, elevation, p);
                    Assert.That(float.IsNaN(azimuth), Is.False, $"lat {lat}, day {day}, p {p}");
                }
            }
        }
    }

    // --- ShadowIntensityFromElevation ---

    [TestCase(-90f, 0f)]
    [TestCase(-0.83f, 0f)]   // exactly at the refraction horizon: still nothing
    [TestCase(0.67f, 0.5f)]  // midpoint of the 3-degree ramp
    [TestCase(2.17f, 1f)]    // top of the ramp
    [TestCase(60f, 1f)]      // and flat at full strength all the way up
    public void ShadowIntensity_RampsOverThreeDegreesAboveTheRefractionHorizon(
        float elevationDegrees, float expected)
    {
        Assert.That(Formulas.ShadowIntensityFromElevation(elevationDegrees),
            Is.EqualTo(expected).Within(Tolerance));
    }

    [Test]
    public void ShadowIntensity_IsMonotonicAndBounded()
    {
        // A dip anywhere in here is the vanilla bug §1 exists to fix (CurShadowStrength dips to 0 right
        // at glow 0.6, mid-afternoon), so the shape matters as much as the endpoints.
        float previous = -1f;
        for (float elevation = -20f; elevation <= 90f; elevation += 0.25f)
        {
            float intensity = Formulas.ShadowIntensityFromElevation(elevation);
            Assert.That(intensity, Is.InRange(0f, 1f));
            Assert.That(intensity, Is.GreaterThanOrEqualTo(previous - 1e-5f), $"dipped at elevation {elevation}");
            previous = intensity;
        }
    }

    // --- ShadowVectorFromSunPosition ---

    [Test]
    public void ShadowVector_HasExactlyTheLengthTheLengthFunctionReports()
    {
        // The vector's magnitude IS ShadowLengthFromElevation — the direction split must not rescale
        // it, or the mesh extrusion downstream would be tuned against a different length than the one
        // the clamps guarantee.
        foreach (float elevation in new[] { 89f, 60f, 45f, 20f, 5f, 1f, 0.5f })
        {
            foreach (float azimuth in new[] { 0f, 37f, 90f, 180f, 274f, 359f })
            {
                Formulas.ShadowVector shadow = Formulas.ShadowVectorFromSunPosition(elevation, azimuth);
                float magnitude = MathF.Sqrt(shadow.X * shadow.X + shadow.Y * shadow.Y);
                Assert.That(magnitude, Is.EqualTo(Formulas.ShadowLengthFromElevation(elevation)).Within(Tolerance),
                    $"elevation {elevation}, azimuth {azimuth}");
            }
        }
    }

    [Test]
    public void ShadowVector_AlwaysPointsDirectlyAwayFromTheSun()
    {
        // Stated as a dot product against the sun's own horizontal direction rather than as four
        // cardinal special cases: the shadow must be antiparallel at every azimuth, not just at N/E/S/W.
        for (float azimuth = 0f; azimuth < 360f; azimuth += 15f)
        {
            Formulas.ShadowVector shadow = Formulas.ShadowVectorFromSunPosition(30f, azimuth);
            float sunEast = MathF.Sin(azimuth * MathF.PI / 180f);
            float sunNorth = MathF.Cos(azimuth * MathF.PI / 180f);
            float dot = shadow.X * sunEast + shadow.Y * sunNorth;
            float magnitude = MathF.Sqrt(shadow.X * shadow.X + shadow.Y * shadow.Y);
            Assert.That(dot, Is.EqualTo(-magnitude).Within(Tolerance), $"azimuth {azimuth}");
        }
    }

    [Test]
    public void ShadowVector_BelowTheHorizon_ReturnsTheClampedMaxLengthNotZero()
    {
        // Documented division of labour: length math never answers "is there a shadow at all" —
        // ShadowIntensityFromElevation does, and the callers gate on it. A future edit that made this
        // return 0 instead would look harmless and would break the moon-shadow path, which feeds this
        // same function a below-horizon sun deliberately.
        Formulas.ShadowVector shadow = Formulas.ShadowVectorFromSunPosition(-10f, 90f);
        float magnitude = MathF.Sqrt(shadow.X * shadow.X + shadow.Y * shadow.Y);
        Assert.That(magnitude, Is.EqualTo(Formulas.MaxShadowLength).Within(Tolerance));
        Assert.That(Formulas.ShadowIntensityFromElevation(-10f), Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void ShadowVector_LengthGrowsMonotonicallyAsTheSunSets()
    {
        // The §1 headline: the dramatic-at-sunset look comes from cot(elevation) growing, not from
        // intensity fading. Assert the growth all the way down to the clamp.
        float previous = 0f;
        for (float elevation = 89f; elevation >= 0.5f; elevation -= 0.5f)
        {
            float length = Formulas.ShadowLengthFromElevation(elevation);
            Assert.That(length, Is.GreaterThanOrEqualTo(previous - 1e-4f), $"shrank at elevation {elevation}");
            Assert.That(length, Is.InRange(0f, Formulas.MaxShadowLength));
            previous = length;
        }
    }

    // --- Latitude strength and the twilight band ---

    [Test]
    public void LatitudeStrength_IsHemisphereSymmetricAndSaturates()
    {
        // Twilight drama depends on distance from the equator, not on which side of it you are.
        foreach (float lat in new[] { 0f, 15f, 30f, 45f, 59.9f, 60f, 75f, 90f })
        {
            Assert.That(Formulas.LatitudeStrength(-lat), Is.EqualTo(Formulas.LatitudeStrength(lat)).Within(Tolerance));
            Assert.That(Formulas.LatitudeStrength(lat), Is.InRange(0f, 1f));
        }

        Assert.That(Formulas.LatitudeStrength(0f), Is.EqualTo(0f).Within(Tolerance));
        Assert.That(Formulas.LatitudeStrength(Formulas.FullStrengthLatitude), Is.EqualTo(1f).Within(Tolerance));
        Assert.That(Formulas.LatitudeStrength(90f), Is.EqualTo(1f).Within(Tolerance)); // clamps, never overshoots
    }

    [Test]
    public void TwilightWarmthFactor_NeverExceedsTheLatitudePeakHeight()
    {
        // The reason §2 takes max() of its two pieces rather than summing them: one shared peak height
        // bounds the whole warm nudge, so dusk can never double-warm where the two bands overlap.
        foreach (float strength in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
        {
            float peak = Formulas.TwilightPeakHeight(strength);
            for (float glow = 0f; glow <= 1f; glow += 0.02f)
            {
                for (float elevation = -10f; elevation <= 10f; elevation += 0.5f)
                {
                    float warmth = Formulas.TwilightWarmthFactor(glow, elevation, strength);
                    Assert.That(warmth, Is.InRange(0f, peak + 1e-5f),
                        $"strength {strength}, glow {glow}, elevation {elevation}");
                }
            }
        }
    }

    [Test]
    public void CivilTwilightPersistence_IsATriangularPulseOverTheCivilBand()
    {
        // Zero at both ends and at the peak in between, with no plateau — the shape that makes the
        // warm tint fade in and back out instead of snapping off at geometric sunset.
        Assert.That(Formulas.CivilTwilightPersistence(0f), Is.EqualTo(0f).Within(Tolerance));
        Assert.That(Formulas.CivilTwilightPersistence(Formulas.CivilTwilightPeakDegrees),
            Is.EqualTo(1f).Within(Tolerance));
        Assert.That(Formulas.CivilTwilightPersistence(Formulas.CivilTwilightEndDegrees),
            Is.EqualTo(0f).Within(Tolerance));

        // Strictly rising from the horizon down to the peak, strictly falling from there to the floor.
        for (float elevation = 0f; elevation > Formulas.CivilTwilightPeakDegrees; elevation -= 0.25f)
        {
            Assert.That(Formulas.CivilTwilightPersistence(elevation - 0.25f),
                Is.GreaterThan(Formulas.CivilTwilightPersistence(elevation)), $"not rising at {elevation}");
        }

        for (float elevation = Formulas.CivilTwilightPeakDegrees;
             elevation > Formulas.CivilTwilightEndDegrees;
             elevation -= 0.25f)
        {
            Assert.That(Formulas.CivilTwilightPersistence(elevation - 0.25f),
                Is.LessThan(Formulas.CivilTwilightPersistence(elevation)), $"not falling at {elevation}");
        }
    }

    // --- ShadowLengthPositionFraction / ShadowLengthScale (§3) ---

    [Test]
    public void ShadowLengthPositionFraction_IsAntisymmetricAboutTheMapCenter()
    {
        // Opposite sides of the map must get opposite-signed adjustments, or the "tilt" becomes a
        // uniform lengthening of every section — visually identical to no tilt at all.
        foreach (float dirX in new[] { 1f, 0.6f, 0f, -0.8f })
        {
            float dirZ = 1f - MathF.Abs(dirX);
            foreach (float offsetX in new[] { 10f, 50f, 120f })
            {
                float positive = Formulas.ShadowLengthPositionFraction(offsetX, 30f, dirX, dirZ, 250f, 250f);
                float negative = Formulas.ShadowLengthPositionFraction(-offsetX, -30f, dirX, dirZ, 250f, 250f);
                Assert.That(negative, Is.EqualTo(-positive).Within(Tolerance), $"dirX {dirX}, offsetX {offsetX}");
            }
        }
    }

    [Test]
    public void ShadowLengthScale_StaysInsideTheVariationBand_ForAnyInput()
    {
        // Includes deliberately unclamped position fractions: the defensive clamp inside
        // ShadowLengthScale is what keeps a caller bug from producing a negative-length shadow.
        foreach (float maxVariation in new[] { 0f, 0.15f, 0.5f })
        {
            foreach (float fraction in new[] { -50f, -1f, -0.5f, 0f, 0.5f, 1f, 50f })
            {
                float scale = Formulas.ShadowLengthScale(fraction, maxVariation);
                Assert.That(scale, Is.InRange(1f - maxVariation - 1e-5f, 1f + maxVariation + 1e-5f),
                    $"maxVariation {maxVariation}, fraction {fraction}");
            }
        }
    }

    // --- Baking §3's tilt into vertex alpha (issue #11) ---
    //
    // Every shipped staticSunShadowHeight in RimWorld 1.6, surveyed from the def XML rather than
    // assumed — this is the set the quantization argument in Formulas has to hold for. (0 is by far
    // the most common value, but a zero-height caster is skipped before it ever reaches the alpha.)
    private static readonly float[] ShippedCasterHeights = { 0.15f, 0.17f, 0.2f, 0.3f, 0.35f, 0.5f, 1f };

    [Test]
    public void NormalizedShadowLengthScale_PutsTheFarEdgeAtExactlyOne()
    {
        // The whole point of the normalization: alpha is a byte, so the largest multiplier the mesh
        // can carry is 1.0, and a height-1.0 caster (walls, rock) already spends all of it. If the
        // far edge came out above 1 the bake would have to clip there, and the longest shadows on
        // the map — the ones the effect is for — would show no gradient at all.
        foreach (float maxVariation in new[] { 0f, 0.15f, 0.5f })
            Assert.That(Formulas.NormalizedShadowLengthScale(1f, maxVariation),
                Is.EqualTo(1f).Within(Tolerance), $"maxVariation {maxVariation}");
    }

    [Test]
    public void NormalizedShadowLengthScale_PreservesTheFarOverNearRatio()
    {
        // Re-anchoring must not weaken the effect: what a player sees is the ratio across the map,
        // and that has to survive the divide unchanged.
        foreach (float maxVariation in new[] { 0.05f, 0.15f, 0.4f })
        {
            float rawRatio = Formulas.ShadowLengthScale(1f, maxVariation)
                / Formulas.ShadowLengthScale(-1f, maxVariation);
            float bakedRatio = Formulas.NormalizedShadowLengthScale(1f, maxVariation)
                / Formulas.NormalizedShadowLengthScale(-1f, maxVariation);

            Assert.That(bakedRatio, Is.EqualTo(rawRatio).Within(Tolerance), $"maxVariation {maxVariation}");
        }
    }

    [Test]
    public void NormalizedShadowLengthScale_NeverExceedsOne_ForAnyInput()
    {
        // Including unclamped fractions and a negative variation, because exceeding 1 here is what
        // would push ShadowCasterAlphaByte into the clamp and silently flatten the gradient.
        foreach (float maxVariation in new[] { -1f, 0f, 0.15f, 0.5f })
        {
            foreach (float fraction in new[] { -50f, -1f, 0f, 0.5f, 1f, 50f })
            {
                float scale = Formulas.NormalizedShadowLengthScale(fraction, maxVariation);
                Assert.That(scale, Is.InRange(0f, 1f + 1e-5f), $"maxVariation {maxVariation}, fraction {fraction}");
            }
        }
    }

    [Test]
    public void ShadowCasterAlphaByte_ClampsInsteadOfWrapping()
    {
        // The failure this guards is not cosmetic. A C# float->byte cast is unchecked, so the
        // un-normalized 255 * 1.0 * 1.15 = 293 wraps to 37 — a wall's shadow would become a stub
        // rather than merely clip. Anything at or over full scale must saturate at 255.
        Assert.That(Formulas.ShadowCasterAlphaByte(1f, 1f), Is.EqualTo(255));
        Assert.That(Formulas.ShadowCasterAlphaByte(1f, 1.15f), Is.EqualTo(255));
        Assert.That(Formulas.ShadowCasterAlphaByte(4f, 3f), Is.EqualTo(255));
        Assert.That(Formulas.ShadowCasterAlphaByte(0f, 1f), Is.EqualTo(0));
        Assert.That(Formulas.ShadowCasterAlphaByte(-1f, 1f), Is.EqualTo(0));
        Assert.That(Formulas.ShadowCasterAlphaByte(1f, -1f), Is.EqualTo(0));
        Assert.That(Formulas.ShadowCasterAlphaByte(float.NaN, 1f), Is.EqualTo(0));
    }

    [Test]
    public void ShadowCasterAlphaByte_MapsTheFullMapEdgeToEdgeRampWithoutClipping()
    {
        // Every shipped caster height, walked across the whole position range: the far edge must
        // land on the height's own un-tilted alpha (that is what "anchored at the far edge" means)
        // and the near edge must come out strictly shorter for anything tall enough to resolve.
        foreach (float height in ShippedCasterHeights)
        {
            byte far = Formulas.ShadowCasterAlphaByte(
                height, Formulas.NormalizedShadowLengthScale(1f, Formulas.ShadowLengthVariation));
            byte near = Formulas.ShadowCasterAlphaByte(
                height, Formulas.NormalizedShadowLengthScale(-1f, Formulas.ShadowLengthVariation));

            Assert.That(far, Is.EqualTo(Formulas.ShadowCasterAlphaByte(height, 1f)), $"far edge, height {height}");
            Assert.That(near, Is.LessThan(far), $"near edge not shorter, height {height}");
        }
    }

    [Test]
    public void ShadowCasterAlphaByte_IsMonotonicAcrossTheMap_ForEveryShippedCasterHeight()
    {
        // Quantization may flatten steps (two adjacent sections sharing a level is fine — the effect
        // is already one value per 17-cell section) but it must never INVERT one, which would read
        // as a shadow getting shorter as it moves away from the sun.
        foreach (float height in ShippedCasterHeights)
        {
            byte previous = 0;
            for (int step = 0; step <= 200; step++)
            {
                float fraction = -1f + step / 100f;
                byte alpha = Formulas.ShadowCasterAlphaByte(
                    height, Formulas.NormalizedShadowLengthScale(fraction, Formulas.ShadowLengthVariation));

                Assert.That(alpha, Is.GreaterThanOrEqualTo(previous), $"height {height}, fraction {fraction}");
                previous = alpha;
            }
        }
    }

    [Test]
    public void ShadowCasterAlphaByte_QuantizationErrorIsBoundedByHalfALevel()
    {
        // The claim the rebake threshold is derived from: rounding (not vanilla's truncation) keeps
        // the stored alpha within half a level of the exact value, for every shipped height. Half a
        // level is 1/510 of the global extrusion vector — 0.03 cells at Formulas.MaxShadowLength.
        foreach (float height in ShippedCasterHeights)
        {
            for (int step = 0; step <= 200; step++)
            {
                float scale = Formulas.NormalizedShadowLengthScale(-1f + step / 100f, Formulas.ShadowLengthVariation);
                float exact = 255f * height * scale;

                Assert.That(Formulas.ShadowCasterAlphaByte(height, scale), Is.EqualTo(exact).Within(0.5f),
                    $"height {height}, scale {scale}");
            }
        }
    }

    [Test]
    public void ShadowCasterAlphaByte_ShortestShippedCasterStillResolvesTheGradient()
    {
        // The specific worry issue #11 raised: does a 15% ramp stacked on a SHORT caster quantize
        // away? At the shortest shipped height (0.15) the ramp still spans several levels, which is
        // the resolution the effect actually needs — the gradient is quantized to ~15 sections along
        // the axis of a 250-cell map, and it is the section grid, not the byte, that is coarser.
        byte far = Formulas.ShadowCasterAlphaByte(
            0.15f, Formulas.NormalizedShadowLengthScale(1f, Formulas.ShadowLengthVariation));
        byte near = Formulas.ShadowCasterAlphaByte(
            0.15f, Formulas.NormalizedShadowLengthScale(-1f, Formulas.ShadowLengthVariation));

        Assert.That(far - near, Is.GreaterThanOrEqualTo(5), "0.15-height ramp collapsed into too few levels");
    }

    [Test]
    public void ShadowAxisDeltaDegrees_MeasuresAngleAndIgnoresLength()
    {
        // Only the axis is baked; the vector's magnitude stays live in the shader global, so a sun
        // that is only getting lower must not trigger a rebake.
        Assert.That(Formulas.ShadowAxisDeltaDegrees(1f, 0f, 12f, 0f), Is.EqualTo(0f).Within(1e-3f));
        Assert.That(Formulas.ShadowAxisDeltaDegrees(1f, 0f, 0f, 1f), Is.EqualTo(90f).Within(1e-3f));
        Assert.That(Formulas.ShadowAxisDeltaDegrees(1f, 0f, -1f, 0f), Is.EqualTo(180f).Within(1e-3f));
        Assert.That(Formulas.ShadowAxisDeltaDegrees(3f, 4f, 3f, 4f), Is.EqualTo(0f).Within(1e-3f));

        // A degenerate vector means nothing is being drawn, so "not stale" is the cheap right
        // answer — the alternative would rebake every check through a moonless night.
        Assert.That(Formulas.ShadowAxisDeltaDegrees(0f, 0f, 1f, 0f), Is.EqualTo(0f));
        Assert.That(Formulas.ShadowAxisDeltaDegrees(1f, 0f, 0f, 0f), Is.EqualTo(0f));
    }

    [Test]
    public void ShadowAxisRebakeDegrees_KeepsBakedAlphaWithinOneLevel_OnSquareMaps()
    {
        // The derivation behind Formulas.ShadowAxisRebakeDegrees, executed rather than asserted in a
        // comment: walk every section center of the shipped square map sizes, rotate the axis by the
        // threshold, and check that no section's baked alpha for the tallest caster moves by more
        // than one level. If someone widens the threshold to save rebakes, this is what fails.
        foreach (int size in new[] { 200, 250, 275, 300 })
        {
            for (int sectionX = 0; sectionX * 17 < size; sectionX++)
            {
                for (int sectionZ = 0; sectionZ * 17 < size; sectionZ++)
                {
                    float offsetX = MathF.Min(sectionX * 17f + 8.5f, size) - size / 2f;
                    float offsetZ = MathF.Min(sectionZ * 17f + 8.5f, size) - size / 2f;

                    for (float azimuth = 0f; azimuth < 360f; azimuth += 1f)
                    {
                        Assert.That(AlphaDriftOverThreshold(offsetX, offsetZ, azimuth, size),
                            Is.LessThanOrEqualTo(1),
                            $"size {size}, offset ({offsetX}, {offsetZ}), azimuth {azimuth}");
                    }
                }
            }
        }
    }

    [Test]
    public void ShadowAxisCheckDue_WakesUpOnABackwardsClockJump()
    {
        // The regression this exists for. A signed `current - last >= interval` reads a backwards
        // jump as "no time has passed" and stays wedged until the clock ticks back past the old
        // value — never, if the game is paused. A live scenario that jumped 17:00 -> 07:00 measured
        // the shadow gradient still baked against the afternoon axis, i.e. inverted, because of it.
        Assert.That(Formulas.ShadowAxisCheckDue(100_000, 125_000, 15), Is.True, "backwards jump");
        Assert.That(Formulas.ShadowAxisCheckDue(125_000, 100_000, 15), Is.True, "forwards jump");
        Assert.That(Formulas.ShadowAxisCheckDue(-30, -30, 15), Is.False, "no time passed");
    }

    [Test]
    public void ShadowAxisCheckDue_ThrottlesWithinTheInterval_InEitherDirection()
    {
        // Paused games must stay free: the sun's position is a function of the tick, so a clock
        // that has not moved cannot have made anything stale.
        for (int delta = -14; delta <= 14; delta++)
            Assert.That(Formulas.ShadowAxisCheckDue(1000 + delta, 1000, 15), Is.False, $"delta {delta}");

        Assert.That(Formulas.ShadowAxisCheckDue(1015, 1000, 15), Is.True);
        Assert.That(Formulas.ShadowAxisCheckDue(985, 1000, 15), Is.True);
    }

    [Test]
    public void ShadowAxisCheckDue_ChecksOnTheFirstUpdateOfAFreshMap()
    {
        // MapComponent_SunShadowAxis seeds lastCheckedTick at -2 * interval precisely so tick 0 is
        // already due — a fresh map must not wait a quarter second before it has an axis.
        Assert.That(Formulas.ShadowAxisCheckDue(0, -30, 15), Is.True);
    }

    // Alpha levels a height-1.0 caster's baked value moves when the axis rotates by exactly the
    // rebake threshold — i.e. the worst drift the mod ever leaves on screen.
    private static int AlphaDriftOverThreshold(float offsetX, float offsetZ, float azimuthDegrees, int mapSize)
    {
        byte before = AlphaAt(offsetX, offsetZ, azimuthDegrees, mapSize);
        byte after = AlphaAt(offsetX, offsetZ, azimuthDegrees + Formulas.ShadowAxisRebakeDegrees, mapSize);
        return Math.Abs(before - after);
    }

    private static byte AlphaAt(float offsetX, float offsetZ, float azimuthDegrees, int mapSize)
    {
        float radians = azimuthDegrees * MathF.PI / 180f;
        float fraction = Formulas.ShadowLengthPositionFraction(
            offsetX, offsetZ, MathF.Sin(radians), MathF.Cos(radians), mapSize, mapSize);

        return Formulas.ShadowCasterAlphaByte(
            1f, Formulas.NormalizedShadowLengthScale(fraction, Formulas.ShadowLengthVariation));
    }
}
