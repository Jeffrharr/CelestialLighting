using System;

namespace CelestialLighting.Tests;

/// <summary>
/// Offline coverage for the pure half of the Planetsmith interop (DESIGN.md §19): the
/// obliquity-parameterized <c>Formulas.SolarDeclinationDegrees</c> overload and the
/// <c>SanitizeObliquityDegrees</c> bound check that guards it.
///
/// The live half — reading Planetsmith's per-world tilt off their world component by reflection —
/// cannot be tested here (it needs Verse types and a loaded World) and is covered by
/// Tests/Scenarios/planetsmith_tilt.json instead. What these tests pin is everything that decides
/// what the sky DOES with a tilt once it has one, which is where a sign slip or a missing clamp
/// would actually change pixels.
///
/// The load-bearing claim, and the one the interop's correctness rests on, is
/// <see cref="Obliquity_ScalesTheSwingWithoutMovingThePhase"/>: Planetsmith supplies a scale and
/// nothing else, so the solstices and equinoxes must land on exactly the same days at every tilt. If
/// that ever stopped holding, feeding their number through our curve would be the mistake
/// AxialTiltCompat says it would be for RAT.
/// </summary>
[TestFixture]
public class FormulasObliquityTests
{
    private const float Tolerance = 1e-3f;

    // Planetsmith's slider range, plus their default and ours. 23.4 vs 23.44 is not a typo: their
    // default and our constant genuinely differ by 0.04 degrees, so merely installing Planetsmith
    // and touching nothing moves our declination by up to that much. Pinned here so the difference
    // is a recorded fact rather than a mystery in a future A/B.
    private static IEnumerable<float> Obliquities()
    {
        yield return 0f;
        yield return 5f;
        yield return 23.4f;   // Planetsmith's default
        yield return 23.44f;  // ours
        yield return 45f;
        yield return 70f;
        yield return 90f;
    }

    // --- The overload agrees with the constant-tilt original ---

    [Test]
    public void AtOurOwnTilt_MatchesTheSingleArgumentOverload()
    {
        for (float day = 0f; day < Formulas.DaysPerYear; day += 1f)
        {
            Assert.That(
                Formulas.SolarDeclinationDegrees(day, Formulas.AxialTiltDegrees),
                Is.EqualTo(Formulas.SolarDeclinationDegrees(day)).Within(Tolerance),
                $"day {day}");
        }
    }

    // --- The scale/phase split the whole interop rests on ---

    /// <summary>
    /// Obliquity scales the declination and never shifts it in time. Checked as a ratio against the
    /// unit phase term at every day of the year, so a phase term that had somehow acquired a
    /// tilt-dependent offset would break this even where the magnitudes still looked plausible.
    /// </summary>
    [Test]
    public void Obliquity_ScalesTheSwingWithoutMovingThePhase()
    {
        foreach (float obliquity in Obliquities())
        {
            for (float day = 0f; day < Formulas.DaysPerYear; day += 0.5f)
            {
                Assert.That(
                    Formulas.SolarDeclinationDegrees(day, obliquity),
                    Is.EqualTo(obliquity * Formulas.DeclinationSign(day)).Within(Tolerance),
                    $"tilt {obliquity}, day {day}");
            }
        }
    }

    /// <summary>
    /// Obliquity enters LINEARLY: doubling the tilt doubles the declination on every day. This is
    /// what makes our number and Planetsmith's commensurable rather than merely both called "tilt".
    /// Their seasonality is scaled by <c>TiltFactor = axialTilt / 23.4</c> — also linear in the tilt —
    /// so a 60° world is 2.56× the seasonal temperature swing for them and 2.56× the declination
    /// amplitude for us, and the sky's seasons stay in proportion to the biomes' by construction
    /// rather than by tuning.
    /// </summary>
    [TestCase(2f)]
    [TestCase(0.5f)]
    [TestCase(60f / 23.4f)]
    public void Obliquity_EntersLinearly(float scale)
    {
        const float baseTilt = 23.4f;

        for (float day = 0f; day < Formulas.DaysPerYear; day += 1f)
        {
            Assert.That(
                Formulas.SolarDeclinationDegrees(day, baseTilt * scale),
                Is.EqualTo(scale * Formulas.SolarDeclinationDegrees(day, baseTilt)).Within(Tolerance),
                $"scale {scale}, day {day}");
        }
    }

