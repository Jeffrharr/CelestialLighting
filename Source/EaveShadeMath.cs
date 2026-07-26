namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file, the same discipline
// EavesMath.cs / IndoorOcclusionMath.cs follow. Compiled into both Source (net481, runs inside
// RimWorld) and Tests (net8.0, runs standalone via `dotnet test`) through a linked <Compile
// Include>, so the exact shipped code is the code under test.
//
// Subsystem 15b (DESIGN.md §15): a roof shades the ground directly under it.
//
// THE HOLE. Vanilla's `SectionLayer_SunShadows` emits, per caster cell, a flat footprint quad whose
// four vertices carry alpha 0, plus perimeter faces whose far vertices carry alpha == caster height.
// That alpha is what the sun-shadow shader displaces a vertex by, and it is also what the fragment
// is drawn at — so the zero-alpha footprint renders FULLY TRANSPARENT. A caster never shades its own
// cell. Nothing ever noticed, because before §15 every caster was an edifice and a wall's sprite
// covers whatever colour the ground under it is. Give the roof itself a caster (§15) and the hole is
// suddenly the visible thing: a porch throws a shadow across the ground beside it while the porch
// floor is lit as though the roof above it were not there.
//
// It cannot be closed inside that mesh. Raising the footprint's alpha to make it opaque is the same
// number that makes the shader push the quad a whole shadow-length away, off the cell it was meant
// to shade — the two meanings share one channel. Hence a layer of our own.
//
// MEASURED, live A/B at 15:00, latitude 45, clear sky, over a 9x9 roof slab on concrete, relative to
// open sunlit ground at 1.000:
//
//     open sunlit ground                                1.000
//     under the roof                                    0.605   <- no shadow of any kind on it
//     the cast shadow, on open ground                   0.742
//     the cast shadow where it laps the roof's own
//       cover fade, i.e. the rim right at the roofline  0.581
//
// Two numbers there are vanilla's, and both check out exactly, which is what pins the model below to
// the arithmetic the renderer is really doing: 0.605 is vanilla's roofed-cell sky cover (1 - 100/255
// = 0.608, `SectionLayer_LightingOverlay.RoofedAreaMinSkyCover`), and 0.742 is the relative
// luminance of vanilla's Clear-day shadow tint (0.718, 0.745, 0.757) = 0.740.
//
// So the porch sits at 0.605 with a 0.581 rim hung off its shadow side — a pale square with a dark
// edge, and the reported jar. It is lighter than its own shadow exactly where the two touch.
//
// THE RULE. An eave cell takes the same shadow multiply the ground beside it takes — no more and no
// less. Not a special number for porches: it is the footprint shading vanilla's mesh would already
// have drawn if that quad were not structurally transparent, put back by the only channel that can
// carry it. The roof's separate cost in *sky* is already vanilla's cover and is left exactly alone.
//
// What that produces: 0.605 x 0.742 = 0.449 under the roof, against 0.742 for the shadow on open
// ground and 0.581 at the rim. The porch becomes the darkest thing in the picture and brightness
// rises monotonically outward from it, which is what a roof looks like from above.
//
// AND IT CANNOT GO PITCH BLACK, which is the failure mode worth guarding by construction rather than
// by taste. The multiply is bounded below by vanilla's own shadow palette: the darkest tint any
// vanilla weather declares is Clear's 0.740, most are 0.92 or lighter, and shadow strength only ever
// lerps that tint back toward white (1.0) as the sun drops — so the deepest an eave can ever go is
// ~0.45 of open sunlit ground, in full midday sun. At dusk, under overcast, and all night the
// multiply is at or near 1 and this contributes nothing at all, leaving the porch at exactly the
// roof cover players already see. Nothing here can reach the fully-occluded interior (0.0) that §7b
// applies to a sealed room; an eave is never treated as interior in the first place.
public static class EaveShadeMath
{
    // How much of the sky a roofed cell renders at once vanilla has clamped its cover — the 0.605
    // measured above. Not an input to the shade (the shade is deliberately independent of it); it is
    // here so the floor this subsystem can reach is stated in one place and asserted in the tests.
    // Derived from IndoorOcclusionMath's mirror of the vanilla constant rather than restated, so
    // there is one copy of 100/255 in the codebase and ApiCompatibilityTests' pin on the vanilla
    // field guards both users of it.
    public const float VanillaRoofedSkyKeep =
        1f - IndoorOcclusionMath.VanillaRoofedMinSkyCover / (float)IndoorOcclusionMath.FullSkyCover;

    // Relative luminance of the sun-shadow tint, i.e. the fraction of light the cast shadow LEAVES.
    // 1 == no shadow (SkyManager lerps this material toward white as shadow strength falls to zero),
    // 0 == fully black.
    //
    // Rec. 709 weights, matching how the eye reads the two regions being compared — the whole point
    // is that a porch and the band beside it read as one depth, which is a question about perceived
    // brightness, not about any single channel. RimWorld's shadow tints are near-neutral anyway
    // (Clear's is a faintly blue grey), so the weighting is a detail rather than a lever.
    public static float ShadowKeep(float r, float g, float b) =>
        Clamp01(0.2126f * r + 0.7152f * g + 0.0722f * b);

    // The alpha to draw the eave shade at: how much of a BLACK overlay to composite over the cell.
    //
    // Alpha-blending black at alpha `a` is `scene * (1 - a)` — a multiply, exactly like the sun
    // shadow and the sky cover it sits alongside, which is why the three compose by plain
    // multiplication and in any draw order. Wanting the cell multiplied by `shadowKeep` therefore
    // makes this simply its complement.
    //
    // Returns 0 for a shadow that leaves everything (dusk, overcast, night, a shadowless biome, or
    // the feature's own off state), so the eave falls back to precisely the roof cover vanilla draws.
    public static float ShadeAlpha(float shadowKeep) => 1f - Clamp01(shadowKeep);

    // The composite an eave cell renders at, as a fraction of open sunlit ground: vanilla's roof
    // cover, then the shadow. Exists so the tests can assert the *visible outcome* — the alpha alone
    // is easy to get plausibly wrong in a way only the product reveals — and so the floor quoted in
    // this file's header is computed rather than asserted from memory.
    public static float EaveBrightness(float roofedSkyKeep, float shadowKeep) =>
        Clamp01(roofedSkyKeep) * (1f - ShadeAlpha(shadowKeep));

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
}
