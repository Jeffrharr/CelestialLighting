using Verse;

namespace CelestialLighting;

// Used by Patch_TwilightColor to gate/scale the twilight-color nudge by how far from the equator
// the map sits. Patch_ShadowDirection computes its own latitude/declination/elevation state
// directly (it needs day-of-year and day-percent too, not just latitude strength), so this is no
// longer a shared multi-patch helper — just Patch_TwilightColor's impure boundary.
//
// This is the impure boundary: it pulls live state off Find.WorldGrid and hands a primitive to
// Formulas.LatitudeStrength, which does the actual math and is fully covered by offline unit
// tests. Keep it that way — if you're tempted to add a formula here, it belongs in Formulas.cs
// instead.
public static class LatitudeEffect
{
    public static float StrengthForMap(Map map)
    {
        float latitude = Find.WorldGrid.LongLatOf(map.Tile).y;
        return Formulas.LatitudeStrength(latitude);
    }
}
