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

    public static Sky SkyForMap(Map map)
    {
        GameComponent_MoonPhase moon = GameComponent_MoonPhase.Current;
        if (moon == null)
            return NoMoon;

        float cyclePosition = moon.CyclePosition;

        // Same live-state pulls SolarPosition.InputsForMap makes, so the moon shares the sun's tile
        // latitude, day-of-year, and day-percent exactly.
        Vector2 longLat = Find.WorldGrid.LongLatOf(map.Tile);
        int dayOfYear = GenDate.DayOfYear(Find.TickManager.TicksAbs, longLat.x);
        float dayPercent = GenLocalDate.DayPercent(map);

        float declination = MoonMath.MoonDeclinationDegrees(dayOfYear, cyclePosition);
        float moonDayPercent = MoonMath.MoonDayPercent(dayPercent, cyclePosition);

        // Reuse Formulas' own solar-position equations for the moon, feeding the moon's declination
        // and lagged day-percent — the moon is just another body on the ecliptic.
        float elevation = Formulas.SolarElevationDegrees(longLat.y, declination, moonDayPercent);
        float azimuth = Formulas.SolarAzimuthDegrees(longLat.y, declination, elevation, moonDayPercent);
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
