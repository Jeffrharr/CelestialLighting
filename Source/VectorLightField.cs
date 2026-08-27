using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// §27's per-map registry: what is emitting light, and the mesh each emitter currently casts.
//
// TWO KINDS OF STALENESS, KEPT SEPARATE ON PURPOSE. A light's IDENTITY can change — one is built,
// switched off, refuelled, recoloured — and its GEOMETRY can change under a light that never moved,
// because somebody built a wall inside its radius. Collapsing those into one dirty flag means every
// lamp toggle anywhere rebakes every polygon on the map, which is precisely the shape §16 records
// killing the across-map tilt ramp over. So a glower registration marks the ROSTER dirty and a
// blocker write marks only the polygons that cell can reach.
//
// NO TIMER, NO POLL, NO SELF-SCHEDULED WORK. Issue #48 states this outright — "if the design ends up
// needing a timer, it is the wrong design" — and §16 has the measurement behind it:
// MapComponent_SunShadowAxis cost only +3.4 microseconds per regenerate and still dominated the live
// profile, purely by provoking ~720 whole-map rebakes a game day. Everything here is invalidated by
// something the player did.
//
// Cached per map.uniqueID rather than in a MapComponent, following OpenSkyMask: Map.ExposeComponents
// scribes a permanent node per component, so a component deleted later logs two red errors per map
// forever (Source/MapComponent_SunShadowAxis.cs is the tombstone). For a prototype that may not
// survive its own live A/B, leaving no save-file residue is the only responsible choice.
public static class VectorLightField
{
    // One emitter and the polygon it currently throws. Position, radius and colour are snapshots,
    // re-read on resync — the same thing Verse.Glow.GlowLight does, and for the same reason: a light
    // that moves or recolours goes through deregister/reregister in vanilla, so a snapshot cannot go
    // stale without the roster being marked dirty anyway.
    public sealed class LightEntry
    {
        public IntVec3 Cell;
        public float Radius;
        public Color Color;
        public Mesh Mesh;
        public MaterialPropertyBlock Props;
        public bool GeometryDirty = true;

        // How vanilla's own GlowGrid identifies this same emitter — a thing id for a glower, a cell
        // index for glowing terrain, with the terrain flag folded in because the two number spaces
        // overlap. §27 phase 3 needs it to ask what THIS light delivers to a cell, as opposed to what
        // everything delivers, which is the difference between subtracting our own lamp back out and
        // subtracting somebody else's mod along with it.
        public long VanillaKey;

        // The visibility polygon, cached so the two consumers can share one build. §27 phase 3 reads
        // it during a section regenerate, where no mesh is being built at all, so it cannot ride on
        // the mesh the way it used to.
        public VectorLightMath.LightPolygon Polygon;

        // Kept separate from GeometryDirty rather than folded into it because the two are consumed
        // by different subsystems at different times: the draw clears GeometryDirty when it rebuilds
        // a mesh, and a mask running with the draw switched off would then never rebuild the polygon
        // at all. Both are set together wherever the world changes.
        public bool PolygonDirty = true;

        // The polygon's cell coverage, baked with it. See VectorLightMath.BuildCoverage: computing
        // this per section instead measured 239 us per section against the crossfade's 20.
        public byte[] Coverage;

        // Radius the coverage grid was baked at, in cells. Held rather than recomputed because the
        // lookup needs it and Mathf.CeilToInt on every cell of every section is exactly the sort of
        // per-cell arithmetic this cache exists to remove.
        public int CoverageRadius;

        // Whether this emitter shadows anything at all — no ray stopped short of the radius. The
        // bake skips such an emitter outright rather than looking its grid up cell by cell.
        public bool Unobstructed;

        // The mesh as the pure core built it, kept rather than discarded after upload. Phase 6
        // needs the vertex POSITIONS again after the fact — to resample vanilla's glow when the
        // lighting around this light changed but its geometry did not — and reading them back off
        // the Unity Mesh allocates a fresh array every time it is asked.
        public VectorLightMath.LightMesh Built;

        // A THIRD KIND OF STALENESS, and the reason it is not folded into GeometryDirty. Under the
        // per-fragment max each vertex carries vanilla's delivered glow at that point, and that
        // value moves whenever any OTHER light near this one changes — a lamp switched on across
        // the room leaves this light's polygon identical and its samples wrong. Reusing
        // GeometryDirty for it would rebake the polygon too, which is exactly the
        // every-toggle-rebakes-everything cost this class exists to avoid; resampling on its own
        // rewrites one UV channel and no geometry.
        public bool SampleDirty = true;

        // Vanilla's delivered glow over this emitter's own square, one texel per cell, for the
        // fragment program to look up per fragment. Per emitter and so cannot live on the shared
        // per-radius material; see VectorLightShader.SetVanillaTexture.
        public Texture2D VanillaField;

        // The polygon's area in square cells, kept for the probes: it is the one number that says
        // "the lit region changed shape" without going anywhere near a pixel. Issue #3 records two
        // wrong conclusions drawn from pixel measurement on exactly this kind of effect.
        public float LitArea;

