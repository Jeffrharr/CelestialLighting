namespace CelestialLighting.Tests;

/// <summary>
/// The MODE half of the moon/§14 coupling. <see cref="MoonSunClockTests"/> pins that the moon rides
/// the warped clock in the default locked mode; this fixture pins the other two things a settings
/// toggle needs: that switching to "Realistic day length" moves the moon at all, and that the moon
/// stays on the sun once it gets there.
///
/// The two modes reconcile our physical sun with vanilla's sky from opposite ends:
///
///   LOCKED    — SunClockAdapter warps the day percent onto vanilla's measured day, and MoonPosition
///               takes ITS percent from SolarPosition.Inputs, so the moon inherits the same warp.
///   REALISTIC — the warp is the identity and Patch_SunGlow rewrites vanilla's glow from our sun's
///               elevation instead. The moon inherits the identity, which is the correct answer:
///               "the moon lags the sun's clock" is the same rule applied to a different clock.
///
/// SCOPE, stated plainly because it limits what a green run here proves. These assertions mirror the
/// composition SunClockAdapter.EffectiveDayPercent and MoonPosition.SkyForMap perform; they do not
/// call it, because both take a live Map. So this fixture documents that the composition is correct
/// and measures how correct — it cannot catch MoonPosition being rewired back to
/// GenLocalDate.DayPercent. That regression is caught by the sun_clock_realistic_moon live scenario,
/// which drives the real adapters through the real game. The two are meant to be read together.
///
/// Deliberately NOT duplicated from elsewhere: the locked-mode versions of the invariants below
/// (MoonSunClockTests already has them, tile for tile), and "a new moon sits on the sun" in realistic
/// mode, which on an identity clock reduces exactly to
/// MoonMathTests.MoonElevation_EqualsSunElevation_AtNewMoon and cannot fail independently.
///
/// NOTE ON EXACTNESS, same as MoonSunClockTests: even on a single clock the full moon does not rise
/// at the precise instant the sun sets. The refraction horizon enters both sunrise equations with the
/// same sign while the full moon's declination is the sun's REFLECTED, so the two windows are not
/// exact complements and ~0.8 degrees of residual is inherent to the moon model. Realistic mode IS
/// the single-clock case, so the bounds below sit at that residual rather than at zero.
/// </summary>
[TestFixture]
public class SunClockModeMoonTests
{
    // Public only because NUnit test methods take it as a parameter and C# will not let a public
    // method expose a private type.
    public enum Mode
    {
        LockedToVanilla,
        Realistic,
    }

    // Latitude, day of year, and the half-day vanilla reports there — the same tiles
    // MoonSunClockTests uses, each measured by bisecting GenCelestial.CelestialSunGlow the way
    // SunClock does, so both fixtures are talking about the same real vanilla numbers.
    private static readonly object[] TileCases =
    {
        new object[] { 0f, 15, 0.35000f },   // equator, equinox     — vanilla 16.80 h day
        new object[] { 30f, 0, 0.32044f },   // lat 30, winter       — vanilla 15.38 h day
        new object[] { 45f, 15, 0.34426f },  // lat 45, equinox      — vanilla 16.52 h day
        new object[] { 60f, 0, 0.30144f },   // lat 60, winter       — vanilla 14.47 h day
        new object[] { -45f, 30, 0.31020f }, // southern lat 45, its own winter
    };

    // Mirrors SunClockAdapter.EffectiveDayPercent: the percent our solar model is actually evaluated
    // at. Realistic mode is the identity because in that mode vanilla follows US.
    private static float EffectiveDayPercent(
        Mode mode, float latitude, int dayOfYear, float rawDayPercent, float vanillaHalfDay)
    {
        if (mode == Mode.Realistic)
            return rawDayPercent;

        float declination = Formulas.SolarDeclinationDegrees(dayOfYear);
        float physicalHalfDay = SunClockMath.PhysicalHalfDay(latitude, declination);
        return SunClockMath.WarpDayPercent(rawDayPercent, vanillaHalfDay, physicalHalfDay);
    }

