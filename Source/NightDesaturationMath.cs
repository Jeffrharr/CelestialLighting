using System;

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
//   2. SkyColorSet.sky (§9's replacement). That colour becomes MatBases.LightOverlay.color,
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
//
// THE COMPLEMENT, for anyone reading this as "colors.sky is a dead end": §19 (polar night blue) is
// the case where a colors.sky tint DOES work, and for exactly the reason dead end 2 gives. A
// multiply cannot pull channels toward each other, but attenuating one relative to the others is
// precisely what it CAN express — and that is what an ozone absorption notch is. The trap §19 had to
// avoid was the second half of dead end 2: its first design lerped toward a 20,000 K blackbody,
// which sits within a few percent of vanilla's night sky in ratio terms and attenuated ground red by
// 5.3%, i.e. the same invisible non-effect measured above. Modelling the absorption directly instead
// gets 24%. The lesson generalises: what matters is not which field you write, it is how far the
// target sits from vanilla's palette in CHANNEL RATIO, since ratio is all a multiply can move.
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

    // --- Where the sky's rod-vision factor is allowed to reach: the "dark rooms at noon" fix ---
    //
    // THE BUG, in one line: a windowless room at noon kept its full daytime colour however dark it
    // was, because the rod-vision factor was applied MAP-WIDE.
    //
    // §9 renders as `vertex alpha x material alpha`, and the two halves used to be
    //
    //     vertex   = CellWash(local glow)        per cell, baked into the section mesh
    //     material = PurkinjeFactor(sky glow)    map-wide, one colour write per frame
    //
    // PurkinjeFactor is an InverseLerpClamped reaching exactly 0 at OnsetGlow, so above sky glow 0.5
    // that second term is not merely small — it is zero, and it multiplies EVERY cell. The per-cell
    // half was already asking the right question (CellWash reads local glow through GroundGlowAt's
    // `ignoreSky`, so a sealed room reads unlit at every hour) and already producing the right
    // answer; it was being multiplied by nothing. Reported by a player as "the desaturation should
    // apply to all dark areas always, not just when it's night", which is exactly the diagnosis.
    //
    // WHY THE SKY TERM CANNOT SIMPLY BE DELETED, which is the obvious fix and is wrong. It is not a
    // gate that got in the way, it is the sky's own contribution to a cell's light, and CellWash
    // deliberately excludes it so that this term can carry it. Drop it and an OUTDOOR cell at noon —
    // local glow 0, because sunlight is not artificial light — washes at full strength and the whole
    // map greys out at midday. The term is load-bearing. What was wrong is that it was applied to
    // cells the sky does not reach.
    //
    // So the factor becomes per-cell, taking one of exactly two values:
    //
    //     sky-exposed cell   ->  exposedFactor   == PurkinjeFactor(sky glow), as before
    //     sky-occluded cell  ->  occludedFactor  == 1, there being no sky here to keep it lit
    //
    // which is the same statement vanilla's own GlowGrid.GroundGlowAt makes when it consults
    // CurSkyGlow only `if (!map.roofGrid.Roofed(c))`. A roofed cell has never taken sky glow as
    // gameplay light; this stops it taking sky glow as a reason to keep its colour either.
    //
    // WHICH CELLS COUNT AS OCCLUDED IS §7b's QUESTION, ASKED ONCE — IndoorOcclusionMath.BlocksSky,
    // never a bare `Roofed`. That predicate is narrower on purpose and both carve-outs matter here
    // for the same reasons they matter there:
    //
    //   - A WALL holds up the roof over it and is NOT interior. Bare `Roofed` instead gives every
    //     exterior wall tile a full wash while the open ground beside it takes none, which at noon
    //     draws a dark grey ring around every building on the map. That is the identical failure
    //     §7b's own header records from the other direction ("printed blackness onto exterior
    //     walls"), so it gets the identical predicate rather than a second one that can drift.
    //   - A DOOR is the boundary itself, so a doorway reads as open ground and keeps daylight colour.
    //
    // Sharing BlocksSky also means the two subsystems agree on what "indoors" is, which is what the
    // player is really asking for: the room §7b darkens is the room §9 drains the colour from.
    //
    // OFF (CelestialLightingFeatures.DarkAreaDesaturation) both factors are 1, the material carries
    // PurkinjeFactor again, and this is the pre-feature formula exactly — an equivalence in the
    // arithmetic rather than a second code path, which is what the harness A/B arm rests on.
    public static float CellWashWithSky(
        float localGlow, bool blocksSky, float exposedFactor, float occludedFactor) =>
        CellWash(localGlow) * Clamp01(blocksSky ? occludedFactor : exposedFactor);

    // The map-wide strength the per-cell wash is multiplied by, in [0, 1]: how far into rod vision
    // the sky is (PurkinjeMath.PurkinjeFactor), scaled by the "Night desaturation" slider and the
    // peak.
    //
    // WITH DarkAreaDesaturation ON THE CALLER PASSES purkinjeFactor: 1 and this reduces to
    // `strength x MaxWash` — a constant. The rod-vision term has not been dropped, it has moved into
    // the mesh as CellWashWithSky's per-cell exposedFactor, because it needed to reach some cells and
    // not others and a material colour is one value for the whole map. See that method for why. With
    // the flag off the factor still arrives here and this is unchanged.
    //
    // Split from CellWash because the two live in different places at different rates — this
    // one is a material colour updated every frame, that one is baked into a mesh and only rebuilt
    // when the glow grid changes.
    public static float MapWash(float purkinjeFactor, float strength) =>
        Clamp01(purkinjeFactor) * Clamp01(strength) * MaxWash;

    // A wash fraction as the byte alpha a mesh vertex carries.
    //
    // Lives here rather than in the layer so the equivalence test in NightWashWindowTests can compare
    // the memoised vertex loop against the pre-memoisation one on the *shipped* conversion instead of
    // a paraphrase of it — the same reason IndoorOcclusionMath owns §7b's CoverAlpha.
    //
    // Math.Round is deliberately not the `(int)(v * 255f + 0.5f)` form CoverAlpha uses: this replaced
    // UnityEngine.Mathf.RoundToInt, which is `(int)Math.Round(f)` — banker's rounding — and the two
    // disagree on every exact midpoint. Reproducing it exactly is what keeps this a pure refactor
    // rather than a one-bit-per-vertex change nobody would have noticed until a pinned probe moved.
    //
    // The clamp is defensive: CellWash already returns [0, 1] and averaging clamped values cannot
    // leave that range, so nothing in §9 can currently reach it.
    public static byte WashAlpha(float wash)
    {
        int scaled = (int)Math.Round(wash * 255f);
        if (scaled < 0)
            return 0;

        return scaled > 255 ? (byte)255 : (byte)scaled;
    }

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
}
