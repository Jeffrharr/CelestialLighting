namespace CelestialLighting.Tests;

// Offline tests for §14's pure core. The headline contract — "locked mode has ZERO day-length error"
// — is provable here without a game: if the warp maps vanilla's half-day onto our physical half-day,
// then our sun crosses the horizon exactly at vanilla's sunrise/sunset by construction, so the test
// asserts that identity directly rather than sampling frames.
[TestFixture]
public class SunClockMathTests
{
    private const float Tolerance = 1e-4f;

    private static float Declination(float dayOfYear) => Formulas.SolarDeclinationDegrees(dayOfYear);

    // Latitudes at 5-degree steps, the sweep the design was validated against.
    private static IEnumerable<float> Latitudes()
    {
        for (int lat = -90; lat <= 90; lat += 5)
            yield return lat;
    }

    // --- PhysicalHalfDay ---

    [Test]
    public void PhysicalHalfDay_Equator_IsHalfADay_AtEquinox()
    {
        // 12 h of daylight at the equator on an equinox, give or take the refraction horizon.
        Assert.That(SunClockMath.PhysicalHalfDay(0f, 0f), Is.EqualTo(0.25f).Within(0.005f));
    }

    [Test]
    public void PhysicalHalfDay_PoleInSummer_IsPolarDay()
    {
        // Declination favours the north pole at day 30, so the sun never sets there.
        Assert.That(SunClockMath.PhysicalHalfDay(89f, Declination(30f)), Is.EqualTo(0.5f).Within(Tolerance));
    }

