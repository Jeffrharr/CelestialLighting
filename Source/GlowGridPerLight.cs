using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using Verse;
using Verse.Glow;

namespace CelestialLighting;

// Vanilla's glow, per emitter, rather than accumulated — the one input §27 phase 3 cannot do without.
//
// WHY IT IS NEEDED. Phase 3 subtracts light back out of the cells its polygons say are shadowed, and
// the whole value of doing it that way is that it removes ONLY the contribution of an emitter we
// modelled. `GlowGrid.VisualGlowAt` gives the sum of everything reaching a cell, so subtracting it
// would take a mod's light, a glowing plant and a fire out along with the lamp we meant. The per-
// light arrays are what make "leave everything we did not model completely alone" a property of the
// arithmetic rather than a hope.
//
// WHAT IS BEING READ. `GlowGrid.lights` is a NativeList<GlowLight>, and `GlowGrid.glowPool` wraps a
// NativeList<LocalGlowArea> whose entries hold one Color32 per cell of that light's local square.
// ComputeGlowGridsJob fills them with falloff(GEODESIC distance) — the exact quantity our own
// falloff would have produced if light bent around corners. Both are private, and both are read
// through the same reflection idiom GlowGridAccess established rather than a second one.
//
// WHY READING THEM IS SAFE. GlowGridUpdate_First schedules both jobs and calls .Complete() on each
// inside the same method, so no job is outstanding by the time anything renders. These are reads of
// a finished buffer, not a race — and they are reads only: nothing here writes, resizes, disposes or
// marks anything dirty, so gameplay light is as untouched as it is everywhere else in §27.
//
// WHY IT IS ALLOWED TO FAIL. Private fields on a Burst-adjacent type are exactly the kind of thing a
// RimWorld update renames, and the failure mode of guessing wrong must not be a crash in the middle
// of a section regenerate. Available goes false, phase 3 stands down to the crossfade, and the Cecil
// API tests fail loudly at build time rather than the game failing quietly at run time.
public static class GlowGridPerLight
{
    private static readonly FieldInfo LightsField =
        AccessTools.DeclaredField(typeof(GlowGrid), "lights");

    private static readonly FieldInfo PoolField =
        AccessTools.DeclaredField(typeof(GlowGrid), "glowPool");

    // The pool's own list, on the private nested GlowPool class. Resolved from the field's declared
    // type rather than by naming the nested type, which our assembly cannot see.
    private static readonly FieldInfo PoolListField =
        PoolField == null ? null : AccessTools.DeclaredField(PoolField.FieldType, "pool");

    public static bool Available =>
        LightsField != null && PoolField != null && PoolListField != null;

    // One map's per-light glow, resolved once and then queried per cell.
    //
    // Resolved once because the reflection is the expensive part: boxing a NativeList through
    // FieldInfo.GetValue for every cell of a section would cost more than the lighting overlay it is
    // correcting. A Reader is built per regenerate and thrown away, so it never outlives the buffers
    // it points at — which is the property that makes holding a NativeList in a field acceptable here
    // and would not be if it were cached across frames.
    public sealed class Reader
    {
        private readonly NativeList<GlowLight> lights;
        private readonly NativeList<LocalGlowArea> pool;

        // Where each emitter sits in `lights`, keyed the way GlowLight identifies itself: a thing id
        // for a glower and a cell index for glowing terrain, which can collide with each other. The
        // terrain flag is folded into the key rather than checked afterwards so a collision resolves
        // instead of returning the wrong emitter's light.
        private readonly Dictionary<long, int> indexByKey = new Dictionary<long, int>();

        // Reused across builds and never shrunk, for the reason SectionLightIndexMath's own header
        // gives: a per-frame allocation here is litter on a path that runs every frame, and this
        // repo has measured that litter slowing an untouched neighbouring stage by 60%.
        private int[] ranges;

        private int mapWidth;

        private int mapHeight;

        // RimWorld's own section size. Named rather than inlined because the index files by it and
        // the query reads back by it, and the two must be the same number.
        public const int SectionSize = 17;

