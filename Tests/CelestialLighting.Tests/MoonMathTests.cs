namespace CelestialLighting.Tests;

/// <summary>
/// Offline edge-case coverage for MoonMath.cs — the pure moon model (subsystem 6). No RimWorld/Unity
/// assembly required, since MoonMath depends only on Formulas.cs and System. These verify the moon's
/// phase, illumination, and on-the-ecliptic position math directly, including the boundary cases
/// (new/full moon, moon below horizon, cycle wraparound) the live shadow/moonlight adapters rely on.
/// </summary>
[TestFixture]
public class MoonMathTests
{
    private const float Tolerance = 0.0001f;

    // --- SynodicCyclePosition ---

    [TestCase(0L, 100L, 0f)] // start of cycle: new moon
    [TestCase(50L, 100L, 0.5f)] // halfway: full moon
    [TestCase(100L, 100L, 0f)] // exactly one period later wraps back to new
    [TestCase(250L, 100L, 0.5f)] // multiple periods later
    public void SynodicCyclePosition_MatchesExpected(long ticks, long period, float expected)
    {
        Assert.That(MoonMath.SynodicCyclePosition(ticks, period), Is.EqualTo(expected).Within(Tolerance));
    }

    [Test]
    public void SynodicCyclePosition_UsesPositiveModulo_ForNegativeTicks()
    {
        // A negative absolute tick (possible for pre-epoch instants) must still land in [0, 1),
        // not go negative — a floored modulo, not C#'s truncating %.
        Assert.That(MoonMath.SynodicCyclePosition(-25L, 100L), Is.EqualTo(0.75f).Within(Tolerance));
    }

    [Test]
    public void SynodicCyclePosition_IsZero_ForNonPositivePeriod()
    {
        // Guard against divide-by-zero if a bad (zero/negative) configured period ever reaches here.
        Assert.That(MoonMath.SynodicCyclePosition(1234L, 0L), Is.EqualTo(0f));
        Assert.That(MoonMath.SynodicCyclePosition(1234L, -100L), Is.EqualTo(0f));
    }

    // --- ElongationDegrees ---

    [TestCase(0f, 0f)]
    [TestCase(0.5f, 180f)]
    [TestCase(0.25f, 90f)]
    public void ElongationDegrees_MatchesExpected(float cyclePosition, float expected)
    {
        Assert.That(MoonMath.ElongationDegrees(cyclePosition), Is.EqualTo(expected).Within(Tolerance));
    }

    // --- IlluminatedFraction ---

    [TestCase(0f, 0f)] // new moon: dark
    [TestCase(0.5f, 1f)] // full moon: fully lit
    [TestCase(0.25f, 0.5f)] // first quarter: half lit
    [TestCase(0.75f, 0.5f)] // last quarter: half lit
    public void IlluminatedFraction_MatchesExpected(float cyclePosition, float expected)
    {
        Assert.That(MoonMath.IlluminatedFraction(cyclePosition), Is.EqualTo(expected).Within(Tolerance));
    }

    // --- IsWaxing ---

    [TestCase(0.1f, true)]
    [TestCase(0.49f, true)]
    [TestCase(0.5f, false)] // full: not waxing (Full is its own labeled phase)
    [TestCase(0.75f, false)]
    public void IsWaxing_MatchesExpected(float cyclePosition, bool expected)
    {
        Assert.That(MoonMath.IsWaxing(cyclePosition), Is.EqualTo(expected));
    }

    // --- PhaseFor / PhaseIndex ---

    [TestCase(0f, MoonMath.MoonPhase.New)]
    [TestCase(0.125f, MoonMath.MoonPhase.WaxingCrescent)]
    [TestCase(0.25f, MoonMath.MoonPhase.FirstQuarter)]
    [TestCase(0.375f, MoonMath.MoonPhase.WaxingGibbous)]
    [TestCase(0.5f, MoonMath.MoonPhase.Full)]
    [TestCase(0.625f, MoonMath.MoonPhase.WaningGibbous)]
    [TestCase(0.75f, MoonMath.MoonPhase.LastQuarter)]
    [TestCase(0.875f, MoonMath.MoonPhase.WaningCrescent)]
    public void PhaseFor_CentersEachPhaseOnItsCyclePoint(float cyclePosition, MoonMath.MoonPhase expected)
    {
        Assert.That(MoonMath.PhaseFor(cyclePosition), Is.EqualTo(expected));
    }

