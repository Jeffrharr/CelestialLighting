using System.Collections.Generic;
using RimWorld;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// Indoor sky occlusion's gather phase: build every dirty on-screen section's SkyOcclusionWindow at
// once, across cores, before vanilla starts regenerating them one at a time.
//
// THE PROBLEM IS THE FRAME, NOT THE CALL. Verse.Section.TryUpdate regenerates every visible dirty
// layer of every visible dirty section in a single frame — there is no rate limiting for anything
// on screen, only off-screen sections are deferred one per frame. So a whole-map GroundGlow change
// (which GlowGrid.DirtyCell raises on every lamp toggle, alongside Roofs) lands as one enormous
// frame rather than a slope. Measured on a 112-section rebake with the shipped defaults, three
// interleaved runs: our postfix alone is 141.0 us per call and **21.3 ms in its worst frame**, which
// is more than an entire 60 fps budget, and 58.8% of the whole lighting-overlay bake it sits inside.
// Making one 141 us call faster does not help a frame that runs a hundred of them; the only lever
// that reaches this shape is doing them at the same time.
//
// WHY THE WORK CANNOT SIMPLY BE DEFERRED INSTEAD, which is the obvious alternative and is wrong.
// Our postfix runs inside vanilla's Regenerate, whose contract is that the mesh is finished when it
// returns. Returning early and patching the mesh a frame later means the section draws with
// un-occluded lighting until we catch up — a bright flash on every lamp toggle, which is a worse
// artefact than the stutter it would fix. Nothing here defers anything; the work happens in the same
// frame it always did, just not all on one thread.
//
// ONLY HALF OF THE POSTFIX CAN MOVE, and the number is measured rather than assumed. A child arm on
// BuildWindow reads 72.22 ms against the postfix's 151.68 in the same run — **47.6%**. The rest is
// the two mesh passes and the mesh.colors32 round trip, which are Unity calls and stay exactly where
// they are. So this is a cut of roughly a third off the worst frame, not a fix for it, and the
// section's own write-up says so rather than quoting the ratio of the part that moved.
//
// NO NEW VANILLA PATCH SURFACE, AND THAT CONSTRAINT SHAPED THE TRIGGER. The obvious hook is a Prefix
// on MapDrawer.MapMeshDrawerUpdate_First — it is the call that runs the whole regenerate loop, so it
// is the last point at which the batch is still visible as a batch. It was built that way, measured,
// and then withdrawn: it puts this mod on a third vanilla member, in the render loop, where the repo
// already notes that the great majority of our patches sit on just two. Every patch surface is a
// place another mod can collide with us.
//
// So the phase is triggered from a method we ALREADY postfix. The first indoor-occlusion postfix of
// a frame gathers every other candidate section before doing its own work; the rest are then hits.
// EnsureGathered is that latch. This costs nothing against the prefix version — the first section
// was going to build a window anyway, and it now builds it inside the batch — and it needs no
// agreement from vanilla beyond what Patch_IndoorSkyOcclusion already has.
//
// THE ORDERING STILL HOLDS, which is the thing that had to be re-checked rather than assumed. The
// first lighting-overlay Regenerate of a frame happens inside Section.TryUpdate, called from
// MapMeshDrawerUpdate_First at Map.MapUpdate line 1173 — after regionAndRoomUpdater
// .TryRebuildDirtyRegionsAndRooms() at 1158 and glowGrid.GlowGridUpdate_First() at 1159. Rooms and
// glow are final for the frame before the first postfix fires, exactly as they were for the prefix.
// SkyManagerUpdate (1155) would NOT do: it runs before both, so a gather there would read last
// frame's rooms.
//
// NO TIMER, NO POLL. Same rule as the vector light field: everything here is provoked by vanilla
// dirtying a section.
//
// Cached in statics keyed by map rather than in a MapComponent, following OpenSkyMask and
// VectorLightField: Map.ExposeComponents scribes a permanent node per component, so a component
// deleted later logs two red errors per map forever (Source/MapComponent_SunShadowAxis.cs is the
// tombstone).
public static class SkyOcclusionGather
{
    // This frame's finished windows, by the section they belong to. Cleared at the top of every
    // gather rather than trusted to expire, so a frame in which the phase stands down cannot serve
    // last frame's answers.
    private static readonly Dictionary<Section, SkyOcclusionWindow> Ready =
        new Dictionary<Section, SkyOcclusionWindow>();

