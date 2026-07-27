namespace CelestialLighting.Tests;

/// <summary>
/// The invariant that couples subsystem 6 (the moon) to subsystem 14 (sun-clock reconciliation):
/// the moon's hour angle is DEFINED as the sun's minus the elongation, so both bodies have to sit on
/// the same clock. When §14's locked mode warps the sun's day percent onto vanilla's day, the moon
/// must be warped with it.
///
/// These tests exist because the first §14 implementation warped only the sun. Nothing in
/// MoonMathTests or SunClockMathTests could catch that — each subsystem was individually correct, and
/// the bug lived entirely in which day percent the live adapter fed to the other. So the assertions
/// here are deliberately written across both pure cores at once, in the same composition
/// MoonPosition.SkyForMap performs, and each one carries the raw-clock counter-measurement so a
/// future reader can see the fix is load-bearing rather than decorative.
///
/// vanillaHalfDay is a test input rather than something computed here: measuring it means sampling
/// GenCelestial's own glow off a live game, which is exactly SunClock's job and is covered by the
/// moon_sun_clock live scenario. What these tests pin is that the warp contract holds for ANY
/// half-day vanilla reports.
///
/// NOTE ON EXACTNESS. Even on a single clock the full moon does not rise at the precise instant the
/// sun sets. The refraction horizon (-0.83 degrees) enters both sunrise equations with the same sign
/// while the full moon's declination is the sun's REFLECTED, so the two windows are not exact
/// complements: at sunset the full moon sits ~0.8 degrees up, not 0. That residual predates §14 and
/// is a property of the moon model, not of the warp — the bounds below are set against it, so these
/// tests measure the clock and nothing else.
/// </summary>
[TestFixture]
public class MoonSunClockTests
{
    // Latitude, day of year, and the half-day vanilla reports there — each measured by bisecting
    // GenCelestial.CelestialSunGlow the way SunClock does, so the warp is exercised against real
    // vanilla numbers rather than invented ones.
    private static readonly object[] TileCases =
    {
        new object[] { 0f, 15, 0.35000f },   // equator, equinox     — vanilla 16.80 h day
        new object[] { 30f, 0, 0.32044f },   // lat 30, winter       — vanilla 15.38 h day
        new object[] { 45f, 15, 0.34426f },  // lat 45, equinox      — vanilla 16.52 h day
        new object[] { 60f, 0, 0.30144f },   // lat 60, winter       — vanilla 14.47 h day, the worst
                                             //                        ordinary-latitude case
        new object[] { -45f, 30, 0.31020f }, // southern lat 45, its own winter
    };

    // The composition under test: the sun's day percent, warped, then lagged by the elongation —
    // exactly what MoonPosition.SkyForMap does now that it takes its percent from SolarPosition.Inputs.
    private static float MoonElevation(
        float latitude, int dayOfYear, float cyclePosition, float rawDayPercent, float vanillaHalfDay)
    {
        float warped = WarpedDayPercent(latitude, dayOfYear, rawDayPercent, vanillaHalfDay);
        return MoonElevationAt(latitude, dayOfYear, cyclePosition, warped);
    }

    // The regression: the moon left on the RAW clock while the sun is warped.
    private static float MoonElevationOnRawClock(
        float latitude, int dayOfYear, float cyclePosition, float rawDayPercent) =>
        MoonElevationAt(latitude, dayOfYear, cyclePosition, rawDayPercent);

    private static float MoonElevationAt(
        float latitude, int dayOfYear, float cyclePosition, float dayPercent)
    {
        float declination = MoonMath.MoonDeclinationDegrees(dayOfYear, cyclePosition);
        return Formulas.SolarElevationDegrees(
            latitude, declination, MoonMath.MoonDayPercent(dayPercent, cyclePosition));
    }

    private static float SunElevation(float latitude, int dayOfYear, float rawDayPercent, float vanillaHalfDay)
    {
        float warped = WarpedDayPercent(latitude, dayOfYear, rawDayPercent, vanillaHalfDay);
        return Formulas.SolarElevationDegrees(latitude, Formulas.SolarDeclinationDegrees(dayOfYear), warped);
    }