        // A FOURTH KIND OF STALENESS, and the one that is deliberately NOT provoked by the other
        // three. The whole-cell occluder silhouette inside this light's window changes only when a
        // blocker is built or removed; a door sliding through its eight quantisation steps dirties
        // the polygon eight more times and leaves the silhouette alone. Issue #188 item C is that
        // observation, and this is where the answer is kept between bakes.
        //
        // Allocated lazily on the first gather rather than with the entry, so an emitter that never
        // bakes — off screen behind the view cull, or on a map nobody is looking at — costs nothing
        // but the null. VectorLightBlockers owns everything in it; nothing here reads it.
        public VectorLightSilhouetteMath.Memo Silhouette;

        // A FIFTH KIND OF STALENESS, split off SampleDirty for the same reason the silhouette was
        // split off PolygonDirty: the two things it stood for do not change together and one of them
        // is far more expensive than the other.
        //
        // UV1 carries where each vertex sits inside the emitter's square, so it goes stale when OUR
        // geometry moves — and Mesh.Clear wipes the channel on every rebuild regardless. SampleDirty
        // means the TEXTURE those coordinates index is stale, which happens when vanilla's glow moves
        // and not otherwise. A door sliding moves our vertices nine times and vanilla's glow not at
        // all, because RimWorld's glow grid never learns a door opened.
        public bool FieldUvsDirty = true;
    }

    private sealed class MapLights
    {
        public readonly Dictionary<object, LightEntry> Entries = new Dictionary<object, LightEntry>();
        public bool RosterDirty = true;
    }

    private static readonly Dictionary<int, MapLights> ByMap = new Dictionary<int, MapLights>();

    // ---- bake accounting ---------------------------------------------------------------------
    //
    // COUNTERS, NOT TIMERS, and the distinction is the whole reason these exist. A timing probe
    // measures one call and never asks how often it happens, so a change that halves the cost of a
    // bake and doubles how often one is provoked reads as a straight win. The door-aperture work hit
    // exactly this and answered it with a counter (GameComponent_DoorAperture.DirtyRequests); this is
    // the same instrument pointed at the field as a whole.
    //
    // WHY THEY CANNOT BE A PROBE THAT RECOMPUTES. The obvious alternative is to have the harness ask
    // for a polygon and time it, and VectorLightProbe already does rebuild one to answer questions
    // about shape. That is useless here: it produces a fresh, correct answer whether or not the cache
    // was consulted, so a memoisation would measure as working while doing nothing at all. Only state
    // the bake itself writes can distinguish "hit" from "recomputed".
    //
    // Live in shipped code rather than behind the probe compile flag because the increments are on
    // the paths being measured; an int++ on a path that is about to run a visibility polygon is not a
    // cost worth a conditional.

    // Polygons actually rebuilt. The numerator of the whole phase.
    public static int PolygonBakes;

    // Rebuilds skipped because the polygon was still clean — the dirty flag doing its job. Read
    // beside PolygonBakes: bakes alone cannot tell a well-cached field from one nobody asked.
    public static int PolygonHits;

    // Segments handed to the visibility polygon, summed over every bake. Says what POPULATION was
    // measured, which a bake count cannot: the cull's gain scales with clutter, so a scenario
    // reporting a healthy bake count over eight segments per bake has verified nothing about it.
    public static int BakeSegments;

    // How many times a blocker write asked the field to invalidate, and how many emitters that
    // actually dirtied. The ratio IS the invalidation radius, measured rather than reasoned about —
    // the epic names MarkGeometryDirtyAround as the thing that turns one toggle into a map-wide
    // rebake, and these two numbers are what settle whether it does.
    public static int InvalidationCalls;
    public static int InvalidationMarks;

    // Roster resyncs against vanilla's glower sets. A lamp toggle costs one; a per-frame resync
    // would mean something is marking the roster dirty on a cadence rather than on an event.
    public static int RosterResyncs;

    // Builds the view cull declined — a dirty polygon out of camera range, left dirty (issue #188
    // item B). Read BESIDE PolygonBakes rather than alone, because on its own it cannot tell a
    // working cull from a scene where every lamp happens to be off screen.
    //
    // IT COUNTS ATTEMPTS, NOT EMITTERS, and the two are easy to confuse. The cull is re-evaluated
    // every frame, so one emitter left dirty while the camera looks elsewhere charges one deferral
    // PER FRAME — the number is a backlog-times-duration, not a population. So bakes and deferrals
    // do not trade one for one, and a scenario cannot assert that their sum is constant; what it
    // can assert is that the emitter baked in one arm and did not in the other.
    //
    // NOT AN ERROR COUNT. A deferral is work correctly postponed, and the emitter is built on the
    // frame it comes back into range. A deferral that never resolves would show as a light with no
    // shadow, which vector_light_lit_area sees.
    public static int PolygonDeferrals;

    // ---- section accounting (issue #188 item 0) -----------------------------------------------
    //
    // THE NUMBER NOTHING IN THIS REPO COULD SEE. Every counter above is about POLYGONS, and #191
    // used them to establish that a blocker write dirties one or two emitters out of twenty-three —
    // then headed the finding "one wall does not rebake the map". True of polygons; the sections
    // were the other half, and that scenario's fifteen probes could not watch one regenerate. So the
    // map-wide re-dirty went unmeasured in the very run that exonerated its neighbour.
    //
    // WHY THEY LIVE HERE AND NOT ON VectorLightMask, where Apply increments one of them from. There
    // are two reset paths in this subsystem — VectorLightField.ResetCounters, which the bake_reset
    // probe calls, and VectorLightMask.ResetTelemetry, which ForceRebuild calls — and a counter
    // split across both drains at different moments in an arm. One home means one reset and no way
    // for two halves of the same measurement to disagree about which arm they belong to.

