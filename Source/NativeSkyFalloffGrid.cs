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
    // Scratch buffers, reused across rebuilds so a colony under construction is not handing the GC
    // ~560 KB of short-lived arrays every time a wall goes up.
    //
    // DOUBLE-BUFFERED, not reused in place, and the distinction is load-bearing. A Reader hands out
    // the live arrays and is documented as valid only for the call that made it, but SectionWorkerPool
    // regenerates sections on worker threads -- so a rebuild that wrote into the arrays a worker was
    // mid-loop over would tear its results rather than serve it stale ones. Writing into the OTHER set
    // and swapping at the end keeps exactly the guarantee the previous allocate-every-time code had,
    // for one generation, which is one more than any Reader lives for.
    private static int[][] scratchDepth = { null, null };
    private static float[][] scratchStrength = { null, null };
    private static bool[][] scratchVisited = { null, null };
    private static byte[] scratchKind;
    private static bool[] scratchRoofed;
    private static int[] scratchQueue;
    private static int scratchGeneration;
    private static int scratchCells = -1;

    // Cell classification for the flood, resolved once per rebuild. A byte rather than the previous
    // bool[] because the door question rides along in the same read: without it, every cell the flood
    // enters pays an edifice lookup and an altitudeLayer compare to ask "is this a door", and the
    // answer is no for all but a handful of cells on the map.
    private const byte CellOpen = 0;
    private const byte CellBlocker = 1;
    private const byte CellDoor = 2;

    private static void Rebuild(Map map, int maxDepth, float doorStrengthSensitivity)
    {
        CellIndices cellIndices = map.cellIndices;
        int numCells = cellIndices.NumGridCells;
        int sizeX = map.Size.x;
        int sizeZ = map.Size.z;

        EnsureScratch(numCells);
        scratchGeneration ^= 1;
        int[] resultDepth = scratchDepth[scratchGeneration];
        float[] resultStrength = scratchStrength[scratchGeneration];
        bool[] visited = scratchVisited[scratchGeneration];
        byte[] kind = scratchKind;
        bool[] roofed = scratchRoofed;
        int[] queue = scratchQueue;

        Array.Clear(resultDepth, 0, numCells);
        Array.Clear(resultStrength, 0, numCells);
        Array.Clear(visited, 0, numCells);
        Array.Clear(kind, 0, numCells);

        // WHY EVERYTHING BELOW IS INDEXED RATHER THAN WALKED AS CELLS. The BFS used to carry IntVec3s
        // through the queue and call cellIndices.CellToIndex on every neighbour it looked at -- a
        // multiply-add and a struct copy per look, against an array read once the index is the thing
        // being carried. RoofGrid.Roofed and EdificeGrid.InnerArray are both index-addressable
        // (decompiled to confirm), so nothing here needs a cell at all. Index arithmetic is
        // z * sizeX + x, the same CellIndicesUtility.CellToIndex uses, so the two agree by
        // construction.
        BuildCellKinds(map, kind, numCells);

        RoofGrid roofGrid = map.roofGrid;
        for (int index = 0; index < numCells; index++)
            roofed[index] = roofGrid.Roofed(index);

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
        //
        // ONLY THE FRONTIER IS QUEUED, and this is the single biggest thing the rebuild does not do any
        // more. Every unroofed cell is still marked visited at depth 0 with strength 1 -- that is what
        // the seed set MEANS and the arrays say so -- but a seed whose eight neighbours are all
        // unroofed can never contribute anything: every one of them is a seed too, so it is already
        // visited, and expanding into it is eight bounds checks and eight array reads that reject
        // themselves. On an open map that is essentially the entire outdoors, tens of thousands of
        // cells queued to do nothing. A cell the flood can actually enter is by definition roofed, so
        // "has a roofed neighbour" is exactly the set worth expanding from.
        // Walked as rows rather than as a flat range so x comes free from the loop counter. The flat
        // version needed `index % sizeX` to know whether a cell was against the map's east or west
        // edge, and that is a division per cell over the whole map -- on an open map, tens of
        // thousands of them, for a number the loop already knows.
        int head = 0;
        int tail = 0;
        int seedIndex = 0;
        for (int z = 0; z < sizeZ; z++)
        {
            for (int x = 0; x < sizeX; x++, seedIndex++)
            {
                if (!roofed[seedIndex])
                {
                    visited[seedIndex] = true;
                    resultStrength[seedIndex] = 1f;

                    if (HasRoofedNeighbour(roofed, seedIndex, x, sizeX, numCells))
                        queue[tail++] = seedIndex;
                }
            }
        }

        int clampedMaxDepth = maxDepth < 0 ? 0 : maxDepth;
        float doorReference = DoorStrengthReference.WoodDoorBaseMaxHitPoints;

        while (head < tail)
        {
            int cellIndex = queue[head++];
            int nextDepth = resultDepth[cellIndex] + 1;
            if (nextDepth <= clampedMaxDepth)
            {
                float cellStrength = resultStrength[cellIndex];
                int x = cellIndex % sizeX;
                bool hasEast = x + 1 < sizeX;
                bool hasWest = x > 0;
                bool hasNorth = cellIndex + sizeX < numCells;
                bool hasSouth = cellIndex - sizeX >= 0;

                // The four orthogonals first and then the four diagonals, in this exact order, because
                // FIRST VISIT WINS for strength: two parents at the same depth can both reach a cell,
                // and whichever is examined first sets the multiplier it carries. Depth is
                // order-independent, strength is not, so reordering these would be a silent change to
                // what a door leaks. Same order the IntVec3 neighbour table this replaced used.
                if (hasEast)
                    TryVisit(cellIndex + 1);
                if (hasWest)
                    TryVisit(cellIndex - 1);
                if (hasNorth)
                    TryVisit(cellIndex + sizeX);
                if (hasSouth)
                    TryVisit(cellIndex - sizeX);

                // Diagonal step through a wall corner: refuse it unless both orthogonal cells that
                // make up the corner are open, the same "no cutting corners" rule
                // AmbientLightFalloff.MapComp_AmbientLight's own RebuildDistance applies to its
                // diagonal neighbours -- otherwise light would flood diagonally past a corner no pawn
                // (or photon) could actually walk or pass through. Both of those cells are in bounds
                // whenever the diagonal itself is, so they need no bounds test of their own.
                if (hasEast && hasNorth)
                    TryVisitDiagonal(cellIndex + sizeX + 1, cellIndex + 1, cellIndex + sizeX);
                if (hasEast && hasSouth)
                    TryVisitDiagonal(cellIndex - sizeX + 1, cellIndex + 1, cellIndex - sizeX);
                if (hasWest && hasNorth)
                    TryVisitDiagonal(cellIndex + sizeX - 1, cellIndex - 1, cellIndex + sizeX);
                if (hasWest && hasSouth)
                    TryVisitDiagonal(cellIndex - sizeX - 1, cellIndex - 1, cellIndex - sizeX);

                void TryVisit(int neighbourIndex)
                {
                    // A blocker never gets flooded into (and is never a seed either -- it's roofed).
                    // Without this, a wall cell reached from one room's interior would still get
                    // enqueued and could hand its depth on to whatever is on the *other* side of that
                    // wall, leaking one sealed room's falloff into its neighbour through solid
                    // geometry. A door is explicitly not a blocker (AltitudeLayer.DoorMoveable), so the
                    // flood still crosses an open threshold.
                    if (!visited[neighbourIndex] && kind[neighbourIndex] != CellBlocker)
                    {
                        visited[neighbourIndex] = true;
                        resultDepth[neighbourIndex] = nextDepth;
                        resultStrength[neighbourIndex] = kind[neighbourIndex] == CellDoor
                            ? cellStrength * DoorCrossingMultiplier(map, neighbourIndex, doorReference, doorStrengthSensitivity)
                            : cellStrength;
                        queue[tail++] = neighbourIndex;
                    }
                }

                void TryVisitDiagonal(int neighbourIndex, int cornerA, int cornerB)
                {
                    if (kind[cornerA] != CellBlocker && kind[cornerB] != CellBlocker)
                        TryVisit(neighbourIndex);
                }
            }
        }

        depths = resultDepth;
        strengths = resultStrength;
    }

    // Whether any of the eight neighbours is roofed, i.e. whether the flood could ever step out of this
    // seed into somewhere it has not already been.
    private static bool HasRoofedNeighbour(bool[] roofed, int index, int x, int sizeX, int numCells)
    {
        bool hasEast = x + 1 < sizeX;
        bool hasWest = x > 0;
        bool hasNorth = index + sizeX < numCells;
        bool hasSouth = index - sizeX >= 0;

        return (hasEast && roofed[index + 1])
            || (hasWest && roofed[index - 1])
            || (hasNorth && roofed[index + sizeX])
            || (hasSouth && roofed[index - sizeX])
            || (hasEast && hasNorth && roofed[index + sizeX + 1])
            || (hasEast && hasSouth && roofed[index - sizeX + 1])
            || (hasWest && hasNorth && roofed[index + sizeX - 1])
            || (hasWest && hasSouth && roofed[index - sizeX - 1]);
    }

    // Allocated once per map size and then reused. The queue is numCells wide because `visited` is set
    // before a cell is enqueued, so no cell can be queued twice and it can never need to grow -- which
    // is also why it is a plain array with a head and a tail rather than a Queue<T>.
    private static void EnsureScratch(int numCells)
    {
        if (scratchCells != numCells)
        {
            scratchCells = numCells;
            for (int generation = 0; generation < 2; generation++)
            {
                scratchDepth[generation] = new int[numCells];
                scratchStrength[generation] = new float[numCells];
                scratchVisited[generation] = new bool[numCells];
            }

            scratchKind = new byte[numCells];
            scratchRoofed = new bool[numCells];
            scratchQueue = new int[numCells];
        }
    }

    // The DoorLeakMath crossing multiplier for a cell the kind grid has already identified as a door
    // (§7d). Only called for those, which is the point of the kind grid: this used to run for every
    // cell the flood entered and answer 1.0 for almost all of them, at the cost of an edifice lookup
    // and a field compare each time.
    //
    // The multiplier lands on the cell being ENTERED, not on a separate "crossing" concept, because a
    // door occupies exactly one cell by construction: stepping onto it is the one place its strength
    // can be charged, and stepping off it back into ordinary open floor multiplies by the ordinary 1
    // again.
    private static float DoorCrossingMultiplier(
        Map map, int cellIndex, float doorReferenceMaxHitPoints, float doorStrengthSensitivity)
    {
        Building edifice = map.edificeGrid.InnerArray[cellIndex];
        if (edifice == null)
            return 1f;

        return DoorLeakMath.CrossingMultiplier(
            edifice.def.BaseMaxHitPoints, doorReferenceMaxHitPoints, doorStrengthSensitivity, edifice.def.blockLight);
    }

    // One pass over the map, so the BFS above can read a cell's classification instead of re-deciding
    // it every time one of its neighbours is dequeued.
    //
    // TWO PASSES, AND THE SHAPE IS THE PERFORMANCE. The obvious way to ask "does any building in this
    // cell block" is to walk the cell's thing list, and it was written that way first: correct, and
    // measurably slower than the per-visit edifice lookups it replaced -- 14.11 ms per whole-map
    // rebuild before, 16.78 after, timed by SkyFalloffRebuildTimingProbe. 62,500 List<Thing> fetches
    // with a type check each cost more than the array reads they saved.
    //
    // So the union is assembled the way vanilla assembles its own: per BUILDING, not per cell.
    //   1. The edifice grid, walked as the flat array it is -- one branch and a couple of field reads
    //      per cell, no CellToIndex, no list, no allocation. This is the whole answer for every
    //      ordinary wall and for natural rock, which cannot be anything BUT its cell's edifice
    //      (nothing stacks under rock), and rock is why the base pass exists at all rather than being
    //      folded into step 2. It is also the only pass that can mark a DOOR, because
    //      DoorCrossingMultiplier reads the edifice and so must agree with it exactly.
    //   2. Every artificial building on the map, from vanilla's own maintained lister group, ORed in
    //      over its occupied cells. This is the pass that catches a building which is NOT its cell's
    //      registered edifice -- the over-wall vent's evicted wall, and any non-edifice wall fixture --
    //      and it is O(buildings on the map) rather than O(cells), which is one to three orders of
    //      magnitude smaller.
    // ThingRequestGroup.BuildingArtificial is every Building except natural and resource rock
    // (ThingDef.IsBuildingArtificial, decompiled to confirm), and it StoreInRegion()s, so ListerThings
    // keeps the list rather than building one per call.
    private static void BuildCellKinds(Map map, byte[] kind, int numCells)
    {
        Building[] edifices = map.edificeGrid.InnerArray;
        for (int index = 0; index < numCells; index++)
        {
            Building edifice = edifices[index];
            if (edifice != null)
            {
                if (BuildingBlocksFlood(edifice))
                    kind[index] = CellBlocker;
                else if (edifice.def.altitudeLayer == AltitudeLayer.DoorMoveable)
                    kind[index] = CellDoor;
            }
        }

        CellIndices cellIndices = map.cellIndices;
        List<Thing> buildings = map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial);
        for (int i = 0; i < buildings.Count; i++)
        {
            if (buildings[i] is Building building && BuildingBlocksFlood(building))
            {
                foreach (IntVec3 cell in building.OccupiedRect())
                {
                    if (cell.InBounds(map))
                        kind[cellIndices.CellToIndex(cell)] = CellBlocker;
                }
            }
        }
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
    // ASKED OF EVERY BUILDING ON THE CELL, not of the cell's edifice -- see BuildBlockerGrid above for
    // how the union is assembled. The blocker test read map.edificeGrid[cell] until an over-wall vent
    // report made the difference matter, and the edifice grid is the wrong grid for this question.
    //
    // It holds ONE building per cell -- Verse.EdificeGrid.Register just writes the array slot, and
    // DeRegister nulls it without checking who is in it -- while Verse.Building.SpawnSetup calls
    // GlowGrid.LightBlockerAdded for EVERY building it spawns, edifice or not. So vanilla's own glow
    // flood answers per building in the cell and this answered per edifice, and the two only agree
    // because vanilla never stands two buildings in one cell. Replace Stuff does exactly that: its
    // over-wall vent shares the wall's cell, with SpawningWipes patched so neither wipes the other, and
    // its 1.6 def no longer carries the isEdifice=false its pre-1.6 def did -- so the vent registers as
    // the edifice and the granite wall it was built onto is silently dropped from that grid while it
    // goes on standing there. Every mod that stands two buildings in one cell has the same shape.
    //
    // Asking it per building is also what makes IsWallApertureFixture reachable, since the question is
    // put to the vent rather than to the cell.
    //
    // The aperture test is skipped whenever blockLight has already said yes, which is semantics-
    // preserving -- BlocksFlood ORs the two terms, so it cannot change an answer -- and is worth the
    // line because it keeps two type checks off every wall and every rock cell on the map. A mountain
    // map has tens of thousands of those and exactly none of them are vents.
    private static bool BuildingBlocksFlood(Building building)
    {
        ThingDef def = building.def;
        bool blocksLight = def.blockLight;

        return NativeSkyFalloffMath.BlocksFlood(
            blocksLight,
            def.altitudeLayer == AltitudeLayer.DoorMoveable,
            !blocksLight && IsWallApertureFixture(building));
    }

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
}
