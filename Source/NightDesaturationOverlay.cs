using UnityEngine;
using Verse;

namespace CelestialLighting;

// The material §9's per-cell wash is drawn with, plus the one per-frame value written into it.
//
// Split this way for the same reason vanilla splits its lighting overlay: the per-CELL part (which
// cells are unlit) is baked into a section mesh and only changes when the glow grid does, while the
// map-wide part (how far into rod vision the sky is right now) changes continuously through dusk.
// Baking the second into the mesh would mean rebuilding every section's mesh every few minutes of
// game time; putting it in the shared material's alpha costs one assignment per frame, and the
// shader multiplies material colour by vertex colour for free.
// Same mandatory attribute, same reason, as EaveShadeOverlay — see the full account there. RimWorld
// logs the identical warning for this type; it has simply been lucky about which thread happened to
// touch the field first, since Patch_NightDesaturationStrength writes the material every frame from
// the main thread. Relying on that ordering is a latent map-generation crash, so pin it.
[StaticConstructorOnStartup]
public static class NightDesaturationOverlay
{
    // Our own Material instance, not a pooled or vanilla one. MaterialPool hands out shared
    // instances keyed on their request, so mutating a pooled material's colour every frame would
    // reach into whatever else drew with it; and MatBases.LightOverlay — the obvious candidate —
    // is the wrong blend mode entirely (it multiplies, which is why the tint approach could not
    // desaturate). ShaderDatabase.Transparent is ordinary alpha blending, and alpha blending IS the
    // lerp-toward-grey that desaturating requires.
    //
    // BaseContent.WhiteTex keeps the sample flat, so the colour comes from the material and the
    // per-vertex alpha alone rather than from any texture pattern.
    private static readonly Material WashMaterial = BuildWashMaterial();

    public static Material Material => WashMaterial;

    // Whether the wash would put anything on screen right now. Read by
    // SectionLayer_NightDesaturation.DrawLayer to skip the submission entirely; see there for why the
    // skip lives at the draw and not in Visible.
    //
    // WITH DarkAreaDesaturation OFF this is false for the whole of daylight, because the material
    // carries PurkinjeMath.PurkinjeFactor, an InverseLerpClamped reaching exactly 0 at OnsetGlow —
    // not merely small, so it is a real "nothing to draw" rather than a threshold anyone tuned.
    //
    // WITH IT ON the material is a constant (strength x MaxWash) and this stays true at noon, because
    // an enclosed room genuinely has something to draw at noon — that is the whole feature. The
    // daylight skip does not disappear, it moves down a level to where it can still be made per
    // section: SectionLayer_NightDesaturation disables the submesh of any section whose vertices all
    // baked to zero, so an outdoor map at midday still submits nothing.
    public static bool Drawing => WashMaterial.color.a > 0f;

    // The rod-vision factor each sky regime's cells are washed by, read by the section layer at bake
    // time. See NightDesaturationMath.CellWashWithSky for the model.
    //
    // Live on the overlay rather than being recomputed by the layer so that the mesh and the material
    // cannot disagree about how deep the night is — the same single-read discipline
    // Patch_NightDesaturationStrength's header argues for, applied across the two halves of §9
    // instead of across two patches.
    //
    // ONE VALUE FOR EVERY LOADED MAP, exactly as the material already is. SetMapWash is called for
    // the current map only (see the patch's guard), so a section baked on a second colony while the
    // player is looking at the first uses the visible map's sky. That was already true of the
    // material half and is the reason the material is a single static object; the mesh half now
    // inherits it. In practice the off-screen map is re-baked when it is next dirtied, and the
    // redraw driver in GameComponent_SkyFalloffRedraw sweeps every map on its own clock.
    public static float ExposedFactor { get; private set; } = 1f;

    public static float OccludedFactor { get; private set; } = 1f;

    private static Material BuildWashMaterial()
    {
        Material material = new Material(ShaderDatabase.Transparent)
        {
            mainTexture = BaseContent.WhiteTex,
        };

        // Starts fully transparent: the first SkyManagerUpdate sets the real alpha, and until then a
        // freshly loaded daytime map must not flash a grey sheet.
        material.color = new Color(
            NightDesaturationMath.WashGrey,
            NightDesaturationMath.WashGrey,
            NightDesaturationMath.WashGrey,
            0f);

        return material;
    }

    // Called once per frame from Patch_NightDesaturationStrength with the same rod-vision factor
    // Patch_LowLightDesaturation keys its tint off, so the two halves of §9 can never disagree about
    // how deep the night is.
    public static void SetMapWash(float purkinjeFactor, float strength)
    {
        // The one branch that decides where the rod-vision factor lives. On, it goes to the mesh
        // per cell and the material holds a constant; off, it stays on the material and both mesh
        // factors are 1, which reproduces the pre-feature formula exactly. Nothing else in §9 needs
        // to know which mode it is in — the arithmetic downstream is the same either way.
        bool perCell = CelestialLightingFeatures.DarkAreaDesaturation;
        ExposedFactor = perCell ? purkinjeFactor : 1f;
        OccludedFactor = 1f;

        float alpha = NightDesaturationMath.MapWash(perCell ? 1f : purkinjeFactor, strength);

        // Colour assignment is a shader property write; skipping the no-op case keeps a daytime map
        // (alpha 0 every frame) from touching the material at all.
        if (Mathf.Approximately(WashMaterial.color.a, alpha))
            return;

        WashMaterial.color = new Color(
            NightDesaturationMath.WashGrey,
            NightDesaturationMath.WashGrey,
            NightDesaturationMath.WashGrey,
            alpha);
    }
}
