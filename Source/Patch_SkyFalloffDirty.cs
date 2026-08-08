using HarmonyLib;
using Verse;

namespace CelestialLighting;

// Invalidation for §7c's whole-map BFS cache (NativeSkyFalloffGrid). Mirrors
// AmbientLightFalloff.DirtyHooks's own validated trigger set (decompiled in full this session, see the
// Epic B plan) rather than inventing a new one: roof changes, plus spawn/despawn of anything that can
// change what NativeSkyFalloffGrid.BlocksSky reads for a cell -- a wall (holdsRoof) or a door
// (AltitudeLayer.DoorMoveable). Nothing else NativeSkyFalloffGrid.BlocksSky reads can change from a
// Thing spawning or despawning, so filtering here (rather than patching Thing-wide, as Ambient Light's
// own OnThingSpawn/OnThingDespawn do) keeps an ordinary item drop or a pawn walking by from touching
// this path at all.
//
// Every patch here only flips a bool (NativeSkyFalloffGrid.MarkDirty) -- the actual BFS rebuild is
// deferred to the next DepthAt call that needs it, so none of these three hooks do real work.
public static class Patch_SkyFalloffDirty
{
    // RoofGrid.SetRoof(IntVec3, RoofDef) -- confirmed against the current Assembly-CSharp.dll this
    // session (same signature Patch_ShadowRoofInvalidation's header already documents). No `c` or
    // `def` argument needed here: any roof change anywhere on the map can shift the BFS's seed set, so
    // there is nothing narrower worth checking.
    [HarmonyPatch(typeof(RoofGrid), nameof(RoofGrid.SetRoof))]
    private static class SetRoof
    {
        // RoofGrid's `map` backing field is private, so it has to come in via Harmony's `___map`
        // injection rather than a property read.
        private static void Postfix(Map ___map) =>
            NativeSkyFalloffGrid.MarkDirty(___map);
    }

    // Building.SpawnSetup(Map, bool) -- not Thing.SpawnSetup: Building overrides it directly
    // (decompiled to confirm), and every holdsRoof/door Thing is a Building by construction, so
    // patching the narrower type still catches every case NativeSkyFalloffGrid.BlocksSky cares about.
    [HarmonyPatch(typeof(Building), nameof(Building.SpawnSetup))]
    private static class SpawnSetup
    {
        private static void Postfix(Building __instance, Map map)
        {
            if (RelevantToFalloff(__instance))
                NativeSkyFalloffGrid.MarkDirty(map);
        }
    }

    // A Prefix, not a Postfix: DeSpawn clears __instance.Map as part of what it does, so the map has to
    // be read before the call runs. Nothing here needs to know despawn actually succeeded -- marking
    // dirty a tick early only costs one wasted dirty flag if a mod cancels the despawn, never a stale
    // cache.
    [HarmonyPatch(typeof(Building), nameof(Building.DeSpawn))]
    private static class DeSpawn
    {
        private static void Prefix(Building __instance)
        {
            if (RelevantToFalloff(__instance) && __instance.Map != null)
                NativeSkyFalloffGrid.MarkDirty(__instance.Map);
        }
    }

    // The only two edifice-derived inputs NativeSkyFalloffGrid.BlocksSky reads (holdsRoof, isDoor) --
    // kept as one predicate so SpawnSetup and DeSpawn cannot drift apart on what counts as relevant.
    private static bool RelevantToFalloff(Building building) =>
        building.def.holdsRoof || building.def.altitudeLayer == AltitudeLayer.DoorMoveable;
}
