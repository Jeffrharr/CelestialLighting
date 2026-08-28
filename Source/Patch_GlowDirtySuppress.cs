using HarmonyLib;
using Verse;

namespace CelestialLighting;

// Declines the SECTION half of a MapMeshDirty raised by our own write to vanilla's light-blocker bit.
// See GlowDirtyScope for what this is for, what it leaves alone, and where it can go wrong.
//
// WHY A PREFIX ON MapMeshDirty RATHER THAN ON GlowGrid.DirtyCell, which is the more obvious target.
// DirtyCell does four things and we want three of them: it sets dirtyCells (so vanilla re-floods), it
// raises anyDirtyCell (likewise), it fires Notify_GlowChanged (which other mods and vanilla's own
// region code listen to), and it dirties the map mesh. Skipping DirtyCell would take out the three
// that make gameplay light correct along with the one that costs us, and this whole change is only
// defensible because gameplay light is untouched.
//
// THE FOUR-ARGUMENT OVERLOAD IS THE RIGHT TARGET, and the two-argument one must not be patched as
// well: it computes regenAdjacentCells from the flags and then calls this one, so patching both would
// count every suppression twice and report a fan-out that is not happening.
//
// MapDrawer is sealed and this method is public and non-virtual, so there is no dispatch to lose and
// no derived class to miss. It is also a method we call ourselves from Patch_VectorLightDraw, which is
// exactly why the scope is a depth counter set around two specific call sites rather than a mode:
// our own section flagging runs inside the draw, far outside any window this opens, and must keep
// working normally.
[HarmonyPatch(typeof(MapDrawer), nameof(MapDrawer.MapMeshDirty))]
[HarmonyPatch(new[] { typeof(IntVec3), typeof(ulong), typeof(bool), typeof(bool) })]
public static class Patch_GlowDirtySuppress
{
    static bool Prefix(MapDrawer __instance, IntVec3 loc, ulong dirtyFlags, bool regenAdjacentCells)
    {
        bool suppressing = CelestialLightingFeatures.VectorLightDoorDirtySuppress
            && GlowDirtyScope.Active;

        if (!suppressing)
        {
            return true;
        }

        // THE GLOBAL FLAGS ARE STILL RAISED. MapMeshDirty ORs into globalDirtyFlags as well as into
        // the sections, and that OR is what tells the map's non-sectioned draw layers anything
        // happened. Dropping it costs nothing measurable and breaks whichever global layer nobody
        // thought to check — the same trap Patch_VectorLightDraw's header records for the move away
        // from WholeMapChanged. Doing it here by hand is the price of skipping the original.
        MapDrawerAccess.RaiseGlobalDirtyFlags(__instance, dirtyFlags);

        GlowDirtyScope.NoteSuppressed(MapDrawerAccess.GetMap(__instance), loc, regenAdjacentCells);
        return false;
    }
}
