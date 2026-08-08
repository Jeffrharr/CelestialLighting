using System;
using System.Collections.Generic;
using Verse;

namespace CelestialLighting;

// Live-state half of §7c (DESIGN.md) -- the adapter that walks Map/RoofGrid/EdificeGrid to answer
// NativeSkyFalloffMath.FractionAt's `depth` argument. See NativeSkyFalloffMath's header for the pure
// formula, and AmbientLightCompat.cs for why this exists as a second, mutually exclusive source rather
// than a merge with that one -- SkyFalloffSource is the dispatcher that picks between the two; this
// file has no opinion on CelestialLightingFeatures or AmbientLightCompat.Active and will compute
// whenever asked, so a probe can read a raw BFS depth independent of whichever flag is currently set.
//
// Whole-map multi-source BFS, run once per map and cached until something changes -- not a
// SkyOcclusionWindow-shaped per-section cache, because "distance to the nearest opening" is not local:
// a cell fifteen tiles into a mine has no correct answer without seeing fifteen tiles in every
// direction, so a section-sized skirt would need to be maxDepth wide and re-run on every section
// regenerate (which fires on every lamp toggle -- see Patch_IndoorSkyOcclusion's own header). A
// multi-source BFS visits each reachable cell once regardless of maxDepth, so "once per map, only when
// connectivity changes" is the cheap shape, mirroring AmbientLightFalloff.MapComp_AmbientLight's own
// RebuildDistance (decompiled to confirm, this session).
//
// Not a MapComponent, per this repo's own convention (parent CLAUDE.md): a single-slot
// WeakReference<Map> cache, the same shape AmbientLightCompat.CachedMap already uses, so deleting this
// type later never leaves a scribed node behind on every map.
public static class NativeSkyFalloffGrid
{
    // 8-directional, orthogonal-first so the corner-cut check below (index >= 4) can assume the last
    // four entries are the diagonals -- same ordering AmbientLightFalloff's own Neigh8 uses.
    private static readonly IntVec3[] Neigh8 =
    {
        new IntVec3(1, 0, 0), new IntVec3(-1, 0, 0), new IntVec3(0, 0, 1), new IntVec3(0, 0, -1),
        new IntVec3(1, 0, 1), new IntVec3(1, 0, -1), new IntVec3(-1, 0, 1), new IntVec3(-1, 0, -1),
    };

    // Fully qualified: Verse.WeakReference<T> and System.WeakReference<T> collide under `using System;`
    // + `using Verse;`, same ambiguity AmbientLightCompat.cs's own CachedMap field already works around.
    private static readonly System.WeakReference<Map> CachedMap = new System.WeakReference<Map>(null);
    private static int[] depths;
    private static bool dirty = true;
    private static int cachedMaxDepth = -1;

    // Marks the cached BFS stale for `map` -- called from Patch_SkyFalloffDirty's postfixes. Cheap by
    // design: it only flips a bool, so a roof/wall/door change costs nothing until the next DepthAt
    // call for that map actually needs the answer, unlike a MapComponentTick poll that pays every tick
    // whether or not anyone reads the result.
    public static void MarkDirty(Map map)
    {
        if (CachedMap.TryGetTarget(out Map cached) && ReferenceEquals(cached, map))
            dirty = true;
    }

    // BFS distance from the nearest non-interior cell, capped at maxDepth. 0 means "not reached" --
    // either the cell is not interior at all (nothing to compute; ordinary vanilla sky cover already
    // applies) or it sits further than maxDepth from any opening -- same convention
    // NativeSkyFalloffMath.FractionAt already expects.
    public static int DepthAt(Map map, IntVec3 cell, int maxDepth)
    {
        if (map == null || !cell.InBounds(map))
            return 0;

        EnsureCurrent(map, maxDepth);
        return depths[map.cellIndices.CellToIndex(cell)];
    }

    public static float FractionAt(Map map, IntVec3 cell, float curSkyGlow, int maxDepth, float passThroughPercent) =>
        NativeSkyFalloffMath.FractionAt(DepthAt(map, cell, maxDepth), curSkyGlow, maxDepth, passThroughPercent);

