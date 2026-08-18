using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace CelestialLighting;

// Verse.GlowGrid keeps its map and its two live emitter sets private, and §27 needs all three: the
// map to know which colony a blocker write belongs to, and the two sets to know what is currently
// emitting light.
//
// WHY READ VANILLA'S SETS RATHER THAN MIRROR THEM. The obvious alternative is to shadow
// RegisterGlower/DeRegisterGlower with our own collection. That collection can then be wrong — a
// glower that changes colour deregisters and re-registers, a gravship swaps maps, a mod calls
// ForceRegister — and every one of those desyncs shows up as a lamp that is lit for gameplay and
// dark on screen, or worse the reverse. Reading `litGlowers` means our answer is vanilla's answer by
// construction, and the patches on those two methods shrink to setting one dirty bool.
//
// litTerrain matters as much as litGlowers here and is easy to forget: glowing terrain is a wholly
// separate registration path off TerrainDef.glowRadius, so a §27 that only knew about CompGlower
// would suppress vanilla's render of it and put nothing back — glowing moss going black the moment
// the feature is switched on.
//
// Same shape and same reasoning as SectionLayerAccess: the reflection lives in one named file so the
// next patch that needs it reuses this rather than reinventing the FieldRef.
public static class GlowGridAccess
{
    private static readonly AccessTools.FieldRef<GlowGrid, Map> MapField =
        AccessTools.FieldRefAccess<GlowGrid, Map>("map");

    private static readonly AccessTools.FieldRef<GlowGrid, HashSet<CompGlower>> LitGlowersField =
        AccessTools.FieldRefAccess<GlowGrid, HashSet<CompGlower>>("litGlowers");

    private static readonly AccessTools.FieldRef<GlowGrid, HashSet<IntVec3>> LitTerrainField =
        AccessTools.FieldRefAccess<GlowGrid, HashSet<IntVec3>>("litTerrain");

    public static Map GetMap(GlowGrid grid) => grid == null ? null : MapField(grid);

    public static HashSet<CompGlower> LitGlowers(GlowGrid grid) => grid == null ? null : LitGlowersField(grid);

    public static HashSet<IntVec3> LitTerrain(GlowGrid grid) => grid == null ? null : LitTerrainField(grid);
}
