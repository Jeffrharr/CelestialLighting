using RimWorld;
using Verse;

namespace CelestialLighting;

// Defs this mod ships itself. Currently exactly one: the private map-mesh dirty flag that lets
// MapComponent_SunShadowAxis rebuild the sun-shadow mesh — and nothing else — when the shadow axis
// drifts far enough for the baked per-section length variation to go stale (DESIGN.md section 3).
// See 1.6/Defs/MapMeshFlagDefs/MapMeshFlags_CelestialLighting.xml for why it is a new flag rather
// than a reuse of MapMeshFlagDefOf.Buildings.
[DefOf]
public static class CelestialLightingDefOf
{
    public static MapMeshFlagDef CL_SunShadowAxis;

    static CelestialLightingDefOf()
    {
        DefOfHelper.EnsureInitializedInCtor(typeof(CelestialLightingDefOf));
    }
}