    private static void EnsureCurrent(Map map, int maxDepth)
    {
        bool sameMap = CachedMap.TryGetTarget(out Map cached) && ReferenceEquals(cached, map);

        // A maxDepth raised via the settings slider can reach cells the last rebuild capped at "beyond
        // reach" -- treat a changed slider exactly like a dirty map rather than serving a stale cap.
        if (sameMap && !dirty && maxDepth == cachedMaxDepth)
            return;

        Rebuild(map, maxDepth);
        CachedMap.SetTarget(map);
        cachedMaxDepth = maxDepth;
        dirty = false;
    }

    private static void Rebuild(Map map, int maxDepth)
    {
        CellIndices cellIndices = map.cellIndices;
        int numCells = cellIndices.NumGridCells;
        var result = new int[numCells];
        var visited = new bool[numCells];
        var queue = new Queue<IntVec3>(numCells / 6 + 32);

        // Seeds: every non-interior cell (depth 0 by definition -- sky is already directly overhead,
        // nothing to redistribute in). A negative/zero maxDepth still seeds correctly; the expansion
        // loop below is what refuses to walk past it.
        foreach (IntVec3 cell in map.AllCells)
        {
            if (BlocksSky(map, cell))
                continue;

            int index = cellIndices.CellToIndex(cell);
            visited[index] = true;
            queue.Enqueue(cell);
        }

        int clampedMaxDepth = maxDepth < 0 ? 0 : maxDepth;

        while (queue.Count > 0)
        {
            IntVec3 cell = queue.Dequeue();
            int nextDepth = result[cellIndices.CellToIndex(cell)] + 1;
            if (nextDepth > clampedMaxDepth)
                continue;

            for (int i = 0; i < Neigh8.Length; i++)
            {
                IntVec3 offset = Neigh8[i];
                IntVec3 neighbour = cell + offset;
                if (!neighbour.InBounds(map))
                    continue;

                int neighbourIndex = cellIndices.CellToIndex(neighbour);
                if (visited[neighbourIndex])
                    continue;

                // Diagonal step through a wall corner: refuse it unless both orthogonal cells that
                // make up the corner are open, the same "no cutting corners" rule
                // AmbientLightFalloff.MapComp_AmbientLight's own RebuildDistance applies to its
                // diagonal neighbours -- otherwise light would flood diagonally past a corner no pawn
                // (or photon) could actually walk or pass through.
                if (i >= 4 && CornerBlocked(map, cell, offset))
                    continue;

                visited[neighbourIndex] = true;
                result[neighbourIndex] = nextDepth;
                queue.Enqueue(neighbour);
            }
        }

        depths = result;
    }

    // Identical classification Patch_IndoorSkyOcclusion.ResolveCell already computes per cell -- see
    // its header for why EaveCells.Encloses (not raw Roofed()) is the right notion of "interior": an
    // eave/porch cell is roofed but still breathes outdoor air, so treating it as a BFS seed (depth 0)
    // rather than something to flood into keeps this cache and §7b's occlusion agreeing about which
    // cells count as indoors, rather than inventing a second, narrower definition here.
    private static bool BlocksSky(Map map, IntVec3 cell)
    {
        RoofDef roof = map.roofGrid.RoofAt(cell);
        Building edifice = map.edificeGrid[cell];
        bool isDoor = edifice != null && edifice.def.altitudeLayer == AltitudeLayer.DoorMoveable;
        bool holdsRoof = edifice != null && edifice.def.holdsRoof;

        return IndoorOcclusionMath.BlocksSky(
            EaveCells.Encloses(map, cell, roof), roof != null && roof.isThickRoof, holdsRoof, isDoor);
    }

    private static bool CornerBlocked(Map map, IntVec3 cell, IntVec3 diagonalOffset)
    {
        IntVec3 a = new IntVec3(cell.x + diagonalOffset.x, 0, cell.z);
        IntVec3 b = new IntVec3(cell.x, 0, cell.z + diagonalOffset.z);
        return (a.InBounds(map) && BlocksSky(map, a)) || (b.InBounds(map) && BlocksSky(map, b));
    }
}
