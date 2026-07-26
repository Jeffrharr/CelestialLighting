using RimWorld;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// Thin adapter: pulls the primitives Formulas' solar-position simulator needs off live
// Map/Find state. Shared by Patch_ShadowDirection (shadow vector/length) and
// Patch_ShadowStrength (the shadow shader's actual alpha, via GenCelestial.CurShadowStrength) so
// both patches always agree on exactly the same sun elevation instead of risking two
// independently-derived values disagreeing — which is exactly what let vanilla's moonlight curve
// keep driving visible "moon shadows" at night even after Patch_ShadowDirection alone zeroed out
// GetLightSourceInfo's own intensity field (see Patch_ShadowStrength for why that wasn't enough).
public static class SolarPosition
{
    public readonly struct Inputs
    {
        public readonly float Latitude;
        public readonly float Declination;
        public readonly float DayPercent;

        public Inputs(float latitude, float declination, float dayPercent)
        {
            Latitude = latitude;
            Declination = declination;
            DayPercent = dayPercent;
        }
    }

    public static Inputs InputsForMap(Map map)
    {
        Vector2 longLat = Find.WorldGrid.LongLatOf(map.Tile);
        float dayPercent = GenLocalDate.DayPercent(map);
        int dayOfYear = GenDate.DayOfYear(Find.TickManager.TicksAbs, longLat.x);
        float declination = Formulas.SolarDeclinationDegrees(dayOfYear);

        // §14: in the default locked mode the day percent is warped so our physical sun crosses the
        // horizon exactly when vanilla's sky does. Doing it HERE rather than in ElevationForMap is
        // deliberate — Patch_ShadowDirection reads Inputs.DayPercent to derive the azimuth, so warping
        // at the shared source keeps the sun's direction and its height on the same clock. Anything
        // that took the raw day percent instead would sweep a shadow that disagreed with its own
        // length.
        //
        // It is also why MoonPosition reads its day percent back out of THIS struct rather than
        // calling GenLocalDate.DayPercent itself. The moon's hour angle is defined as the sun's minus
        // the elongation, so "the moon keeps its own clock" is not a coherent option — leaving it on
        // the raw percent while the sun is warped puts a full moon in a bright sky and pops the dusk
        // shadow handoff. See the comment in MoonPosition.SkyForMap for the measured damage.
        dayPercent = SunClockAdapter.EffectiveDayPercent(map, longLat.y, declination, dayPercent);

        return new Inputs(longLat.y, declination, dayPercent);
    }

    public static float ElevationForMap(Map map)
    {
        Inputs inputs = InputsForMap(map);
        return Formulas.SolarElevationDegrees(inputs.Latitude, inputs.Declination, inputs.DayPercent);
    }
}
