using HarmonyLib;
using RimWorld;
using Verse;

namespace CelestialLighting;

// §24's draw hook (issue #90): draws the additive snow-glare quad once per frame on the visible map.
//
// SAME HOOK AND THE SAME REASONING AS Patch_AuroraCurtainDraw, deliberately — see that file's header
// for the full argument against GameCondition.SkyOverlays. The short version is that
// GameConditionManager.GameConditionManagerDraw is the exact point in Map.MapUpdate where vanilla
// draws its overlays, it is non-virtual, and it is already inside vanilla's own
// `drawingMap && Find.CurrentMap == this` gate, so an off-screen map never pays for this at all. A
// second patch class on the same method is the repo's normal shape rather than a smell: CLAUDE.md
// notes most of our patches already share two vanilla members, and these two do not interact — the
// aurora draws at night on a driver condition, glare draws in daylight over snow.
//
// WHY NOT A MapComponent, which is the other obvious way to get a per-frame callback:
// Map.ExposeComponents scribes a permanent node per component, so deleting the type later logs two
// red errors per map forever (Source/MapComponent_SunShadowAxis.cs is the tombstone that documents
// this). For a PROTOTYPE that may well be deleted after the live A/B — issue #90 explicitly expects
// "reads as washed out" to be a possible outcome — a postfix that leaves no save-file residue is the
// only responsible choice.
[HarmonyPatch(typeof(GameConditionManager), nameof(GameConditionManager.GameConditionManagerDraw))]
public static class Patch_SnowGlareDraw
{
    static void Postfix(GameConditionManager __instance, Map map)
    {
        // GameConditionManagerDraw recurses into its Parent (the world-level manager) after drawing
        // its own conditions, so this postfix fires once per manager in the chain — twice for an
        // ordinary map. Comparing identity keeps us to the map's own pass; without it the glare quad
        // is drawn twice per frame, doubling both the draw call and the additive contribution, which
        // would read as a brighter effect rather than as a bug.
        if (map == null || map.gameConditionManager != __instance)
            return;

        SnowGlareOverlay.Draw(map);
    }
}