        // The section index, and the map it was built for. See BuildSectionIndex.
        private int[] sectionStarts;

        private int[] sectionItems;

        private int sectionsAcross;

        private int sectionCount;

        private bool indexed;

        internal Reader(NativeList<GlowLight> lights, NativeList<LocalGlowArea> pool, Map map)
        {
            this.lights = lights;
            this.pool = pool;

            for (int i = 0; i < lights.Length; i++)
                indexByKey[KeyFor(lights[i].id, lights[i].isTerrain)] = i;

            BuildSectionIndex(map);
        }

        // Which lights touch which section, filed once per frame so the mask does not re-read the
        // whole list per section regenerate.
        //
        // WHY HERE AND NOT IN THE MASK. This object is already built once per frame and already
        // walks every light to fill indexByKey, so the index rides along on a pass that was being
        // paid for anyway. Building it in the mask instead would rebuild it per section, which is
        // the cost being removed.
        //
        // THE MARGIN GOES ON AT INSERT TIME, and that is what makes a query a single bucket read
        // with no deduplication. Both callers examine one cell beyond their section on every side
        // -- VectorLightMask.CellMargin -- so a light is filed under every section its reach
        // overlaps once EXPANDED by that margin. Asking the other way round, by expanding the query
        // instead, would span up to four buckets and need the results merged and de-duplicated,
        // which on a non-commutative fold means merged IN ORDER. This way the bucket is already the
        // answer, already ascending.
        //
        // A FAILED BUILD IS NOT AN ERROR, it is the full scan. `indexed` false means every caller
        // falls back to walking the list, which is what it did before this existed.
        private void BuildSectionIndex(Map map)
        {
            // CLOCKED, BECAUSE IT SITS OUTSIDE EVERY OTHER CLOCK IN THE SUBSYSTEM. A Reader is built
            // from the first line of VectorLightMask.Apply, before its stopwatch starts, so an index
            // built here is pure saving as far as ApplyWallMs is concerned -- and a saving measured
            // against a cost nobody timed is the oldest way to report a change as free. This is the
            // other half of the ledger and the number the stage saving has to be read net of.
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                BuildSectionIndexInner(map);
            }
            finally
            {
                IndexBuildWallMs += clock.Elapsed.TotalMilliseconds;
                IndexBuilds++;
            }
        }

        private void BuildSectionIndexInner(Map map)
        {
            indexed = false;

            if (map == null || lights.Length <= 0)
                return;

            int width = map.Size.x;
            int height = map.Size.z;

            mapWidth = width;
            mapHeight = height;

            sectionsAcross = (width + SectionSize - 1) / SectionSize;
            sectionCount = SectionDirtyMath.SectionCount(width, height, SectionSize);

            if (sectionsAcross <= 0 || sectionCount <= 0)
                return;

            int need = lights.Length * SectionLightIndexMath.IntsPerItem;

            if (ranges == null || ranges.Length < need)
                ranges = new int[need];

            for (int i = 0; i < lights.Length; i++)
            {
                CellRect reach = lights[i].AffectedRect;
                int at = i * SectionLightIndexMath.IntsPerItem;

                bool on = SectionDirtyMath.SectionRange(
                    SectionDirtyMath.Changed(
                        reach.minX, reach.minZ, reach.maxX, reach.maxZ, VectorLightMask.CellMargin),
                    SectionSize, width, height,
                    out int minSx, out int minSz, out int maxSx, out int maxSz);

                ranges[at] = on ? minSx : SectionLightIndexMath.Absent;
                ranges[at + 1] = maxSx;
                ranges[at + 2] = minSz;
                ranges[at + 3] = maxSz;
            }

            SectionLightIndexMath.Build(
                ranges, lights.Length, sectionsAcross, sectionCount,
                ref sectionStarts, ref sectionItems);

            indexed = true;
        }