    // The composition under test, exactly as MoonPosition.SkyForMap performs it: take the sun's
    // (mode-dependent) day percent, lag it by the elongation, evaluate the moon there.
    private static float MoonElevation(
        Mode mode, float latitude, int dayOfYear, float cyclePosition, float rawDayPercent, float vanillaHalfDay)
    {
        float effective = EffectiveDayPercent(mode, latitude, dayOfYear, rawDayPercent, vanillaHalfDay);
        float declination = MoonMath.MoonDeclinationDegrees(dayOfYear, cyclePosition);
        return Formulas.SolarElevationDegrees(
            latitude, declination, MoonMath.MoonDayPercent(effective, cyclePosition));
    }

    // --- the toggle actually reaches the moon ---

    [TestCaseSource(nameof(TileCases))]
    public void TogglingTheMode_MovesTheMoon(float latitude, int dayOfYear, float vanillaHalfDay)
    {
        // A moon that ignored the setting would report identical elevations in both modes, so assert
        // the two disagree. This is the assertion that would have failed on a build where the mode
        // reached the sun and not the moon.
        float biggestGap = 0f;
        for (int hour = 0; hour < 24; hour++)
        {
            float p = hour / 24f;
            float locked = MoonElevation(Mode.LockedToVanilla, latitude, dayOfYear, 0.5f, p, vanillaHalfDay);
            float realistic = MoonElevation(Mode.Realistic, latitude, dayOfYear, 0.5f, p, vanillaHalfDay);
            biggestGap = System.MathF.Max(biggestGap, System.MathF.Abs(locked - realistic));
        }

        // Measured 21.9-33.5 degrees across these tiles — the same order as the sun's own gap between
        // the two modes, because the moon is carried by the identical warp.
        Assert.That(
            biggestGap,
            Is.GreaterThan(5f),
            "the moon reported the same position in both modes, so the day-length setting is not reaching it");
    }

    [TestCaseSource(nameof(TileCases))]
    public void TogglingTheMode_LeavesTheWarpsFixedPointsAlone(
        float latitude, int dayOfYear, float vanillaHalfDay)
    {
        // The control for the test above. Midnight and noon are fixed points of WarpDayPercent (both
        // windows are anchored there), so a mode change must move the moon by exactly zero at those
        // two instants. Together the two tests say the toggle applies a TIME WARP to the moon rather
        // than a blanket offset — the same argument moon_sun_clock.json makes live at hour 0.
        foreach (float p in new[] { 0f, 0.5f })
        {
            float locked = MoonElevation(Mode.LockedToVanilla, latitude, dayOfYear, 0.5f, p, vanillaHalfDay);
            float realistic = MoonElevation(Mode.Realistic, latitude, dayOfYear, 0.5f, p, vanillaHalfDay);
            Assert.That(
                locked,
                Is.EqualTo(realistic).Within(0.001f),
                $"the moon moved at day percent {p}, which is a fixed point of the warp");
        }
    }

    // --- and once there, realistic mode's moon is on realistic mode's sky ---
    //
    // In this mode the sky's day/night window is not vanilla's any more: Patch_SunGlow replaces the
    // glow with SunClockMath.GlowFromElevation, whose zero is exactly the refraction horizon — so the
    // window IS SunClockMath.PhysicalHalfDay. That is an analytic solve, while moonrise below is
    // found numerically off the moon's own declination and lag, so these three tests cross-check two
    // independently-derived things rather than restating one.

    private static float RealisticHalfDay(float latitude, int dayOfYear) =>
        SunClockMath.PhysicalHalfDay(latitude, Formulas.SolarDeclinationDegrees(dayOfYear));

    // vanillaHalfDay is unused on this path by construction — realistic mode never consults it — so
    // it is passed as 0 rather than threaded through, which would imply it mattered.
    private static float RealisticMoonElevation(
        float latitude, int dayOfYear, float cyclePosition, float rawDayPercent) =>
        MoonElevation(Mode.Realistic, latitude, dayOfYear, cyclePosition, rawDayPercent, vanillaHalfDay: 0f);

    // Raw day percent at which `elevationAt` rises through the refraction horizon, or NaN if it never
    // does. Coarse scan to bracket, then bisect.
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