    /// <summary>
    /// At zero obliquity our sky is exactly seasonless, where Planetsmith's biome scoring keeps 12% of
    /// a baseline swing (its <c>MinTiltFactor</c> floor). Pinned as a KNOWN and deliberate divergence:
    /// that floor exists so their biome scoring does not degenerate into a single band, not because an
    /// upright planet has seasons, and mimicking it would mean tilting our sun on a world the player
    /// asked to stand upright. The two only disagree at the very end of the slider.
    /// </summary>
    [Test]
    public void ZeroObliquity_IsSeasonlessForUsEvenThoughPlanetsmithFloorsItsOwnSwing()
    {
        for (float day = 0f; day < Formulas.DaysPerYear; day += 1f)
        {
            Assert.That(
                Formulas.SolarDeclinationDegrees(day, 0f), Is.EqualTo(0f).Within(Tolerance), $"day {day}");
        }
    }

    /// <summary>
    /// The equinoxes stay put. Day 15 and day 45 are where DeclinationSign crosses zero, and they
    /// must do so at every tilt — this is the "no phase change" claim at its two most visible points,
    /// and it is also the reason a scenario cannot measure this interop at an equinox.
    /// </summary>
    [TestCase(15f)]
    [TestCase(45f)]
    public void Equinoxes_AreTiltIndependent(float dayOfYear)
    {
        foreach (float obliquity in Obliquities())
        {
            Assert.That(
                Formulas.SolarDeclinationDegrees(dayOfYear, obliquity),
                Is.EqualTo(0f).Within(Tolerance),
                $"tilt {obliquity}");
        }
    }

    /// <summary>
    /// The solstices stay put too, and there the declination is the full tilt with the sign of the
    /// hemisphere — day 30 north, day 0 south. This is the pair of days a live scenario should
    /// sample, because it is where two tilts differ by the most.
    /// </summary>
    [TestCase(30f, 1f)]
    [TestCase(0f, -1f)]
    public void Solstices_ReachTheFullTilt(float dayOfYear, float sign)
    {
        foreach (float obliquity in Obliquities())
        {
            Assert.That(
                Formulas.SolarDeclinationDegrees(dayOfYear, obliquity),
                Is.EqualTo(sign * obliquity).Within(Tolerance),
                $"tilt {obliquity}");
        }
    }

    /// <summary>
    /// An upright planet has no seasons at all: the sun sits on the celestial equator every day of
    /// the year. Planetsmith's slider goes to 0, so this is a reachable world and not a limit case.
    /// </summary>
    [Test]
    public void ZeroObliquity_GivesNoSeasonAnywhereInTheYear()
    {
        for (float day = 0f; day < Formulas.DaysPerYear; day += 0.5f)
        {
            Assert.That(
                Formulas.SolarDeclinationDegrees(day, 0f), Is.EqualTo(0f).Within(Tolerance), $"day {day}");
        }
    }

    /// <summary>
    /// Declination is bounded by the tilt at every day and every tilt — the same invariant
    /// FormulasSolarGeometryTests sweeps for our fixed constant, restated for a tilt we do not own.
    /// </summary>
    [Test]
    public void Declination_IsAlwaysBoundedByTheObliquity()
    {
        foreach (float obliquity in Obliquities())
        {
            for (float day = 0f; day < Formulas.DaysPerYear; day += 0.5f)
            {
                float declination = Formulas.SolarDeclinationDegrees(day, obliquity);
                Assert.That(
                    Math.Abs(declination),
                    Is.LessThanOrEqualTo(obliquity + Tolerance),
                    $"tilt {obliquity}, day {day}");
            }
        }
    }

    /// <summary>
    /// A year is still a year at any tilt. Guards against anything tilt-dependent leaking into the
    /// day term, which would show up as a sky that slowly drifts out of step with the calendar.
    /// </summary>
    [Test]
    public void Declination_IsPeriodicOverTheYearAtEveryObliquity()
    {
        foreach (float obliquity in Obliquities())
        {
            for (float day = 0f; day < Formulas.DaysPerYear; day += 1f)
            {
                Assert.That(
                    Formulas.SolarDeclinationDegrees(day + Formulas.DaysPerYear, obliquity),
                    Is.EqualTo(Formulas.SolarDeclinationDegrees(day, obliquity)).Within(Tolerance),
                    $"tilt {obliquity}, day {day}");
            }
        }
    }

    // --- SanitizeObliquityDegrees: the values another mod's slider can actually hand us ---

    [TestCase(0f, 0f)]
    [TestCase(23.4f, 23.4f)]
    [TestCase(90f, 90f)]
    public void Sanitize_PassesEveryValueInPlanetsmithsRangeThrough(float input, float expected)
    {
        Assert.That(Formulas.SanitizeObliquityDegrees(input), Is.EqualTo(expected).Within(Tolerance));
    }