    private static float WarpedDayPercent(
        float latitude, int dayOfYear, float rawDayPercent, float vanillaHalfDay)
    {
        float declination = Formulas.SolarDeclinationDegrees(dayOfYear);
        float physicalHalfDay = SunClockMath.PhysicalHalfDay(latitude, declination);
        return SunClockMath.WarpDayPercent(rawDayPercent, vanillaHalfDay, physicalHalfDay);
    }

    // Raw day percent at which `elevationAt` rises through the horizon, or NaN if it never does.
    // Coarse scan to bracket, then bisect — the curve is smooth and crosses at most twice a day, so
    // nothing subtler is warranted.
    private static float Moonrise(System.Func<float, float> elevationAt)
    {
        const int Samples = 2880; // every 30 seconds of game time
        float horizon = Formulas.AtmosphericRefractionDegrees;

        for (int i = 0; i < Samples; i++)
        {
            float lo = (float)i / Samples;
            float hi = (float)(i + 1) / Samples;
            bool crossesUpward = elevationAt(lo) <= horizon && elevationAt(hi) > horizon;
            if (crossesUpward)
            {
                for (int k = 0; k < 40; k++)
                {
                    float mid = (lo + hi) * 0.5f;
                    if (elevationAt(mid) <= horizon)
                        lo = mid;
                    else
                        hi = mid;
                }

                return (lo + hi) * 0.5f;
            }
        }

        return float.NaN;
    }

    // Signed a - b in HOURS, wrapped into [-12, 12) so a crossing just after midnight compares
    // sanely against one just before it.
    private static float HoursBetween(float a, float b)
    {
        float d = a - b;
        return (d - MathF.Floor(d + 0.5f)) * 24f;
    }

    [TestCaseSource(nameof(TileCases))]
    public void FullMoon_RisesWhenVanillasSunSets(float latitude, int dayOfYear, float vanillaHalfDay)
    {
        // Vanilla's sunset in raw day percent. The warp is built so our sun crosses the horizon
        // exactly here, so this is also the instant both shadow patches hand off from sun to moon.
        float sunset = 0.5f + vanillaHalfDay;

        float warpedMoonrise = Moonrise(p => MoonElevation(latitude, dayOfYear, 0.5f, p, vanillaHalfDay));
        float rawMoonrise = Moonrise(p => MoonElevationOnRawClock(latitude, dayOfYear, 0.5f, p));

        Assert.That(warpedMoonrise, Is.Not.NaN, "full moon never rose at all");

        float warpedError = MathF.Abs(HoursBetween(warpedMoonrise, sunset));
        float rawError = MathF.Abs(HoursBetween(rawMoonrise, sunset));

        // Measured: 0.15 h at the equator, 0.21-0.32 h at latitude 45, 0.90 h at latitude 60 in
        // winter (the warp stretches the moon's arc by the SUN's ratio, which is not quite the
        // moon's, and that approximation costs most where the two half-days differ most). Against a
        // pre-§14 single-clock baseline of 0.11-0.37 h over the same tiles.
        Assert.That(
            warpedError,
            Is.LessThan(1f),
            $"full moon rose {warpedError:0.00} h away from vanilla's sunset");

        // The comparison that makes the bound above meaningful: the raw clock is 4-13 h out at these
        // same tiles, so a regression cannot hide inside a loose absolute tolerance.
        Assert.That(
            warpedError * 4f,
            Is.LessThan(rawError),
            $"warping the moon only improved moonrise from {rawError:0.00} h to {warpedError:0.00} h off sunset");
    }