    // Sections flagged dirty, summed. Both arms charge themselves: the narrow path counts what it
    // actually flags, the WholeMapChanged path counts the map's whole section count, because
    // vanilla dirties them all without ever counting them and a baseline of zero measures nothing.
    public static int SectionDirties;

    // Frames on which at least one section was flagged. The denominator, and on its own the answer
    // to "is something dirtying on a cadence rather than on an event".
    public static int SectionDirtyPasses;

    // Calls into VectorLightMask.Apply, i.e. lighting-overlay section regenerates that actually
    // reached us. THE OUTCOME MEASURE, and the one that needs no special handling per arm: it counts
    // work done rather than work requested, so it is directly comparable between the two paths and
    // between two builds. A flag can halve the flags raised and leave this unchanged, and that would
    // mean the saving was on sections nobody was looking at anyway.
    public static int MaskApplies;

    // Sections that baked WITHOUT an emitter reaching them, because it had no polygon at all yet.
    // THE DEFECT COUNT: every one is a section that rendered a frame with a shadow missing, and --
    // because VectorLightMask.Apply returns true having collected nothing -- with vanilla's flood
    // left unsuppressed as well, so the room reads brighter than its settled state rather than
    // darker. Zero once a scene has settled.
    //
    // It is not the same thing as PolygonDeferrals. A deferral is an emitter the VIEW CULL declined
    // to build because nobody is looking at it, which costs nothing on screen. This counts the case
    // where somebody IS looking.
    //
    // A nonzero reading during map load is expected and meaningless: nothing has been baked yet.
    // Reset the counters after the scene settles and read it across the window that matters.
    public static int MaskSkipsNoPolygon;

    // Sections that baked from an emitter's PREVIOUS polygon because the current one had been
    // marked dirty and the rebuild had not run yet. THE FIX WORKING, not a defect -- see
    // VectorLightMask.CollectReaching for why a stale shape beats a dropped one by so much.
    //
    // Worth reading beside the skip count rather than alone. On its own it cannot tell a scenario
    // that exercised the fallback from one that never provoked a rebuild at all, and a regression
    // test asserting only that skips are zero would pass just as happily against a scene where
    // nothing ever moved.
    public static int MaskStalePolygonUses;

    // Cleared per arm, the way the door-aperture counter is, so an arm counts its own bakes from zero
    // instead of inheriting the previous arm's total.

    // Cleared per arm, the way the door-aperture counter is, so an arm counts its own bakes from zero
    // instead of inheriting the previous arm's total.
    public static void ResetCounters()
    {
        PolygonBakes = 0;
        PolygonHits = 0;
        BakeSegments = 0;
        InvalidationCalls = 0;
        InvalidationMarks = 0;
        RosterResyncs = 0;
        PolygonDeferrals = 0;
        ParallelBakePasses = 0;
        SerialBakePasses = 0;
        LargestBakeBatch = 0;
        BakeWallMs = 0.0;
        GatherWallMs = 0.0;
        UploadMeshWallMs = 0.0;
        UploadFieldWallMs = 0.0;
        FieldTextureUploads = 0;
        FieldUvOnlyUploads = 0;

        // Lives on VectorLightBlockers because that is what increments it, and is drained from here
        // because there is one reset path per arm and a counter that drains on a different schedule
        // from its neighbours is a counter that will one day belong to the previous arm.
        VectorLightBlockers.ResetCounters();
        SectionDirties = 0;
        SectionDirtyPasses = 0;
        MaskApplies = 0;
        MaskSkipsNoPolygon = 0;
        MaskStalePolygonUses = 0;
    }

    public static void MarkRosterDirty(Map map)
    {
        if (map == null || !ByMap.TryGetValue(map.uniqueID, out MapLights lights))
            return;

        lights.RosterDirty = true;

        // A light was built, removed, recoloured or switched — so vanilla's glow has moved under
        // every light that can see the same cells, and their samples are stale even though their
        // polygons are not. Marking all of them is deliberately blunt: the roster changing is rare,
        // resampling is a UV rewrite rather than a rebake, and working out which lights overlap the
        // changed one would need the position of a light that may already be gone.
        foreach (LightEntry entry in lights.Entries.Values)
            entry.SampleDirty = true;
    }

