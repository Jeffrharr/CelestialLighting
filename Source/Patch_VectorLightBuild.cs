using HarmonyLib;
using Verse;

namespace CelestialLighting;

// Issue #218's fix: build this frame's vector-light polygons at the last moment BEFORE the sections
// that read them regenerate, rather than after.
//
// WHY THIS HOOK. Map.MapUpdate's order is glowGrid.GlowGridUpdate_First (vanilla's flood recomputes)
// → mapDrawer.MapMeshDrawerUpdate_First (dirty sections regenerate, which is where VectorLightMask
// bakes) → mapDrawer.DrawMapMesh → gameConditionManager.GameConditionManagerDraw (where
// Patch_VectorLightDraw runs). Rebuilding polygons in the draw therefore landed a whole step after
// the bake that reads them, and on any frame a door moved a section baked vanilla's fresh glow
// against our stale coverage — see CelestialLightingFeatures.VectorLightBuildFirst for the full
// mechanism and what it looked like on screen. A prefix on MapMeshDrawerUpdate_First is the last
// point in the frame that is still ahead of the regenerate.
//
// THE GATE IS INHERITED, NOT REBUILT. MapMeshDrawerUpdate_First has exactly one caller, and it sits
// inside Map.MapUpdate's own `drawingMap && Find.CurrentMap == this` block — the same gate
// Patch_VectorLightDraw relies on. So this fires once per frame for the map on screen and never for
// one that is not, which is the property the draw hook was chosen for in the first place.
//
// THE VIEW CULL STILL MEANS WHAT IT MEANT, and this is the change's one non-obvious claim.
// BuildAndDirty culls against Find.CameraDriver.CurrentViewRect.ExpandedBy(1) because that is
// MapDrawer.ViewRect, which decides whether a section regenerates at all. Reading it here is not
// merely as good as reading it in the draw — it is strictly better, because vanilla reads the same
// property a few instructions later inside the very method this prefixes. The failure the cull risks
// is a stale strip at the screen edge while scrolling, and that risk shrinks rather than grows.
//
// WHY NOT A TRANSPILER OR A POSTFIX. A postfix is the bug: the sections have already regenerated. A
// transpiler would let the build land between the global-layer pass and the section pass, which buys
// nothing — the mask is a section layer, so being ahead of the whole method is ahead of everything
// that reads a polygon.
[HarmonyPatch(typeof(MapDrawer), nameof(MapDrawer.MapMeshDrawerUpdate_First))]
public static class Patch_VectorLightBuild
{
    // ___map is MapDrawer's private backing field, injected by name. Taken from the drawer rather
    // than read off Find.CurrentMap so the build is charged to the map whose sections are about to
    // regenerate even if some future caller ever runs this for another one — the identity check
    // Patch_VectorLightDraw needs for the same reason, arrived at from the other direction.
    static void Prefix(Map ___map)
    {
        if (___map == null || !CelestialLightingFeatures.VectorLightBuildFirst)
            return;

        if (!VectorLightMask.Active)
            return;

        Patch_VectorLightDraw.BuildAndDirty(___map);
    }
}
