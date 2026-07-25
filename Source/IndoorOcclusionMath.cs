namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file, same discipline as
// Formulas.cs / NightRadianceMath.cs. Compiled into both Source (net481, runs inside RimWorld) and
// Tests (net8.0, runs standalone via `dotnet test`) via a linked <Compile Include>, so the exact
// shipped code is the code under test. Anything that needs Map/RoofGrid/Color32 belongs in the
// adapter (Patch_IndoorSkyOcclusion), not here.
//
// Subsystem 7b (DESIGN.md §7b): "indoor sky occlusion" — stop the sky lighting roofed cells.
//
// Why this exists at all. §7a (Patch_PitchBlackOverlay) darkens the whole-screen light overlay, but
// on-screen brightness is not a single global value: Verse.SectionLayer_LightingOverlay bakes a
// per-vertex *sky cover* into the lighting mesh's vertex alpha, and the shader mixes the sky colour
// in by how uncovered that vertex is. Vanilla clamps that cover for roofed cells to a constant:
//
//     private const byte RoofedAreaMinSkyCover = 100;                     // 100/255 == 0.392
//     if (flag /* a neighbouring cell is roofed */ && a < 100) a = 100;
//
// It is a *minimum* and nothing in vanilla ever raises it, so a sealed, unlit cave renders at
// ~61% of the current sky colour — day or night. That is why a pitch-black night still shows a
// visibly lit cave interior, and it is why no amount of §7a darkening can reach true black indoors:
// the interior is a fixed fraction *of the sky*, so it only goes black if the sky does.
//
// Polarity of that alpha (higher == more occluded == less sky) is confirmed two ways from vanilla
// plus one third-party mod, all decompiled: an *unroofed* cell keeps the glow grid's own alpha (0 in
// the common case) while a roofed one is forced up to 100; `disableSkyLighting` biomes (the Odyssey
// undercave) kill the sky contribution wholesale with `MatBases.LightOverlay.color = (1,1,1,0)`,
// which is exactly why those maps are black away from lamps; and Dub's Skylights makes a roofed cell
// sky-lit by temporarily nulling `map.roofGrid` across `Regenerate` so vanilla's roofed branch never
// fires at all. Occlusion is therefore expressed here as a 0..1 fraction — 1 == full cover, no sky.
public static class IndoorOcclusionMath
{
    // Vanilla's own roofed-cell floor, mirrored here as the documented baseline we raise from (its
    // real declaration is the private const SectionLayer_LightingOverlay.RoofedAreaMinSkyCover).
    // ApiCompatibilityTests pins that the vanilla field still exists with this value, so a Ludeon
    // change to the compromise is a loud test failure rather than a silent look regression.
    public const byte VanillaRoofedMinSkyCover = 100;

    // Full sky cover: the vertex takes none of the sky colour, so an unlit roofed cell renders from
    // its artificial glow alone — black when there is no lamp.
    public const byte FullSkyCover = 255;

    // How much sky a door lets past, as a fraction (0 == a door occludes like solid roof).
    //
    // Vanilla has no door leak: `SectionLayer_LightingOverlay` lumps doors in *with* roof for cover
    // purposes (`thing.def.altitudeLayer == AltitudeLayer.DoorMoveable` is one of the disjuncts that
    // sets its roofed flag), and a closed door's `blockLight` keeps it from contributing glow either.
    // So at full occlusion a doorway would go dead black, which reads wrong — a door is the one part
    // of a wall you expect a sliver of outside light around. This is deliberately small: enough to
    // suggest a threshold, not enough to light the room through it.
    public const float DefaultDoorSkyLeak = 0.15f;

    // How occluded a single cell is. Unroofed cells are left entirely to vanilla (0 — the sky is
    // genuinely overhead there); a roofed cell is fully occluded unless it is a door, which keeps
    // `doorSkyLeak` of the sky. Kept as one function so the door rule can never diverge between the
    // per-cell pass and the per-corner averaging below.
    public static float CellOcclusion(bool roofed, bool isDoor, float doorSkyLeak)
    {
        if (!roofed)
            return 0f;

        return isDoor ? 1f - Clamp01(doorSkyLeak) : 1f;
    }

    // A lattice corner is shared by up to four cells, so its occlusion is their mean. This is what
    // gives the wall line a gradient instead of a hard edge: a corner deep inside a building sees
    // four roofed cells (1.0), a corner sitting on an exterior wall sees two (0.5), and the shader
    // interpolates between them across the quad — so the blackness fades out over the wall rather
    // than printing a black halo onto the ground outside. `validCells` is the count actually inside
    // the map (corners on the map edge have fewer), which also keeps the value identical for the two
    // adjacent sections that each bake their own copy of a shared boundary vertex — no seams.
    public static float CornerOcclusion(float occlusionSum, int validCells) =>
        validCells <= 0 ? 0f : Clamp01(occlusionSum / validCells);

    // The legibility escape hatch. The accessibility brightness floor (Patch_BrightnessFloor) lifts
    // CurSkyGlow, which is *gameplay* light — it cannot brighten a sealed interior, because roofed
    // cells never take sky glow in the first place (Verse.GlowGrid.GroundGlowAt returns early for
    // them). So the floor has to reach interiors through this second path: capping occlusion at
    // 1 - floor leaves exactly `floor` worth of sky bleeding in, which makes the in-game "toggle
    // minimum brightness" hotkey work indoors as well as out. With the floor disabled the cap is 1
    // and this is the identity.
    public static float CapOcclusion(float occlusion, float brightnessFloor) =>
        Clamp01(Min(Clamp01(occlusion), 1f - Clamp01(brightnessFloor)));

    // Resolve a final vertex alpha, never *lowering* what vanilla baked. Only-ever-raising matters
    // for composition: other mods legitimately write this alpha for their own reasons (Dub's
    // Skylights unroofs skylit cells, Biomes! Caverns reclassifies cavern roofs), and taking the max
    // means our feature can add occlusion without ever undoing someone else's decision to let light
    // in — the worst case is that we leave their value alone.
    public static byte CoverAlpha(float occlusion, byte vanillaAlpha)
    {
        int scaled = (int)(Clamp01(occlusion) * FullSkyCover + 0.5f);
        return scaled > vanillaAlpha ? (byte)scaled : vanillaAlpha;
    }

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    private static float Min(float a, float b) => a < b ? a : b;
}
