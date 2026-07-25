using System;

namespace CelestialLighting;

// Pure math only — System only, no UnityEngine/Verse — so it links into the offline test project and
// the exact staging arithmetic that runs in-game is the exact arithmetic under test (same discipline
// as Formulas.cs/MoonMath.cs/EclipseMath.cs).
//
// This exists purely to *film* the natural eclipse (DESIGN.md §10a). A real natural eclipse only
// happens once every few game years (that rarity is the whole point of the node model), which is far
// too long to wait for a demo/regression video. Rather than fake the eclipse, this computes the two
// epoch shifts that phase-slide the modeled moon so that a genuine new-moon-at-a-node alignment lands
// a short "pre-roll" after a chosen tick — after which the *real* live trigger
// (GameComponent_NaturalEclipse) detects the transit from the *real* geometry and fires a *real*
// short Eclipse that plays through the *real* darkening ramp. Only the calendar alignment is staged,
// exactly as an astronomer would fast-forward an ephemeris to the next eclipse to film it. Applied
// only in the dev-only test harness (TestMod bridge); the shipped mod never shifts the moon.
public static class EclipseStaging
{
    // How far (in ticks) the arranged new-moon-at-node alignment lands after the anchor tick, so a
    // timelapse can start just before the eclipse and capture the whole fly-in → maximum → fly-out.
    // One game-hour is a sensible default (a central natural eclipse lasts ~an hour at the default
    // synodic period), leaving a little lead-in before first contact.
    public const long DefaultPreRollTicks = 2500L; // GenDate.TicksPerHour

    // The two epoch shifts (in ticks, added to the absolute tick before deriving each cycle position)
    // that stage a central eclipse. Synodic shift phases the moon to a new moon at the target tick;
    // nodal shift regresses the node so it coincides with the sun's ecliptic longitude there, which
    // puts the moon's ecliptic latitude at zero — a dead-central transit. Both are consumed by
    // GameComponent_MoonPhase's dev-only debug-shift fields.
    public readonly struct AlignmentShifts
    {
        public readonly long SynodicShiftTicks;
        public readonly long NodalShiftTicks;

        public AlignmentShifts(long synodicShiftTicks, long nodalShiftTicks)
        {
            SynodicShiftTicks = synodicShiftTicks;
            NodalShiftTicks = nodalShiftTicks;
        }
    }

    // Compute the shifts that make a central new-moon-at-node eclipse occur at (anchorTicksAbs +
    // preRollTicks). All periods in ticks; ticksPerDay/daysPerYear describe the game calendar so the
    // sun's ecliptic longitude at the target tick can be reproduced exactly the way the live trigger
    // does. Guards against non-positive periods by returning zero shifts (no staging) rather than
    // dividing by zero.
    public static AlignmentShifts ComputeAlignmentShifts(
        long anchorTicksAbs,
        long synodicPeriodTicks,
        long nodalPeriodTicks,
        long ticksPerDay,
        float daysPerYear,
        long preRollTicks)
    {
        if (synodicPeriodTicks <= 0L || nodalPeriodTicks <= 0L || ticksPerDay <= 0L || daysPerYear <= 0f)
            return new AlignmentShifts(0L, 0L);

        long eclipseTick = anchorTicksAbs + preRollTicks;

        // Synodic: shift so (eclipseTick + shift) is exactly on a new moon (cycle position 0).
        long synodicShift = -FloorMod(eclipseTick, synodicPeriodTicks);

        // Nodal: put the ascending node at the sun's ecliptic longitude at the eclipse tick, so the
        // new moon (which sits at that same longitude) is on the node and its ecliptic latitude is 0.
        float sunLongitudeDeg = SunLongitudeDegrees(eclipseTick, ticksPerDay, daysPerYear);
        // AscendingNodeLongitudeDegrees(pos) == -pos*360, so we need pos == -sunLongitude/360 (mod 1).
        float targetNodalPosition = Frac(-sunLongitudeDeg / 360f);
        long targetNodalWrapped = (long)Math.Round(targetNodalPosition * nodalPeriodTicks);
        long nodalShift = targetNodalWrapped - FloorMod(eclipseTick, nodalPeriodTicks);

        return new AlignmentShifts(synodicShift, nodalShift);
    }

    // Continuous day-of-year → sun's ecliptic longitude in degrees, matching MoonMath's convention
    // (dayOfYear / daysPerYear * 360). Continuous (not integer) so it agrees with the live trigger's
    // sub-day resolution — a whole-day rounding would move the node up to ~6° and blow the alignment.
    private static float SunLongitudeDegrees(long ticksAbs, long ticksPerDay, float daysPerYear)
    {
        float dayOfYear = (float)((double)ticksAbs / ticksPerDay % daysPerYear);
        return dayOfYear / daysPerYear * 360f;
    }

    // Floored (always-non-negative) modulo, so pre-epoch/negative ticks still land in [0, period) —
    // the same discipline MoonMath's cycle-position helpers use.
    private static long FloorMod(long value, long modulus) => ((value % modulus) + modulus) % modulus;

    private static float Frac(float x) => x - MathF.Floor(x);
}
