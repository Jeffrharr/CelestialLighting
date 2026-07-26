using RimWorld;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// Thin per-map adapter for the moon, the moon-side twin of SolarPosition. It pulls the primitives
// MoonMath needs off live Map/Find/GameComponent state (tile lat/long, day-of-year, day-percent, and
// the game-wide cycle position) and hands them to the pure core, which does all the trigonometry.
//
// Both consumers of moon geometry go through this one adapter so they can never derive a different
// moon from each other — the same discipline SolarPosition already enforces for the sun across the
// two shadow patches. If the moon component is absent (no game loaded, or on the main menu), every
// accessor degrades to "no moon" (elevation far below the horizon, zero shadow, zero moonlight)
// rather than throwing on the render path.
public static class MoonPosition
{
    public readonly struct Sky
    {
        public readonly float ElevationDegrees;
        public readonly float AzimuthDegrees;
        public readonly float IlluminatedFraction;

        public Sky(float elevationDegrees, float azimuthDegrees, float illuminatedFraction)
        {
            ElevationDegrees = elevationDegrees;
            AzimuthDegrees = azimuthDegrees;
            IlluminatedFraction = illuminatedFraction;
        }
    }

    // A far-below-horizon, unilluminated moon used when there is no live moon component to read.
    // Every downstream helper treats this as "no moon up", so callers need no separate null path.
    private static readonly Sky NoMoon = new Sky(-90f, 0f, 0f);

    // Dev-only escape hatch, always true in a real game — exactly the SunClockAdapter.WarpEnabled
    // pattern and there for the same reason. Setting it false leaves the moon on the raw day percent
    // while the sun stays warped, which reproduces the shipped-§14 artifact this file's SkyForMap
    // comment describes, so the live harness can capture it as the BEFORE half of an A/B. Flipping
    // SunClockAdapter.WarpEnabled instead would not do: that reverts the SUN too, giving the pre-§14
    // single-clock world rather than the bug. Nothing in the shipped mod ever writes this — see
    // ProbeRegistration's moon_clock_warp bridge.
    public static bool WarpMoonClock = true;

    public static Sky SkyForMap(Map map)
    {
        GameComponent_MoonPhase moon = GameComponent_MoonPhase.Current;
        if (moon == null)
            return NoMoon;

        float cyclePosition = moon.CyclePosition;

        // The moon's day percent is NOT independent of the sun's — MoonMath.MoonDayPercent exists to
        // produce moonHourAngle == sunHourAngle - elongation, so whatever clock the sun is on, the
        // moon must lag THAT clock. So take the day percent from SolarPosition.InputsForMap rather
        // than reading GenLocalDate.DayPercent again: in §14's default locked mode the sun's percent
        // is warped onto vanilla's day, and the moon has to be warped with it.
        //
        // Reading the raw percent here instead is what §14 originally shipped, and it silently broke
        // every sun-moon relationship the model is built on. The moon lagged the wrong sun by
        // (vanillaHalfDay - physicalHalfDay) * 24 — 1.5 to 3.1 h within +/-60 degrees, 7.7 h at
        // latitude 70 in winter. Measured against the shipped build, with the pre-§14 single-clock
        // numbers in brackets as the baseline this restores:
        //   - a full moon rose 2.2-3.3 h before sunset, 4.97 h worst case  [0.11-0.37 h],
        //   - it hung in a lit sky 3.5-6.5 h a day, 13.2 h at latitude 70  [0.22-1.36 h],
        //   - a new moon sat 12-35 degrees of elevation off the sun        [0 — it IS the sun's
        //     position at elongation 0], and
        //   - the dusk shadow handoff popped. Both shadow patches switch from sun to moon at the
        //     sun's horizon crossing, where ShadowIntensityFromElevation has ramped the sun shadow to
        //     0; the moon shadow then starts at whatever the moon's elevation implies, over a ramp
        //     only 3 degrees wide. On the raw clock the moon was already 10-36 degrees up there, so
        //     the shadow snapped straight to full MoonShadowMaxStrength (0.28) pointing somewhere
        //     unrelated, every clear night around full moon.
        //
        // That last one is worth stating precisely, because the fix does not make it zero. Refraction
        // enters both sunrise equations with the same sign while the full moon's declination is the
        // sun's REFLECTED, so the two windows are not exact complements: at sunset a full moon sits
        // ~0.8 degrees up, for a handoff step of 0.155. That step is inherent to the moon model and
        // predates §14 — what this restores is the baseline, not perfection.
        //
        // Latitude comes from the same Inputs for the same reason SolarPosition centralizes it: two
        // adapters deriving their own tile latitude is how the sun and moon drift apart.
        SolarPosition.Inputs sun = SolarPosition.InputsForMap(map);
        float dayPercent = WarpMoonClock ? sun.DayPercent : GenLocalDate.DayPercent(map);

        // Day-of-year still comes straight off the tick: it selects the moon's place on the ecliptic
        // (its declination), which is an orbital fact and has nothing to do with the time-of-day warp.
        Vector2 longLat = Find.WorldGrid.LongLatOf(map.Tile);
        int dayOfYear = GenDate.DayOfYear(Find.TickManager.TicksAbs, longLat.x);

        float declination = MoonMath.MoonDeclinationDegrees(dayOfYear, cyclePosition);
        float moonDayPercent = MoonMath.MoonDayPercent(dayPercent, cyclePosition);

        // Reuse Formulas' own solar-position equations for the moon, feeding the moon's declination
        // and lagged day-percent — the moon is just another body on the ecliptic.
        float elevation = Formulas.SolarElevationDegrees(sun.Latitude, declination, moonDayPercent);
        float azimuth = Formulas.SolarAzimuthDegrees(sun.Latitude, declination, elevation, moonDayPercent);
        return new Sky(elevation, azimuth, MoonMath.IlluminatedFraction(cyclePosition));
    }

    // Moon-cast shadow for this map, or null when the moon is down / new (so no shadow should render).
    // Vector points directly away from the moon, exactly like the sun's shadow vector, with a length
    // from the same cot(elevation) curve; strength is the faint, phase-and-altitude-scaled moon alpha.
    public static (Vector2 vector, float strength)? ShadowForMap(Map map)
    {
        // Feature gate (default on): when off, report no moon shadow so both shadow patches fall
        // back to a shadowless night — the faithful pre-moon baseline. See MoonShadows in
        // CelestialLightingFeatures for why this single choke point is where the toggle lives.
        if (!CelestialLightingFeatures.MoonShadows)
            return null;

        Sky sky = SkyForMap(map);
        float strength = MoonMath.MoonShadowStrength(sky.IlluminatedFraction, sky.ElevationDegrees);
        if (strength <= 0f)
            return null;

        Formulas.ShadowVector shadow = Formulas.ShadowVectorFromSunPosition(sky.ElevationDegrees, sky.AzimuthDegrees);
        return (new Vector2(shadow.X, shadow.Y), strength);
    }

    // Normalized 0..1 moonlight contribution for the current map — the seam the night-radiance
    // subsystem (§7) consumes.
    // TODO(integration): #4/#7 night-radiance should read this and sum it with its starlight/airglow
    // floors (scaled by its own moonlight slider) rather than recomputing the moon itself.
    public static float MoonlightBrightnessForMap(Map map)
    {
        Sky sky = SkyForMap(map);
        return MoonMath.MoonlightBrightness(sky.IlluminatedFraction, sky.ElevationDegrees);
    }
}
