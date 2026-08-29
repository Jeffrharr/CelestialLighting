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
        // range (issue #188 item B). Read BESIDE Bakes and never alone. It counts ATTEMPTS: the cull
        // runs every frame, so one emitter left dirty across a four-frame lapse charges four. A
        // deferral is postponed work, not an error.
        Deferrals,

        // ---- the threaded bake ------------------------------------------------------------------
        //
        // Bake passes handed out across threads, and passes that ran on the calling thread because
        // the batch was under the threshold. MEANINGLESS APART. Most frames bake nothing, and a
        // frame that bakes one emitter is supposed to stay serial, so a fan-out count of zero is the
        // correct answer for almost every scenario in the repo — it separates "the flag is off" from
        // "nothing worth threading happened" only when the serial count is read next to it.
        ParallelBakePasses,
        SerialBakePasses,

        // The largest batch either path was handed. The one number that says whether a scenario
        // exercised the threaded path with enough work to mean anything: a fan-out over a batch of
        // four has taken the branch without testing the design, and pinning this is what stops an
        // arm quietly degrading into that.
        LargestBakeBatch,

        // Wall-clock milliseconds the calling thread spent baking. THE ONLY METRIC HERE THAT CAN
        // SCORE THE THREADED PATH, because the Circinus arms report time exclusive of their armed
        // children and threading moves time rather than removing it. See VectorLightField.BakeWallMs.
        BakeWallMs,

        // ---- the silhouette memo (issue #188 item C) ---------------------------------------------

        // Gathers that reused a recorded silhouette, and gathers that had to rescan the window.
        //
        // MEANINGLESS APART, in the same way the two bake-pass counts are. A hit count alone cannot
        // separate a working memo from a scene where nothing ever asked twice, and a rebuild count
        // alone cannot separate a memo that never helps from one that is being correctly refused
        // because walls really are going up. Their SUM is the number of times an occluder set was
        // assembled at all, which is what makes each of them a share rather than a raw tally.
        SilhouetteHits,
        SilhouetteRebuilds,

        // Wall-clock milliseconds the calling thread spent in the GATHER — reading each emitter's
        // occluder set off the map — as opposed to baking a polygon out of it.
        //
        // THE ONLY METRIC HERE THAT CAN SCORE THE MEMO, and for the mirror image of the reason
        // BakeWallMs is the only one that can score threading. BakeWallMs starts after the gather on
        // purpose, so it is blind to this change by construction; this one stops before the bake, so
        // it is blind to threading. Read both, and whichever half a change did not touch is a
        // control on the run rather than a second opinion about it.
        GatherWallMs,

        // Wall-clock milliseconds handing built geometry to Unity: the mesh channels and the
        // per-emitter glow texture. THE THIRD OF THE FRAME NEITHER CLOCK ABOVE CAN SEE, and the
        // third that cannot be threaded, because every call in it is a Unity object write.
        UploadWallMs,
        UploadMeshWallMs,
        UploadFieldWallMs,
        FieldTextureUploads,
        FieldUvOnlyUploads,

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

        // ---- the dirty suppression ---------------------------------------------------------------
        //
        // MapMeshDirty calls declined inside a glow-blocker write, and the distinct sections those
        // calls would have flagged. BOTH, because they answer different questions and the RATIO
        // between them is the point: the call count says how often a door swing provokes vanilla, the
        // section count says what that provocation costs, and the quotient is the fan-out that turned
        // a few hundred writes into thousands of regenerates. An afternoon of reading the code could
        // not account for that multiplier; this counts it instead of arguing about it.
        //
        // Zero is the correct reading with vector_light_door_dirty_suppress off, which is also what a
        // scenario that never swung a door reports — so these two are only meaningful beside
        // SectionDirties and MaskApplies, never on their own.
        SuppressedDirtyCalls,
        SuppressedDirtySections,

        // Sections that baked without an emitter reaching them, because it had no polygon at all.
        // A DEFECT COUNT rather than a workload one: nonzero after the scene has settled means a
        // frame rendered with a shadow missing. See VectorLightField.MaskSkipsNoPolygon.
        MaskSkipsNoPolygon,

        // Sections that baked from an emitter's previous polygon rather than dropping it. The
        // fallback working; read beside the skip count, which alone cannot tell a fixed subsystem
        // from a scenario that never provoked a rebuild.
        MaskStalePolygonUses,

        // Bakes whose coverage grid came out byte-identical to the one it replaced, so no section
        // was dirtied for them. THE SPURIOUS-INVALIDATION COUNT, and the number the changed-dirty
        // feature is scored on.
        //
        // READ AS A RATIO AGAINST Bakes, never alone, for the reason every pair in this enum is:
        // zero means either that every invalidation was real or that the comparison never ran, and
        // those are opposite findings. Under the flag turned off it is zero by construction, which
        // is what makes the off arm a baseline rather than a picture of the feature missing.
        UnchangedBakes,

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

        // How many coverage-grid CELLS are allocated across every emitter on the map — the size of
        // the bake rather than what is in it.
        //
        // WHY IT IS SEPARATE FROM LitCells, which is the mistake it exists to stop somebody making
        // twice. LitCells counts bytes equal to 255, so it moves when the grid SATURATES as well as
        // when the grid GROWS, and those are different facts: lamp glow reach pushes the polygon
        // past vanilla's rim, which fills in the partly-covered discretisation cells there and
        // raises LitCells without allocating one extra byte. Read alone it says a grid grew when it
        // did not. This one is the array length and nothing else, so "the coverage bake does not
        // scale with reach" is a claim it can settle on its own.
        CoverageCells,

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

        if (metric == Metric.BakeWallMs)
            return (float)VectorLightField.BakeWallMs;

        if (metric == Metric.SilhouetteHits)
            return VectorLightBlockers.SilhouetteHits;

        if (metric == Metric.SilhouetteRebuilds)
            return VectorLightBlockers.SilhouetteRebuilds;

        if (metric == Metric.GatherWallMs)
            return (float)VectorLightField.GatherWallMs;

        if (metric == Metric.UploadWallMs)
            return (float)VectorLightField.UploadWallMs;

        if (metric == Metric.UploadMeshWallMs)
            return (float)VectorLightField.UploadMeshWallMs;

        if (metric == Metric.UploadFieldWallMs)
            return (float)VectorLightField.UploadFieldWallMs;

        if (metric == Metric.FieldTextureUploads)
            return VectorLightField.FieldTextureUploads;

        if (metric == Metric.FieldUvOnlyUploads)
            return VectorLightField.FieldUvOnlyUploads;

        if (metric == Metric.ParallelBakePasses)
            return VectorLightField.ParallelBakePasses;

        if (metric == Metric.SerialBakePasses)
            return VectorLightField.SerialBakePasses;

        if (metric == Metric.LargestBakeBatch)
            return VectorLightField.LargestBakeBatch;

        if (metric == Metric.SectionDirties)
            return VectorLightField.SectionDirties;

        if (metric == Metric.SectionDirtyPasses)
            return VectorLightField.SectionDirtyPasses;

        if (metric == Metric.SectionsPerPass)
            return Ratio(VectorLightField.SectionDirties, VectorLightField.SectionDirtyPasses);

        if (metric == Metric.MaskApplies)
            return VectorLightField.MaskApplies;

        if (metric == Metric.MaskSkipsNoPolygon)
            return VectorLightField.MaskSkipsNoPolygon;

        if (metric == Metric.MaskStalePolygonUses)
            return VectorLightField.MaskStalePolygonUses;

        if (metric == Metric.UnchangedBakes)
            return VectorLightField.UnchangedBakes;

        if (metric == Metric.CoverageMean)
            return CoverageMean(map);

        if (metric == Metric.LitCells)
            return LitCells(map);

        if (metric == Metric.CoverageCells)
            return CoverageCells(map);

        if (metric == Metric.SuppressedDirtyCalls)
        {
            return GlowDirtyScope.SuppressedCalls;
        }

        if (metric == Metric.SuppressedDirtySections)
        {
            return GlowDirtyScope.SuppressedSections;
        }

        if (metric == Metric.Reset)
        {
            VectorLightField.ResetCounters();

            // Drained by the SAME probe call as the field's own counters, deliberately. These are
            // read per arm beside vector_light_section_dirties, and a pair of counters that reset at
            // two different moments would let one arm's storm leak into the next one's reading —
            // which is the exact defect stress_door_colony's per-arm reset was added to fix.
            GlowDirtyScope.ResetCounters();
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

    // Total allocated coverage grid, in cells, over every emitter. See Metric.CoverageCells for why
    // this is not LitCells with a different predicate.
    private static float CoverageCells(Map map)
    {
        if (map == null)
            return 0f;

        int cells = 0;

        foreach (VectorLightField.LightEntry entry in VectorLightField.LightsFor(map))
        {
            if (entry?.Coverage != null)
                cells += entry.Coverage.Length;
        }

        return cells;
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
