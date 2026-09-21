using HarmonyLib;
using Verse;

namespace CelestialLighting;

// Verse.TerrainGrid keeps its map private, and the void veto's invalidation hook needs it: a terrain
// change arrives as a cell and a grid, and the field it has to dirty is indexed by map.
//
// Same shape and same reasoning as GlowGridAccess and SectionLayerAccess — the reflection lives in
// one named file so the next patch that needs it reuses this rather than reinventing the FieldRef.
public static class TerrainGridAccess
{
    private static readonly AccessTools.FieldRef<TerrainGrid, Map> MapField =
        AccessTools.FieldRefAccess<TerrainGrid, Map>("map");

    public static Map GetMap(TerrainGrid grid) => grid == null ? null : MapField(grid);
}
