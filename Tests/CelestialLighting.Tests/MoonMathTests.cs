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

    // --- MoonEquivalentSunDayOfYear ---

    [TestCase(0f, 0f)]
    [TestCase(0f, 0.25f)]
    [TestCase(0f, 0.5f)]
    [TestCase(0f, 0.75f)]
    [TestCase(15f, 0.13f)]
    [TestCase(30f, 0.5f)]
    [TestCase(42f, 0.87f)]
    [TestCase(59f, 0.31f)]
    public void MoonEquivalentSunDayOfYear_ReproducesMoonDeclination_ThroughTheSunModel(
        float dayOfYear, float cyclePosition)
    {
        // The load-bearing equivalence. MoonPosition no longer calls MoonDeclinationDegrees; it
        // evaluates the SUN's declination function at this shifted day, so the moon follows whatever
        // seasonal model the sun is on (ours, or Realistic Axial Tilt's, which is phase-shifted).
        //
        // This pins that the indirection is exactly inert when the sun model is our own — that the
        // refactor changed no shipped number for a player without RAT. If it drifts, every
        // moon-shadow scenario pin drifts with it.
        float viaSunModel = Formulas.SolarDeclinationDegrees(
            MoonMath.MoonEquivalentSunDayOfYear(dayOfYear, cyclePosition));
        float direct = MoonMath.MoonDeclinationDegrees(dayOfYear, cyclePosition);

        Assert.That(viaSunModel, Is.EqualTo(direct).Within(Tolerance));
    }

    [TestCase(0f)]
    [TestCase(15f)]
    [TestCase(42f)]
    public void MoonEquivalentSunDayOfYear_IsIdentity_AtNewMoon(float dayOfYear)
    {
        // Elongation 0 puts the moon at the sun's own ecliptic longitude, so the shift must vanish
        // outright rather than merely round to something close. This is the property that keeps sun
        // and moon provably on the same day under ANY declination model, including one whose phase
        // we don't control.
        Assert.That(
            MoonMath.MoonEquivalentSunDayOfYear(dayOfYear, cyclePosition: 0f),
            Is.EqualTo(dayOfYear).Within(Tolerance));
    }

    [TestCase(0f)]
    [TestCase(15f)]
    [TestCase(42f)]
    public void MoonEquivalentSunDayOfYear_IsHalfAYearAhead_AtFullMoon(float dayOfYear)
    {
        // Elongation 180 is half a year of ecliptic travel. Any sinusoidal declination model negates
        // across half a year, which is what makes the full moon ride opposite the sun — and it does
        // so without this test needing to know which model is in play.
        Assert.That(
            MoonMath.MoonEquivalentSunDayOfYear(dayOfYear, cyclePosition: 0.5f),
            Is.EqualTo(dayOfYear + Formulas.DaysPerYear / 2f).Within(Tolerance));
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

    // --- MoonShadowStrength (§6b: contrast against the sky, not a bare phase ramp) ---

    // Sun elevation for "as dark as the sky ever gets" — past the end of astronomical twilight, where
    // IlluminanceMath clamps to the moonless starlight floor. Named because most cases below want to
    // isolate the moon's own behaviour from the twilight term.
    private const float FullDarknessSunElevation = -30f;

    [Test]
    public void MoonShadowStrength_IsZero_ForNewMoon()
    {
        // A new moon (illuminated 0) casts no shadow even directly overhead in a fully dark sky.
        Assert.That(
            MoonMath.MoonShadowStrength(
                illuminatedFraction: 0f, moonElevationDegrees: 90f,
                sunElevationDegrees: FullDarknessSunElevation),
            Is.EqualTo(0f));
    }

    [Test]
    public void MoonShadowStrength_IsZero_WhenMoonBelowHorizon()
    {
        Assert.That(
            MoonMath.MoonShadowStrength(
                illuminatedFraction: 1f, moonElevationDegrees: -5f,
                sunElevationDegrees: FullDarknessSunElevation),
            Is.EqualTo(0f));
    }

    [Test]
    public void MoonShadowStrength_IsFaint_ForFullMoonWellUp()
    {
        // Full moon well up in a fully dark sky: it is overwhelmingly the dominant light source, so
        // the contrast ratio is ~1 and the alpha lands on the artistic ceiling. Looser than Tolerance
        // because the ratio approaches the cap asymptotically — the starlight floor never quite goes
        // away — rather than reaching it exactly.
        float strength = MoonMath.MoonShadowStrength(
            illuminatedFraction: 1f, moonElevationDegrees: 45f,
            sunElevationDegrees: FullDarknessSunElevation);

        Assert.That(strength, Is.EqualTo(MoonMath.MoonShadowMaxStrength).Within(0.005f));
        Assert.That(strength, Is.LessThan(0.5f)); // documents "faint, not daytime"
    }

    [Test]
    public void MoonShadowStrength_IsZero_InBroadDaylight()
    {
        // The headline §6b claim. A full moon directly overhead at midday casts nothing visible: the
        // sun is ~400,000x brighter, so the ratio is ~0.000003 — four orders of magnitude under the
        // eye's contrast threshold. Before §6b this case could not even be asked, because the sun
        // being up was a hard gate rather than an input.
        Assert.That(
            MoonMath.MoonShadowStrength(
                illuminatedFraction: 1f, moonElevationDegrees: 90f, sunElevationDegrees: 60f),
            Is.EqualTo(0f));
    }

    [Test]
    public void MoonShadowStrength_IsZero_AtSunset_NoPopAtTheHandover()
    {
        // The specific regression §6b fixes. The old model switched the moon shadow on the instant the
        // sun passed the refraction horizon, at full strength for a well-placed moon — a visible pop in
        // both alpha and direction. The sky there is still ~200 lux, ~750x the full moon, so the honest
        // answer is nothing at all.
        Assert.That(
            MoonMath.MoonShadowStrength(
                illuminatedFraction: 1f, moonElevationDegrees: 30f,
                sunElevationDegrees: Formulas.AtmosphericRefractionDegrees),
            Is.EqualTo(0f));
    }

    [Test]
    public void MoonShadowStrength_FadesInThroughTwilight_NotAtSunset()
    {
        // A full moon well up, walked down through the twilight band. It must be invisible at civil
        // twilight's end, clearly present by nautical's, and never weaken on the way.
        float civil = MoonMath.MoonShadowStrength(1f, 60f, -6f);
        float mid = MoonMath.MoonShadowStrength(1f, 60f, -9f);
        float nautical = MoonMath.MoonShadowStrength(1f, 60f, -12f);

        Assert.That(civil, Is.EqualTo(0f), "a full moon cannot compete with a 3.4 lux civil-twilight sky");
        Assert.That(mid, Is.GreaterThan(0f), "by mid-twilight the moon should be casting");
        Assert.That(nautical, Is.GreaterThan(mid), "and it must keep strengthening as the sky darkens");
        Assert.That(nautical, Is.GreaterThan(MoonMath.MoonShadowMaxStrength * 0.9f),
            "by the end of nautical twilight a full moon is essentially at full contrast");
    }

    [TestCase(-6f)]
    [TestCase(-9f)]
    [TestCase(-12f)]
    [TestCase(-20f)]
    public void MoonShadowStrength_NeverExceedsTheCap(float sunElevationDegrees)
    {
        Assert.That(MoonMath.MoonShadowStrength(1f, 90f, sunElevationDegrees),
            Is.LessThanOrEqualTo(MoonMath.MoonShadowMaxStrength));
    }

    [Test]
    public void MoonShadowStrength_DimmerMoonNeedsADarkerSky()
    {
        // Phase's real consequence under §6b: not a proportional dimming, but a later arrival. A
        // quarter moon is ~11x fainter than a full one, so the sky has to fall ~11x further before its
        // shadow registers. At mid-twilight the full moon is casting and the quarter one is not.
        Assert.That(MoonMath.MoonShadowStrength(1f, 60f, -8f), Is.GreaterThan(0f));
        Assert.That(MoonMath.MoonShadowStrength(0.5f, 60f, -8f), Is.EqualTo(0f));

        // Given enough darkness, though, the quarter moon does get there.
        Assert.That(MoonMath.MoonShadowStrength(0.5f, 60f, -14f), Is.GreaterThan(0f));
    }

    [Test]
    public void MoonShadowStrength_InFullDarkness_HalfMoonApproachesFullMoonContrast()
    {
        // Pins the deliberate behaviour change §6b makes, so nobody "fixes" it back later. Contrast is
        // a ratio: once a caster is far above the starlight floor, halving its output barely moves the
        // shadow's contrast. What a half moon costs is scene brightness (§7's night radiance), not
        // shadow contrast. The pre-§6b model made this exactly 0.5 — linear in illuminated fraction —
        // which was the wrong physics.
        float full = MoonMath.MoonShadowStrength(1f, 90f, FullDarknessSunElevation);
        float half = MoonMath.MoonShadowStrength(0.5f, 90f, FullDarknessSunElevation);

        Assert.That(half / full, Is.GreaterThan(0.9f),
            "a half moon in true darkness still casts a near-full-contrast shadow");
        Assert.That(half, Is.LessThan(full), "but it is still strictly weaker");
    }

    [Test]
    public void MoonShadowStrength_ThinCrescentCastsNothing_EvenInFullDarkness()
    {
        // The other end of the phase curve, and the reason the ratio model does not simply flatten
        // every phase to one shadow. A 5%-lit crescent puts out ~1.5e-5 lux — genuinely dimmer than
        // the starlight floor it competes with — so it casts nothing at all.
        Assert.That(MoonMath.MoonShadowStrength(0.05f, 90f, FullDarknessSunElevation),
            Is.EqualTo(0f));
    }

    [Test]
    public void MoonShadowStrength_RisesMonotonically_AsTheSkyDarkens()
    {
        // No non-monotonic kink anywhere across the span, which is what would show up in game as a
        // flicker or a reversal partway through the twilight fade.
        float previous = -1f;
        for (float sunElevation = 10f; sunElevation >= -25f; sunElevation -= 0.25f)
        {
            float strength = MoonMath.MoonShadowStrength(1f, 60f, sunElevation);
            Assert.That(strength, Is.GreaterThanOrEqualTo(previous),
                $"strength went backwards at sun elevation {sunElevation:0.00}");
            previous = strength;
        }
    }

    // --- MoonShadowDarkening / MoonShadowIsPerceptible (§6b's visibility gate) ---

    [Test]
    public void MoonShadowDarkening_MatchesVanillasLerp_AtPeakStrength()
    {
        // At the cap the rendered darkening must be exactly §6a's target, since MoonShadowColorValue
        // is defined by inverting the same lerp.
        Assert.That(MoonMath.MoonShadowDarkening(MoonMath.MoonShadowMaxStrength),
            Is.EqualTo(MoonMath.MoonShadowPeakDarkening).Within(1e-5f));
    }

    [Test]
    public void MoonShadowIsPerceptible_SplitsOnTheSharedVisibilityThreshold()
    {
        // The gate is §13a's PerceptibleDarkening, deliberately reused rather than duplicated. Step
        // either side of the alpha that renders exactly at it.
        float alphaAtThreshold = WeatherDimmingMath.PerceptibleDarkening
            / (1f - MoonMath.MoonShadowColorValue(
                MoonMath.MoonShadowPeakDarkening, MoonMath.MoonShadowMaxStrength));

        Assert.That(MoonMath.MoonShadowIsPerceptible(alphaAtThreshold * 1.01f), Is.True);
        Assert.That(MoonMath.MoonShadowIsPerceptible(alphaAtThreshold * 0.99f), Is.False);
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

    // --- MoonShadowColorValue (§6a: making moon shadows visible at all) ---

    [Test]
    public void MoonShadowColorValue_DeliversExactlyThePeakDarkening_ThroughVanillasLerp()
    {
        // The whole point: feed this value to SkyTarget.colors.shadow and vanilla's
        // Color.Lerp(white, shadow, strength) must land on peakDarkening at max strength.
        float value = MoonMath.MoonShadowColorValue(
            MoonMath.MoonShadowPeakDarkening, MoonMath.MoonShadowMaxStrength);
        float rendered = 1f - MoonMath.MoonShadowMaxStrength * (1f - value);
        Assert.That(1f - rendered, Is.EqualTo(MoonMath.MoonShadowPeakDarkening).Within(1e-5f));
    }

    [Test]
    public void MoonShadowColorValue_ScalesDownProportionallyForAWeakerAlpha()
    {
        // Half the alpha must render at half the contrast — for free, because the strength term stays
        // vanilla's and this colour is a fixed target it lerps toward.
        //
        // Stated against an explicit half-of-cap alpha rather than against a half-lit moon, which is
        // what it used to do. Under §6b a half-lit moon in darkness no longer produces half the alpha
        // (it produces ~0.98 of it — see MoonShadowStrength_InFullDarkness_HalfMoonApproachesFull-
        // MoonContrast for why that is the correct physics), so deriving the alpha from a phase would
        // now be testing the phase curve here instead of the colour inversion this test is about.
        float value = MoonMath.MoonShadowColorValue(
            MoonMath.MoonShadowPeakDarkening, MoonMath.MoonShadowMaxStrength);
        float halfStrength = MoonMath.MoonShadowMaxStrength * 0.5f;
        float rendered = 1f - halfStrength * (1f - value);
        Assert.That(1f - rendered, Is.EqualTo(MoonMath.MoonShadowPeakDarkening / 2f).Within(1e-5f));
    }

    [Test]
    public void MoonShadowColorValue_IsFarDarkerThanVanillasNightShadow()
    {
        // Regression guard for the actual bug. Vanilla's night colors.shadow is 0.85 (Clear) / 0.92
        // (everything else), which caps the rendered darkening at ~4% and ~2% at our alpha — invisible.
        // Whatever the tuning, this value must be well below those.
        float value = MoonMath.MoonShadowColorValue(
            MoonMath.MoonShadowPeakDarkening, MoonMath.MoonShadowMaxStrength);
        Assert.That(value, Is.LessThan(0.85f));

        float vanillaClearNight = 1f - MoonMath.MoonShadowMaxStrength * (1f - 0.85f);
        float ours = 1f - MoonMath.MoonShadowMaxStrength * (1f - value);
        Assert.That(1f - ours, Is.GreaterThan((1f - vanillaClearNight) * 4f),
            "the fix should be several times more visible than vanilla's near-white night shadow");
    }

    [Test]
    public void MoonShadowColorValue_ZeroMaxStrength_LeavesTheGroundAlone()
    {
        // Degenerate config: no alpha can produce any darkening, so asking for a darker colour is
        // meaningless — return white rather than dividing by zero.
        Assert.That(MoonMath.MoonShadowColorValue(0.25f, 0f), Is.EqualTo(1f).Within(1e-5f));
    }

    [Test]
    public void MoonShadowColorValue_ClampsAnOverAmbitiousDarkening()
    {
        // Asking for more darkening than the alpha can carry saturates at black instead of going
        // negative (which would render as an out-of-range colour).
        Assert.That(MoonMath.MoonShadowColorValue(1f, 0.28f), Is.EqualTo(0f).Within(1e-5f));
    }
}
