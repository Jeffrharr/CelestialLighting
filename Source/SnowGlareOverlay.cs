using UnityEngine;
using Verse;

namespace CelestialLighting;

// §24 (prototype), issue #90: draws the additive snow-glare pass — ONE full-map quad per frame,
// tinted by SnowGlare.AlphaFor.
//
// WHY THIS IS THE CHEAP SHAPE, and why "additive pass" does not imply §16's cost ledger. The
// expensive design for a snow effect is a per-cell one: a SectionLayer subscribing to
// MapMeshFlagDefOf.Snow, a flag SnowGrid.CheckVisualOrPathCostChange raises constantly during
// snowfall, forcing mesh rebuilds — exactly what issues #20 and #60 measured and what DESIGN.md §21
// defers on. This file needs none of it, because §21's model is ALREADY a whole-map areal mean
// (SurfaceBuildup reads TotalDepth / Area, not per cell). There is no per-cell information in the
// quantity being drawn, so a uniform quad is not a compromise on the model — it is exactly its
// resolution. Per frame this costs one Graphics.DrawMesh against a pooled, never-regenerated mesh.
//
// MeshPool.wholeMapPlane and SkyOverlay.DrawWorldOverlay's geometry, i.e. vanilla's own mechanism for
// every weather overlay it ships. Deliberately NOT AuroraCurtainOverlay's bespoke per-sheet quads:
// that file's header explains it abandoned wholeMapPlane because a TILED texture on a shared static
// mesh would have needed UV edits that reach every other mod's rain and snow. A flat untextured wash
// has no UVs to tile, so the shared mesh is fine here and the aurora's reason does not transfer.
//
// AND THE COST PROFILE IS NOT THE AURORA'S, which is the objection that shaped this file. The aurora
// is rare and time-limited, so its per-frame cost is amortised over how seldom it runs; snow glare
// on an ice sheet is EVERY DAYLIGHT FRAME, ALL YEAR. A standing cost has to be genuinely near-zero
// rather than merely acceptable in bursts, which is why this draws one quad with no texture, no mesh
// regeneration, no per-cell read, and no allocation after startup — and why the material's colour is
// only written when it actually changes (see Draw).
//
// [StaticConstructorOnStartup] is mandatory, same as AuroraCurtainOverlay / EaveShadeOverlay /
// NightDesaturationOverlay: `new Material(...)` must happen on Unity's main thread, and the attribute
// is what guarantees the static initialiser runs there (at startup, after ShaderDatabase has loaded)
// instead of on whichever thread first touches the type. Without it this is a latent crash.
[StaticConstructorOnStartup]
public static class SnowGlareOverlay
{
    // Additive, not alpha-blended — the entire point of the subsystem. SkyColorSet.sky is a multiply
    // with no headroom above (1,1,1), so an alpha-blended overlay could only ever move the scene
    // TOWARD its own colour, i.e. wash it out; MoteGlow ADDS, which is the only way to render a scene
    // brighter than the palette's ceiling. Same shader the aurora uses, for the same reason.
    //
    // This composites rendered pixels only. SkyTarget.glow, GlowGrid, plant growth, solar output and
    // pawn vision are all untouched — §24 stays inside the mod's colour-only lane, like §11 and §13
    // and unlike §7/§17/§18d, which deliberately write .glow.
    private static readonly Material GlareMat = new Material(ShaderDatabase.MoteGlow);

    // The alpha the material currently holds. Material.color is a native round trip in both
    // directions, so the last written value is remembered here instead of read back — the same reason
    // AuroraCurtainOverlay wraps its materials in SheetMaterial rather than holding bare ones. On a
    // static map at a fixed hour the alpha barely moves, so this turns a per-frame native write into
    // an occasional one.
    //
    // NaN rather than 0 as the initial value, so the first draw always writes: 0 is a legitimate
    // steady-state alpha, and seeding with it would let a genuine first frame at 0 skip the write and
    // leave whatever colour the material was constructed with.
    private static float lastAlpha = float.NaN;

