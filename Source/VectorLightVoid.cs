using RimWorld;
using Verse;

namespace CelestialLighting;

// Which cells have no surface for artificial light to land on: the open void outside a gravship hull
// or an orbital platform's deck. See CelestialLightingFeatures.VectorLightVoidVeto for the report
// this answers and for why the mask rides in the field texture's alpha.
//
// TERRAIN IS THE TEST, inVacuum IS ONLY THE GATE. Vacuum.cs argues against adding a second branch
// that can only ever agree with the first, and this is deliberately not one: `inVacuum` is a
// whole-map property and the question here is per-cell, since a pressurised hull and the void it
// flies through share one map. What the veto tests is TerrainDefOf.Space. What `inVacuum` decides is
// whether to bother asking — and on a planet map the answer is no, so the terrain grid is never
// touched at all.
//
// That ordering is load-bearing for cost rather than for correctness. The per-cell read runs once
// per texel of every emitter's square on every field upload, so a colony map paying even an array
// index per texel for a question whose answer is always the same would be a real regression on the
// 99% of maps that have an atmosphere. Gating first makes it exactly zero there.
public static class VectorLightVoid
{
    // Surface present, in the byte scale CopyField writes into the field texture's alpha. Named
    // rather than inlined because the shader's polarity depends on which of the two is which, and
    // the reason it is this way round is a fail-safe worth being able to point at: an unbound
    // sampler reads Unity's blackTexture, whose alpha is 1, so absent data means "all surface" and
    // the veto stands down instead of deleting the fan.
    public const byte SurfaceAlpha = 255;

    // No surface. The fragment program multiplies by this and draws nothing.
    public const byte VoidAlpha = 0;

    // One map's void question, resolved once and then asked per cell without re-reading anything.
    //
    // A STRUCT HOLDING THE GRID RATHER THAN A MAP, because the alternative is map.terrainGrid and a
    // feature-flag read inside a per-texel loop. Inactive is represented by a null grid, so `Active`
    // and "was this map ever going to have void cells" are one field rather than two that could
    // disagree.
    public readonly struct VoidCells
    {
        private readonly TerrainGrid grid;
        private readonly int sizeX;
        private readonly int sizeZ;

        internal VoidCells(TerrainGrid grid, int sizeX, int sizeZ)
        {
            this.grid = grid;
            this.sizeX = sizeX;
            this.sizeZ = sizeZ;
        }

        // False on every map with an atmosphere and whenever the flag is off, which is what lets the
        // callers skip their per-cell work wholesale rather than asking cell by cell.
        public bool Active => grid != null;

        // Whether this cell is open void. Out-of-bounds reads FALSE — "surface" — rather than
        // throwing or reading as void: an emitter's square overhangs the map edge by design, and the
        // texels out there cover no cell any fan can land on, so the answer only has to be the one
        // that changes nothing.
        public bool At(int cellX, int cellZ)
        {
            if (grid == null)
                return false;

            if (cellX < 0 || cellZ < 0 || cellX >= sizeX || cellZ >= sizeZ)
                return false;

            return grid.TerrainAt(new IntVec3(cellX, 0, cellZ)) == TerrainDefOf.Space;
        }

        // The alpha this cell's texel carries, which is the whole of what the shader needs.
        public byte AlphaAt(int cellX, int cellZ) => At(cellX, cellZ) ? VoidAlpha : SurfaceAlpha;
    }

    // Which cells in one emitter's square are open void, rebuilt only when something said so.
    //
    // FOR THE PAWN-SHADOW CLIP, which cannot ask the live grid itself: PawnShadowMath.BuildFrom runs
    // on the thread pool under Parallel.For, and a TerrainGrid read from a worker is exactly the
    // kind of live-state access this repo's pure/adapter split exists to keep off those threads. So
    // the question is answered once per emitter here, on the calling thread, and the workers read a
    // plain bool[].
    //
    // SIZED AND INDEXED LIKE Coverage, on purpose rather than coincidentally: the shadow clip walks
    // a bearing through this grid with VectorLightMath.VoidBoundaryDistance, and having one relative
    // indexing convention for both per-emitter grids is what lets that function be read against
    // CoverageAt without holding two layouts in mind.
    //
    // CLEARED TO NULL WHEN THE MAP HAS NO VOID, not merely left unbuilt. A map can stop being a
    // vacuum map for these purposes the moment the feature is switched off, and a stale grid from
    // before that would keep clipping shadows with nothing to justify it — the flag must reproduce
    // the pre-feature behaviour exactly, and for this one that means the grid going away.
    public static void EnsureGrid(VectorLightField.LightEntry entry, VoidCells cells, int radiusCells)
    {
        if (!cells.Active)
        {
            entry.VoidGrid = null;
            entry.VoidGridDirty = false;
            return;
        }

        int span = radiusCells * 2 + 1;

        if (!entry.VoidGridDirty && entry.VoidGrid != null && entry.VoidGrid.Length == span * span)
            return;

        if (entry.VoidGrid == null || entry.VoidGrid.Length != span * span)
            entry.VoidGrid = new bool[span * span];

        for (int zi = 0; zi < span; zi++)
        {
            for (int xi = 0; xi < span; xi++)
            {
                entry.VoidGrid[zi * span + xi] = cells.At(
                    entry.Cell.x + xi - radiusCells,
                    entry.Cell.z + zi - radiusCells);
            }
        }

        entry.VoidGridDirty = false;
    }

    // This map's void question. Inactive — and therefore free at every call site — unless the veto
    // is on AND the map is one that can have void cells at all.
    public static VoidCells For(Map map)
    {
        if (!CelestialLightingFeatures.VectorLightVoidVeto || map == null)
            return default;

        if (!Vacuum.InVacuumForMap(map))
            return default;

        return new VoidCells(map.terrainGrid, map.Size.x, map.Size.z);
    }
}
