using Verse;

namespace CelestialLighting;

// Single dispatcher for §7b's third CapOcclusion argument -- picks between the two mutually exclusive
// sources for "how much sky is reaching this interior cell": IndoorGlowPassthrough (another MOD's
// under-roof light, gameplay-authoritative wherever it answers) and NativeSkyFalloffGrid (§7c, this
// mod's own whole-map BFS). Patch_IndoorSkyOcclusion's two call sites go through this rather than
// either source directly, so there is exactly one place that decides which (if either) answers a
// given cell.
//
// Deferral, not composition, when another mod is answering: the two sources use different maxDepth
// values and formulas by construction (players tune each independently), so blending them (e.g. Max())
// would put a visible seam wherever the smaller maxDepth runs out -- a discontinuity neither gradient
// has on its own. Another mod's value is also gameplay-authoritative (Ambient Light's mouseover
// readout already reports exactly it), so it wins outright rather than being treated as one input
// among several. The native BFS is not consulted for a cell somebody else has already lit.
//
// WHY THE FIRST ARM IS NO LONGER AMBIENT-LIGHT-SPECIFIC. It used to be AmbientLightCompat, which bound
// by reflection to one named mod -- its map component, its settings object, its GetDepth -- and
// re-derived its private falloff formula byte-for-byte. IndoorGlowPassthrough asks the glow grid
// instead of the mod, so it covers Ambient Light (measured: it recovers the same 0.4583 that
// reflection did, with no reflection) AND every other mod that lights interiors. That matters for a
// case the old arm structurally could not reach: ReBuild: Doors and Corners transpiles GroundGlowAt so
// cells near its GLASS WALLS receive sky glow, and a glass wall holds roof and is not a door, so §7c's
// BFS treats it as solid and floods nothing through it. Neither arm covered glass before this.
public static class SkyFalloffSource
{
    public static float FractionAt(Map map, IntVec3 cell)
    {
        // > 0 means some mod actually put sky-sourced light in this cell. 0 covers both "no such mod"
        // and "this particular cell is beyond its reach", which is exactly when the native BFS should
        // answer instead -- so the two compose without either needing to know the other exists.
        float fromOtherMod = IndoorGlowPassthrough.SkyFractionAt(map, cell);
        if (fromOtherMod > 0f)
            return fromOtherMod;

        if (!CelestialLightingFeatures.NativeSkyFalloff)
            return 0f;

        NativeSkyFalloffSettings settings = NativeSkyFalloffSettings.Current;
        return NativeSkyFalloffGrid.FractionAt(
            map, cell, map.skyManager.CurSkyGlow,
            settings.MaxDepth, settings.PassThroughPercent);
    }
}