    // Scratch, reused across frames so the phase does not allocate two lists per frame on the render
    // path. Never read outside Gather.
    private static readonly List<Section> Candidates = new List<Section>();
    private static readonly List<CellRect> CandidateRects = new List<CellRect>();
    private static SkyOcclusionWindow[] built = new SkyOcclusionWindow[0];

    // What Ready is good for. A window is only served to a section on the same map and the same
    // frame it was built on — the map can change under us between frames (a wall goes up, a door
    // opens) and a stale window renders as a room lit for a layout that is no longer there.
    //
    // Time.frameCount rather than renderedFrameCount, for the reason FrameStamp records: frameCount
    // advances once per Update, which is the pass MapMeshDrawerUpdate_First and every Regenerate
    // under it run in.
    //
    // A STRONG Map REFERENCE, held for at most one frame, and deliberately not a WeakReference the way
    // NativeSkyFalloffGrid's CachedMap is. That one caches a whole-map grid across many frames and
    // genuinely would pin an abandoned map; this is cleared at the top of every Gather, and a Gather
    // runs on the first lighting-overlay regenerate of any frame on any map. So the longest a
    // destroyed map can be held here is until the next frame draws one — not worth the machinery, and
    // worth saying so, since the two fields look like the same situation and are not.
    private static int readyFrame = -1;
    private static Map readyMap;

    // The once-per-frame latch, kept separate from readyFrame on purpose: readyFrame says "there are
    // windows to serve", and a frame in which the phase stood down (one candidate, gate closed) sets
    // this and not that. Sharing one field would re-run the whole candidate collection on every
    // postfix of every such frame — a hundred pointless viewport walks per rebake.
    private static int gatheredFrame = -1;

    // The map the latch was closed for. Separate from readyMap, and the distinction is load-bearing:
    // a frame where the phase stands down (one candidate, gate closed, feature off) leaves readyMap
    // NULL while still having been gathered, so latching on readyMap would re-walk the viewport on
    // every one of that frame's postfixes — a hundred pointless collections per rebake, which is
    // slower than the code this file replaces.
    private static Map gatheredMap;

    // Set once if the phase ever throws. See EnsureGathered.
    private static bool faulted;

    // ---- accounting ---------------------------------------------------------------------------
    //
    // COUNTERS, NOT TIMERS, for the reason VectorLightField's own block spells out: a timing probe
    // measures one call and never asks how often it happens. Here the question they answer is
    // narrower and sharper — a gather phase that silently stops matching (because the candidate
    // predicate drifted from TryUpdate's, or because the safety gate closed on somebody's install)
    // costs nothing, breaks nothing, and reads exactly like a gather phase that is working. Hits
    // against misses is the only thing that can tell those apart.
    public static int GatherPasses;

    public static int GatheredSections;

    public static int GatherHits;

    public static int GatherMisses;

    // Whether the phase is allowed to run on this install at all. See ResolveParallelSafe.
    private static bool? parallelSafe;

    // Zeroes the counters. For the live harness only — the counters accumulate across a whole run, so
    // a scenario measuring two arms would report the second with the first still inside it.
    public static void ResetCounters()
    {
        GatherPasses = 0;
        GatheredSections = 0;
        GatherHits = 0;
        GatherMisses = 0;
    }