    // Something at `cell` changed the shape every light that can see it throws, and no other light
    // is affected at all.
    //
    // `blockerMoved` SEPARATES THE TWO KINDS OF CHANGE THAT REACH HERE, which is issue #188 item C.
    // A wall built or mined rewrites the whole-cell occluder grid; a door sliding through one of its
    // quantisation steps does not, because a part-open door is a hole in that grid whatever step it
    // is on. Both dirty the polygon, and only the first can invalidate a recorded silhouette — so
    // collapsing them would rescan every window nine times a swing and leave nothing to reuse, which
    // is exactly the cost the memo exists to remove.
    //
    // NO DEFAULT ON THE PARAMETER, deliberately. A caller that has not thought about which kind of
    // change it is carrying is the one way this goes quietly wrong: passing true where false belongs
    // costs performance and nothing else, passing false where true belongs holds a stale wall, and a
    // default would pick one of those silently for whoever adds the next call site.
    public static void MarkGeometryDirtyAround(Map map, IntVec3 cell, bool blockerMoved)
    {
        if (map == null || !ByMap.TryGetValue(map.uniqueID, out MapLights lights))
            return;

        InvalidationCalls++;

        foreach (LightEntry entry in lights.Entries.Values)
        {
            // Squared distance against squared radius, so a wall built across the map costs one
            // multiply per light rather than a square root.
            float dx = entry.Cell.x - cell.x;
            float dz = entry.Cell.z - cell.z;
            float reach = entry.Radius + 1f;

            if (dx * dx + dz * dz <= reach * reach)
            {
                entry.GeometryDirty = true;
                entry.PolygonDirty = true;
                InvalidationMarks++;

                // A wall appearing or vanishing also rewrites vanilla's geodesic distances through
                // that cell, so the samples go with the geometry. A rebuild resamples anyway; this
                // is for the case where the rebuild is skipped because the light is off-screen.
                //
                // ONLY WHEN A BLOCKER MOVED, which is the same distinction the silhouette memo
                // needs and the reason one parameter serves both. A door sliding does not rewrite
                // vanilla's distances: its glow grid is not told, and the shipped mod never tells it.
                //
                // IT IS TOLD UNDER vector_light_door_glow_blocker, and that case routes itself
                // correctly with no special handling here. That flag makes VectorLightDoorEvents
                // call glowGrid.LightBlockerAdded/Removed, which Patch_VectorLightBlockerAdded and
                // its sibling postfix — so the write arrives back through BlockerChanged with
                // blockerMoved true, and the samples are invalidated because they really did move.
                if (blockerMoved)
                    entry.SampleDirty = true;

                if (blockerMoved && entry.Silhouette != null)
                    entry.Silhouette.Invalidate();
            }
        }
    }

    // Everything currently emitting on this map, resynced from vanilla's own sets if anything has
    // registered or deregistered since the last call.
    // Build every dirty polygon on this map, once per frame, OUTSIDE the section bake.
    //
    // WHY IT IS HOISTED. §27 phase 3 reads polygons during a section regenerate, and building one
    // there put geometry construction inside the bake: a whole-map rebake measured 49 ms in
    // VectorLightMask.Apply while everything Apply calls summed to 6, and the missing 43 was
    // EnsurePolygon running under CollectReaching. The crossfade builds the same polygons in the
    // DRAW path, so its own bake row never contained them — which made the two rows a comparison
    // between different quantities rather than between two implementations.
    //
    // Called once per frame from the draw, so by the time any section bakes, every polygon it might
    // ask for is already there. The work is not removed — it is the same builds on the same cadence
    // — it simply stops being charged to, and serialised inside, the regenerate.
    //
    // A SECTION BAKED WHILE A POLYGON WAS STILL DIRTY SKIPPED THAT EMITTER, and nothing would ever
    // dirty the section again — so "the mask catches up next frame" was permanently false and the
    // feature rendered pixel-identical to vanilla with every probe healthy. Whoever builds the
    // polygons has to re-dirty the map afterwards, once, so the sections bake again with them ready.
    //
    // RETURNS WHERE, NOT WHETHER, and that is issue #188 item A. This used to answer a bool and the
    // caller turned it into WholeMapChanged, which regenerates every section under the camera
    // whatever changed and wherever it was — so a door opening across the colony rebaked the
    // lighting overlay under the player's cursor, nine times per swing. The union of the rebuilt
    // emitters' reach is enough for the caller to dirty only the sections that can actually look
    // different, and it costs one struct per frame to carry.
    // BUILDS ONLY WHAT CAN REACH `within`, which is issue #188 item B. An emitter outside it keeps
    // its dirty flag and is built on the first frame it comes back into range — the caller passes
    // the camera's view, so that is the frame it scrolls on screen. Nothing is lost by waiting,
    // because the only consumers of a polygon are the draw and the mask and neither runs for a
    // section nobody is looking at.
    //
    // WHY THIS IS SAFE ONLY BECAUSE OF ITEM A. The header above records the bug where a section
    // baked while a polygon was still dirty, skipped that emitter, and was never dirtied again. A
    // view cull creates exactly that state deliberately and en masse, so it depends on the caller
    // re-dirtying whatever it builds — which is what the returned bounds are for. The two changes
    // are separable in the diff and not in the design.
    //
    // The caller passes SectionDirtyMath.WholeMap to cull nothing, which is how the flag turned off
    // reproduces the previous behaviour exactly rather than approximately.
    public static SectionDirtyMath.CellBounds EnsurePolygons(
        Map map, SectionDirtyMath.CellBounds within)
    {
        if (map == null)
            return default;

        SectionDirtyMath.CellBounds touched = default;

        BakeBatch.Clear();

        foreach (LightEntry entry in LightsFor(map))
        {
            if (entry.PolygonDirty || entry.Polygon.Count == 0)
            {
                // Accumulated from the emitter's REACH rather than its cell: the polygon that is
                // about to change shape is what the mask subtracts across the emitter's whole
                // square, so the sections that need rebaking are every one that square touches, not
                // the one the lamp happens to stand in. VectorLightMask.ReachMargin keeps that in
                // step with the predicate CollectReaching admits emitters by.
                SectionDirtyMath.CellBounds reach = SectionDirtyMath.Reach(
                    entry.Cell.x, entry.Cell.z, entry.Radius, VectorLightMask.ReachMargin);

                // Computed BEFORE the build rather than after, because it is also the cull test and
                // an emitter that fails it must not be built at all. Deferring the build leaves
                // PolygonDirty set, which is the entire mechanism — there is no separate queue.
                if (SectionDirtyMath.Intersects(reach, within))
                {
                    // COLLECTED RATHER THAN BUILT HERE, so the bake can be handed out across
                    // threads. The selection walks a dictionary and the bake does not, which is the
                    // whole reason the two are now separate passes — see BakeSelected.
                    BakeBatch.Add(entry);
                    touched = SectionDirtyMath.Union(touched, reach);
                }
                else
                {
                    PolygonDeferrals++;
                }
            }
        }

        BakeSelected(map, BakeBatch);

        return touched;
    }