    [Test]
    public void PhaseFor_WrapsBackToNew_JustBeforeEndOfCycle()
    {
        // 0.9375 is the boundary between Waning Crescent and New; floor(x*8 + 0.5) == 8 must fold
        // back to New (0), not overflow the enum.
        Assert.That(MoonMath.PhaseFor(0.9375f), Is.EqualTo(MoonMath.MoonPhase.New));
        Assert.That(MoonMath.PhaseFor(0.99f), Is.EqualTo(MoonMath.MoonPhase.New));
    }

    // --- MoonDeclinationDegrees ---

    [TestCase(0f)]
    [TestCase(15f)]
    [TestCase(30f)]
    [TestCase(42f)]
    public void MoonDeclinationDegrees_EqualsSunDeclination_AtNewMoon(float dayOfYear)
    {
        // At new moon the moon sits at the sun's own ecliptic longitude, so its declination is the
        // sun's declination exactly — the whole model reduces to the (already-tested) sun model.
        float moon = MoonMath.MoonDeclinationDegrees(dayOfYear, cyclePosition: 0f);
        float sun = Formulas.SolarDeclinationDegrees(dayOfYear);
        Assert.That(moon, Is.EqualTo(sun).Within(Tolerance));
    }

    [TestCase(0f)]
    [TestCase(15f)]
    [TestCase(30f)]
    [TestCase(42f)]
    public void MoonDeclinationDegrees_IsNegatedSunDeclination_AtFullMoon(float dayOfYear)
    {
        // At full moon the moon is 180 degrees around the ecliptic from the sun, so its declination
        // is the sun's reflected across the equator — this is why a winter full moon rides high.
        float moon = MoonMath.MoonDeclinationDegrees(dayOfYear, cyclePosition: 0.5f);
        float sun = Formulas.SolarDeclinationDegrees(dayOfYear);
        Assert.That(moon, Is.EqualTo(-sun).Within(Tolerance));
    }

    // --- MoonDayPercent ---

    [TestCase(0.5f, 0f, 0.5f)] // new moon: moon tracks the sun (meridian at noon)
    [TestCase(0f, 0.5f, 0.5f)] // full moon at midnight: moon at the meridian (highest at night)
    [TestCase(0.5f, 0.5f, 0f)] // full moon at noon: moon at its lowest (opposite the noon sun)
    public void MoonDayPercent_LagsSunByElongation(float dayPercent, float cyclePosition, float expected)
    {
        Assert.That(MoonMath.MoonDayPercent(dayPercent, cyclePosition), Is.EqualTo(expected).Within(Tolerance));
    }

    [Test]
    public void MoonDayPercent_WrapsIntoUnitInterval()
    {
        // dayPercent - cyclePosition can go negative; the result must fold into [0, 1).
        float result = MoonMath.MoonDayPercent(dayPercent: 0.1f, cyclePosition: 0.4f);
        Assert.That(result, Is.EqualTo(0.7f).Within(Tolerance));
        Assert.That(result, Is.GreaterThanOrEqualTo(0f));
        Assert.That(result, Is.LessThan(1f));
    }

    [Test]
    public void MoonElevation_EqualsSunElevation_AtNewMoon()
    {
        // End-to-end consistency: because declination and day-percent both collapse to the sun's at
        // new moon, feeding them back through Formulas yields the sun's own elevation — the new moon
        // is up during the day, exactly with the sun.
        const float latitude = 45f;
        const float dayOfYear = 15f;
        const float dayPercent = 0.5f;

        float moonDecl = MoonMath.MoonDeclinationDegrees(dayOfYear, cyclePosition: 0f);
        float moonDayPercent = MoonMath.MoonDayPercent(dayPercent, cyclePosition: 0f);
        float moonElevation = Formulas.SolarElevationDegrees(latitude, moonDecl, moonDayPercent);

        float sunDecl = Formulas.SolarDeclinationDegrees(dayOfYear);
        float sunElevation = Formulas.SolarElevationDegrees(latitude, sunDecl, dayPercent);

        Assert.That(moonElevation, Is.EqualTo(sunElevation).Within(Tolerance));
    }

    // --- MoonlightBrightness ---

    [Test]
    public void MoonlightBrightness_IsZero_WhenMoonBelowHorizon()
    {
        Assert.That(MoonMath.MoonlightBrightness(illuminatedFraction: 1f, moonElevationDegrees: -10f),
            Is.EqualTo(0f));
    }