    // Signed a - b in HOURS, wrapped into [-12, 12) so a crossing just after midnight compares sanely
    // against one just before it.
    private static float HoursBetween(float a, float b)
    {
        float d = a - b;
        return (d - System.MathF.Floor(d + 0.5f)) * 24f;
    }

    [TestCaseSource(nameof(TileCases))]
    public void Realistic_FullMoonRisesWhenTheSkyGoesDark(
        float latitude, int dayOfYear, float vanillaHalfDay)
    {
        // Sunset here means "where realistic mode's glow reaches zero", which is also where both
        // shadow patches hand off from the sun's shadow to the moon's.
        float sunset = 0.5f + RealisticHalfDay(latitude, dayOfYear);
        float moonrise = Moonrise(p => RealisticMoonElevation(latitude, dayOfYear, 0.5f, p));

        Assert.That(moonrise, Is.Not.NaN, "full moon never rose at all");

        // Measured 0.11-0.37 h — realistic mode is the single-clock case, so it lands exactly on the
        // model's inherent residual, the same range MoonSunClockTests quotes as the pre-§14 baseline.
        // (Locked mode adds the warp's own approximation on top and runs 0.15-0.90 h.)
        float error = System.MathF.Abs(HoursBetween(moonrise, sunset));
        Assert.That(error, Is.LessThan(0.5f), $"full moon rose {error:0.00} h away from sunset");
    }

    [TestCaseSource(nameof(TileCases))]
    public void Realistic_FullMoonCoversTheWholeNight(float latitude, int dayOfYear, float vanillaHalfDay)
    {
        // What a player actually sees, sampled per game-minute so a moon that dipped mid-night would
        // still be caught. A mode that moved the sun's window without moving the moon would open a
        // dark, moonless wedge at one end of the night and hang the moon in a lit sky at the other —
        // so these two counters catch a mismatch from both sides.
        const int Samples = 1440;
        float horizon = Formulas.AtmosphericRefractionDegrees;
        float halfDay = RealisticHalfDay(latitude, dayOfYear);
        int darkAndMoonless = 0;
        int litAndMoonUp = 0;

        for (int i = 0; i < Samples; i++)
        {
            float p = (i + 0.5f) / Samples;
            bool skyLit = System.MathF.Abs(p - 0.5f) < halfDay;
            bool moonUp = RealisticMoonElevation(latitude, dayOfYear, 0.5f, p) > horizon;

            if (!skyLit && !moonUp)
                darkAndMoonless++;
            if (skyLit && moonUp)
                litAndMoonUp++;
        }

        // Exact: a full moon covers every dark minute.
        Assert.That(darkAndMoonless, Is.Zero, "the full moon left part of the night unlit");

        // The overhang is the soft half. Measured 0.20-0.73 h — tighter than locked mode's 0.30-1.80 h,
        // because there is no warp approximation here to widen it.
        float overhangHours = litAndMoonUp * 24f / Samples;
        Assert.That(overhangHours, Is.LessThan(1f), $"full moon hung in a lit sky for {overhangHours:0.00} h");
    }

    [TestCaseSource(nameof(TileCases))]
    public void Realistic_ShadowHandoffStaysAtTheBaselineStep(
        float latitude, int dayOfYear, float vanillaHalfDay)
    {
        // MoonMath.MoonShadowStrength ramps over only ~3 degrees of moon altitude, so a moon that is
        // meaningfully up at the handoff snaps the shadow straight to full strength in one frame. On a
        // single clock the full moon is 0.83 degrees up there (strength 0.155) — the step the moon
        // model has always had, and what realistic mode must land on rather than above.
        float sunset = 0.5f + RealisticHalfDay(latitude, dayOfYear);
        float elevation = RealisticMoonElevation(latitude, dayOfYear, 0.5f, sunset);
        float strength = MoonMath.MoonShadowStrength(MoonMath.IlluminatedFraction(0.5f), elevation);

        Assert.That(elevation, Is.LessThan(2f), $"moon was {elevation:0.0} degrees up at the handoff");
        Assert.That(
            strength,
            Is.LessThan(0.16f),
            $"moon shadow snapped in at strength {strength:0.000}, above the single-clock baseline");
    }
}
