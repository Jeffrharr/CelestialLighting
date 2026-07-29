using UnityEngine;
using Verse;

namespace CelestialLighting;

// The material §15b's eave shade is drawn with, plus the one per-frame value written into it.
//
// Split the same way NightDesaturationOverlay is, for the same reason vanilla splits its own
// lighting: the per-CELL part (which cells are eaves) is baked into a section mesh and changes only
// when roofs or buildings do, while the map-wide part (how deep the sun's shadow is right now)
// changes every frame. Baking the second into the mesh would mean rebuilding every section on the
// map continuously; putting it in the shared material's alpha costs one assignment per frame, and
// the shader multiplies material colour by vertex colour for free.
// [StaticConstructorOnStartup] is mandatory, not decoration, and RimWorld says so itself at load:
// "Type EaveShadeOverlay probably needs a StaticConstructorOnStartup attribute, because it has a
// field ShadeMaterial of type Material. All assets must be loaded in the main thread."
//
// It is not a style note. Without it the static initializer runs whenever the field is first touched
// — and the first toucher is SectionLayer_EaveShade's constructor, which Verse.Section runs during
// MAP GENERATION, on a background thread (LongEventHandler.RunEventFromAnotherThread). Constructing a
// Unity Material there corrupts the runtime rather than throwing cleanly: the observed failure was
// `BadImageFormatException: Method has zero rva` with a garbage file name, followed by a SIGSEGV
// inside the mono GC, reported as "error while generating map" with nothing pointing at this file.
// The attribute makes RimWorld run the initializer during startup, on the main thread, where asset
// creation is legal.
[StaticConstructorOnStartup]
public static class EaveShadeOverlay
{
    // Our own Material instance, not a pooled or vanilla one — MaterialPool hands out shared
    // instances keyed on their request, so mutating a pooled material's colour every frame would
    // reach into whatever else drew with it.
    //
    // ShaderDatabase.Transparent is ordinary alpha blending, and blending BLACK at alpha `a` is
    // `scene * (1 - a)` — a multiply, which is the same operation the sun-shadow tint and the sky
    // cover already perform. That is what lets EaveShadeMath reason about the three in plain
    // arithmetic. MatBases.SunShadow is emphatically not reusable here: its shader displaces every
    // vertex by the shadow vector scaled by that vertex's alpha, which is the very coupling that
    // makes a caster unable to shade its own cell.
    //
    // BaseContent.WhiteTex keeps the sample flat, so the result comes from the material colour and
    // the per-vertex alpha alone rather than from any texture pattern.
    private static readonly Material ShadeMaterial = BuildShadeMaterial();

    public static Material Material => ShadeMaterial;

    private static Material BuildShadeMaterial()
    {
        Material material = new Material(ShaderDatabase.Transparent)
        {
            mainTexture = BaseContent.WhiteTex,
        };

        // Starts fully transparent: the first SkyManagerUpdate writes the real alpha, and until then
        // a freshly loaded map must not flash a dark slab over every porch.
        material.color = new Color(0f, 0f, 0f, 0f);
        return material;
    }

    // Called once per frame from Patch_EaveShade with the sun-shadow tint SkyManager just composed.
    // Reading that material's colour rather than re-deriving the shadow depth is deliberate: it is
    // the one value every shadow feature we own already converges on (§1's elevation ramp, §13's
    // weather softening, the angular-size penumbra, the moon's night handoff all reach the screen
    // through GenCelestial.CurShadowStrength, which SkyManager lerps this colour by), so the eave
    // shade cannot drift from the cast shadow it is matching no matter what any of them do later.
    public static void SetShadowTint(Color sunShadowTint)
    {
        SetAlpha(EaveShadeMath.ShadeAlpha(
            EaveShadeMath.ShadowKeep(sunShadowTint.r, sunShadowTint.g, sunShadowTint.b)));
    }

    // Held at zero when the feature is off, so a mid-game toggle is a true no-op either way round —
    // the layer stops drawing as well (SectionLayer_EaveShade.Visible), which makes this
    // belt-and-braces, but it is what the harness's SetFeature A/B asserts.
    public static void Clear() => SetAlpha(0f);

    private static void SetAlpha(float alpha)
    {
        // A colour assignment is a shader property write; skipping the no-op case keeps a map with no
        // shadow at all (alpha 0 every frame) from touching the material.
        if (Mathf.Approximately(ShadeMaterial.color.a, alpha))
            return;

        ShadeMaterial.color = new Color(0f, 0f, 0f, alpha);
    }
}
