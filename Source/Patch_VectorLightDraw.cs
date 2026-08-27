using HarmonyLib;
using RimWorld;
using Verse;

namespace CelestialLighting;

// §27's draw hook: puts the vector lights on screen once per frame, on the visible map.
//
// SAME HOOK AND SAME REASONING AS Patch_SnowGlareDraw and Patch_AuroraCurtainDraw — see those files
// for the full argument. In short, GameConditionManager.GameConditionManagerDraw is the exact point
// in Map.MapUpdate where vanilla draws its overlays, it is non-virtual, and it already sits inside
// vanilla's own `drawingMap && Find.CurrentMap == this` gate, so an off-screen map never pays.
//
// A third patch class on this method is the repo's normal shape rather than a smell; the three do not
// interact (aurora at night on a driver condition, glare in daylight over snow, lights wherever an
// emitter is in view).
//
// WHY NOT A MapComponent: Map.ExposeComponents scribes a permanent node per component, so deleting
// the type later logs two red errors per map forever — Source/MapComponent_SunShadowAxis.cs is the
// tombstone. §27 is a prototype that may not survive its own live A/B, so leaving no save-file
// residue is the only responsible choice.
[HarmonyPatch(typeof(GameConditionManager), nameof(GameConditionManager.GameConditionManagerDraw))]
public static class Patch_VectorLightDraw
{
    static void Postfix(GameConditionManager __instance, Map map)
    {
        // GameConditionManagerDraw recurses into its world-level Parent after drawing its own
        // conditions, so this fires twice per ordinary map. Without the identity check every light is
        // drawn twice per frame, which on an additive pass doubles the light rather than looking like
        // a bug — it reads as "the effect is too strong" and sends you to tune a constant.
        if (map == null || map.gameConditionManager != __instance)
            return;

        // Polygons are built HERE, once per frame, and never inside a section bake. §27 phase 3
        // reads them during a regenerate, and building one there charged 43 ms of geometry
        // construction to a whole-map rebake — the crossfade builds the same polygons on this path,
        // so its bake row never contained them and the two were not comparable. Doing it before the
        // draw also means the mask never has to wait a frame for a shadow it could have had now.
        // Re-dirtying after a build is not optional: a section that baked while a polygon was still
        // dirty skipped that emitter, and without this nothing would ever ask it to bake again. It
        // terminates on its own — the next frame builds nothing and so dirties nothing.
        //
        // ONLY THE SECTIONS THE REBUILT EMITTERS TOUCH, which is issue #188 item A. This was
        // WholeMapChanged, and the trouble with that is not its cost when a wall goes up — a wall
        // goes up rarely — but that door aperture tracking provokes it nine times per door swing.
        // The map-wide call regenerates every section under the camera whatever changed and wherever
        // it was, so a pawn opening a door on the far side of the colony rebaked the lighting
        // overlay, the darkness layer, night desaturation, eave shade and our own mask beneath the
        // player's cursor, for emitters none of which it could reach.
        if (VectorLightMask.Active)
            BuildAndDirty(map);

        VectorLightOverlay.Draw(map);

        // After the light, and on the same hook for the same reason: this is a per-frame draw whose
        // cost is proportional to what is on screen, and it needs the polygons the call above has
        // just made sure exist.
        VectorLightPawnShadows.Draw(map);
    }

    // Build this frame's dirty polygons and dirty whatever they changed. The two halves are one
    // method because they are one decision: the cull is only safe because the dirty is precise, and
    // the dirty is only cheap because the cull kept the build local.
    //
    // BOTH FLAGS OFF REPRODUCE THE ORIGINAL EXACTLY, which is what makes the arms a baseline rather
    // than a picture of the feature missing. The cull's "off" is the whole map as the build window,
    // not a skipped branch; the dirty's "off" is WholeMapChanged, not a wider rect.
    private static void BuildAndDirty(Map map)
    {
        SectionDirtyMath.CellBounds wholeMap =
            SectionDirtyMath.WholeMap(map.Size.x, map.Size.z);

        // Expanded by 1 to match MapDrawer.ViewRect, which is what decides whether a section
        // regenerates at all. Culling against the raw camera rect would leave the one-section fringe
        // that vanilla still regenerates baking against polygons we declined to build — a stale
        // strip at the edge of the screen, appearing only while scrolling, which is close to the
        // least debuggable symptom this subsystem could produce.
        SectionDirtyMath.CellBounds window = wholeMap;

        if (CelestialLightingFeatures.VectorLightViewCull)
        {
            CellRect view = Find.CameraDriver.CurrentViewRect.ExpandedBy(1);
            window = new SectionDirtyMath.CellBounds(view.minX, view.minZ, view.maxX, view.maxZ);
        }

        SectionDirtyMath.CellBounds touched = VectorLightField.EnsurePolygons(map, window);

        if (CelestialLightingFeatures.VectorLightSectionDirty)
        {
            DirtyBakedSections(map);
            return;
        }

        // The pre-item-A path, kept reachable rather than described. `Any` stands in for the bool
        // EnsurePolygons used to return: it is true exactly when something was built.
        if (!touched.Any)
            return;

        map.mapDrawer?.WholeMapChanged((ulong)MapMeshFlagDefOf.GroundGlow);

        // CHARGED FOR WHAT IT DIRTIED, even though vanilla never counts them. WholeMapChanged flags
        // every section on the map, so this arm's honest figure is the map's section count — and
        // without it the baseline would read 0 section dirties beside the new path's handful, which
        // is a feature-present/absent picture rather than a comparison.
        VectorLightField.SectionDirties +=
            SectionDirtyMath.SectionCount(map.Size.x, map.Size.z, Section.Size);
        VectorLightField.SectionDirtyPasses++;
    }