    // This frame's selected emitters and the segment window each was gathered with.
    //
    // STATIC AND REUSED, which is safe for the same reason the batch can be threaded at all:
    // EnsurePolygons is called once per frame from the draw, on the main thread, and returns before
    // anything else can call it. Nothing here survives the call — the list is cleared on entry and
    // the segment slots are released on exit.
    private static readonly List<LightEntry> BakeBatch = new List<LightEntry>();
    private static VectorLightMath.Segment[][] BatchSegments = new VectorLightMath.Segment[0][];

    // How many bake passes were handed out across threads, and how many ran on the calling thread
    // because the batch was too small to be worth it. Read as a pair: fan-outs alone cannot tell a
    // working threshold from a scene that never bakes more than one emitter at a time, which is
    // most frames.
    public static int ParallelBakePasses;
    public static int SerialBakePasses;

    // Wall-clock milliseconds the CALLING THREAD spent inside the bake, summed over passes.
    //
    // WHY THIS EXISTS WHEN THERE IS ALREADY A CIRCINUS ARM ON EVERY STAGE. Those arms report time
    // EXCLUSIVE of their armed children — measured, not assumed: circ_vlbuilddirty reads 1.3-1.5 ms
    // in a window where the Build and BuildCoverage arms inside it read 6-9 ms each. That makes them
    // blind to this change by construction. Threading does not reduce the total time the stages
    // spend, it moves that time onto other threads; what it reduces is how long the main thread
    // waits, and no arm in the bank measures a wait.
    //
    // MEASURED FROM THE CALLING THREAD ONLY, which is the whole point. The serial path accumulates
    // every bake because it runs them; the threaded path accumulates the fan-out and the join, and
    // the workers' own time lands nowhere. That is the comparison a player experiences as a frame.
    //
    // A double, and summed rather than averaged, because passes per window vary with what got dirty
    // and a mean would hide a single catastrophic frame — the one thing a rolling-refresh design
    // exists to avoid.
    public static double BakeWallMs;

    // The calling thread's time in the GATHER — reading each emitter's occluder set off the map —
    // over the same window, and the number the silhouette memo moves.
    //
    // WHY IT IS A SECOND CLOCK RATHER THAN A WIDER ONE. BakeWallMs above deliberately starts after
    // the gather, because the gather was identical work in both of ITS arms and including it would
    // have diluted a threading ratio with a constant. The memo is the mirror image: it removes work
    // from the gather and changes nothing about the bake, so it is invisible to that clock for
    // exactly the same reason. Two clocks over two halves, and a change to either half is scored by
    // the half it touched while the other stands as a control.
    //
    // Summed, not averaged, for the reason above: what a player feels is the frame that rescanned
    // twenty-two windows at once, not the mean over the four hundred frames that rescanned none.
    public static double GatherWallMs;

    // The THIRD half of the same frame, and the one neither clock above can reach: handing the built
    // geometry to Unity. Mesh.SetVertices / SetUVs / SetTriangles, and the per-emitter glow texture's
    // GetRawTextureData copy and Apply.
    //
    // WHY IT IS WORTH A CLOCK OF ITS OWN. Threading the bake took the calling thread's bake time from
    // 403 ms to 72 and did not move the worst frame at all, which is only explicable if the worst
    // frame is somewhere else. The candidate named at the time was mesh upload: a rebuild drops every
    // mesh, and Mesh.SetVertices is a main-thread API no amount of threading touches. That was an
    // inference from a number that did not move, which is the weakest kind of evidence there is --
    // consistent with upload being expensive, and equally consistent with the cost being anywhere
    // else in the draw. This measures it instead.
    //
    // NOT THREADABLE, and that is the point of separating it rather than folding it into the bake.
    // Every call inside it is a Unity object write; there is no version of this that moves to a pool
    // thread. If it turns out to be the expensive half, the answer has to be doing less of it.
    //
    // SPLIT IN TWO, because "upload" is two different APIs with two different costs and optimising
    // the wrong one is the documented way to waste a day here: a mesh channel write is a managed
    // list copied into native memory, and a texture Apply is a GPU transfer. A single total would
    // have said which third of the frame to look at and nothing about what to do when you got there.
    public static double UploadMeshWallMs;
    public static double UploadFieldWallMs;

