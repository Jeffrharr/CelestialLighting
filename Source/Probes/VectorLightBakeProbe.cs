using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// What the vector-light field's bake cache actually DID, as opposed to what a polygon looks like.
//
// WHY THIS IS SEPARATE FROM VectorLightProbe. That probe answers shape questions — lit area, ray
// count, penumbra area — and to do it, it calls VectorLightMath.Build and recomputes a polygon of
// its own. That is the right instrument for "is the geometry correct" and precisely the wrong one
// for "is the geometry being rebuilt too often": a recomputation returns a fresh, correct answer
// whether or not the cache was consulted, so a memoisation would read as working while doing
// nothing. Every metric here is a counter the bake itself incremented.
//
// The model is door_aperture_bakes. The reasoning behind that probe applies unchanged and one step
// up: a per-call timer cannot see a call COUNT rise, so a bake made twice as cheap and provoked
// twice as often looks like a clean win in every timing table in the repo.
public sealed class VectorLightBakeProbe : IProbe
{
    public enum Metric
    {
        // Visibility polygons actually rebuilt since the last reset. The number the whole
        // performance phase is about.
        Bakes,

        // Rebuilds skipped because the polygon was still clean. Meaningless alone and load-bearing
        // beside Bakes: a field nobody asked about also reports zero bakes.
        Hits,

        // Silhouette segments summed over every bake. States the POPULATION a bake count was
        // measured over — the ray cull's gain scales with clutter, so a bake count taken over an
        // empty window has verified nothing about it.
        Segments,

        // Mean segments per bake. Derived rather than pinned on its own, but worth its own metric
        // because it is the one number that says "this scenario is measuring a cluttered scene"
        // without the reader dividing two pins in their head.
        SegmentsPerBake,

        // Blocker writes that asked the field to invalidate, and emitters that actually dirtied.
        InvalidationCalls,
        InvalidationMarks,

        // Emitters dirtied per invalidating write: the invalidation RADIUS, measured. The epic names
        // MarkGeometryDirtyAround as the suspect that turns one lamp toggle into a map-wide rebake,
        // and this is the number that settles it — it is the fan-out, and against the emitter count
        // it says whether "only the lights that can see the cell" is true in practice.
        MarksPerCall,

        // Roster resyncs against vanilla's glower sets. One per lamp toggle is correct; a count that
        // tracks the frame number means something is dirtying the roster on a cadence.
        RosterResyncs,

        // Builds the view cull declined, leaving the polygon dirty until its emitter is back in
        // range (issue #188 item B). Read BESIDE Bakes and never alone: bakes falling while
        // deferrals rise by the same amount is the cull working, and both falling together is a
        // scenario that stopped provoking anything. A deferral is postponed work, not an error.
        Deferrals,

        // ---- sections (issue #188 item 0) -----------------------------------------------------
        //
        // Every metric above is about POLYGONS. #191 used them to establish that a blocker write
        // dirties one or two emitters out of twenty-three, and headed the finding "one wall does not
        // rebake the map" -- true of polygons, and silent about sections, because nothing here could
        // watch one regenerate. These four close that.

        // Sections flagged dirty, and the frames that flagged them. Both paths charge themselves,
        // the map-wide one at the map's full section count, so the arms report one quantity.
        SectionDirties,
        SectionDirtyPasses,

        // Sections flagged per provocation. THE HEADLINE for item A: the map's whole section count
        // before, a handful after. Read against vector_light_mask_applies, which says how much of
        // that reduction was work anybody was going to do.
        SectionsPerPass,

        // Lighting-overlay regenerates that actually ran through the mask. THE OUTCOME, and the only
        // one of the four that needs no per-arm adjustment: dirty flags are work requested, and
        // vanilla regenerates only the sections in view, so a change can cut flags fifty-fold and
        // leave this flat -- which would mean the saving was on sections nobody was looking at.
        MaskApplies,

        // How many emitters the field currently holds. The denominator for MarksPerCall, and the
        // thing that says a scenario's lamps actually registered.
        Emitters,

        // THE COVERAGE GRID ITSELF, summarised, and the reason it is here is that nothing else
        // reads it. Every other shape probe in this repo recomputes from the visibility polygon --
        // lit area, shadow fraction, vertex count are all polygon or mesh quantities -- so a change
        // to BuildCoverage could rewrite every byte of every grid and no scenario in the repo would
        // move. The bounds landed with the offline suite asserting them bit-for-bit and no live
        // check at all, which is the same gap the ray cull was careful to close and would have been
        // a worse one here: coverage is what the mask multiplies vanilla's glow by.
        //
        // TWO NUMBERS BECAUSE THEY FAIL DIFFERENTLY. LitCells counts the cells the grid calls FULLY
        // lit, which is exactly what the nearest-ray fast path writes, so a bound that is too
        // generous shows up here first and directly. CoverageMean averages every byte, which is
        // what moves when the farthest-ray path wrongly zeroes a cell, and it also catches the
        // partial values at a shadow edge that a count of 255s cannot see. Either alone would leave
        // one of the two bounds unwatched.
        //
        // A MEAN RATHER THAN A SUM, because a probe reads as float: the sum over 23 radius-10
        // emitters is around five million, which is inside float's exact-integer range but not far
        // enough inside it to be worth relying on as the population grows. The mean is a byte-scaled
        // number in [0, 255] that stays exact where it matters and reads as a quantity rather than
        // as a hash.
        CoverageMean,
        LitCells,