    // Gather once per frame, from whichever indoor-occlusion postfix happens to run first. Every
    // later postfix in the same frame finds the latch closed and goes straight to its window.
    //
    // WRAPPED, BECAUSE THIS NOW RUNS INSIDE VANILLA'S try/catch. Section.TryUpdate and
    // RegenerateDirtyLayers both catch around Regenerate, log "Could not regenerate layer" and carry
    // on — so a throw in here would leave the frame looking exactly like vanilla with every probe
    // healthy, which is the single hardest failure in this repo to notice. Catching it ourselves
    // turns that into one named error and a permanent stand-down: the phase switches off for the
    // session and every section builds its window inline, which is what it did before this file
    // existed. Logged once rather than per section, because the provoking case is a whole-map rebake
    // and per-section would be a hundred identical red lines a frame.
    public static void EnsureGathered(Map map)
    {
        if (faulted || (gatheredMap == map && gatheredFrame == Time.frameCount))
            return;

        gatheredFrame = Time.frameCount;
        gatheredMap = map;

        try
        {
            Gather(map);
        }
        catch (System.Exception e)
        {
            faulted = true;
            Ready.Clear();
            readyMap = null;
            Log.Error(
                "[CelestialLighting] indoor sky occlusion gather phase failed and is now off for this "
                + "session; sections will build their windows inline. " + e);
        }
    }

    // Build every candidate section's window for this frame.
    private static void Gather(Map map)
    {
        Ready.Clear();
        readyMap = null;

        if (map == null || !CelestialLightingFeatures.IndoorSkyOcclusion
            || !CelestialLightingFeatures.IndoorOcclusionGather || !ParallelSafe)
            return;

        CollectCandidates(map);

        int workers = CloudBake.WorkerCount(SystemInfo.processorCount);

        if (!SkyOcclusionGatherMath.Worthwhile(Candidates.Count, workers))
            return;

        // EVERYTHING UNSAFE HAPPENS HERE, ON THIS THREAD, BEFORE ANY WORKER STARTS. Two things, and
        // both of them are lazily-built caches rather than anything that looks like shared state:
        //
        //  1. SkyFalloffSource.ForSection can run NativeSkyFalloffGrid's whole-map BFS, which writes
        //     static arrays. Built once here and handed to every worker as a value.
        //  2. Room.UsesOutdoorTemperature reaches Room.CellCount and District.CellCount, and BOTH are
        //     `cachedCellCount = 0;` followed by a `+=` loop behind a -1 sentinel. That is not a
        //     benign race: two threads interleaving there can leave a partial sum cached permanently,
        //     and the symptom would be a room that decides it is outdoors and stops being occluded —
        //     once, unreproducibly, on somebody else's machine.
        SkyFalloffSource.SectionReader falloff = SkyFalloffSource.ForSection(map);
        WarmRoomCaches(map);

        if (built.Length < Candidates.Count)
            built = new SkyOcclusionWindow[Candidates.Count];

        GatherPasses++;
        GatheredSections += Candidates.Count;

        // Sections are handed out ONE AT A TIME by the partitioner rather than sliced into equal
        // blocks, the same choice CloudBake.Rows makes for cloud rows and for the same reason: the
        // cost of a section is wildly uneven. A section of open field short-circuits
        // EaveCells.Encloses on every unroofed cell and never reaches a room query; one full of small
        // rooms pays for all 361. An even split by COUNT is an uneven split by WORK, and a static
        // partition finishes when its unluckiest block does.
        //
        // Routed through CloudBake.Rows rather than calling Parallel.For directly so this inherits
        // the mod's one worker-count policy (one core left for the game, serial at two cores or
        // fewer) and the scheduler that CloudBakeTests already pins serial-equals-parallel.
        CloudBake.Rows(Candidates.Count, (start, end) =>
        {
            for (int i = start; i < end; i++)
                built[i] = Patch_IndoorSkyOcclusion.BuildWindow(map, CandidateRects[i], falloff);
        });

        // Folded into the dictionary on this thread rather than written from the workers, so there is
        // no concurrent write to a Dictionary anywhere in the phase. The array slot is the handoff,
        // and slot i belongs to worker i outright.
        for (int i = 0; i < Candidates.Count; i++)
            Ready[Candidates[i]] = built[i];

        readyFrame = Time.frameCount;
        readyMap = map;
    }