    [TestCase(-1f, 0f)]
    [TestCase(-90f, 0f)]
    [TestCase(91f, 90f)]
    [TestCase(1000f, 90f)]
    public void Sanitize_ClampsOutOfRangeToTheReachableEnd(float input, float expected)
    {
        Assert.That(Formulas.SanitizeObliquityDegrees(input), Is.EqualTo(expected).Within(Tolerance));
    }

    /// <summary>
    /// NaN falls back to Earth's tilt rather than clamping to an end of the range. Clamp would send
    /// it to whichever bound the comparison happened to fail into — silently 0 in this
    /// implementation, i.e. a seasonless planet — where the fallback is a defensible sky. Infinity
    /// clamps normally, since its comparisons are meaningful.
    /// </summary>
    [Test]
    public void Sanitize_FallsBackToEarthsTiltOnNaN()
    {
        Assert.That(
            Formulas.SanitizeObliquityDegrees(float.NaN),
            Is.EqualTo(Formulas.AxialTiltDegrees).Within(Tolerance));
    }

    [TestCase(float.PositiveInfinity, 90f)]
    [TestCase(float.NegativeInfinity, 0f)]
    public void Sanitize_ClampsInfinities(float input, float expected)
    {
        Assert.That(Formulas.SanitizeObliquityDegrees(input), Is.EqualTo(expected).Within(Tolerance));
    }

    /// <summary>
    /// The sanitizer is reached THROUGH the declination overload, not only when called directly — so
    /// a garbage tilt that got past the adapter still cannot produce a NaN declination, and the sky
    /// renders on Earth's tilt rather than not at all.
    /// </summary>
    [Test]
    public void Declination_NeverReturnsNaN_ForAnyObliquityIncludingGarbage()
    {
        float[] tilts = { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -50f, 1e9f, 0f, 90f };

        foreach (float tilt in tilts)
        {
            for (float day = 0f; day < Formulas.DaysPerYear; day += 5f)
            {
                float declination = Formulas.SolarDeclinationDegrees(day, tilt);
                Assert.That(float.IsNaN(declination), Is.False, $"tilt {tilt}, day {day}");
                Assert.That(
                    Math.Abs(declination),
                    Is.LessThanOrEqualTo(Formulas.MaxObliquityDegrees + Tolerance),
                    $"tilt {tilt}, day {day}");
            }
        }
    }

    /// <summary>
    /// A NaN tilt produces exactly the sky we would have rendered without Planetsmith at all, day for
    /// day. Stronger than "not NaN": it pins that the failure mode is the pre-feature baseline rather
    /// than some third thing.
    /// </summary>
    [Test]
    public void Declination_OnGarbageTilt_MatchesOurOwnSkyExactly()
    {
        for (float day = 0f; day < Formulas.DaysPerYear; day += 1f)
        {
            Assert.That(
                Formulas.SolarDeclinationDegrees(day, float.NaN),
                Is.EqualTo(Formulas.SolarDeclinationDegrees(day)).Within(Tolerance),
                $"day {day}");
        }
    }

    // --- What a steep Planetsmith world actually does to the sky ---

    /// <summary>
    /// The point of the interop, stated as a measurement rather than a description: at 60 degrees of
    /// tilt a mid-latitude midsummer sun stands far higher than it does on Earth's, and midwinter is
    /// a polar night that our own tilt never produces there. These are the numbers a live scenario's
    /// pins should agree with.
    /// </summary>
    [TestCase(45f, 30f, 23.44f, 68.44f)]  // our tilt, midsummer at 45N: sun peaks 68.4 degrees up
    [TestCase(45f, 30f, 60f, 75f)]        // 60-degree world, same day: 75 degrees, near-overhead
    [TestCase(45f, 0f, 23.44f, 21.56f)]   // our tilt, midwinter: low but well up
    [TestCase(45f, 0f, 60f, -15f)]        // 60-degree world: the sun does not rise at all
    public void NoonElevation_ShowsWhatASteeperWorldCosts(
        float latitude, float dayOfYear, float obliquity, float expectedElevation)
    {
        float declination = Formulas.SolarDeclinationDegrees(dayOfYear, obliquity);

        Assert.That(
            Formulas.SolarElevationDegrees(latitude, declination, 0.5f),
            Is.EqualTo(expectedElevation).Within(0.01f));
    }
}
