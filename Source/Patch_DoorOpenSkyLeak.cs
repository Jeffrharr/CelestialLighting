using HarmonyLib;
using RimWorld;
using Verse;

namespace CelestialLighting;

// §7b's per-door leak (IndoorOcclusionMath.DoorSkyLeakFor) now reads Building_Door.Open, but the sky
// cover alpha it feeds is baked into a section mesh, not recomputed per frame — exactly the "toggling
// a setting has no visible effect until something else dirties the map" trap IndoorOcclusionRedraw
// exists to solve for the settings sliders. Nothing in vanilla dirties GroundGlow when a door's own
// open/closed state changes (its animation is a per-frame draw offset, not a mesh rewrite), so without
// these patches a door opening or closing would leave the wrong alpha baked until an unrelated event —
// a lamp toggling, a fire starting — happened to regenerate that section.
//
// Verse.MapEvents.Notify_DoorOpened/Notify_DoorClosed are the narrowest hook: Building_Door.DoorOpen
// and DoorTryClose only call them on an actual state transition (DoorOpen no-ops if already open;
// DoorTryClose can refuse — held open, something blocking the threshold — and skips the notify when it
// does), so this never dirties a section for a door that did not actually move. Split into two patch
// classes, one per notify method, following this repo's one-patch-point-per-class convention rather
// than a single class patching both by method name.
public static class DoorOpenSkyLeakRedraw
{
    // A single cell's worth of dirty, not WholeMapChanged: DESIGN.md's own profiling table calls out
    // WholeMapChanged(GroundGlow) as the expensive, every-section option, appropriate for a settings
    // change but not for an event a colony can generate dozens of times a minute just by walking
    // around. regenAdjacentCells: true (matching vanilla's own default for a Buildings-shaped change)
    // covers a door sitting on a section boundary, whose corner lattice reads one cell into the
    // neighbouring section.
    public static void Redraw(Building_Door door)
    {
        if (!CelestialLightingFeatures.IndoorSkyOcclusion)
            return;

        Map map = door.Map;
        if (map == null)
            return;

        map.mapDrawer?.MapMeshDirty(door.Position, (ulong)MapMeshFlagDefOf.GroundGlow, true, false);
    }
}

[HarmonyPatch(typeof(MapEvents), nameof(MapEvents.Notify_DoorOpened))]
public static class Patch_DoorOpenedSkyLeak
{
    static void Postfix(Building_Door door) => DoorOpenSkyLeakRedraw.Redraw(door);
}

[HarmonyPatch(typeof(MapEvents), nameof(MapEvents.Notify_DoorClosed))]
public static class Patch_DoorClosedSkyLeak
{
    static void Postfix(Building_Door door) => DoorOpenSkyLeakRedraw.Redraw(door);
}