    // Neutral white, and no saturation term of its own. Snow glare is achromatic — §21 already makes
    // that argument for why it writes no saturation (§9 owns the desaturation ramp and consumes the
    // raised brightness for free), and the same holds here: this adds luminance and lets §8's colour
    // temperature and §9's Purkinje ramp keep sole ownership of hue and saturation. A tinted glare
    // would be a second opinion about sky colour.
    private static readonly Color GlareColor = Color.white;

    // Draws this frame's glare over `map`, or draws nothing when there is none. Called once per frame
    // from Patch_SnowGlareDraw for the currently-visible map only.
    public static void Draw(Map map)
    {
        float alpha = SnowGlare.AlphaFor(map);

        // The overwhelmingly common case on the overwhelming majority of maps: no buildup, or no
        // cloud deck, or no overflow. Returning before touching the material or the draw call is what
        // makes "a map with no snow pays nothing" literally true rather than approximately true.
        if (alpha <= 0f)
            return;

        if (alpha != lastAlpha)
        {
            GlareMat.color = new Color(GlareColor.r, GlareColor.g, GlareColor.b, alpha);
            lastAlpha = alpha;
        }

        // ALTITUDE IS LOAD-BEARING HERE, AND THE OBVIOUS CHOICE IS WRONG. AltitudeLayer.Weather (31)
        // is where vanilla's own weather overlays draw and was this file's first cut — but it sits
        // directly BELOW LightingOverlay (32), so everything drawn there is multiplied by the sky
        // colour afterwards. For rain that is correct. For an additive pass whose entire purpose is
        // to exceed what that very multiply can express, it is self-defeating in principle: the glare
        // gets scaled back down by the same (1,1,1)-capped palette that clamped the amplification
        // away in the first place.
        //
        // MEASURED, BECAUSE THE PRINCIPLED VERSION OF THAT ARGUMENT OVERSTATES IT. A Weather-altitude
        // build was run and did NOT render nothing — it measured ΔE 19.79 at overcast noon, because an
        // overcast daytime palette is a ~0.8 multiply and passes most of the addition through. The
        // real failure is conditional rather than total: the attenuation tracks the palette, so it
        // bites hardest exactly where the sky is darkest, and glare would fade as the deck thickened,
        // which is backwards. Worth writing down rather than leaving the original absolute claim
        // standing, since a reader can only check it against a number.
        //
        // VisEffects (33) is above LightingOverlay and below FogOfWar, which is the pair of properties
        // §11a's curtain needed for the same first reason — see AuroraCurtainOverlay's own altitude
        // note, and ApiCompatibilityTests.AltitudeLayer_VisEffects_SitsAboveLightingOverlayAndBelow-
        // FogOfWar, which asserts the ORDERING rather than merely the names because that ordering is
        // the whole argument. Staying below FogOfWar (unlike the aurora, which deliberately clears it)
        // is the right call for glare specifically: an aurora is sky and is not hidden by a player's
        // ignorance of the terrain, but glare is light bouncing off GROUND, so unexplored ground
        // having none of it is correct rather than a limitation.
        //
        // KNOWN LIMITATION OF THE PROTOTYPE, recorded rather than hidden: SectionLayer_IndoorMask
        // draws between Weather and VisEffects (measured, per the aurora's note), so this quad washes
        // over roofed interiors that should not see outdoor glare at all. It is not fixable by moving
        // altitude — below the mask is also below LightingOverlay, which is the fatal case above — so
        // it needs a real answer (a masked mesh, or a per-room term) IF the effect survives the look
        // test that issue #90 says to run first. Deliberately left for that verdict.
        SkyOverlay.DrawWorldOverlay(map, GlareMat, AltitudeLayer.VisEffects.AltitudeFor());
    }
}
