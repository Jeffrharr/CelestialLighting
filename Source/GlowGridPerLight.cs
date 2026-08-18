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

        internal Reader(NativeList<GlowLight> lights, NativeList<LocalGlowArea> pool)
        {
            this.lights = lights;
            this.pool = pool;

            for (int i = 0; i < lights.Length; i++)
                indexByKey[KeyFor(lights[i].id, lights[i].isTerrain)] = i;
        }

        public static long KeyFor(int id, bool isTerrain) => ((long)id << 1) | (isTerrain ? 1L : 0L);

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

            cached = new Reader(lights, pool);
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
