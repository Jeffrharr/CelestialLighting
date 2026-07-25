namespace CelestialLighting.Tests;

/// <summary>
/// Offline coverage for EclipseStaging.cs — the pure arithmetic that phase-slides the modeled moon so
/// a genuine new-moon-at-node eclipse can be filmed on demand (DESIGN.md §10a demo path). These pin
/// the key invariant the live harness relies on: after applying the computed shifts, the *real*
/// MoonMath geometry at the target tick actually reports a central transit, so the live trigger fires
/// a real eclipse rather than the staging silently producing no eclipse. Uses the real RimWorld
/// calendar constants (60000 ticks/day, 60-day year) so the numbers match the game.
/// </summary>
[TestFixture]
public class EclipseStagingTests
{
    private const long TicksPerDay = 60000L;
    private const float DaysPerYear = 60f; // == Formulas.DaysPerYear
    private static readonly long SynodicPeriodTicks = (long)(MoonMath.DefaultSynodicMonthDays * TicksPerDay);
    private static readonly long NodalPeriodTicks = (long)(MoonMath.DefaultNodalPeriodDays * TicksPerDay);

    // A spread of arbitrary "current game tick" anchors, including ones deliberately mid-cycle and
    // mid-year, to prove the alignment works from any starting phase rather than only a lucky epoch.
    [TestCase(0L)]
    [TestCase(123_456L)]
    [TestCase(37_000_000L)]
    [TestCase(1_000_000_003L)]
    [TestCase(45_678_912L)]
    public void ComputeAlignmentShifts_ProducesCentralTransit_AtTargetTick(long anchorTicksAbs)
    {
        long preRoll = EclipseStaging.DefaultPreRollTicks;
        EclipseStaging.AlignmentShifts shifts = EclipseStaging.ComputeAlignmentShifts(
            anchorTicksAbs, SynodicPeriodTicks, NodalPeriodTicks, TicksPerDay, DaysPerYear, preRoll);

        long eclipseTick = anchorTicksAbs + preRoll;

        // Reproduce exactly what the live trigger samples at the arranged eclipse tick.
        float cyclePosition = MoonMath.SynodicCyclePosition(eclipseTick + shifts.SynodicShiftTicks, SynodicPeriodTicks);
        float nodalPosition = MoonMath.NodalCyclePosition(eclipseTick + shifts.NodalShiftTicks, NodalPeriodTicks);
        float dayOfYear = (float)((double)eclipseTick / TicksPerDay % DaysPerYear);

        float separation = MoonMath.SunMoonSeparationDegrees(dayOfYear, cyclePosition, nodalPosition);

        // New moon at the target tick.
        Assert.That(cyclePosition, Is.EqualTo(0f).Within(0.001f), "target tick should be a new moon");
        // The discs overlap (a transit is happening) …
        Assert.That(EclipseMath.IsGeometricTransit(
                separation, EclipseMath.SunAngularRadiusDegrees, EclipseMath.MoonAngularRadiusDegrees),
            Is.True, $"staged separation {separation:F4}° should be inside the disc sum");
        // … and it is essentially dead-central, so it films as a full eclipse, not a sliver.
        double magnitude = EclipseMath.TransitMagnitude(
            separation, EclipseMath.SunAngularRadiusDegrees, EclipseMath.MoonAngularRadiusDegrees);
        Assert.That(magnitude, Is.GreaterThan(0.9), $"staged eclipse should be near-central (magnitude {magnitude:F3})");
    }

    [Test]
    public void ComputeAlignmentShifts_IsInert_ForNonPositivePeriods()
    {
        // Degenerate calendar inputs must not divide by zero; they return no shift (staging off).
        EclipseStaging.AlignmentShifts shifts = EclipseStaging.ComputeAlignmentShifts(
            1000L, synodicPeriodTicks: 0L, nodalPeriodTicks: NodalPeriodTicks,
            ticksPerDay: TicksPerDay, daysPerYear: DaysPerYear, preRollTicks: 2500L);
        Assert.That(shifts.SynodicShiftTicks, Is.EqualTo(0L));
        Assert.That(shifts.NodalShiftTicks, Is.EqualTo(0L));
    }
}
