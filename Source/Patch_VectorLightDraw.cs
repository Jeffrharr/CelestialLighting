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
            DirtyTouchedSections(map, VectorLightField.EnsurePolygons(map));

        VectorLightOverlay.Draw(map);

        // After the light, and on the same hook for the same reason: this is a per-frame draw whose
        // cost is proportional to what is on screen, and it needs the polygons the call above has
        // just made sure exist.
        VectorLightPawnShadows.Draw(map);
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
    private static void DirtyTouchedSections(Map map, SectionDirtyMath.CellBounds touched)
    {
        MapDrawer drawer = map.mapDrawer;

        if (drawer == null)
            return;

        bool any = SectionDirtyMath.SectionRange(
            touched, Section.Size, map.Size.x, map.Size.z,
            out int minSectionX, out int minSectionZ, out int maxSectionX, out int maxSectionZ);

        if (!any)
            return;

        ulong flags = (ulong)MapMeshFlagDefOf.GroundGlow;

        for (int sx = minSectionX; sx <= maxSectionX; sx++)
        {
            for (int sz = minSectionZ; sz <= maxSectionZ; sz++)
            {
                IntVec3 anchor = new IntVec3(
                    SectionDirtyMath.SectionAnchor(sx, Section.Size), 0,
                    SectionDirtyMath.SectionAnchor(sz, Section.Size));

                drawer.MapMeshDirty(anchor, flags, regenAdjacentCells: false, regenAdjacentSections: false);
            }
        }
    }
}
