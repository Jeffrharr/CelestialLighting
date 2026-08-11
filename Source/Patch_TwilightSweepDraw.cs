using HarmonyLib;
using RimWorld;
using Verse;

namespace CelestialLighting;

// §26's draw hook (issue #140): draws the twilight sweep once per frame on the visible map.
//
// ITS OWN PATCH CLASS RATHER THAN A FOURTH LINE IN Patch_CloudLayersDraw, which is a deliberate
// split. That file groups §23b, §23c and §25 because they are three statements about ONE cloud deck,
// so the order between them is a real question — a cloud's shadow must not draw on top of the cloud.
// §26 is not a statement about cloud at all: it draws on a cloudless evening, it reads no cloud
// fraction, and it would still be correct on a map where the weather system was disabled entirely.
// Filing it under the cloud hook would imply a coupling that does not exist and would make the cloud
// lanes' gate order read as if it applied here too.
//
// ORDER AGAINST THOSE THREE IS NOT A QUESTION, which is what makes the split safe. §26 and the two
// illumination lanes are all ADDITIVE draws at the same altitude, and addition commutes — whichever
// Harmony patch runs first, the frame sums to the same pixels. §25 draws above FogOfWar and is sorted
// there by altitude rather than by call order.
//
// SAME HOOK AND SAME REASONING AS Patch_SnowGlareDraw and Patch_AuroraCurtainDraw — see the latter's
// header for the full argument against GameCondition.SkyOverlays. The short version is that
// GameConditionManager.GameConditionManagerDraw is the exact point in Map.MapUpdate where vanilla
// draws its own overlays, it is non-virtual, and it is already inside vanilla's own
// `drawingMap && Find.CurrentMap == this` gate, so an off-screen map never pays for this at all.
//
// WHY NOT A MapComponent, the other obvious way to get a per-frame callback: Map.ExposeComponents
// scribes a permanent node per component, so deleting the type later logs two red errors per map
// forever (Source/MapComponent_SunShadowAxis.cs is the tombstone that documents this). §26 ships off
// and issue #140 explicitly allows "it reads as a rendering artifact" to be the answer, so leaving no
// save-file residue is the only responsible choice for a prototype that may be deleted.
[HarmonyPatch(typeof(GameConditionManager), nameof(GameConditionManager.GameConditionManagerDraw))]
public static class Patch_TwilightSweepDraw
{
    static void Postfix(GameConditionManager __instance, Map map)
    {
        // GameConditionManagerDraw recurses into its Parent (the world-level manager) to draw that
        // manager's own conditions, so this postfix fires once per manager in the chain — twice for an
        // ordinary colony map. The identity guard makes the second pass a no-op, which is what keeps
        // the quad from being drawn (and therefore added) twice: a doubled additive pass reads as a
        // stronger effect rather than as a bug, so nothing downstream would catch it.
        if (map == null || map.gameConditionManager != __instance)
            return;

        TwilightSweepOverlay.Draw(map);
    }
}