        // The lights filed under the section this cell sits in, as a window into one shared array.
        //
        // Returns false when there is no index, which the callers answer by walking the whole list.
        // ONLY VALID FOR A SECTION-ALIGNED QUERY: the index was filed by section, so a caller asking
        // about some other rectangle would get the bucket of whichever section its corner happens to
        // fall in. The callers check alignment rather than trusting it, because the one caller that
        // is not a section regenerate has not been written yet and would fail silently.
        public bool TrySectionLights(int cellX, int cellZ, out int[] items, out int start, out int end)
        {
            items = sectionItems;
            start = 0;
            end = 0;

            if (!indexed)
                return false;

            int section = SectionLightIndexMath.SectionAt(
                cellX, cellZ, SectionSize, mapWidth, mapHeight, sectionsAcross);

            if (section == SectionLightIndexMath.Absent || section >= sectionCount)
                return false;

            start = sectionStarts[section];
            end = sectionStarts[section + 1];
            return true;
        }

        public static long KeyFor(int id, bool isTerrain) => ((long)id << 1) | (isTerrain ? 1L : 0L);

        // EVERY emitter on the map, ours and everybody else's, indexed positionally.
        //
        // WHY THE WHOLE LIST AND NOT JUST OURS. §27 phase 5b has to reconstruct the sum vanilla
        // PROJECTED, and vanilla projected a sum over every light reaching the cell — a mod's lamp,
        // a glowing plant, a fire. Reconstructing it from our own field's entries alone would
        // under-count the sum, read the cell as unsaturated and leave the over-subtraction exactly
        // where it was in the case most likely to saturate: a room several mods are all lighting.
        //
        // Positional rather than keyed because the caller wants to walk them all once; the
        // dictionary above exists for the opposite question, "where is this one emitter".
        public int LightCount => lights.Length;

        public bool TryLightAt(int index, out GlowLight light, out UnsafeList<Color32> colors)
        {
            light = default;
            colors = default;

            if (index < 0 || index >= lights.Length)
                return false;

            light = lights[index];
            LocalGlowArea area = pool[light.localGlowPoolIndex];

            if (!area.colors.IsCreated)
                return false;

            colors = area.colors;
            return true;
        }

        // Does the emitter at this index reach anywhere inside this box? Asked WITHOUT resolving the
        // emitter, which is the whole reason it exists as a separate method.
        //
        // WHY A REJECT NEEDS ITS OWN ENTRY POINT. Both callers of this walk EVERY light on the map
        // once per section regenerate, and at a colony's worth of lamps the overwhelming majority of
        // them are nowhere near the section being rebaked. TryLightAt costs two native-container
        // indexers -- `lights[index]` and then `pool[light.localGlowPoolIndex]` -- to hand back a
        // colour array that a non-overlapping light's caller then never reads. This is the first of
        // those two reads and four integer compares, so the reject path stops paying for the accept
        // path's work.
        //
        // THE BOX IS FOUR INTS RATHER THAN A CellRect because both callers have already expanded
        // their section by the one-cell margin the accumulators read over, and rebuilding a CellRect
        // here to intersect against would put a struct construction back into the loop this exists
        // to make cheap.
        //
        // OUT OF RANGE IS "DOES NOT OVERLAP", matching TryLightAt's own bounds guard rather than
        // throwing. The callers loop to LightCount so an index outside it is a caller bug, but the
        // failure mode of one must not be an exception in the middle of a section regenerate.
        public bool OverlapsAt(int index, int minX, int maxX, int minZ, int maxZ)
        {
            if (index < 0 || index >= lights.Length)
                return false;

            CellRect reach = lights[index].AffectedRect;

            return reach.maxX >= minX && reach.minX <= maxX
                && reach.maxZ >= minZ && reach.minZ <= maxZ;
        }