    // The window for this section: the one a worker built this frame, or a fresh one built right here
    // if there is not one.
    //
    // A MISS IS ORDINARY. Section.DrawSection -> RegenerateDirtyLayers is a second entry point into
    // Regenerate, running later in the same MapUpdate than the gather phase, and a section arriving
    // through it was never a candidate. So is every section on a frame where the phase stood down.
    // The inline build is not a degraded mode; it is what every section did before this file existed.
    public static SkyOcclusionWindow TakeOrBuild(Map map, Section section, CellRect rect)
    {
        if (readyMap == map && readyFrame == Time.frameCount && section != null
            && Ready.TryGetValue(section, out SkyOcclusionWindow window))
        {
            GatherHits++;
            return window;
        }

        GatherMisses++;
        return Patch_IndoorSkyOcclusion.BuildWindow(map, rect, SkyFalloffSource.ForSection(map));
    }

    // Every section the view rect covers whose flags say its lighting overlay is about to regenerate.
    //
    // Walked through MapDrawer.SectionAt, which is public, rather than reflecting the private
    // `sections` array: the arithmetic for "which sections does this cell rect cover" is already in
    // SectionDirtyMath, where an offline test can reach it, and reusing it here keeps one answer to
    // that question rather than two.
    //
    // ViewRect expanded by 1 to match what MapDrawer itself passes to TryUpdate. Culling against the
    // raw camera rect would leave the one-section fringe vanilla still regenerates outside the batch
    // — not a bug, since those sections would simply build inline, but it would put a permanent floor
    // under GatherMisses and make the counters harder to read than they need to be.
    private static void CollectCandidates(Map map)
    {
        Candidates.Clear();
        CandidateRects.Clear();

        MapDrawer drawer = map.mapDrawer;

        if (drawer == null)
            return;

        CellRect view = Find.CameraDriver.CurrentViewRect.ExpandedBy(1).ClipInsideMap(map);
        SectionDirtyMath.CellBounds bounds =
            new SectionDirtyMath.CellBounds(view.minX, view.minZ, view.maxX, view.maxZ);

        bool any = SectionDirtyMath.SectionRange(
            bounds, Section.Size, map.Size.x, map.Size.z,
            out int minSectionX, out int minSectionZ, out int maxSectionX, out int maxSectionZ);

        if (!any)
            return;

        ulong relevant = (ulong)MapMeshFlagDefOf.Roofs | (ulong)MapMeshFlagDefOf.GroundGlow;

        for (int sx = minSectionX; sx <= maxSectionX; sx++)
        {
            for (int sz = minSectionZ; sz <= maxSectionZ; sz++)
                CollectSection(map, drawer, sx, sz, relevant);
        }
    }

    private static void CollectSection(Map map, MapDrawer drawer, int sx, int sz, ulong relevant)
    {
        IntVec3 anchor = new IntVec3(
            SectionDirtyMath.SectionAnchor(sx, Section.Size), 0,
            SectionDirtyMath.SectionAnchor(sz, Section.Size));

        Section section = drawer.SectionAt(anchor);

        if (section == null || !SkyOcclusionGatherMath.WillRegenerate(section.dirtyFlags, relevant))
            return;

        CellRect rect = new CellRect(section.botLeft.x, section.botLeft.z, Section.Size, Section.Size);
        rect.ClipInsideMap(map);

        Candidates.Add(section);
        CandidateRects.Add(rect);
    }

