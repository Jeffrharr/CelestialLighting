using HarmonyLib;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// §27's other half: stop vanilla drawing the artificial light that §27 has taken over.
//
// WHY THIS IS NEEDED AT ALL. Without it both models draw at once, and vanilla's is the one that wins
// where it matters: its geodesic flood has already put light in every cell around the corner that
// §27 just carved a shadow into, so every shadow fills back in from underneath and the wedge through
// a doorway sits inside a blob that ignores the doorway. There is no additive trick that removes
// light, so the only way to have vector shadows is for vanilla's render not to be there.
//
// WHAT IS ACTUALLY BEING TOUCHED, and why it is safe. SectionLayer_LightingOverlay packs two
// unrelated things into one mesh: the RGB of each vertex is the artificial glow, averaged from
// GlowGrid.VisualGlowAt over the cells meeting at that lattice point, and the ALPHA is the sky-cover
// term (RoofedAreaMinSkyCover = 100). We zero the RGB and do not touch the alpha, so what every cell
// ends up in is the state an UNLIT cell already has in vanilla — an existing, well-defined state
// rather than a novel one. §7b's occlusion alpha, §7c/§7d's falloff, §9's wash and the sky colour all
// keep working exactly as before.
//
// GAMEPLAY LIGHT IS UNTOUCHED. map.glowGrid is not read, written or invalidated here.
// GroundGlowAt/PsychGlowAt/VisualGlowAt return what they always did, so plant growth, work speed,
// mood, StatPart_Glow, DarklightUtility, unnatural darkness and every mod reading them see no change
// at all. §27 is a render, which is the whole reason it is allowed to be this opinionated.
//
// ORDERING. Patch_IndoorSkyOcclusion postfixes this same method at Priority.First and touches only
// alpha, so the two do not contend — but running last means we are also after Dub's Skylights and
// Biomes! Caverns, both of which bracket or transpile this method and both of which are already in
// About.xml's loadAfter.
[HarmonyPatch(typeof(SectionLayer_LightingOverlay), nameof(SectionLayer_LightingOverlay.Regenerate))]
public static class Patch_VectorLightSuppress
{
    [HarmonyPriority(Priority.Last)]
    static void Postfix(SectionLayer_LightingOverlay __instance)
    {
        if (!CelestialLightingFeatures.VectorLights)
            return;

        LayerSubMesh subMesh = __instance.GetSubMesh(MatBases.LightOverlay);
        Mesh mesh = subMesh?.mesh;

        if (mesh == null)
            return;

        Color32[] colors = mesh.colors32;

        if (colors == null)
            return;

        for (int i = 0; i < colors.Length; i++)
        {
            colors[i].r = 0;
            colors[i].g = 0;
            colors[i].b = 0;
        }

        mesh.colors32 = colors;
    }
}
