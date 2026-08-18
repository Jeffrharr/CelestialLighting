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

        VectorLightOverlay.Draw(map);
    }
}