        // One emitter resolved ONCE, so the caller can then walk its cells with plain array
        // arithmetic.
        //
        // WHY THIS EXISTS RATHER THAN TryGlowAt ALONE. Asking per cell measured 239 us per section:
        // a dictionary lookup on a long key, a CellRect.Contains, and two native-container indexers,
        // about eighteen hundred times per section. None of that depends on the cell except the last
        // step. Resolving the emitter once and handing back its rect and its colour array turns the
        // inner loop into an index and a compare, and lets the caller iterate only the cells the
        // emitter actually reaches instead of every cell of the section.
        public bool TryResolveEmitter(long key, out GlowLight light, out UnsafeList<Color32> colors)
        {
            light = default;
            colors = default;

            if (!indexByKey.TryGetValue(key, out int index))
                return false;

            light = lights[index];
            LocalGlowArea area = pool[light.localGlowPoolIndex];

            if (!area.colors.IsCreated)
                return false;

            colors = area.colors;
            return true;
        }

        // What this one emitter delivers to this one cell, in vanilla's own units. False when the
        // emitter is not vanilla's to begin with, or when the cell is outside its square — which is
        // not an error, just "this light does not reach here", and the caller wants a zero.
        //
        // Kept for the probes, which ask about a handful of cells and want the convenient form. The
        // bake goes through TryResolveEmitter instead.
        public bool TryGlowAt(long key, IntVec3 cell, out Color32 glow)
        {
            glow = default;

            if (!indexByKey.TryGetValue(key, out int index))
                return false;

            GlowLight light = lights[index];

            // AffectedRect first, because WorldToLocalIndex does not range check: a cell outside the
            // square produces an index that is merely wrong rather than out of bounds, so a missing
            // check reads as another light's glow rather than as an exception.
            if (!light.AffectedRect.Contains(cell))
                return false;

            int local = light.WorldToLocalIndex(cell);
            LocalGlowArea area = pool[light.localGlowPoolIndex];

            if (local < 0 || local >= area.colors.Length)
                return false;

            glow = area.colors[local];
            return true;
        }
    }

    // What building the section index cost, on the calling thread, and how many times it was paid.
    //
    // Static rather than per Reader because a Reader lives one frame and the question spans a
    // window. Drained by VectorLightMask.ResetTelemetry alongside the stage clocks, so the two
    // numbers describe the same window and can be subtracted -- the mistake that made
    // vector_light_mask_applies unreadable against those clocks was exactly a counter draining on a
    // different schedule from the durations it was meant to divide.
    public static double IndexBuildWallMs;

    public static long IndexBuilds;

    // The last Reader handed out, and the map and frame it was built for.
    //
    // A REGENERATE IS PER SECTION AND A REBAKE IS 112 OF THEM. Building a Reader costs three
    // reflection GetValue calls — each of which boxes a NativeList — plus a dictionary built over
    // every emitter on the map. Paying that per section was most of what made the first
    // implementation slow, and none of it changes within a frame.
    private static Reader cached;
    private static int cachedMapId = -1;
    private static int cachedFrame = -1;

    public static Reader For(Map map)
    {
        if (!Available || map?.glowGrid == null)
            return null;

        // Time.frameCount rather than a tick: sections regenerate during the draw, and several
        // sections of one rebake land in the same frame. A stale Reader across frames would hold
        // native buffers that a glower registration may since have reallocated.
        if (cached != null && cachedMapId == map.uniqueID && cachedFrame == Time.frameCount)
            return cached;

        try
        {
            object poolBox = PoolField.GetValue(map.glowGrid);

            if (poolBox == null)
                return null;

            NativeList<GlowLight> lights = (NativeList<GlowLight>)LightsField.GetValue(map.glowGrid);
            NativeList<LocalGlowArea> pool = (NativeList<LocalGlowArea>)PoolListField.GetValue(poolBox);

            if (!lights.IsCreated || !pool.IsCreated)
                return null;

            cached = new Reader(lights, pool, map);
            cachedMapId = map.uniqueID;
            cachedFrame = Time.frameCount;
            return cached;
        }
        catch (Exception ex)
        {
            // Once, not per section. A regenerate happens on every glow change, so a message here
            // without a latch would fill the log faster than the player could read the first one.
            if (!warned)
            {
                warned = true;
                Log.Warning(
                    "[CelestialLighting] Could not read GlowGrid's per-light arrays; §27's subtractive "
                    + "mask is unavailable and falls back to the crossfade. " + ex.Message);
            }

            return null;
        }
    }

    private static bool warned;
}
