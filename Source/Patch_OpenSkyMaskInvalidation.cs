using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace CelestialLighting;

// The additive pass's mask invalidation: marks a map's open-sky mesh stale whenever a roof is
// written. Shared by every consumer of OpenSkyMask (§24's snow glare, §23b's cloud underlight), which
// is why it is named for the mask rather than for either of them.
//
// WHY PATCH RoofGrid RATHER THAN SUBSCRIBE TO A MapMeshFlagDef. Patch_ShadowRoofInvalidation solves
// the neighbouring problem by widening SectionLayer_SunShadows' `relevantChangeTypes` to include
// Roofs, which is the right answer THERE because that consumer is already a section layer with a
// subscription to widen. The open-sky mask is not a section layer — it is one whole-map mesh,
// deliberately, because the quantities drawn through it are whole-map means — so it has no
// subscription to widen and would have to become a section layer purely to receive the notification.
// Patching the two writers instead is two trivial postfixes against that structural change.
//
// It is also STRICTLY CHEAPER THAN THE FLAG, which is worth stating because the flag looks like the
// idiomatic answer. Patch_ShadowRoofInvalidation's own header records that `Roofs` is not the rare
// player-initiated flag it sounds like: `GlowGrid.DirtyCell` raises it alongside `GroundGlow`, so a
// lamp toggling rebuilds every subscriber. RoofGrid.SetRoof is the actual roof write, so this rebuilds
// on roofs and only on roofs — no wasted rebuild when a lamp flickers.
//
// MarkDirty is a dictionary probe and a bool write, and it does nothing at all for a map that has
// never drawn through the mask. The rebuild happens later, on the next frame that actually draws.
public static class Patch_OpenSkyMaskInvalidation
{
    // The ordinary write, used by construction, collapse, and map generation.
    [HarmonyPatch(typeof(RoofGrid), nameof(RoofGrid.SetRoof))]
    public static class SetRoof
    {
        static void Postfix(RoofGrid __instance) => MarkDirtyFor(__instance);
    }

    // The unchecked sibling. Patched as well rather than assumed unreachable: it is public, it is what
    // the name says it is, and a mask that silently missed one of the two writers would show up as
    // an additive pass lingering over a collapsed roof — a bug that looks like a rendering fault
    // rather than a missing subscription, which is the most expensive kind to chase.
    [HarmonyPatch(typeof(RoofGrid), nameof(RoofGrid.RemoveRoofUnsafe))]
    public static class RemoveRoofUnsafe
    {
        static void Postfix(RoofGrid __instance) => MarkDirtyFor(__instance);
    }

    // RoofGrid holds its map in a private field, so it is reached through the current game's map list
    // rather than by reflection: whichever loaded map owns this grid is the one to dirty. The list is
    // at most a handful of entries and this runs on a roof write, not per frame.
    private static void MarkDirtyFor(RoofGrid grid)
    {
        if (grid == null || Current.Game == null)
            return;

        List<Map> maps = Find.Maps;
        if (maps == null)
            return;

        for (int i = 0; i < maps.Count; i++)
        {
            if (maps[i]?.roofGrid == grid)
            {
                OpenSkyMask.MarkDirty(maps[i]);
                return;
            }
        }
    }
}
