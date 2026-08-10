using System.Collections.Generic;

namespace CelestialLighting;

// §24's roof mask, pure half: turns a map's roofed/unroofed grid into the smallest set of horizontal
// runs that covers the UNROOFED cells, so the glare quad can be built from open sky only.
//
// WHY A MASK IS NEEDED AT ALL. §24 draws at AltitudeLayer.VisEffects, which it must (below that sits
// LightingOverlay, whose multiply is the very ceiling this subsystem exists to exceed — see
// SnowGlareOverlay). But SectionLayer_IndoorMask draws between Weather and VisEffects, so vanilla's
// own roof masking happens BELOW us and does not apply: the first prototype washed glare straight
// across roofed interiors, which have no sky and should see none of it. Moving down is not an option,
// so the mask has to be geometry we build ourselves.
//
// WHY RUNS RATHER THAN ONE QUAD PER CELL. A 250x250 map is 62,500 cells, i.e. 250,000 vertices if
// each cell is its own quad — a mesh big enough to need a 32-bit index buffer and slow enough to
// rebuild that it would need its own throttling. But roofs are overwhelmingly CONTIGUOUS: a colony is
// a handful of rectangular rooms in a sea of open ground, so merging each row's unroofed cells into
// spans collapses that to a few hundred quads on a typical map, and to exactly ONE quad per row on a
// map with no roofs at all. The compression is best precisely where the map is emptiest, which is
// also where this runs most often.
//
// WHY NOT MERGE VERTICALLY TOO. Merging identical adjacent rows into rectangles would compress a
// no-roof map from 250 quads to 1, and it is genuinely tempting. It is not done because the mesh is
// rebuilt only when a roof changes (see Patch_SnowGlareRoofInvalidation) and a few hundred extra
// quads cost nothing per frame — while a two-dimensional merge is the kind of code that is wrong in
// exactly one configuration nobody tests. Row runs are provably correct by inspection and their
// worst case is bounded by the row count.
//
// Pure by the repo's convention: no UnityEngine, no Verse, primitives in and out.
public static class SnowGlareMaskMath
{
    // One horizontal span of unroofed cells on row Z, covering cells [XStart, XEnd] inclusive.
    //
    // A struct rather than a tuple so the field names survive into the mesh builder — an (int, int,
    // int) is exactly the shape where an argument-order slip produces a mesh that renders as nothing
    // with no error, which AuroraCurtainOverlay's own winding note records the cost of.
    public readonly struct Run
    {
        public readonly int Z;
        public readonly int XStart;
        public readonly int XEnd;

        public Run(int z, int xStart, int xEnd)
        {
            Z = z;
            XStart = xStart;
            XEnd = xEnd;
        }

        // Inclusive on both ends, so a single-cell run has width 1 rather than 0.
        public int Width => XEnd - XStart + 1;
    }

    // Every maximal run of unroofed cells, row by row. `roofed` is indexed the way RimWorld indexes
    // its cell grids — z * width + x — so the adapter can hand over a grid it read straight off
    // Map.roofGrid without transposing anything.
    //
    // An entirely roofed map returns an empty list, which the caller must treat as "draw nothing"
    // rather than as "draw everything": a fully-enclosed map has no sky, and the §17 map-kind gates
    // would already have stood the subsystem down before reaching here anyway.
    public static List<Run> UnroofedRuns(bool[] roofed, int width, int height)
    {
        List<Run> runs = new List<Run>();

        if (roofed == null || width <= 0 || height <= 0)
            return runs;

        for (int z = 0; z < height; z++)
        {
            int rowStart = z * width;
            int runStart = -1;

            for (int x = 0; x < width; x++)
            {
                bool isOpen = !roofed[rowStart + x];

                // Open cell with no run in progress: this is where one begins.
                if (isOpen && runStart < 0)
                    runStart = x;

                // Roofed cell closing a run that was in progress. The run ends at the PREVIOUS cell,
                // which is why the end is stored inclusive rather than as a length — an off-by-one
                // here leaks glare one cell under the eaves, and that is the single most likely place
                // for this file to be subtly wrong.
                if (!isOpen && runStart >= 0)
                {
                    runs.Add(new Run(z, runStart, x - 1));
                    runStart = -1;
                }
            }

            // A run still open when the row ends closes at the map edge rather than being dropped.
            if (runStart >= 0)
                runs.Add(new Run(z, runStart, width - 1));
        }

        return runs;
    }

    // Whether the mask covers the whole map, i.e. nothing is roofed. The adapter uses this to skip
    // its own mesh entirely and fall back to MeshPool.wholeMapPlane — a shared, already-resident mesh
    // that costs nothing to keep and nothing to build.
    //
    // This is the common case by a wide margin. Most maps a player never builds on are entirely
    // unroofed, and even a developed colony's roofs are a small fraction of a 250x250 map, so the
    // cheap path is worth having explicitly rather than as an emergent property of the run count.
    public static bool CoversWholeMap(List<Run> runs, int width, int height)
    {
        if (runs == null || width <= 0 || height <= 0)
            return false;

        // One full-width run per row is the only way to cover everything, since runs are maximal.
        if (runs.Count != height)
            return false;

        for (int i = 0; i < runs.Count; i++)
        {
            if (runs[i].Width != width)
                return false;
        }

        return true;
    }
}
