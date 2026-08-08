using Verse;

namespace CelestialLighting;

// Single dispatcher for §7b's third CapOcclusion argument -- picks between the two mutually exclusive
// sources for "how much sky is reaching this interior cell via a nearby opening": AmbientLightCompat
// (§7a interop, gameplay-authoritative when that mod is present) and NativeSkyFalloffGrid (§7c, this
// mod's own whole-map BFS). Patch_IndoorSkyOcclusion's two call sites go through this rather than
// either source directly, so there is exactly one place that decides which (if either) answers a
// given cell.
//
// Deferral, not composition, when Ambient Light is active: the two sources use different maxDepth
// values and formulas by construction (players tune each independently), so blending them (e.g. Max())
// would put a visible seam wherever the smaller maxDepth runs out -- a discontinuity neither gradient
// has on its own. Ambient Light's own value is also gameplay-authoritative (its mouseover readout
// already reports it), so it wins outright rather than being treated as one input among several. The
// native BFS is never even run in that case -- running it for nothing on every map with Ambient Light
// installed would be pure waste (see NativeSkyFalloffGrid's header).
public static class SkyFalloffSource
{
    public static float FractionAt(Map map, IntVec3 cell)
    {
        if (AmbientLightCompat.Active)
            return AmbientLightCompat.SkyFractionAt(map, cell);

        if (!CelestialLightingFeatures.NativeSkyFalloff)
            return 0f;

        return NativeSkyFalloffGrid.FractionAt(
            map, cell, map.skyManager.CurSkyGlow,
            NativeSkyFalloffMath.DefaultMaxDepth, NativeSkyFalloffMath.DefaultPassThroughPercent);
    }
}
