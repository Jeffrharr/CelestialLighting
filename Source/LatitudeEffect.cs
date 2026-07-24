using RimWorld;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// Shared by Patch_ShadowDirection, Patch_TwilightColor, and Patch_ShadowTilt so all three
// effects agree on exactly the same latitude/season state for a given map and tick — computing
// this independently in each patch risks two patches disagreeing by a frame or a rounding step,
// which would show up as a visible seam between the shadow lean and the twilight color band.
//
// This is the impure boundary: it pulls live state off Find.WorldGrid/Find.TickManager and hands
// primitives to Formulas.ComputeLatitudeContext, which does the actual math and is fully covered
// by offline unit tests. Keep it that way — if you're tempted to add a formula here, it belongs
// in Formulas.cs instead.
public static class LatitudeEffect
{
    public static Formulas.LatitudeContext ForMap(Map map)
    {
        Vector2 longLat = Find.WorldGrid.LongLatOf(map.Tile);
        float latitude = longLat.y;
        int dayOfYear = GenDate.DayOfYear(Find.TickManager.TicksAbs, longLat.x);
        return Formulas.ComputeLatitudeContext(latitude, dayOfYear);
    }
}