    // The two ways a glow-field refresh can end: the texture refilled and pushed to the GPU, or only
    // UV1 rewritten because the texture was already right.
    //
    // A RATIO, LIKE THE SILHOUETTE COUNTERS, and for the same reason: the duration above cannot say
    // whether it fell because the work got cheaper or because a scene happened to ask for less of
    // it. Their sum is how many field refreshes happened at all.
    public static int FieldTextureUploads;
    public static int FieldUvOnlyUploads;

    public static double UploadWallMs => UploadMeshWallMs + UploadFieldWallMs;

    // The largest batch either path has been handed. The number that says whether a scenario
    // exercised the threaded path at all, and by how much — a fan-out count of 1 over a batch of 4
    // has verified almost nothing about a design meant for a whole-map rebake.
    public static int LargestBakeBatch;

    // Below this many emitters the batch is baked on the calling thread.
    //
    // A FAN-OUT IS NOT FREE: Parallel.For has to wake pool threads, hand out ranges and join, which
    // is tens of microseconds against a bake's ~0.35 ms. That is a fine trade on the frame a map
    // loads or a wall goes up in a lit room, and a bad one in the steady state, where the dirty flag
    // does its job and a frame bakes nothing at all or one emitter after a door swings.
    //
    // FOUR RATHER THAN TWO because the win has to clear the join as well as pay for it, and rather
    // than sixteen because the frames worth rescuing are the ones that bake a room's worth of lamps
    // at once, not only the whole-map rebake. It is deliberately not a setting: a threshold nobody
    // can measure the effect of is a knob that only generates support questions.
    private const int ParallelBakeMinimum = 4;

    // The coverage bake's working arrays, one set per thread that ever bakes.
    //
    // THREAD-LOCAL RATHER THAN ONE STATIC, and this is load-bearing rather than defensive. Two
    // emitters baking concurrently through one scratch would interleave their writes into the same
    // column and row arrays, and the result is not a crash — it is a coverage grid with a few wrong
    // bytes, which renders as one cell of one shadow at the wrong depth. No probe in the repo pins
    // that and no CIELAB comparison separates it from noise, so it is exactly the class of defect
    // that ships. The buffers are a few kilobytes per worker and the pool reuses its threads.
    //
    // See VectorLightMath.CoverageScratch for why the pure core takes this as a parameter rather
    // than keeping a static of its own — that decision is what makes this one a local matter here
    // instead of a rewrite there.
    [System.ThreadStatic]
    private static VectorLightMath.CoverageScratch ThreadScratch;

    private static VectorLightMath.CoverageScratch Scratch
    {
        get { return ThreadScratch ?? (ThreadScratch = new VectorLightMath.CoverageScratch()); }
    }

    // Bake every selected emitter, across threads when there are enough of them to pay for it.
    //
    // THE SPLIT IS BETWEEN LIVE STATE AND ARITHMETIC, and it is the only reason this is safe.
    // Gathering the silhouette reads the map — the edifice grid, door state, thing positions — and
    // that happens here, serially, on the main thread. Everything after it is arithmetic over a
    // Segment[] and writes only to the entry it was handed, so two bakes cannot observe each other.
    //
    // WHY THE MAIN THREAD BEING BLOCKED IS THE ARGUMENT. RimWorld ticks and draws on one thread, and
    // this runs inside the draw via Patch_VectorLightDraw. While the join is outstanding the main
    // thread is inside Parallel.For and therefore not ticking, so nothing can spawn, despawn, open a
    // door or move a wall underneath a worker. That is a property of WHERE this is called from, not
    // of this method, which is why moving the call is a threading change and not a refactor.
    private static void BakeSelected(Map map, List<LightEntry> batch)
    {
        if (batch.Count == 0)
            return;

        if (LargestBakeBatch < batch.Count)
            LargestBakeBatch = batch.Count;

        if (BatchSegments.Length < batch.Count)
            BatchSegments = new VectorLightMath.Segment[batch.Count][];

        // The half that touches the map. Also where the counters are kept, so they stay increments
        // on one thread rather than an interlocked write per bake.
        //
        // TIMED SEPARATELY FROM THE BAKE, and the two clocks are the reason either change can be
        // scored. Threading moved work off this thread without making less of it, so only a
        // calling-thread stopwatch around the bake could see it; the silhouette memo does the
        // opposite — it removes work from this loop and moves nothing — so only a stopwatch around
        // the gather can see THAT. One clock over both halves would have shown each change diluted
        // by the other half's constant.
        System.Diagnostics.Stopwatch gatherClock = System.Diagnostics.Stopwatch.StartNew();

        for (int i = 0; i < batch.Count; i++)
        {
            LightEntry entry = batch[i];

            BatchSegments[i] = GatherFor(map, entry);
            PolygonBakes++;
            BakeSegments += BatchSegments[i].Length;
        }

        GatherWallMs += gatherClock.Elapsed.TotalMilliseconds;

        // Started AFTER the gather, so the two arms are compared over the half that actually
        // differs. The gather is identical work on the same thread either way, and including it
        // would dilute the ratio with a constant.
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

        if (ShouldFanOut(batch.Count))
        {
            ParallelBakePasses++;
            Parallel.For(0, batch.Count, i => BakeGathered(batch[i], BatchSegments[i]));
        }
        else
        {
            SerialBakePasses++;

            for (int i = 0; i < batch.Count; i++)
                BakeGathered(batch[i], BatchSegments[i]);
        }

        BakeWallMs += clock.Elapsed.TotalMilliseconds;

        // Dropped rather than left for the next frame to overwrite: a whole-map rebake's segment
        // arrays are the largest thing this subsystem allocates, and holding them until the next
        // bake means holding them for as long as nobody builds a wall.
        for (int i = 0; i < batch.Count; i++)
            BatchSegments[i] = null;
    }