    // Touch the two lazy counts on every room, on this thread, so no worker can be the first to reach
    // them. See the block in Gather for why these two specifically.
    //
    // O(ROOMS) AND NOT O(CELLS), which is what makes this affordable every frame the phase runs. The
    // first touch of a cold room walks its cells — exactly the work a worker would otherwise have
    // done, just safely — and every touch after that is a field read behind a filled sentinel. Region
    // rebuilds reset the caches, so the cold case recurs, and it recurs on the main thread each time.
    //
    // AllRooms rather than walking the candidate windows to find their rooms: the walk would be
    // RoomAtNoRebuild per cell, which is most of what the workers are being spun up to do. Warming a
    // handful of rooms the batch never asks about is far cheaper than finding out which ones it will.
    //
    // regionGrid.AllRooms is a plain list read; RegionGrid.DirectGrid is the property that provokes a
    // rebuild, and this is deliberately not that one.
    private static void WarmRoomCaches(Map map)
    {
        IReadOnlyList<Room> rooms = map.regionGrid?.AllRooms;

        if (rooms == null)
            return;

        for (int i = 0; i < rooms.Count; i++)
        {
            // The value is thrown away — the point is the side effect of filling the sentinel.
            bool _ = rooms[i].UsesOutdoorTemperature;
        }
    }

    // Is it safe to run our per-cell reads on a worker thread on THIS install?
    //
    // One thing can make it unsafe, and it is not ours. IndoorGlowPassthrough.SkyFractionAt — which
    // ResolveCell reaches through the falloff reader — calls GlowGrid.GroundGlowAt, and that is the
    // patched surface every interop mod postfixes (§7c's own header names ReBuild: Doors and Corners
    // transpiling it and Ambient Light postfixing it). Calling it from a worker means running SOMEBODY
    // ELSE'S code off the main thread, and a mod that caches or logs in its postfix would fail in a way
    // that does not name the file it came from.
    //
    // So the gate is ownership, not a guess about any particular mod: if anyone other than us has
    // patched either glow accessor, the phase stands down for the session and every section builds its
    // window inline exactly as it did before. That is a real cost — the installs most likely to want
    // this are the heavily-modded ones — and it is the right way round, because the failure it avoids
    // is a rare nondeterministic crash in another mod's code and the failure it accepts is the frame
    // time we already ship.
    //
    // Resolved once and cached: Harmony.GetPatchInfo walks every patch on the method, which is not a
    // thing to do per frame, and the answer cannot change after startup without a mod hot-patching.
    private static bool ParallelSafe => parallelSafe ?? (bool)(parallelSafe = ResolveParallelSafe());

    private static bool ResolveParallelSafe()
    {
        // Nothing reads the glow grid when the passthrough is off, so an install that has it switched
        // off is safe whatever else is loaded. Checked first because it is the cheap answer and
        // because it keeps the harness able to exercise the parallel path with any mod list.
        if (!CelestialLightingFeatures.IndoorGlowPassthrough)
            return true;

        return !ForeignPatchesOn(AccessTools.Method(
                   typeof(GlowGrid), nameof(GlowGrid.GroundGlowAt)))
            && !ForeignPatchesOn(AccessTools.Method(
                   typeof(GlowGrid), nameof(GlowGrid.VisualGlowAt), new[] { typeof(IntVec3) }));
    }

    // Any patch owner on this method that is not us. A null method (Ludeon renamed or removed it) is
    // reported as foreign: we would rather stand the phase down than thread a call we can no longer
    // identify.
    private static bool ForeignPatchesOn(System.Reflection.MethodBase method)
    {
        if (method == null)
            return true;

        HarmonyLib.Patches info = Harmony.GetPatchInfo(method);

        if (info == null)
            return false;

        return HasForeignOwner(info.Prefixes) || HasForeignOwner(info.Postfixes)
            || HasForeignOwner(info.Transpilers) || HasForeignOwner(info.Finalizers);
    }

    private static bool HasForeignOwner(IReadOnlyCollection<Patch> patches)
    {
        if (patches == null)
            return false;

        foreach (Patch patch in patches)
        {
            if (patch.owner != CelestialLightingMod.HarmonyId)
                return true;
        }

        return false;
    }
}