        // Side-effecting: zeroes every counter above and reads 0, following circinus_*_reset.
        //
        // It exists so the counting window and the PROFILING window can be opened at the same point
        // in a scenario. Without it the counters reset on the vector_lights flag toggle, several
        // steps earlier, and the first run of this scenario duly reported 161 bakes against
        // Circinus's 46 calls to the same method — two true numbers over two different windows,
        // which is exactly the sort of pair that gets quoted as a ratio by mistake.
        Reset,
    }

    private readonly Metric metric;

    public string Name { get; }

    public VectorLightBakeProbe(string name, Metric metric)
    {
        Name = name;
        this.metric = metric;
    }

    public float Read(Map map)
    {
        if (metric == Metric.Bakes)
            return VectorLightField.PolygonBakes;

        if (metric == Metric.Hits)
            return VectorLightField.PolygonHits;

        if (metric == Metric.Segments)
            return VectorLightField.BakeSegments;

        if (metric == Metric.SegmentsPerBake)
            return Ratio(VectorLightField.BakeSegments, VectorLightField.PolygonBakes);

        if (metric == Metric.InvalidationCalls)
            return VectorLightField.InvalidationCalls;

        if (metric == Metric.InvalidationMarks)
            return VectorLightField.InvalidationMarks;

        if (metric == Metric.MarksPerCall)
            return Ratio(VectorLightField.InvalidationMarks, VectorLightField.InvalidationCalls);

        if (metric == Metric.RosterResyncs)
            return VectorLightField.RosterResyncs;

        if (metric == Metric.Deferrals)
            return VectorLightField.PolygonDeferrals;

        if (metric == Metric.SectionDirties)
            return VectorLightField.SectionDirties;

        if (metric == Metric.SectionDirtyPasses)
            return VectorLightField.SectionDirtyPasses;

        if (metric == Metric.SectionsPerPass)
            return Ratio(VectorLightField.SectionDirties, VectorLightField.SectionDirtyPasses);

        if (metric == Metric.MaskApplies)
            return VectorLightField.MaskApplies;

        if (metric == Metric.CoverageMean)
            return CoverageMean(map);

        if (metric == Metric.LitCells)
            return LitCells(map);

        if (metric == Metric.Reset)
        {
            VectorLightField.ResetCounters();
            return 0f;
        }

        return EmitterCount(map);
    }

    // Zero rather than NaN on an empty denominator. A scenario that provoked nothing should read as
    // "nothing happened" and fail its pin, not poison the report with a value JSON cannot carry.
    private static float Ratio(int numerator, int denominator) =>
        denominator == 0 ? 0f : (float)numerator / denominator;

    // Mean coverage byte over every baked grid on the map. Zero when nothing has baked yet, which
    // reads as "no coverage" and fails a pin rather than dividing by nothing.
    private static float CoverageMean(Map map)
    {
        if (map == null)
            return 0f;

        double total = 0.0;
        long cells = 0;

        foreach (VectorLightField.LightEntry entry in VectorLightField.LightsFor(map))
        {
            if (entry?.Coverage != null)
            {
                for (int i = 0; i < entry.Coverage.Length; i++)
                    total += entry.Coverage[i];

                cells += entry.Coverage.Length;
            }
        }

        return cells == 0 ? 0f : (float)(total / cells);
    }

    // Cells any emitter calls FULLY lit. An integer small enough to be exact as a float, and the
    // number the nearest-ray fast path decides directly.
    private static float LitCells(Map map)
    {
        if (map == null)
            return 0f;

        int lit = 0;

        foreach (VectorLightField.LightEntry entry in VectorLightField.LightsFor(map))
        {
            if (entry?.Coverage != null)
            {
                for (int i = 0; i < entry.Coverage.Length; i++)
                {
                    if (entry.Coverage[i] == 255)
                        lit++;
                }
            }
        }

        return lit;
    }

    private static float EmitterCount(Map map)
    {
        if (map == null)
            return 0f;

        int count = 0;

        foreach (VectorLightField.LightEntry entry in VectorLightField.LightsFor(map))
        {
            // Referenced so the loop is not optimised into a plain Count, which would read the
            // dictionary without going through LightsFor's roster resync — and a stale roster is one
            // of the things this probe exists to catch.
            if (entry != null)
                count++;
        }

        return count;
    }
}
