namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file (same discipline as
// Formulas.cs and PurkinjeMath.cs). It is <Compile Include>-linked into the test project, so the
// exact numbers that ship are the exact numbers under test. Anything needing Material, Map or the
// glow grid belongs in the adapters (SectionLayer_NightDesaturation, NightDesaturationOverlay).
//
// This is the per-cell half of §9. PurkinjeMath answers "how far into rod vision is the SKY right
// now"; this file answers "how much of that applies to THIS cell", which is the question the
// subsystem got wrong twice.
//
// Why a separate mechanism exists at all — the two dead ends, both measured live rather than argued:
//
//   1. SkyColorSet.saturation (the original §9). Lands on Find.CameraColor, a ColorCorrectionCurves
//      IMAGE EFFECT over the finished frame. It cannot tell a campfire from the dark ground around
//      it, so flames came out as grey as the dirt. Global by construction; no tuning fixes it.
//
//   2. SkyColorSet.sky (§9's replacement, PR #44). That colour becomes MatBases.LightOverlay.color,
//      which the overlay MULTIPLIES the scene by. A multiply can scale channels or shift hue but can
//      never pull them toward each other, so it cannot desaturate anything. Confirmed on a live A/B:
//      draining that colour toward its own grey made unlit ground MORE saturated (0.398 -> 0.488),
//      because neutralising the blue light let the ground's own brown show through. The version that
//      shipped instead lerped toward a fixed "cool blue-grey" (0.55, 0.60, 0.72) that sits within a
//      few percent of vanilla's Clear night sky (0.482, 0.603, 0.682), so it moved unlit ground's
//      saturation by 0.001 — an effect that was, measurably, not there.
//
// Desaturating is lerp(colour, grey, t), and the only channel that can express a lerp per cell is a
// mesh drawn with an ALPHA-BLENDED material: alpha compositing IS that lerp, and the alpha can vary
// per vertex. Hence SectionLayer_NightDesaturation, and hence this file computing its alphas.
public static class NightDesaturationMath
{
    // Local glow at or above which a cell keeps its full daytime colour — no wash at all.
    //
    // Shared with PurkinjeMath.OnsetGlow rather than duplicated, because it is the same anchor for
    // the same reason: Verse.GlowGrid.GroundGlowAt caps ordinary artificial light at exactly 0.5
    // (`b = Mathf.Min(0.5f, b)`) and PlantProperties.growMinGlow is 0.51, so 0.5 is the brightest an
    // ordinary lamp-lit cell ever reads and must render at full colour. A campfire's own cell sits
    // at or above it, which is precisely the "campfire keeps its standard colour" requirement.
    public const float LitExemptGlow = PurkinjeMath.OnsetGlow;

    // Peak alpha of the wash, on a fully unlit cell at full rod vision with the slider at 1.
    //
    // Not 1.0, which would replace the scene with flat grey. Measured against the frame the old
    // global multiply produced, which is the look this is reproducing: at 0.55 an unlit night ground
    // patch goes from 0.114 saturation to 0.066, against 0.046 for the global version it replaces —
    // most of the drain, with none of it reaching the fires.
    public const float MaxWash = 0.55f;

    // The grey the wash composites toward. Deliberately DARK rather than mid-grey: alpha compositing
    // lifts whatever it blends toward, so a mid-grey wash would raise black night ground into a flat
    // haze and undo §7a's pitch-black nights. At 0.11 the lift on already-dark ground is small, and
    // the layer draws UNDER the lighting overlay (AltitudeLayer.Weather, below
    // AltitudeLayer.LightingOverlay) so vanilla's own night multiply darkens the result afterwards.
    public const float WashGrey = 0.11f;

    // How much of the wash a cell takes, in [0, 1], from its LOCAL light alone.
    //
    // Sky glow is deliberately excluded by the caller (GroundGlowAt's ignoreSky) — the sky's
    // contribution is what PurkinjeMath already measures for the whole map, and counting it here
    // would double it and make outdoor night cells exempt themselves as the sky brightens.
    //
    // Linear from full wash at unlit to zero at LitExemptGlow. Linear on purpose: the eye reads this
    // as a gradient around every light source, and an eased curve would put a visible ring where the
    // curve's knee falls.
    public static float CellWash(float localGlow)
    {
        if (localGlow >= LitExemptGlow)
            return 0f;
        if (localGlow <= 0f)
            return 1f;
        return 1f - localGlow / LitExemptGlow;
    }

    // The map-wide strength the per-cell wash is multiplied by, in [0, 1]: how far into rod vision
    // the sky is (PurkinjeMath.PurkinjeFactor), scaled by the "Night desaturation" slider and the
    // peak. Split from CellWash because the two live in different places at different rates — this
    // one is a material colour updated every frame, that one is baked into a mesh and only rebuilt
    // when the glow grid changes.
    public static float MapWash(float purkinjeFactor, float strength) =>
        Clamp01(purkinjeFactor) * Clamp01(strength) * MaxWash;

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
}
