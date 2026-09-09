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
// WHERE THE EDIT HAPPENS. Two places, one at a time. Patch_LightingOverlayStore reaches vanilla's
// colour array from inside GenerateLightingOverlay, before the store, and calls EditInPlace on it;
// the postfix below then finds its mesh already handled and returns. With that flag off, or on a
// build where the transpiler did not apply, the postfix reads the mesh back and makes the same edit
// on the copy, as it always did. The edit itself is shared; only the round trip differs.
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

        // The in-place hook already made this edit on the array vanilla stored. Nothing to read
        // back, nothing to write.
        if (Patch_LightingOverlayStore.TakeHandled(mesh))
            return;

        // The flag asked for the hook and the hook did not run for this mesh: the transpiler did
        // not apply, or something else stored the colours. Counted so an arm can see it, then the
        // postfix path exactly as before.
        if (CelestialLightingFeatures.VectorLightOverlayInPlace)
            Patch_LightingOverlayStore.Misses++;

        // §27 phase 3 takes over this method entirely when it is on: it edits the same vertex colours
        // rather than zeroing them, and the two must not both run. Handing over here rather than in a
        // separate patch keeps a single writer for this mesh, which is what stops the ordering
        // between them from being a live concern.
        if (VectorLightMask.Active && ApplyMask(__instance, subMesh))
            return;

        if (!CelestialLightingFeatures.VectorLightSuppress)
            return;

        Color32[] colors = mesh.colors32;

        if (colors == null)
            return;

        Crossfade(colors);

        mesh.colors32 = colors;
    }

    // The same edit as the postfix, on vanilla's own array before it is stored. Called by
    // Patch_LightingOverlayStore with the map and clipped rect GenerateLightingOverlay was given,
    // which are the section's own -- the postfix rebuilds exactly these from the Section.
    internal static void EditInPlace(Map map, Color32[] colors, CellRect rect)
    {
        if (VectorLightMask.Active && VectorLightMask.ApplyTo(map, colors, rect))
            return;

        if (!CelestialLightingFeatures.VectorLightSuppress)
            return;

        Crossfade(colors);
    }

    // The crossfade keeps a fraction of vanilla's flood underneath instead of removing it. Zero
    // is the original behaviour and is what the arithmetic below reduces to when the flag is off,
    // so there is one code path rather than two.
    private static void Crossfade(Color32[] colors)
    {
        float floor = CelestialLightingFeatures.VectorLightBlend
            ? VectorLightMath.DefaultVanillaFloor
            : 0f;

        for (int i = 0; i < colors.Length; i++)
        {
            colors[i].r = VectorLightMath.FlooredChannel(colors[i].r, floor);
            colors[i].g = VectorLightMath.FlooredChannel(colors[i].g, floor);
            colors[i].b = VectorLightMath.FlooredChannel(colors[i].b, floor);
        }
    }

    // The section's own cell rect, rebuilt the way vanilla builds it rather than reflected out of
    // the private field that holds it: SectionLayer_LightingOverlay.Regenerate computes exactly this
    // on the first regenerate and caches it, so recomputing costs nothing and reads without a
    // FieldRef that a version change could quietly break.
    private static bool ApplyMask(SectionLayer_LightingOverlay layer, LayerSubMesh subMesh)
    {
        Section section = SectionLayerAccess.GetSection(layer);

        if (section == null)
            return false;

        CellRect rect = new CellRect(section.botLeft.x, section.botLeft.z, 17, 17);
        rect.ClipInsideMap(section.map);

        return VectorLightMask.Apply(section.map, subMesh.mesh, subMesh.verts, rect);
    }
}
