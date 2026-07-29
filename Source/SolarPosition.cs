using System;
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
//
// InputsForMap is memoized per (map, frame) — see GeometryMemo.cs. Keep that the only entry point
// that touches Find/GenDate/GenLocalDate: a caller that re-derives the sun outside it pays the full
// per-frame cost the memo removes AND reopens the disagreement the paragraph above describes.
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

    // Per-frame memo (issue #12; rationale in GeometryMemo.cs). This function is the single entry
    // point every sun-reading patch funnels through, which is exactly why it was being evaluated 14
    // times per map per frame on the CurSkyTarget path alone, with identical arguments — the fan-out
    // is the design working, and the memo is what makes the fan-out cheap (DESIGN.md's
    // "Per-frame geometry memo" has the full count). The value it caches is a plain readonly struct of
    // three floats, so a hit hands back the same numbers a recompute would produce, bit for bit.
    private static readonly GeometryMemo<Inputs> InputsMemo = new GeometryMemo<Inputs>();

    // Held as a static delegate so the Get call below allocates nothing per frame.
    private static readonly Func<Map, Inputs> ComputeInputs = ComputeInputsForMap;

    public static Inputs InputsForMap(Map map) =>
        InputsMemo.Get(map.uniqueID, FrameStamp.Current(), map, ComputeInputs);

    private static Inputs ComputeInputsForMap(Map map)
    {
        // Find.WorldGrid.LongLatOf is the expensive step, not the trigonometry below it: it goes
        // through PlanetLayer.GetTileCenter, a managed->native [BurstCompile] call.
        Vector2 longLat = Find.WorldGrid.LongLatOf(map.Tile);
        float dayPercent = GenLocalDate.DayPercent(map);
        int dayOfYear = GenDate.DayOfYear(Find.TickManager.TicksAbs, longLat.x);
        // Routed through AxialTiltCompat rather than straight to Formulas so that, with Realistic
        // Axial Tilt installed, every sun-derived effect in the mod picks up that planet's obliquity
        // AND its seasonal phase from one place. Without RAT this is exactly
        // Formulas.SolarDeclinationDegrees(dayOfYear), so the seam is inert for the single-mod case.
        float declination = AxialTiltCompat.SolarDeclinationDegrees(dayOfYear);

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
