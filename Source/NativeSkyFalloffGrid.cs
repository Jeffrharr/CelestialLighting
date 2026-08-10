using System;
using System.Collections.Generic;
using Verse;

namespace CelestialLighting;

// Live-state half of §7c (DESIGN.md) -- the adapter that walks Map/RoofGrid/EdificeGrid to answer
// NativeSkyFalloffMath.FractionAt's `depth` argument. See NativeSkyFalloffMath's header for the pure
// formula, and SkyFalloffSource.cs for why this exists as a second, mutually exclusive source rather
// than a merge with that one -- SkyFalloffSource is the dispatcher that picks between the two; this
// file has no opinion on CelestialLightingFeatures or whether another mod answered, and will compute
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
// WeakReference<Map> cache, so deleting this
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
    // + `using Verse;`.
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

        // Seeds: every UNROOFED cell (depth 0 by definition -- sky is already directly overhead,
        // nothing to redistribute in). Matches AmbientLightFalloff.MapComp_AmbientLight's own
        // RebuildDistance seed condition exactly (`!roofGrid.Roofed(cell)`, decompiled to confirm) --
        // deliberately NOT `!BlocksSky(cell)`. BlocksSky answers "should §7b paint this cell fully
        // dark", which is false for a WALL cell too (a wall gets the corner-ramp treatment, not a flat
        // fill -- see IndoorOcclusionMath.BlocksSky's own header), so seeding on it treated every wall
        // around a room as an opening: a 9x9 room has no floor cell more than a few tiles from *some*
        // wall, so the whole room read a shallow, near-uniform depth instead of a gradient from the
        // door outward (#124 follow-up). A negative/zero maxDepth still seeds correctly; the expansion
        // loop below is what refuses to walk past it.
        foreach (IntVec3 cell in map.AllCells)
        {
            if (map.roofGrid.Roofed(cell))
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

                // A wall never gets flooded into (and is never a seed either -- it's roofed). Without
                // this, a wall cell reached from one room's interior would still get enqueued and could
                // hand its depth on to whatever is on the *other* side of that wall, leaking one sealed
                // room's falloff into its neighbour through solid geometry. A door is explicitly not a
                // wall here (AltitudeLayer.DoorMoveable), so the flood still crosses an open threshold.
                if (IsWall(map, neighbour))
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

    // Physically solid -- holds up the roof and is not a door. Deliberately NOT
    // IndoorOcclusionMath.BlocksSky: that predicate answers "should §7b's rendering pass paint this
    // cell fully dark" (false for a wall, which gets the corner-ramp treatment instead), not "can the
    // flood pass through this cell". A door never counts as a wall here, so the BFS still crosses an
    // open threshold the way both DepthAt's own header and Rebuild's seed loop above expect.
    private static bool IsWall(Map map, IntVec3 cell)
    {
        Building edifice = map.edificeGrid[cell];
        return edifice != null && edifice.def.holdsRoof
            && edifice.def.altitudeLayer != AltitudeLayer.DoorMoveable;
    }

    private static bool CornerBlocked(Map map, IntVec3 cell, IntVec3 diagonalOffset)
    {
        IntVec3 a = new IntVec3(cell.x + diagonalOffset.x, 0, cell.z);
        IntVec3 b = new IntVec3(cell.x, 0, cell.z + diagonalOffset.z);
        return (a.InBounds(map) && IsWall(map, a)) || (b.InBounds(map) && IsWall(map, b));
    }
}
