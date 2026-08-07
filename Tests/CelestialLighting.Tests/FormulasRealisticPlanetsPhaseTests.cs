using System;

namespace CelestialLighting.Tests;

/// <summary>
/// Offline coverage for the pure half of the Realistic Planets 2 interop (DESIGN.md's "Interop:
/// Realistic Planets 2"): <c>Formulas.RealisticPlanetsSolarDeclinationDegrees</c>, which is our
/// declination curve read a quarter of a year ahead.
///
/// The live half — reading their per-save tilt step off a static field by reflection and mapping it
/// through their own enum table — needs Verse types and a loaded game, so it is covered by
/// Tests/Scenarios/realistic_planets_tilt.json instead. What these tests pin is the arithmetic that
/// decides where in the year the sun peaks once we have their tilt, which is the half of this
/// interop that differs from the Planetsmith one and therefore the half nothing else covers.
///
/// The load-bearing claim is <see cref="MatchesTheirSinPhase"/>: RP2 computes
/// <c>tilt * sin(2*pi * yearPhase)</c> and we have to land on the same number, day for day, or the
/// sky and the weather they simulate are describing different planets. Every other test here exists
/// to stop that one passing for the wrong reason.
/// </summary>
[TestFixture]
public class FormulasRealisticPlanetsPhaseTests
{
    private const float Tolerance = 1e-3f;

    // Their five steps, from AxialTiltCurves.GetTiltDegrees. Pinned as literals rather than read
    // through reflection because that is the point: if upstream retunes the ladder, the live
    // scenario's declination pin should fail, and these tests should keep describing the maths
    // rather than following the table around.
    private static IEnumerable<float> TiltSteps()
    {
        yield return 0f;      // VeryLow
        yield return 11.25f;  // Low
        yield return 22.5f;   // Normal, their default
        yield return 33.75f;  // High
        yield return 45f;     // VeryHigh
    }

    // --- We land on their curve ---

    /// <summary>
    /// Reproduces Planets.WorldGen.SolarGeometry.GetSolarDeclinationRad directly — tilt times the
    /// sine of the year phase — and asserts our shifted-cosine formulation agrees with it at every
    /// day of the year, for every tilt step they offer.
    ///
    /// Written as their formula rather than as ours-plus-fifteen so the test is an independent
    /// statement of what we are trying to match. A test that restated our own implementation would
    /// pass through any change to the offset constant, which is the exact thing most likely to be
    /// got wrong here.
    /// </summary>
    [Test]
    public void MatchesTheirSinPhase()
    {
        foreach (float tilt in TiltSteps())
        {
            for (float day = 0f; day < Formulas.DaysPerYear; day += 0.25f)
            {
                float theirs = tilt * MathF.Sin(day / Formulas.DaysPerYear * MathF.PI * 2f);

                Assert.That(
                    Formulas.RealisticPlanetsSolarDeclinationDegrees(day, tilt),
                    Is.EqualTo(theirs).Within(Tolerance),
                    $"tilt {tilt}, day {day}");
            }
        }
    }

    /// <summary>
    /// The offset is a quarter of a year, in the direction that puts their solstice EARLIER.
    ///
    /// Direction is the whole content of this test. A quarter-year offset applied the other way
    /// round also turns a cosine into a sine — into the negative of one — so a sign slip here would
    /// still look like "a quarter of a year" in every summary statistic while putting midsummer
    /// where midwinter belongs.
    /// </summary>
    [Test]
    public void TheirSolsticeLandsFifteenDaysBeforeOurs()
    {
        // Ours peaks at day 30 (-cos), theirs at day 15 (sin).
        Assert.That(
            Formulas.RealisticPlanetsSolarDeclinationDegrees(15f, 45f),
            Is.EqualTo(45f).Within(Tolerance),
            "their midsummer");

        Assert.That(
            Formulas.RealisticPlanetsSolarDeclinationDegrees(45f, 45f),
            Is.EqualTo(-45f).Within(Tolerance),
            "their midwinter");

        Assert.That(Formulas.RealisticPlanetsYearPhaseOffsetDays, Is.EqualTo(15f).Within(Tolerance));
    }