    private static bool ShouldFanOut(int count)
    {
        // OFF REPRODUCES THE SERIAL PATH EXACTLY rather than skipping the bake — the batch is baked
        // in index order on the calling thread, which is the order EnsurePolygons built it in and
        // therefore the order the previous shape baked in.
        return CelestialLightingFeatures.VectorLightParallelBake
            && count >= ParallelBakeMinimum
            && System.Environment.ProcessorCount > 1;
    }

    // The visibility polygon for one emitter, built if the world has changed under it since the last
    // time anybody asked. Shared by the draw and by §27 phase 3's mask so the two cannot disagree
    // about the shape of a shadow — a disagreement would show as the mask darkening cells the draw
    // had just lit.
    //
    // The one-at-a-time entry point. EnsurePolygons does not route through it, because gathering and
    // baking have to be separable for the batch to be threaded; this keeps the single-emitter
    // spelling for anything that wants one, and both spellings end in BakeGathered so they cannot
    // disagree about what a bake is.
    public static void EnsurePolygon(Map map, LightEntry entry)
    {
        if (!entry.PolygonDirty && entry.Polygon.Count > 0)
        {
            PolygonHits++;
            return;
        }

        VectorLightMath.Segment[] segments = GatherFor(map, entry);

        PolygonBakes++;
        BakeSegments += segments.Length;

        BakeGathered(entry, segments);
    }

    // One emitter's occluder set, read off the live map.
    //
    // THE ONLY PLACE EITHER SPELLING TOUCHES THE MAP, which is what makes the threading argument in
    // BakeSelected checkable by reading rather than by tracing: the batch path and the one-at-a-time
    // path both come through here, on the calling thread, before any pool thread exists.
    //
    // The memo is allocated on first use rather than with the entry. An emitter the view cull keeps
    // deferring never reaches this line, and one that does reaches it every time thereafter, so the
    // allocation lands exactly where it starts paying for itself. Issue #188 item C.
    private static VectorLightMath.Segment[] GatherFor(Map map, LightEntry entry)
    {
        entry.Silhouette = entry.Silhouette ?? new VectorLightSilhouetteMath.Memo();

        return VectorLightBlockers.SegmentsAround(
            map, entry.Cell, entry.Radius, entry.Silhouette);
    }

    // One emitter's bake, once its silhouette is in hand.
    //
    // NOTHING IN HERE MAY TOUCH THE MAP, because this is what runs on a pool thread. Everything it
    // reads is either a value already on the entry or the Segment[] it was handed, and everything it
    // writes is a field of that same entry — so two of these running at once cannot observe each
    // other, and the join that ends the batch is what publishes the writes to the main thread.
    //
    // `Math.Ceiling` rather than `Mathf.CeilToInt`, which is the one line where that rule bites. The
    // two are the same arithmetic and Mathf is pure managed code, so the swap changes no answer —
    // but "no UnityEngine calls on this path" is a rule that has to be checkable by reading, and a
    // Mathf call sitting here invites the next person to reach for a Mathf member that is not pure.
    private static void BakeGathered(LightEntry entry, VectorLightMath.Segment[] segments)
    {
        entry.Polygon = VectorLightMath.Build(
            entry.Cell.x + 0.5f, entry.Cell.z + 0.5f, entry.Radius, segments,
            VectorLightMath.DefaultBaseRayCount);

        // Baked alongside the polygon, on the same cadence and for the same reason: both change only
        // when somebody builds or removes a wall in range, and both are asked for once per cell of
        // every section that overlaps this emitter.
        entry.CoverageRadius = (int)System.Math.Ceiling(entry.Radius);
        entry.Coverage = VectorLightMath.BuildCoverage(
            entry.Polygon, entry.Cell.x, entry.Cell.z, entry.CoverageRadius,
            VectorLightMath.DefaultCoverageSamples, Scratch);
        entry.Unobstructed = VectorLightMath.IsUnobstructed(entry.Polygon, entry.Radius);

        // LAST, DELIBERATELY. The flag is what every other reader uses to decide the entry is
        // usable, so it must not be cleared until the three fields above are written. On the
        // threaded path the join is the barrier that makes that ordering visible to the main thread.
        entry.PolygonDirty = false;
    }

    public static Dictionary<object, LightEntry>.ValueCollection LightsFor(Map map)
    {
        MapLights lights = EnsureMap(map);

        if (lights.RosterDirty)
            Resync(map, lights);

        return lights.Entries.Values;
    }

    // Drops every mesh on every map. Called when the feature is switched off, so an off run holds no
    // GPU memory and — more importantly for the harness — leaves nothing behind that could still be
    // drawn and quietly contaminate the A/B baseline.
    public static void ClearAll()
    {
        foreach (MapLights lights in ByMap.Values)
        {
            foreach (LightEntry entry in lights.Entries.Values)
                DestroyMesh(entry);

            lights.Entries.Clear();
            lights.RosterDirty = true;
        }
    }