    [Test]
    public void PhysicalHalfDay_PoleInWinter_IsPolarNight()
    {
        Assert.That(SunClockMath.PhysicalHalfDay(89f, Declination(0f)), Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void PhysicalHalfDay_IsMirroredAcrossTheEquator()
    {
        // The southern hemisphere gets the opposite season — the property vanilla gets right below 70
        // and wrong above it (its curves clamp on signed latitude), and which we must not inherit here.
        foreach (float lat in Latitudes())
        {
            float north = SunClockMath.PhysicalHalfDay(lat, Declination(30f));
            float south = SunClockMath.PhysicalHalfDay(-lat, Declination(0f));
            Assert.That(south, Is.EqualTo(north).Within(0.002f), $"latitude {lat} is not mirrored");
        }
    }

    // --- WarpDayPercent: the zero-error contract ---

    [Test]
    public void Warp_MapsVanillaSunriseOntoPhysicalSunrise_AtEveryLatitudeAndSeason()
    {
        // THE contract. Feed the warp vanilla's sunrise instant (0.5 - hv) and it must return our
        // physical sunrise instant (0.5 - hp) — i.e. our sun is exactly at the horizon when vanilla's
        // sky changes. Same for sunset. Day-length error is therefore 0, not "small".
        foreach (float lat in Latitudes())
        {
            for (int day = 0; day < 60; day += 5)
            {
                float hp = SunClockMath.PhysicalHalfDay(lat, Declination(day));
                foreach (float hv in new[] { 0.1f, 0.25f, 0.4f })
                {
                    float sunrise = SunClockMath.WarpDayPercent(0.5f - hv, hv, hp);
                    float sunset = SunClockMath.WarpDayPercent(0.5f + hv, hv, hp);

                    float expected = Clamp(hp, SunClockMath.MinWarpHalfDay, 0.5f - SunClockMath.MinWarpHalfDay);
                    Assert.That(sunrise, Is.EqualTo(0.5f - expected).Within(0.001f),
                        $"sunrise mismatch at lat {lat}, day {day}, vanilla half-day {hv}");
                    Assert.That(sunset, Is.EqualTo(0.5f + expected).Within(0.001f),
                        $"sunset mismatch at lat {lat}, day {day}, vanilla half-day {hv}");
                }
            }
        }
    }

    [Test]
    public void Warp_PinsNoonAndMidnight()
    {
        // Noon must stay noon and midnight must stay midnight, or the whole day slides against the
        // colony's clock and pawn schedules stop lining up with the light.
        Assert.That(SunClockMath.WarpDayPercent(0.5f, 0.3f, 0.2f), Is.EqualTo(0.5f).Within(Tolerance));
        Assert.That(SunClockMath.WarpDayPercent(0f, 0.3f, 0.2f), Is.EqualTo(0f).Within(0.001f));
    }

    [Test]
    public void Warp_IsStrictlyIncreasing()
    {
        // Monotonic means no event is ever reordered — the sun cannot appear to jump backwards.
        // Exclusive of 1.0: dayPercent is a fraction of the day, so 1.0 IS 0.0 (midnight) and the
        // warp deliberately wraps it — see Warp_WrapsAFullDayBackToMidnight.
        float previous = float.NegativeInfinity;
        for (int i = 0; i < 400; i++)
        {
            float warped = SunClockMath.WarpDayPercent(i / 400f, 0.32f, 0.21f);
            Assert.That(warped, Is.GreaterThan(previous), $"warp went backwards at dayPercent {i / 400f}");
            previous = warped;
        }
    }

    [Test]
    public void Warp_IsIdentity_WhenBothClocksAgree()
    {
        for (int i = 0; i < 20; i++)     // exclusive of 1.0, which wraps to 0.0
        {
            float dp = i / 20f;
            Assert.That(SunClockMath.WarpDayPercent(dp, 0.3f, 0.3f), Is.EqualTo(dp).Within(0.001f));
        }
    }

    [Test]
    public void Warp_WrapsAFullDayBackToMidnight()
    {
        // dayPercent 1.0 and 0.0 are the same instant. The warp normalises into [-0.5, 0.5) around
        // noon, so it maps them identically rather than running off the end of the night branch.
        Assert.That(SunClockMath.WarpDayPercent(1f, 0.3f, 0.2f),
            Is.EqualTo(SunClockMath.WarpDayPercent(0f, 0.3f, 0.2f)).Within(Tolerance));
    }

    [Test]
    public void Warp_VanillaPolarDay_KeepsOurSunUpAllDay()
    {
        // hv == 0.5 with a physical polar day too: nothing to remap, midnight must NOT dip below the
        // horizon or we would invent a sunset vanilla does not have.
        Assert.That(SunClockMath.WarpDayPercent(0f, 0.5f, 0.5f), Is.EqualTo(0f).Within(0.001f));
        Assert.That(SunClockMath.WarpDayPercent(0.25f, 0.5f, 0.5f), Is.EqualTo(0.25f).Within(0.001f));
    }

    [Test]
    public void Warp_VanillaPolarNight_NeverProducesDaytime()
    {
        // hv == 0: every instant belongs to the night branch, so the warped time never lands inside
        // the physical daytime window.
        float hp = 0.3f;
        for (int i = 0; i <= 40; i++)
        {
            float warped = SunClockMath.WarpDayPercent(i / 40f, 0f, hp);
            float offset = MathF.Abs(warped - 0.5f);
            Assert.That(offset, Is.GreaterThanOrEqualTo(hp - 0.001f),
                $"vanilla polar night produced daylight at dayPercent {i / 40f}");
        }
    }

    [Test]
    public void Warp_ClampsAwayFromZeroLengthDays()
    {
        // Vanilla says the sun rises, our physical sun says polar night. Without the clamp the target
        // window is zero-length, the whole day collapses to one instant and the sun freezes for 24 h.
        float sunrise = SunClockMath.WarpDayPercent(0.5f - 0.2f, 0.2f, 0f);
        Assert.That(sunrise, Is.EqualTo(0.5f - SunClockMath.MinWarpHalfDay).Within(0.001f));
    }

    // --- GlowFromElevation (REALISTIC mode) ---

    [Test]
    public void Glow_IsZeroAtTheHorizonAndBelow()
    {
        Assert.That(SunClockMath.GlowFromElevation(Formulas.AtmosphericRefractionDegrees), Is.EqualTo(0f).Within(Tolerance));
        Assert.That(SunClockMath.GlowFromElevation(-20f), Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void Glow_HitsTheDaytimeBar_ExactlyAtTheFittedElevation()
    {
        // This anchor IS the day-length knob: vanilla's IsDaytime is glow > 0.6.
        Assert.That(SunClockMath.GlowFromElevation(SunClockMath.DaytimeElevationDegrees),
            Is.EqualTo(SunClockMath.DaytimeGlow).Within(Tolerance));
    }

    [Test]
    public void Glow_SaturatesAtTheSaturationAnchor_AndStaysThere()
    {
        Assert.That(SunClockMath.GlowFromElevation(SunClockMath.SaturationElevationDegrees), Is.EqualTo(1f).Within(Tolerance));
        Assert.That(SunClockMath.GlowFromElevation(90f), Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void Glow_KeepsAGradualDusk_NotVanillasCollapsedRamp()
    {
        // Regression guard for the single-scale-factor trap: if glow saturated near the horizon
        // (the K = 6.3 fit), dusk would collapse from ~240 min to ~37 min and every glow-keyed
        // subsystem with it. Halfway to saturation must still be well short of full daylight.
        Assert.That(SunClockMath.GlowFromElevation(20f), Is.LessThan(0.95f));
        Assert.That(SunClockMath.GlowFromElevation(10f), Is.LessThan(0.8f));
    }

    [Test]
    public void Glow_IsMonotonic()
    {
        float previous = -1f;
        for (int e = -5; e <= 90; e++)
        {
            float g = SunClockMath.GlowFromElevation(e);
            Assert.That(g, Is.GreaterThanOrEqualTo(previous), $"glow decreased at elevation {e}");
            previous = g;
        }
    }

    private static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
}
