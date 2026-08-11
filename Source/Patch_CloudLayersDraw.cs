using HarmonyLib;
using RimWorld;
using Verse;

namespace CelestialLighting;

// The cloud lanes' draw hook: draws §23b's underlight, §23c's daylight shadows and §25's cloud sheet
// once per frame on the visible map.
//
// ONE PATCH FOR THREE LANES, unlike §11a and §24 which each have their own. They share a field, a
// mask, a drift clock and a cache, and — more to the point — they are three statements about ONE cloud
// deck, so an ordering question between them is a real question rather than an accident of which
// Harmony patch ran first. Ordering them here, in one place, in the order they physically compose
// (light on the ground first, then the sky above it) is worth more than the symmetry of a class each.
//
// The first two are mutually exclusive in time anyway — underlight needs the sun below the horizon,
// shadows need it above — so on any frame at most two of the three actually draw.
//
// THE SAME HOOK AND THE SAME REASONING AS Patch_SnowGlareDraw and Patch_AuroraCurtainDraw — see the
// latter's header for the full argument against GameCondition.SkyOverlays. The short version is that
// GameConditionManager.GameConditionManagerDraw is the exact point in Map.MapUpdate where vanilla
// draws its own overlays, it is non-virtual, and it is already inside vanilla's own
// `drawingMap && Find.CurrentMap == this` gate, so an off-screen map never pays anything.
//
// A THIRD patch class on one vanilla method is this repo's normal shape rather than a smell (CLAUDE.md
// notes most of our patches already share two vanilla members), and these three cannot interact: the
// aurora draws at night on a driver condition, glare draws in daylight over snow, and this draws in
// the few minutes either side of a cloudy sunset. Nothing about any of them reads what the others
// wrote — they are three independent additive contributions to the same frame, which is exactly the
// composition epic #103 describes.
//
// WHY NOT A MapComponent, the other obvious way to get a per-frame callback: Map.ExposeComponents
// scribes a permanent node per component, so deleting the type later logs two red errors per map
// forever (Source/MapComponent_SunShadowAxis.cs is the tombstone that documents this). §23b ships off
// and issue #88 explicitly allows for "this reads as mud" being the answer, so leaving no save-file
// residue is the only responsible choice.
[HarmonyPatch(typeof(GameConditionManager), nameof(GameConditionManager.GameConditionManagerDraw))]
public static class Patch_CloudLayersDraw
{
    static void Postfix(GameConditionManager __instance, Map map)
    {
        // GameConditionManagerDraw recurses into its Parent (the world-level manager) to draw that
        // manager's own conditions, so this postfix fires once per manager in the chain — twice for an
        // ordinary colony map. The identity guard makes the second pass a no-op, which is what keeps
        // the layer from being drawn (and therefore added) twice.
        if (map == null || map.gameConditionManager != __instance)
            return;

        // Order is composition order, not importance. §23b and §23c both change the light reaching the
        // ground and are drawn at VisEffects, under FogOfWar; §25 draws the deck itself above FogOfWar,
        // i.e. between the camera and everything the other two just did. Reversing them would put a
        // cloud's own shadow on top of the cloud.
        CloudUnderlightOverlay.Draw(map);
        CloudShadowOverlay.Draw(map);
        CloudSheetOverlay.Draw(map);
    }
}