    private static MapLights EnsureMap(Map map)
    {
        if (!ByMap.TryGetValue(map.uniqueID, out MapLights lights))
        {
            lights = new MapLights();
            ByMap[map.uniqueID] = lights;
        }

        return lights;
    }

    // Rebuilds the roster from GlowGrid's live sets, keeping the mesh of anything that has not moved
    // or changed size. Keeping those is what makes a lamp toggle cost one polygon rather than all of
    // them: the roster is dirty, but every other light's geometry is not.
    private static void Resync(Map map, MapLights lights)
    {
        lights.RosterDirty = false;
        RosterResyncs++;

        HashSet<object> seen = new HashSet<object>();
        AddGlowers(map, lights, seen);
        AddTerrain(map, lights, seen);
        RemoveUnseen(lights, seen);
    }

    private static void AddGlowers(Map map, MapLights lights, HashSet<object> seen)
    {
        HashSet<CompGlower> glowers = GlowGridAccess.LitGlowers(map.glowGrid);

        if (glowers == null)
            return;

        foreach (CompGlower glower in glowers)
        {
            // A glower can be in the set while its parent is between maps (gravships) or mid-despawn.
            // Filtering here rather than guarding at draw time keeps the roster to things that
            // genuinely exist on this map.
            if (BelongsTo(glower, map))
            {
                ColorInt glow = glower.GlowColor;
                Upsert(lights, seen, glower.parent.thingIDNumber, glower.parent.Position,
                    glower.GlowRadius, glow.r / 255f, glow.g / 255f, glow.b / 255f,
                    GlowGridPerLight.Reader.KeyFor(glower.parent.thingIDNumber, isTerrain: false));
            }
        }
    }

    private static bool BelongsTo(CompGlower glower, Map map)
    {
        Thing parent = glower?.parent;
        return parent != null && parent.Map == map;
    }

    // Glowing terrain is a separate registration path off TerrainDef.glowRadius, with no CompGlower
    // anywhere. It has to be here because §27 suppresses vanilla's render of ALL artificial light:
    // a version that only knew about glowers would put glowing moss out entirely the moment the
    // feature was switched on, which is a regression rather than a missing feature.
    private static void AddTerrain(Map map, MapLights lights, HashSet<object> seen)
    {
        HashSet<IntVec3> litTerrain = GlowGridAccess.LitTerrain(map.glowGrid);

        if (litTerrain == null)
            return;

        foreach (IntVec3 cell in litTerrain)
        {
            TerrainDef terrain = map.terrainGrid.TerrainAt(cell);

            if (terrain != null && terrain.glowRadius > 0f)
            {
                ColorInt glow = terrain.glowColor;
                Upsert(lights, seen, cell, cell, terrain.glowRadius,
                    glow.r / 255f, glow.g / 255f, glow.b / 255f,
                    GlowGridPerLight.Reader.KeyFor(map.cellIndices.CellToIndex(cell), isTerrain: true));
            }
        }
    }

    private static void Upsert(
        MapLights lights, HashSet<object> seen, object key,
        IntVec3 cell, float radius, float r, float g, float b, long vanillaKey)
    {
        seen.Add(key);

        if (!lights.Entries.TryGetValue(key, out LightEntry entry))
        {
            entry = new LightEntry { Props = new MaterialPropertyBlock() };
            lights.Entries[key] = entry;
        }

        // Only a move or a resize invalidates the polygon. A recolour does not — the shape is
        // identical and the colour rides on the material, so a colour-picker lamp being retinted
        // costs nothing but a property block write.
        if (entry.Cell != cell || entry.Radius != radius)
        {
            entry.GeometryDirty = true;
            entry.PolygonDirty = true;
        }

        entry.Cell = cell;
        entry.Radius = radius;
        entry.VanillaKey = vanillaKey;

        float scale = VectorLightMath.PeakScale(r, g, b);
        entry.Color = new Color(r * scale, g * scale, b * scale, 1f);
    }

    private static void RemoveUnseen(MapLights lights, HashSet<object> seen)
    {
        List<object> gone = null;

        foreach (KeyValuePair<object, LightEntry> pair in lights.Entries)
        {
            if (!seen.Contains(pair.Key))
            {
                gone = gone ?? new List<object>();
                gone.Add(pair.Key);
            }
        }

        if (gone == null)
            return;

        foreach (object key in gone)
        {
            DestroyMesh(lights.Entries[key]);
            lights.Entries.Remove(key);
        }
    }

    private static void DestroyMesh(LightEntry entry)
    {
        if (entry.Mesh != null)
            Object.Destroy(entry.Mesh);

        entry.Mesh = null;

        // The vanilla field goes with the mesh. Both are unmanaged Unity objects that the GC will
        // not collect on its own, and an emitter is dropped from the roster whenever a lamp is
        // deconstructed or the whole field is cleared by a settings toggle — which on a large colony
        // is enough textures to matter if they are only ever created.
        if (entry.VanillaField != null)
            Object.Destroy(entry.VanillaField);

        entry.VanillaField = null;
    }
}
