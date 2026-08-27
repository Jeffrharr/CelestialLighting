using System;
using System.Collections.Generic;
using HarmonyLib;
using Unity.Collections;
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

    // The blocker bit array itself, READ-ONLY, and the reason it is worth reaching for.
    //
    // LightBlockerAdded and LightBlockerRemoved are plain `Set` calls, so writing the value a cell
    // already holds changes nothing about the grid -- which is what let §27e's door reconcile be
    // unconditional. It is not free to US, though: both methods are postfixed by
    // Patch_VectorLightBlockerAdded and its sibling, and those route into MarkGeometryDirtyAround
    // with `blockerMoved: true`, which throws away the recorded silhouette every light near that
    // cell was reusing. So a redundant write is a rescan of every window around the door, and a door
    // swing raises four of them -- against a memo whose entire purpose is that a swing raises none.
    //
    // Reading the bit turns "write, and let our own patch decide it mattered" into "write only when
    // it moved". With the feature switched OFF that collapses to no writes at all, which is the
    // repo's rule about a flag reproducing the pre-feature behaviour applied to the invalidation
    // cadence and not merely to the grid's contents.
    //
    // Null when a RimWorld update renames or reshapes the field, in which case callers fall back to
    // writing unconditionally: the optimisation is lost and the feature is not.
    private static readonly AccessTools.FieldRef<GlowGrid, NativeBitArray> LightBlockersField =
        BuildLightBlockersRef();

    private static AccessTools.FieldRef<GlowGrid, NativeBitArray> BuildLightBlockersRef()
    {
        try
        {
            return AccessTools.FieldRefAccess<GlowGrid, NativeBitArray>("lightBlockers");
        }
        catch (Exception e)
        {
            Log.Warning(
                "[CelestialLighting] GlowGrid.lightBlockers not readable (" + e.GetType().Name
                + ") — §27e will rewrite a door's blocker bit even when it has not moved, costing a "
                + "silhouette rescan per door swing.");
            return null;
        }
    }

    // Whether vanilla currently considers this cell a light blocker. False return means "could not
    // tell", which is NOT the same as "not blocked" and is why this is a Try rather than a bool.
    //
    // `map.cellIndices` rather than GlowGrid's own private `indices`: the constructor assigns one
    // from the other (GlowGrid.cs:207), so they are the same object and the public one needs no
    // reflection. The bounds check is not a formality — an unspawned or out-of-map cell indexes past
    // the end of a NativeBitArray, which in a release build reads whatever is there.
    public static bool TryGetBlocksLight(GlowGrid grid, Map map, IntVec3 cell, out bool blocked)
    {
        blocked = false;

        if (grid == null || map == null || LightBlockersField == null)
        {
            return false;
        }

        NativeBitArray bits = LightBlockersField(grid);
        if (!bits.IsCreated)
        {
            return false;
        }

        int index = map.cellIndices.CellToIndex(cell);
        if (index < 0 || index >= bits.Length)
        {
            return false;
        }

        blocked = bits.IsSet(index);
        return true;
    }

    public static Map GetMap(GlowGrid grid) => grid == null ? null : MapField(grid);

    public static HashSet<CompGlower> LitGlowers(GlowGrid grid) => grid == null ? null : LitGlowersField(grid);

    public static HashSet<IntVec3> LitTerrain(GlowGrid grid) => grid == null ? null : LitTerrainField(grid);
}