    [Test]
    public void MoonlightBrightness_IsZero_AtHorizon()
    {
        // Exactly at the refraction-adjusted horizon the moon contributes nothing yet.
        Assert.That(MoonMath.MoonlightBrightness(illuminatedFraction: 1f, moonElevationDegrees: MoonMath.MoonHorizonElevationDegrees),
            Is.EqualTo(0f));
    }

    [Test]
    public void MoonlightBrightness_IsOne_ForFullMoonAtZenith()
    {
        Assert.That(MoonMath.MoonlightBrightness(illuminatedFraction: 1f, moonElevationDegrees: 90f),
            Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void MoonlightBrightness_ScalesWithIlluminatedFraction()
    {
        // Half-lit moon at zenith is half as bright as a full moon at zenith.
        Assert.That(MoonMath.MoonlightBrightness(illuminatedFraction: 0.5f, moonElevationDegrees: 90f),
            Is.EqualTo(0.5f).Within(Tolerance));
    }

    // --- MoonShadowStrength ---

    [Test]
    public void MoonShadowStrength_IsZero_ForNewMoon()
    {
        // A new moon (illuminated 0) casts no shadow even directly overhead.
        Assert.That(MoonMath.MoonShadowStrength(illuminatedFraction: 0f, moonElevationDegrees: 90f),
            Is.EqualTo(0f));
    }

    [Test]
    public void MoonShadowStrength_IsZero_WhenMoonBelowHorizon()
    {
        Assert.That(MoonMath.MoonShadowStrength(illuminatedFraction: 1f, moonElevationDegrees: -5f),
            Is.EqualTo(0f));
    }

    [Test]
    public void MoonShadowStrength_IsFaint_ForFullMoonWellUp()
    {
        // Full moon high enough that the elevation ramp is saturated (1): strength is exactly the
        // faint per-illumination cap, never a full daytime shadow.
        float strength = MoonMath.MoonShadowStrength(illuminatedFraction: 1f, moonElevationDegrees: 45f);
        Assert.That(strength, Is.EqualTo(MoonMath.MoonShadowMaxStrength).Within(Tolerance));
        Assert.That(strength, Is.LessThan(0.5f)); // documents "faint, not daytime"
    }

    [Test]
    public void MoonShadowStrength_ScalesWithIlluminatedFraction()
    {
        float full = MoonMath.MoonShadowStrength(illuminatedFraction: 1f, moonElevationDegrees: 45f);
        float half = MoonMath.MoonShadowStrength(illuminatedFraction: 0.5f, moonElevationDegrees: 45f);
        Assert.That(half, Is.EqualTo(full * 0.5f).Within(Tolerance));
    }

    // --- Lunar nodes / ecliptic latitude / separation (natural-eclipse geometry, §10a) ---

    [TestCase(0f)]
    [TestCase(0.5f)]
    [TestCase(1f)]
    public void MoonEclipticLatitude_IsZero_AtAscendingNode(float nodalPosition)
    {
        // At the node the moon crosses the ecliptic, so its latitude is exactly zero regardless of the
        // node's current longitude — pick the day/phase that puts the moon *on* the node for this
        // nodal position: moon longitude == node longitude.
        float nodeLon = MoonMath.AscendingNodeLongitudeDegrees(nodalPosition);
        // Solve for a dayOfYear (at new moon, cyclePosition 0) whose sun/moon longitude equals nodeLon.
        float dayOfYear = ((nodeLon % 360f + 360f) % 360f) / 360f * Formulas.DaysPerYear;
        float latitude = MoonMath.MoonEclipticLatitudeDegrees(dayOfYear, cyclePosition: 0f, nodalPosition);
        Assert.That(latitude, Is.EqualTo(0f).Within(0.01f));
    }

    [Test]
    public void MoonEclipticLatitude_ReachesInclination_QuarterOrbitFromNode()
    {
        // A quarter turn (90°) of ecliptic longitude past the node, the moon is at its maximum swing
        // off the ecliptic — the full orbital inclination. Node at longitude 0 (nodalPosition 0),
        // moon at longitude 90 (dayOfYear a quarter of the year, new moon).
        float latitude = MoonMath.MoonEclipticLatitudeDegrees(
            dayOfYear: Formulas.DaysPerYear * 0.25f, cyclePosition: 0f, nodalPosition: 0f);
        Assert.That(latitude, Is.EqualTo(MoonMath.LunarInclinationDegrees).Within(0.001f));
    }

    [Test]
    public void SunMoonSeparation_IsTiny_AtNewMoonOnNode()
    {
        // New moon (elongation 0) sitting on the node (latitude 0): the discs are essentially on top
        // of each other — this is the geometry that produces an eclipse.
        float sep = MoonMath.SunMoonSeparationDegrees(dayOfYear: 0f, cyclePosition: 0f, nodalPosition: 0f);
        Assert.That(sep, Is.EqualTo(0f).Within(0.01f));
        Assert.That(EclipseMath.IsGeometricTransit(
            sep, EclipseMath.SunAngularRadiusDegrees, EclipseMath.MoonAngularRadiusDegrees), Is.True);
    }

    [Test]
    public void SunMoonSeparation_IsInclination_AtNewMoonFarFromNode()
    {
        // New moon a quarter-orbit from the node: longitude gap ~0 but the moon rides a full
        // inclination above the ecliptic, so the discs miss by ~5° — no eclipse, the common case that
        // keeps eclipses from firing every new moon.
        float sep = MoonMath.SunMoonSeparationDegrees(
            dayOfYear: Formulas.DaysPerYear * 0.25f, cyclePosition: 0f, nodalPosition: 0f);
        Assert.That(sep, Is.EqualTo(MoonMath.LunarInclinationDegrees).Within(0.01f));
        Assert.That(EclipseMath.IsGeometricTransit(
            sep, EclipseMath.SunAngularRadiusDegrees, EclipseMath.MoonAngularRadiusDegrees), Is.False);
    }

    [Test]
    public void SunMoonSeparation_IsWide_AtFullMoon()
    {
        // Full moon (elongation 180): the moon is on the far side of the sky, ~180° away — never an
        // eclipse, whatever the node.
        float sep = MoonMath.SunMoonSeparationDegrees(dayOfYear: 0f, cyclePosition: 0.5f, nodalPosition: 0f);
        Assert.That(sep, Is.GreaterThan(170f));
    }

    [Test]
    public void EclipseCadence_IsRareButRecurring_OverManyYears()
    {
        // The whole point of the node model: eclipses must be rare (not every new moon) but still
        // recur every few game years. This simulates the exact geometry the live trigger samples —
        // MoonMath.SunMoonSeparationDegrees vs the summed disc radii — across many years at fine time
        // resolution, counts distinct eclipse events (contiguous below-threshold spans), and pins the
        // rate. If DefaultNodalPeriodDays, LunarInclinationDegrees, or the disc radii change, this is
        // the guardrail that catches "eclipses now fire monthly" or "never fire" regressions.
        const int years = 300;
        const float stepDays = 0.02f; // ~30 min; fine enough not to skip a ~1.5-day eclipse window
        float synodicDays = MoonMath.DefaultSynodicMonthDays;
        float nodalDays = MoonMath.DefaultNodalPeriodDays;

        int events = 0;
        bool wasInEclipse = false;
        for (float day = 0f; day < years * Formulas.DaysPerYear; day += stepDays)
        {
            float cyclePosition = Frac(day / synodicDays);
            float nodalPosition = Frac(day / nodalDays);
            float dayOfYear = day % Formulas.DaysPerYear;

            float sep = MoonMath.SunMoonSeparationDegrees(dayOfYear, cyclePosition, nodalPosition);
            bool inEclipse = EclipseMath.IsGeometricTransit(
                sep, EclipseMath.SunAngularRadiusDegrees, EclipseMath.MoonAngularRadiusDegrees);

            events += (inEclipse && !wasInEclipse) ? 1 : 0;
            wasInEclipse = inEclipse;
        }

        float perYear = events / (float)years;
        // Target: "rare, but not too rare — every few game years." One every ~2–5 years => 0.2–0.5/yr.
        Assert.That(perYear, Is.GreaterThan(0.18f),
            $"eclipses too rare: {events} in {years} years ({perYear:F3}/yr) — expected ~1 per few years");
        Assert.That(perYear, Is.LessThan(0.55f),
            $"eclipses too frequent: {events} in {years} years ({perYear:F3}/yr) — should not be near-monthly");
    }

    private static float Frac(float x) => x - MathF.Floor(x);
}