    // Turn the cell bounds the field handed back into section dirty flags. Thin, because everything
    // that could be got wrong here — the margin, the clip, the negative-coordinate truncation — is
    // in SectionDirtyMath where an offline test can reach it.
    //
    // MapMeshDirty rather than writing Section.dirtyFlags directly: it also raises globalDirtyFlags,
    // which is how the global (non-sectioned) draw layers learn anything happened. WholeMapChanged
    // did that too, and dropping it would have taken out those layers' updates in a way that shows
    // up only on whichever map layer nobody thought to look at.
    //
    // regenAdjacentCells and regenAdjacentSections are both FALSE on purpose. Both exist to paper
    // over a caller that knows a cell changed but not how far the consequences spread; we know
    // exactly how far, because SectionDirtyMath.Reach is the mask's own admission predicate solved
    // for the section, and the loop below already visits every section in that range. Asking for
    // adjacency on top would dirty a ring of sections that provably cannot look different — one
    // section's worth of margin on each side of the range, which on a small emitter is most of the
    // work back again.
    private static void DirtyBakedSections(Map map)
    {
        MapDrawer drawer = map.mapDrawer;

        if (drawer == null || VectorLightField.Dirtied.Count == 0)
            return;

        int across = (map.Size.x + Section.Size - 1) / Section.Size;
        int up = (map.Size.z + Section.Size - 1) / Section.Size;

        // ONE FLAG PER SECTION, NOT PER BOX. The boxes overlap constantly — a door swing changes the
        // shadow of a dozen lamps standing within a radius of each other — and MapMeshDirty is
        // idempotent, so a duplicate costs nothing on screen. It costs the MEASUREMENT: SectionDirties
        // is what the A/B is read on, and a section counted once per box that touched it would report
        // this change making things worse in precisely the scene it makes best.
        //
        // A bool array rather than a HashSet because the whole map is a few hundred entries and this
        // runs every frame; clearing it is cheaper than allocating a set's buckets once.
        if (Flagged.Length < across * up)
            Flagged = new bool[across * up];

        System.Array.Clear(Flagged, 0, across * up);

        ulong flags = (ulong)MapMeshFlagDefOf.GroundGlow;
        bool anyFlagged = false;

        for (int i = 0; i < VectorLightField.Dirtied.Count; i++)
        {
            anyFlagged |= DirtySections(drawer, map, VectorLightField.Dirtied[i], across, flags);
        }

        // Counted as a pass regardless of how many sections the frame flagged, so the ratio against
        // SectionDirties reads "sections per provocation" — which is the number item A moves, from
        // the map's whole section count down to a handful, and this one moves again.
        if (anyFlagged)
            VectorLightField.SectionDirtyPasses++;
    }

    // The per-frame section flags, reused rather than allocated. Safe as a static for the reason
    // VectorLightField.BakeBatch is: this runs once per frame on the main thread inside the draw,
    // and nothing in it survives the call.
    private static bool[] Flagged = new bool[0];

    private static bool DirtySections(
        MapDrawer drawer, Map map, SectionDirtyMath.CellBounds bounds, int across, ulong flags)
    {
        bool any = SectionDirtyMath.SectionRange(
            bounds, Section.Size, map.Size.x, map.Size.z,
            out int minSectionX, out int minSectionZ, out int maxSectionX, out int maxSectionZ);

        if (!any)
            return false;

        bool flagged = false;

        for (int sx = minSectionX; sx <= maxSectionX; sx++)
        {
            for (int sz = minSectionZ; sz <= maxSectionZ; sz++)
            {
                int slot = sz * across + sx;

                if (Flagged[slot])
                    continue;

                Flagged[slot] = true;
                flagged = true;
                VectorLightField.SectionDirties++;

                IntVec3 anchor = new IntVec3(
                    SectionDirtyMath.SectionAnchor(sx, Section.Size), 0,
                    SectionDirtyMath.SectionAnchor(sz, Section.Size));

                drawer.MapMeshDirty(anchor, flags, regenAdjacentCells: false, regenAdjacentSections: false);
            }
        }

        return flagged;
    }
}
