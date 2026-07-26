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
        float alpha = NightDesaturationMath.MapWash(purkinjeFactor, strength);

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
