using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// §14 adapter: measures how long VANILLA thinks the day is, so SunClockMath can warp our physical sun
// onto that clock (DESIGN.md §14; the pure core carries the full rationale).
//
// It samples GenCelestial's own public glow function rather than re-implementing its curve. That was a
// deliberate call: vanilla's sun is a pile of tuned constants (a 19-degree sun-lift scaled by one
// SimpleCurve, a further 17 degrees fading out by latitude 60, a seasonal term damped to 0.2 below
// latitude 70), and a private copy of all that would silently rot the first time Ludeon retunes any of
// it — producing a day-length "match" that quietly no longer matches. Asking the live function costs
// ~20 evaluations once per in-game day and can never drift.
public static class SunClock
{
    // Bisection depth: 18 halvings of a half-day is a resolution of about 0.1 game-seconds, far below
    // one tick, so the answer is exact for every purpose here.
    private const int BisectionSteps = 18;

    private readonly struct CachedDay
    {
        public readonly int DayIndex;
        public readonly float HalfDay;

        public CachedDay(int dayIndex, float halfDay)
        {
            DayIndex = dayIndex;
            HalfDay = halfDay;
        }
    }

    // Keyed by tile: a caravan or second colony sits at a different latitude and needs its own answer.
    // Recomputed when the absolute day rolls over, so this is a handful of trig calls per map per day.
    private static readonly Dictionary<int, CachedDay> Cache = new Dictionary<int, CachedDay>();

    // Fraction of the day from sunrise to solar noon, per vanilla. 0 == vanilla polar night,
    // 0.5 == vanilla polar day. Both suns are symmetric about noon (vanilla rotates by
    // (dayPercent - 0.5) * 360 and dots; our HourAngleDegrees uses the same convention), so this one
    // number describes the whole window.
    public static float VanillaHalfDayForMap(Map map)
    {
        int tileId = map.Tile.tileId;
        int ticksAbs = Find.TickManager.TicksAbs;
        int dayIndex = ticksAbs / GenDate.TicksPerDay;

        if (Cache.TryGetValue(tileId, out CachedDay cached) && cached.DayIndex == dayIndex)
            return cached.HalfDay;

        float halfDay = MeasureVanillaHalfDay(map.Tile, ticksAbs);
        Cache[tileId] = new CachedDay(dayIndex, halfDay);
        return halfDay;
    }

    // Dropped on load so a new game (or a different planet) never inherits the previous one's answers.
    public static void Clear() => Cache.Clear();

    private static float MeasureVanillaHalfDay(PlanetTile tile, int ticksAbs)
    {
        float longitude = Find.WorldGrid.LongLatOf(tile).x;

        // Tick at local dayPercent 0 for this tile, so sampling a dayPercent means adding a fraction
        // of a day to it. Longitude matters: GenDate applies a per-tile time-zone offset.
        float nowPercent = GenDate.DayPercent(ticksAbs, longitude);
        int dayStartTick = ticksAbs - Mathf.RoundToInt(nowPercent * GenDate.TicksPerDay);

        float GlowAt(float dayPercent) =>
            GenCelestial.CelestialSunGlow(tile, dayStartTick + Mathf.RoundToInt(dayPercent * GenDate.TicksPerDay));

        // Noon is the day's maximum and midnight its minimum, so these two samples classify the
        // degenerate cases outright and save bisecting a curve with no crossing on it.
        if (GlowAt(0.5f) <= 0f)
            return 0f;
        if (GlowAt(0f) > 0f)
            return 0.5f;

        // Sunrise lies somewhere in (0, 0.5): dark at midnight, lit at noon, monotonic between.
        float dark = 0f;
        float lit = 0.5f;
        for (int i = 0; i < BisectionSteps; i++)
        {
            float mid = (dark + lit) * 0.5f;
            if (GlowAt(mid) > 0f)
                lit = mid;
            else
                dark = mid;
        }

        return 0.5f - (dark + lit) * 0.5f;
    }
}

// The seam SolarPosition calls. Split from SunClock itself so the "measure vanilla" concern and the
// "which mode are we in" concern stay separable, and so a mode switch is a plain flag read with no
// cached state to invalidate — the warp is recomputed every call from numbers that are already cached.
public static class SunClockAdapter
{
    // Returns the day percent our solar model should be evaluated at.
    //
    // Realistic mode is the identity: the physical sun runs on its own clock and Patch_SunGlow makes
    // VANILLA follow IT instead. Locked mode is the reverse and is the default, because it is the only
    // one of the two with zero gameplay impact.
    // Dev-only escape hatch, always true in a real game. The live harness flips it to capture the
    // BEFORE half of the A/B: with the warp off, locked mode degenerates to exactly the pre-§14
    // behaviour (physical sun, vanilla glow, no reconciliation), which is the artifact this subsystem
    // exists to remove. Nothing in the shipped mod ever writes it — see ProbeRegistration's
    // sun_clock_warp bridge.
    public static bool WarpEnabled = true;

    public static float EffectiveDayPercent(Map map, float latitude, float declination, float dayPercent)
    {
        if (!WarpEnabled || CelestialLightingFeatures.SunClock != SunClockMode.LockedToVanilla)
            return dayPercent;

        float vanillaHalfDay = SunClock.VanillaHalfDayForMap(map);
        float physicalHalfDay = SunClockMath.PhysicalHalfDay(latitude, declination);
        return SunClockMath.WarpDayPercent(dayPercent, vanillaHalfDay, physicalHalfDay);
    }
}
