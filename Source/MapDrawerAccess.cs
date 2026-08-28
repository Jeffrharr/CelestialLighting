using HarmonyLib;
using Verse;

namespace CelestialLighting;

// Verse.MapDrawer.globalDirtyFlags is private with no public accessor, and the door dirty
// suppression needs to keep raising it while declining to flag any section.
//
// WHY IT CANNOT SIMPLY BE DROPPED ALONG WITH THE REST. MapMeshDirty does two unrelated things in one
// method: it ORs the flags into the sections around a cell, and it ORs them into globalDirtyFlags,
// which is what MapMeshDrawerUpdate_First reads to decide whether the map's NON-sectioned draw layers
// need regenerating. Only the first of those costs anything here — a section flag is a promise to
// re-run every layer on that section, which is where the mask's per-regenerate millisecond goes —
// while the second is a single OR that global layers depend on. A prefix that skipped the whole
// method would silently stop updating whichever global layer nobody thought to check, which is the
// same failure mode Patch_VectorLightDraw's header records for WholeMapChanged.
//
// Kept as its own file on SectionLayerAccess's precedent: the next patch that needs to reach inside
// MapDrawer should reuse this rather than write a second FieldRef for the same field.
public static class MapDrawerAccess
{
    private static readonly AccessTools.FieldRef<MapDrawer, ulong> GlobalDirtyFlagsField =
        AccessTools.FieldRefAccess<MapDrawer, ulong>("globalDirtyFlags");

    public static void RaiseGlobalDirtyFlags(MapDrawer drawer, ulong dirtyFlags)
    {
        GlobalDirtyFlagsField(drawer) |= dirtyFlags;
    }

    // The map a drawer belongs to. Also private, and needed for the same reason: a Harmony patch on
    // MapMeshDirty is handed the drawer and nothing else, while the section arithmetic it has to
    // account for needs the map's size.
    private static readonly AccessTools.FieldRef<MapDrawer, Map> MapField =
        AccessTools.FieldRefAccess<MapDrawer, Map>("map");

    public static Map GetMap(MapDrawer drawer) => MapField(drawer);
}
