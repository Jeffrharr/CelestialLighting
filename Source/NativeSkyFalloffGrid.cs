using System;
using System.Collections.Generic;
using RimWorld;
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
    private static float[] strengths;
    private static bool dirty = true;
    private static int cachedMaxDepth = -1;
    private static float cachedDoorStrengthSensitivity = float.NaN;

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
    public static int DepthAt(Map map, IntVec3 cell, int maxDepth, float doorStrengthSensitivity)
    {
        if (map == null || !cell.InBounds(map))
            return 0;

        EnsureCurrent(map, maxDepth, doorStrengthSensitivity);
        return depths[map.cellIndices.CellToIndex(cell)];
    }

    // Running product of every DoorLeakMath.CrossingMultiplier crossed on the way to `cell` -- 1.0
    // for a cell reached without crossing any door (or with doorStrengthSensitivity at its no-op
    // floor). See §7d's header on DoorLeakMath for why this is a SEPARATE multiply from DepthAt's own
    // BFS distance rather than folded into it: depth answers "how deep is this room", strength answers
    // "how much of the sky survived getting here", and a stronger door only changes the second one.
    public static float StrengthAt(Map map, IntVec3 cell, int maxDepth, float doorStrengthSensitivity)
    {
        if (map == null || !cell.InBounds(map))
            return 1f;

        EnsureCurrent(map, maxDepth, doorStrengthSensitivity);
        return strengths[map.cellIndices.CellToIndex(cell)];
    }

    public static float FractionAt(Map map, IntVec3 cell, float curSkyGlow, int maxDepth, float passThroughPercent, float doorStrengthSensitivity)
    {
        int depth = DepthAt(map, cell, maxDepth, doorStrengthSensitivity);
        float strength = StrengthAt(map, cell, maxDepth, doorStrengthSensitivity);
        return NativeSkyFalloffMath.FractionAt(depth, curSkyGlow, maxDepth, passThroughPercent) * strength;
    }

    // A reader bound to one map for the duration of one caller's loop, for callers that ask about
    // hundreds of cells in a row -- §7b's window fill asks about 361 of them per section regenerate.
    //
    // WHY THIS EXISTS, measured. FractionAt above is correct and is the wrong shape for a loop: it
    // calls DepthAt and StrengthAt, and *each* of those independently null-checks the map, runs
    // cell.InBounds(map), runs EnsureCurrent (a WeakReference deref plus three comparisons) and calls
    // cellIndices.CellToIndex -- all of which answer the same for every cell in the loop, all to
    // retrieve two array elements. Two of everything, 361 times over, plus the settings fetch and the
    // CurSkyGlow read its caller was doing per cell as well. Live measurement put this arm at 109.1 µs
    // of §7b's 228 µs postfix, against 22.3 for the glow-grid passthrough it sits next to -- i.e. the
    // ceremony cost several times what the two array reads did.
    //
    // NOT A CACHE, deliberately, and worth being precise about because a cache was the obvious thing
    // to reach for: this holds nothing across calls and can serve nothing stale. It is the same
    // arithmetic on the same grid, with the loop-invariant half hoisted out of the loop. The grid it
    // reads is the cache, it is already invalidated by Patch_SkyFalloffDirty, and none of that changes.
    //
    // Valid only for the call that made it. Rebuild swaps the arrays wholesale, so a Reader kept
    // across a dirty would keep serving the old ones; every caller builds one, loops, drops it.
    public readonly struct Reader
    {
        private readonly int[] depths;
        private readonly float[] strengths;
        private readonly int sizeX;
        private readonly int sizeZ;
        private readonly float curSkyGlow;
        private readonly int maxDepth;
        private readonly float passThroughPercent;

        internal Reader(
            int[] depths, float[] strengths, int sizeX, int sizeZ,
            float curSkyGlow, int maxDepth, float passThroughPercent)
        {
            this.depths = depths;
            this.strengths = strengths;
            this.sizeX = sizeX;
            this.sizeZ = sizeZ;
            this.curSkyGlow = curSkyGlow;
            this.maxDepth = maxDepth;
            this.passThroughPercent = passThroughPercent;
        }

        // False for a Reader that never got a grid (no map). Callers fall back to 0, which is the same
        // "no cap" answer CapOcclusion already treats as absent.
        public bool Valid => depths != null;

        // Same value FractionAt(map, cell, ...) returns for the same cell, by construction: identical
        // index arithmetic (CellIndicesUtility.CellToIndex is z * sizeX + x, decompiled to confirm) and
        // identical formula. The off-map answer is 0 rather than DepthAt's 0-and-StrengthAt's-1,
        // because NativeSkyFalloffMath.FractionAt(depth: 0, ...) is 0 anyway -- the product is the same
        // either way, and this states it once instead of composing two sentinels that happen to agree.
        public float FractionAt(int x, int z)
        {
            if (depths == null || x < 0 || z < 0 || x >= sizeX || z >= sizeZ)
                return 0f;

            int index = z * sizeX + x;
            return NativeSkyFalloffMath.FractionAt(depths[index], curSkyGlow, maxDepth, passThroughPercent)
                * strengths[index];
        }
    }

    // Builds the grid if it is stale, then hands back a reader over it. This is the one place the
    // per-loop work happens: one EnsureCurrent, one map-size read, one CurSkyGlow read.
    public static Reader ReaderFor(
        Map map, float curSkyGlow, int maxDepth, float passThroughPercent, float doorStrengthSensitivity)
    {
        if (map == null)
            return default;

        EnsureCurrent(map, maxDepth, doorStrengthSensitivity);
        return new Reader(
            depths, strengths, map.Size.x, map.Size.z, curSkyGlow, maxDepth, passThroughPercent);
    }

    private static void EnsureCurrent(Map map, int maxDepth, float doorStrengthSensitivity)
    {
        bool sameMap = CachedMap.TryGetTarget(out Map cached) && ReferenceEquals(cached, map);

        // A maxDepth raised via the settings slider can reach cells the last rebuild capped at "beyond
        // reach" -- treat a changed slider exactly like a dirty map rather than serving a stale cap.
        // Same for doorStrengthSensitivity: it changes every door's crossing multiplier, so a stale
        // grid would keep serving strengths computed under the old sensitivity until something else
        // happened to dirty the map.
        if (sameMap && !dirty && maxDepth == cachedMaxDepth && doorStrengthSensitivity == cachedDoorStrengthSensitivity)
            return;

        Rebuild(map, maxDepth, doorStrengthSensitivity);
        CachedMap.SetTarget(map);
        cachedMaxDepth = maxDepth;
        cachedDoorStrengthSensitivity = doorStrengthSensitivity;
        dirty = false;
    }

    // Plain FIFO BFS -- unchanged in shape from before §7d. Depth is deliberately door-oblivious (a
    // door costs exactly the same one step as open floor): what a stronger door changes is a SEPARATE
    // running strength multiplier carried alongside depth, propagated forward through `strengths` and
    // only ever multiplied, never used to decide traversal order -- so first-visit-wins is still exactly
    // correct for depth, the same guarantee the pre-§7d flat-weight BFS relied on. See DoorLeakMath's
    // header for why a door dims the flood instead of pushing it further away.
    private static void Rebuild(Map map, int maxDepth, float doorStrengthSensitivity)
    {
        CellIndices cellIndices = map.cellIndices;
        int numCells = cellIndices.NumGridCells;
        var resultDepth = new int[numCells];
        var resultStrength = new float[numCells];
        var visited = new bool[numCells];
        var queue = new Queue<IntVec3>(numCells / 6 + 32);

        // Blockers are resolved for the whole map up front rather than at each neighbour visit. The BFS
        // asks about a given cell up to nine times over (once as each of its eight neighbours' targets,
        // plus the corner tests), so answering once per cell costs less than answering per visit even
        // though each answer now walks a thing list instead of indexing the edifice grid.
        bool[] blocked = BuildBlockerGrid(map);

        // Seeds: every UNROOFED cell (depth 0, strength 1.0 -- sky is already directly overhead,
        // nothing crossed yet). Matches AmbientLightFalloff.MapComp_AmbientLight's own RebuildDistance
        // seed condition exactly (`!roofGrid.Roofed(cell)`, decompiled to confirm) -- deliberately NOT
        // `!BlocksSky(cell)`. BlocksSky answers "should §7b paint this cell fully dark", which is false
        // for a WALL cell too (a wall gets the corner-ramp treatment, not a flat fill -- see
        // IndoorOcclusionMath.BlocksSky's own header), so seeding on it treated every wall around a
        // room as an opening: a 9x9 room has no floor cell more than a few tiles from *some* wall, so
        // the whole room read a shallow, near-uniform depth instead of a gradient from the door outward
        // (#124 follow-up). A negative/zero maxDepth still seeds correctly; the expansion loop below is
        // what refuses to walk past it.
        foreach (IntVec3 cell in map.AllCells)
        {
            if (map.roofGrid.Roofed(cell))
                continue;

            int index = cellIndices.CellToIndex(cell);
            visited[index] = true;
            resultStrength[index] = 1f;
            queue.Enqueue(cell);
        }

        int clampedMaxDepth = maxDepth < 0 ? 0 : maxDepth;
        float doorReference = DoorStrengthReference.WoodDoorBaseMaxHitPoints;

        while (queue.Count > 0)
        {
            IntVec3 cell = queue.Dequeue();
            int cellIndex = cellIndices.CellToIndex(cell);
            int nextDepth = resultDepth[cellIndex] + 1;
            if (nextDepth > clampedMaxDepth)
                continue;

            float cellStrength = resultStrength[cellIndex];

            for (int i = 0; i < Neigh8.Length; i++)
            {
                IntVec3 offset = Neigh8[i];
                IntVec3 neighbour = cell + offset;
                if (!neighbour.InBounds(map))
                    continue;

                int neighbourIndex = cellIndices.CellToIndex(neighbour);
                if (visited[neighbourIndex])
                    continue;

                // A blocker never gets flooded into (and is never a seed either -- it's roofed).
                // Without this, a wall cell reached from one room's interior would still get enqueued
                // and could hand its depth on to whatever is on the *other* side of that wall, leaking
                // one sealed room's falloff into its neighbour through solid geometry. A door is
                // explicitly not a blocker (AltitudeLayer.DoorMoveable), so the flood still crosses an
                // open threshold.
                if (blocked[neighbourIndex])
                    continue;

                // Diagonal step through a wall corner: refuse it unless both orthogonal cells that
                // make up the corner are open, the same "no cutting corners" rule
                // AmbientLightFalloff.MapComp_AmbientLight's own RebuildDistance applies to its
                // diagonal neighbours -- otherwise light would flood diagonally past a corner no pawn
                // (or photon) could actually walk or pass through.
                if (i >= 4 && CornerBlocked(map, blocked, cell, offset))
                    continue;

                visited[neighbourIndex] = true;
                resultDepth[neighbourIndex] = nextDepth;
                resultStrength[neighbourIndex] = cellStrength * CrossingMultiplier(map, neighbour, doorReference, doorStrengthSensitivity);
                queue.Enqueue(neighbour);
            }
        }

        depths = resultDepth;
        strengths = resultStrength;
    }

    // 1.0 (no dimming) for every ordinary cell; DoorLeakMath.CrossingMultiplier for a cell a door
    // occupies -- §7d. The multiplier lands on the cell being ENTERED, not a separate "crossing"
    // concept, because a door occupies exactly one cell by construction: stepping onto it is the one
    // place its strength can be charged, and stepping off it back into ordinary open floor multiplies
    // by the ordinary 1 again.
    private static float CrossingMultiplier(Map map, IntVec3 cell, float doorReferenceMaxHitPoints, float doorStrengthSensitivity)
    {
        Building edifice = map.edificeGrid[cell];
        if (edifice == null || edifice.def.altitudeLayer != AltitudeLayer.DoorMoveable)
            return 1f;

        return DoorLeakMath.CrossingMultiplier(edifice.def.BaseMaxHitPoints, doorReferenceMaxHitPoints, doorStrengthSensitivity, edifice.def.blockLight);
    }

    // One pass over the map, so the BFS below can index an array instead of re-deciding a cell every
    // time one of its neighbours is dequeued.
    private static bool[] BuildBlockerGrid(Map map)
    {
        CellIndices cellIndices = map.cellIndices;
        var blocked = new bool[cellIndices.NumGridCells];

        foreach (IntVec3 cell in map.AllCells)
            blocked[cellIndices.CellToIndex(cell)] = CellBlocksFlood(map, cell);

        return blocked;
    }

    // Whether the flood stops at this cell. The rule itself is NativeSkyFalloffMath.BlocksFlood, which
    // carries the full "why" -- including why it is vanilla's own blockLight set and why a door is
    // deliberately not a blocker; everything here is the reading of live grids a pure function cannot
    // do. Deliberately NOT IndoorOcclusionMath.BlocksSky: that predicate answers "should §7b's
    // rendering pass paint this cell fully dark" (false for a wall, which gets the corner-ramp
    // treatment instead), not "can the flood pass through this cell".
    //
    // Named for what it does rather than what usually does it. It was called IsWall while it required
    // def.holdsRoof, and the name was load-bearing in the wrong direction: a Vent is not a wall, and it
    // was not treated as one, so sky light poured through it into a sealed room. What stops light is
    // not what holds up a roof.
    //
    // The blockLight check is what makes a see-through wall (a modded glass partition, e.g. Vanilla
    // Furniture Expanded - Architect's VFEArch_CellWall: holdsRoof true, blockLight false, and -- unlike
    // ReBuild's own glass walls -- no GroundGlowAt patch of its own to own the whole-map gradient
    // instead) read as ordinary open floor rather than solid rock: the BFS crosses it for free, exactly
    // as it crosses a door with CrossingMultiplier's own no-op floor, since CrossingMultiplier only ever
    // special-cases AltitudeLayer.DoorMoveable and leaves every other edifice at its default 1. This is
    // NOT the "bespoke transparent-wall leak" §7b's header records as deleted for measuring inert --
    // that measurement was taken with ReBuild loaded, which stands this entire BFS down map-wide via
    // UnderRoofFalloffOwner, so it could never have exercised this branch in the first place. A glass
    // wall from a mod that does not own the gradient is the one live case where this is not inert.
    //
    // It asks EVERY BUILDING IN THE CELL, not the cell's edifice. This read map.edificeGrid[cell] until
    // an over-wall vent report made the difference matter, and the edifice grid is the wrong grid for
    // this question in two ways at once.
    //
    // It holds ONE building per cell -- Verse.EdificeGrid.Register just writes the array slot, and
    // DeRegister nulls it without checking who is in it -- while Verse.Building.SpawnSetup calls
    // GlowGrid.LightBlockerAdded for EVERY building it spawns, edifice or not. So vanilla's own glow
    // flood answers per building in the cell and this answered per edifice, and the two only agree
    // because vanilla never stands two buildings in one cell. Replace Stuff does exactly that (its
    // over-wall cooler and vent share the wall's cell, with SpawningWipes patched so neither wipes the
    // other), and mods that add non-edifice wall fixtures do too.
    //
    // And a non-edifice building is invisible to it entirely: Replace Stuff's Vent_Over sets
    // isEdifice false, so a bare one -- no wall under it, which that mod's PlaceWorker_Vent prefix
    // allows -- never reached this test at all, whatever it then asked about the def.
    //
    // Walking the thing list is what makes IsWallApertureFixture reachable as well, since it is asked
    // of the vent rather than of the cell.
    private static bool CellBlocksFlood(Map map, IntVec3 cell)
    {
        List<Thing> things = map.thingGrid.ThingsListAtFast(cell);

        for (int i = 0; i < things.Count; i++)
        {
            if (things[i] is Building building && BuildingBlocksFlood(building))
                return true;
        }

        return false;
    }

    private static bool BuildingBlocksFlood(Building building) =>
        NativeSkyFalloffMath.BlocksFlood(
            building.def.blockLight,
            building.def.altitudeLayer == AltitudeLayer.DoorMoveable,
            IsWallApertureFixture(building));

    // A vent or a cooler: the two vanilla buildings that exist to fill an aperture in a wall, and the
    // two whose PlaceWorkers (PlaceWorker_Vent, PlaceWorker_Cooler) demand a wall between two rooms to
    // be placed at all. Both core defs already set blockLight true, so this changes nothing for them
    // and is stated as an agreement with vanilla rather than an override of it.
    //
    // What it is FOR is the modded over-wall pair -- Replace Stuff's Cooler_Over / Vent_Over, which are
    // the same two thingClasses with blockLight turned FALSE because the wall they are built onto is
    // expected to do the blocking. Built without that wall they read as an open window to every
    // blockLight test there is, vanilla's included, and the sky poured through one at exactly the
    // strength of an open doorway (depth 2, fraction 0.2625 -- measured beside a wall-plus-vent arm
    // that read 0). A glass wall makes the opposite claim with the same flag and is neither of these
    // two classes, so it still crosses freely: this is the narrowest thing that separates them.
    //
    // Type checks rather than defNames on purpose -- any mod's vent subclassing Building_Vent gets the
    // same answer, and a defName list would go stale the day Replace Stuff adds its wide variants.
    // Not Building_TempControl, their shared base: that would take in Building_Heater, which is a
    // free-standing appliance inside a room and would notch a dark cell out of the interior gradient.
    public static bool IsWallApertureFixture(Building building) =>
        building is Building_Vent || building is Building_Cooler;

    private static bool CornerBlocked(Map map, bool[] blocked, IntVec3 cell, IntVec3 diagonalOffset)
    {
        IntVec3 a = new IntVec3(cell.x + diagonalOffset.x, 0, cell.z);
        IntVec3 b = new IntVec3(cell.x, 0, cell.z + diagonalOffset.z);
        CellIndices cellIndices = map.cellIndices;
        return (a.InBounds(map) && blocked[cellIndices.CellToIndex(a)])
            || (b.InBounds(map) && blocked[cellIndices.CellToIndex(b)]);
    }
}
