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

        Section section = SectionLayerAccess.GetSection(__instance);
        Map map = section?.map;

        if (map == null)
            return;

        // Somebody else draws this map's lighting overlay, so vanilla's mesh is baked and discarded
        // and suppressing light in it would be invisible work. AsAboveSoBelowCompat postfixes the
        // layer they do draw and calls ApplyToMesh below on it. See that file's header.
        if (AsAboveSoBelowCompat.OwnsOverlay(map))
            return;

        CellRect rect = new CellRect(section.botLeft.x, section.botLeft.z, Section.Size, Section.Size);
        rect.ClipInsideMap(map);

        ApplyToMesh(map, __instance.GetSubMesh(MatBases.LightOverlay), rect);
    }

    // The suppression, over whichever lighting-overlay mesh is the one actually being drawn.
    //
    // TWO CALLERS, ONE DEFINITION, for the same reason Patch_IndoorSkyOcclusion.ApplyToMesh has two:
    // the postfix above owns vanilla's mesh, and AsAboveSoBelowCompat owns the one As above, So below
    // II draws in its place on a banded map.
    //
    // TAKING A LayerSubMesh RATHER THAN A COLOUR ARRAY IS WHAT MAKES THE MASK REACHABLE HERE. Vector
    // lighting has two suppression paths: the flooring below is per-vertex and needs no geometry, but
    // the mask — which EDITS vertex colours per emitter instead of zeroing them, and is the shipped
    // default — needs the vertex list. Handing over a bare Color32[] would deliver banded maps the
    // pre-mask behaviour and quietly call it done. Their layer caches the LayerSubMesh vanilla's own
    // Bake built for it, so there is no reason to settle for that.
    //
    // Returns whether it wrote.
    internal static bool ApplyToMesh(Map map, LayerSubMesh subMesh, CellRect rect)
    {
        Mesh mesh = subMesh?.mesh;

        if (mesh == null)
            return false;

        // §27 phase 3 takes over this method entirely when it is on: it edits the same vertex colours
        // rather than zeroing them, and the two must not both run. Handing over here rather than in a
        // separate patch keeps a single writer for this mesh, which is what stops the ordering
        // between them from being a live concern.
        if (VectorLightMask.Active && VectorLightMask.Apply(map, mesh, subMesh.verts, rect))
            return true;

        if (!CelestialLightingFeatures.VectorLightSuppress)
            return false;

        // The crossfade keeps a fraction of vanilla's flood underneath instead of removing it. Zero
        // is the original behaviour and is what the arithmetic below reduces to when the flag is off,
        // so there is one code path rather than two.
        Color32[] colors = mesh.colors32;

        if (!FloorChannels(colors, CurrentFloor()))
            return false;

        mesh.colors32 = colors;
        return true;
    }

    // The flooring itself, over a colour array rather than a mesh: the pass is purely per-vertex, so
    // it floors each of the three artificial-light channels and reads no geometry at all. Split out
    // so an offline test can reach it without a Mesh.
    //
    // Returns whether it wrote. False only for a null array.
    internal static bool FloorChannels(Color32[] colors, float floor)
    {
        if (colors == null)
            return false;

        for (int i = 0; i < colors.Length; i++)
        {
            colors[i].r = VectorLightMath.FlooredChannel(colors[i].r, floor);
            colors[i].g = VectorLightMath.FlooredChannel(colors[i].g, floor);
            colors[i].b = VectorLightMath.FlooredChannel(colors[i].b, floor);
        }

        return true;
    }

    // The crossfade floor the suppression uses right now — one reader, so no caller can drift from
    // another.
    internal static float CurrentFloor() =>
        CelestialLightingFeatures.VectorLightBlend ? VectorLightMath.DefaultVanillaFloor : 0f;

}