    /// <summary>
    /// Where our year is flat, theirs is at full swing — and the other way round. This is the pair of
    /// days a live scenario has to sample to see the interop at all, so it is pinned here to keep the
    /// scenario's choice of day honest rather than lucky.
    ///
    /// Day 15 is the strongest possible signal: our declination is exactly zero there, so the two
    /// arms of the A/B are "no season at all" against "the full tilt", and no tolerance can absorb
    /// the difference. Day 30 is its mirror and catches an implementation that had somehow made both
    /// curves agree by flattening ours.
    /// </summary>
    [Test]
    public void TheTwoModelsDisagreeMostWhereOneOfThemIsFlat()
    {
        const float Tilt = 45f;

        Assert.That(Formulas.SolarDeclinationDegrees(15f, Tilt), Is.EqualTo(0f).Within(Tolerance));
        Assert.That(
            Formulas.RealisticPlanetsSolarDeclinationDegrees(15f, Tilt),
            Is.EqualTo(Tilt).Within(Tolerance));

        Assert.That(Formulas.SolarDeclinationDegrees(30f, Tilt), Is.EqualTo(Tilt).Within(Tolerance));
        Assert.That(
            Formulas.RealisticPlanetsSolarDeclinationDegrees(30f, Tilt),
            Is.EqualTo(0f).Within(Tolerance));
    }

    // --- It is still a phase shift, not a different curve ---

    /// <summary>
    /// Same swing, same shape, same tilt-proportionality as our own curve — only the phase moves.
    ///
    /// Guards the thing a future reader is most likely to try: replacing the day offset with a second
    /// trigonometric expression written from scratch. That would be free to acquire a different
    /// amplitude or a stray scale factor, and every test above would still pass at the four cardinal
    /// days it samples.
    /// </summary>
    [Test]
    public void IsOurOwnCurveWithNothingButThePhaseChanged()
    {
        foreach (float tilt in TiltSteps())
        {
            for (float day = 0f; day < Formulas.DaysPerYear; day += 0.25f)
            {
                Assert.That(
                    Formulas.RealisticPlanetsSolarDeclinationDegrees(day, tilt),
                    Is.EqualTo(Formulas.SolarDeclinationDegrees(
                        day + Formulas.RealisticPlanetsYearPhaseOffsetDays, tilt)).Within(Tolerance),
                    $"tilt {tilt}, day {day}");
            }
        }
    }

    /// <summary>
    /// A day-of-year past the end of the year needs no wrapping: the shift pushes days 45-59 past 60,
    /// and the cosine is periodic, so day 58 + 15 has to give what day 13 gives.
    ///
    /// Pinned because the obvious "fix" for a shifted day running off the end of the year is a
    /// modulo, and adding one would be harmless here but would diverge from
    /// MoonPosition's use of the same seam, which passes a shifted day-of-year of its own.
    /// </summary>
    [Test]
    public void PastTheEndOfTheYearWrapsByPeriodicityAlone()
    {
        for (float day = 45f; day < Formulas.DaysPerYear; day += 0.5f)
        {
            Assert.That(
                Formulas.RealisticPlanetsSolarDeclinationDegrees(day, 45f),
                Is.EqualTo(Formulas.RealisticPlanetsSolarDeclinationDegrees(
                    day - Formulas.DaysPerYear, 45f)).Within(Tolerance),
                $"day {day}");
        }
    }

    /// <summary>
    /// An upright planet is seasonless on their phase exactly as it is on ours. Their VeryLow step is
    /// literally 0 degrees, so this is a world a player can actually generate rather than a boundary
    /// value — and the phase shift has to be invisible on it, because there is nothing to shift.
    /// </summary>
    [Test]
    public void ZeroTiltIsSeasonlessOnTheirPhaseToo()
    {
        for (float day = 0f; day < Formulas.DaysPerYear; day += 1f)
        {
            Assert.That(
                Formulas.RealisticPlanetsSolarDeclinationDegrees(day, 0f),
                Is.EqualTo(0f).Within(Tolerance),
                $"day {day}");
        }
    }

    // --- The guard the overload brings with it ---

    /// <summary>
    /// The sanitizing clamp reaches this path too, because it goes through the same overload. Their
    /// enum cannot currently produce an out-of-range tilt, but the clamp is what makes that a fact
    /// about their table rather than a dependency of ours on it.
    /// </summary>
    [TestCase(120f, 90f)]
    [TestCase(-10f, 0f)]
    public void OutOfRangeTiltIsClampedBeforeItReachesTheSky(float input, float expected)
    {
        // Day 15 is their solstice, where the declination is the tilt itself, so the clamped value
        // reads straight out of the result.
        Assert.That(
            Formulas.RealisticPlanetsSolarDeclinationDegrees(15f, input),
            Is.EqualTo(expected).Within(Tolerance));
    }

    /// <summary>
    /// NaN falls back to Earth's tilt rather than propagating, same as it does on the Planetsmith
    /// path. RealisticPlanetsCompat rejects a non-finite value before it gets here as well; this is
    /// the braces to that belt.
    /// </summary>
    [Test]
    public void NaNTiltFallsBackRatherThanPoisoningTheSky()
    {
        Assert.That(
            Formulas.RealisticPlanetsSolarDeclinationDegrees(15f, float.NaN),
            Is.EqualTo(Formulas.AxialTiltDegrees).Within(Tolerance));
    }
}