    [TestCaseSource(nameof(TileCases))]
    public void ShadowHandoff_HasNoStepAtAll(
        float latitude, int dayOfYear, float vanillaHalfDay)
    {
        // Patch_ShadowDirection and Patch_ShadowStrength switch from the sun's shadow to the moon's at
        // the sun's horizon crossing, where Formulas.ShadowIntensityFromElevation has ramped the sun
        // shadow to 0.
        //
        // This test used to bound the resulting step rather than forbid it. Under the pre-§6b model the
        // moon shadow started at whatever the moon's elevation implied, over a ramp only 3 degrees
        // wide, so a moon even slightly up was already a visible fraction of full strength: a single
        // clock put the moon 0.83 degrees up at that instant for a step of 0.155, and the raw-clock bug
        // put it 10-36 degrees up for the full 0.28 — a hard pop in alpha and direction on every clear
        // night around full moon. The assertion was "no worse than the 0.155 baseline, which the model
        // cannot deliver zero for".
        //
        // §6b delivers zero. Moon shadow strength is now the moonlight-to-skylight ratio, and the sky
        // at the handoff is still ~200 lux against the full moon's 0.27 — so the moon contributes
        // nothing there no matter where it sits, and the shadow fades in later, on its own, as the
        // twilight drains. The step is gone rather than bounded.
        //
        // The elevation assertion below is the one that still guards §14: it is what actually detects
        // the moon being warped onto the wrong clock. The strength assertion can no longer detect that
        // (it is zero either way now), so it is kept as a §6b regression guard instead — if a future
        // change reintroduces a strength that depends on moon elevation alone, this fires.
        float sunset = 0.5f + vanillaHalfDay;
        float elevation = MoonElevation(latitude, dayOfYear, 0.5f, sunset, vanillaHalfDay);
        float strength = MoonMath.MoonShadowStrength(
            MoonMath.IlluminatedFraction(0.5f), elevation, Formulas.AtmosphericRefractionDegrees);

        Assert.That(elevation, Is.LessThan(2f), $"moon was {elevation:0.0} degrees up at the handoff");
        Assert.That(
            strength,
            Is.EqualTo(0f),
            $"moon shadow stepped in at strength {strength:0.000} instead of fading in after twilight");
    }

    [TestCaseSource(nameof(TileCases))]
    public void NewMoon_SitsOnTheSunAllDay(float latitude, int dayOfYear, float vanillaHalfDay)
    {
        // At elongation 0 the moon's declination equals the sun's and its lag is zero, so a new moon
        // is geometrically the same point in the sky as the sun. Any elevation gap here is the two
        // bodies running on different clocks — the raw-clock bug opened this to 12-35 degrees.
        for (int hour = 0; hour < 24; hour++)
        {
            float p = hour / 24f;
            float moon = MoonElevation(latitude, dayOfYear, 0f, p, vanillaHalfDay);
            float sun = SunElevation(latitude, dayOfYear, p, vanillaHalfDay);
            Assert.That(moon, Is.EqualTo(sun).Within(0.001f), $"new moon left the sun at hour {hour}");
        }
    }

    [TestCaseSource(nameof(TileCases))]
    public void FullMoon_LightsTheWholeNightAndBarelyOverhangsTheDay(
        float latitude, int dayOfYear, float vanillaHalfDay)
    {
        // What a player actually sees. Sampled per game-minute rather than at the endpoints, so a moon
        // that dipped mid-night would still be caught.
        const int Samples = 1440;
        float horizon = Formulas.AtmosphericRefractionDegrees;
        int darkAndMoonless = 0;
        int litAndMoonUp = 0;

        for (int i = 0; i < Samples; i++)
        {
            float p = (i + 0.5f) / Samples;
            bool sunUp = SunElevation(latitude, dayOfYear, p, vanillaHalfDay) > horizon;
            bool moonUp = MoonElevation(latitude, dayOfYear, 0.5f, p, vanillaHalfDay) > horizon;

            if (!sunUp && !moonUp)
                darkAndMoonless++;
            if (sunUp && moonUp)
                litAndMoonUp++;
        }

        // Exact: a full moon covers every dark minute, on the single clock and on the warped one alike.
        Assert.That(darkAndMoonless, Is.Zero, "the full moon left part of the night unlit");

        // The overhang is the soft half. Measured 0.31-0.64 h below latitude 45 and 1.81 h at latitude
        // 60 in winter, against a pre-§14 baseline of 0.22-0.73 h; the raw-clock bug ran to 9.34 h.
        float overhangHours = litAndMoonUp * 24f / Samples;
        Assert.That(
            overhangHours,
            Is.LessThan(2f),
            $"full moon hung in a lit sky for {overhangHours:0.00} h");
    }
}
