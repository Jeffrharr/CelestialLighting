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
//
// **The classification below is vanilla's own, question for question — only the magnitude is ours.**
// Vanilla decides per *vertex* which cells count as covered:
//
//   corner vertex:  flag |= roofDef != null && (roofDef.isThickRoof || thing == null
//                                               || !thing.def.holdsRoof
//                                               || thing.def.altitudeLayer == DoorMoveable)
//   centre vertex:  if (roofGrid.Roofed(c) && (thing == null || !thing.def.holdsRoof)) a = 100;
//                   else a = mean of the cell's four corners
//
// Two things fall out of that which our first cut got wrong. A cell holding up a thin roof — i.e. a
// *wall* — is explicitly NOT covered in either pass, so an exterior wall is a boundary, not an
// interior; and a cell that is not itself covered takes the *mean of its four corners* rather than a
// value of its own, which is what makes the transition a straight ramp across the wall. Ignoring both
// (treating every roofed cell, walls included, as fully covered, and giving each one a hard 1.0 centre
// over averaged corners) printed blackness onto exterior walls and out past them, and left a
// diamond-shaped bloom radiating from every boundary cell — the mesh fans four triangles out of that
// centre vertex, so a centre that disagrees with its corners shades as a star, not a flat tile.
// Matching vanilla's structure and changing only the magnitude (255 instead of 100) means the *shape*
// of our shading is the shape players already see from vanilla roof cover; only its depth is ours.
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

    // Every cell has exactly four lattice corners, and an uncovered cell's centre is their mean — the
    // same divisor vanilla uses when it averages those same four vertices.
    public const int CornersPerCell = 4;

    // How much sky a door lets past, as a fraction (0 == a door occludes like solid roof).
    //
    // Vanilla has no door leak: `SectionLayer_LightingOverlay` lumps doors in *with* roof when it bakes
    // corner vertices (`thing.def.altitudeLayer == AltitudeLayer.DoorMoveable` is one of the disjuncts
    // that sets its roofed flag), and a closed door's `blockLight` keeps it from contributing glow
    // either. So at full occlusion a doorway would go dead black, which reads wrong — a door is the one
    // part of a wall you expect a sliver of outside light around. This is deliberately small: enough to
    // suggest a threshold, not enough to light the room through it.
    public const float DefaultDoorSkyLeak = 0.15f;

    // Does this cell block the sky outright — is it *interior*? The single question the whole subsystem
    // turns on, and deliberately narrower than "is this cell roofed":
    //
    //   - A door is never interior. It is the boundary itself; its leak is applied at the vertices
    //     (see CornerOcclusion) so a threshold stays a threshold instead of a black plug. Stated first
    //     so the door rule is single-sourced and cannot be reached past by the roof cases below.
    //   - An unroofed cell is never interior — the sky genuinely is overhead. This is what keeps the
    //     feature from touching the outdoors at all.
    //   - A cell holding up the roof over it — a wall — is not interior either, exactly as vanilla
    //     decides for both of its vertex passes. Getting this wrong is what painted exterior walls
    //     black and pushed the darkness a cell past them onto open ground.
    //   - Unless the roof is *thick*: a mountain buries whatever is under it, wall or not, and vanilla
    //     makes the same exception (`roofDef.isThickRoof` short-circuits its holdsRoof test).
    public static bool BlocksSky(bool roofed, bool thickRoof, bool holdsRoof, bool isDoor)
    {
        if (isDoor)
            return false;

        if (!roofed)
            return false;

        return thickRoof || !holdsRoof;
    }

    // A lattice corner is shared by up to four cells and is covered if *any* of them is interior — an
    // OR, not a mean. That is vanilla's rule (its `flag` is set by a loop over the same four cells) and
    // it is what makes an interior read flat: every vertex inside a room, corners and centres alike,
    // lands on exactly 1.0, so there is nothing for the shader to interpolate and no per-tile structure
    // to see. Averaging instead gave an interior cell beside a wall corners lower than its own centre,
    // which is precisely the diamond bloom this replaced.
    //
    // The fade back to open sky is then carried entirely by the boundary cells (see CentreOcclusion):
    // a wall's inner corners are 1.0 and its outer corners 0.0, so the ramp lives on the wall tile
    // where it belongs and nothing beyond the building is darkened at all.
    //
    // Touching a leaky boundary caps the corner instead of raising it — a corner shared by a doorway and
    // the room behind it keeps `skyLeak` worth of sky, so the threshold reads brighter than the rest of
    // that room's edge. Cells outside the map simply do not contribute (they cannot be
    // interior), which needs no special case here and keeps the two sections that each bake a shared
    // boundary vertex in exact agreement — no 17-cell seams.
    //
    // `skyLeak` arrives already resolved as the MAX over the (up to) four cells meeting at this corner.
    // Max rather than sum or mean for the same reason CapOcclusion composes its two floors with Min:
    // whichever neighbour currently promises the most sky is the one this corner should honour.
    //
    // A leak of 0 makes this the identity, which is the path every corner without a doorway takes.
    public static float CornerOcclusion(bool anyNeighbourBlocksSky, float skyLeak)
    {
        float occlusion = anyNeighbourBlocksSky ? 1f : 0f;
        return Min(occlusion, 1f - Clamp01(skyLeak));
    }

    // The centre vertex of a cell: always the mean of its own four corners, which is what vanilla does
    // for every cell it did not force to 100. That mean is the whole reason a boundary reads as a ramp
    // rather than a starburst: it is the value bilinear interpolation across the quad would have
    // produced anyway, so the four triangles fanning out of the centre shade as one flat surface.
    //
    // Concretely, for an exterior wall: inner corners 1.0, outer corners 0.0, centre 0.5 — a straight
    // gradient across the wall tile, reaching exactly 0 on its outer face.
    //
    // WHY THERE IS NO LONGER A `blocksSky ? 1f` FORCE HERE, which this function carried until the
    // glass-wall work. For a sealed interior cell the force was a no-op that merely looked load-bearing:
    // every corner of an interior cell has that same cell among the four it ORs over, so all four are
    // already 1.0 and their mean is already exactly 1.0. The force could therefore only ever bite on a
    // cell whose corners were NOT all 1.0 — which is precisely a cell some leak reached — and there it
    // did active harm. Pinning the centre to 1.0 while two of the corners sat at 0 made the four
    // triangles fan from a black hub out to lit corners, which renders as a row of dark spikes: the
    // sawtooth measured along a glass wall in Tests/Screenshots/glass_wall_noon_leak_on_sawtooth.png
    // before this changed. Taking the mean instead gives that cell 0.5 and a smooth one-cell ramp from the
    // glass, and leaves every sealed cell on exactly the 1.0 it already had. It also softens the same
    // (much milder, leak 0.15) sawtooth a doorway always had.
    //
    // The interior still cannot be flooded from here: only cells whose corners a leak actually touched
    // move at all, so light reaches one cell in and stops. Grading it deeper needs a per-cell
    // distance-from-opening field, which is IndoorSkyGlowFraction's job, not this function's.
    public static float CentreOcclusion(float cornerOcclusionSum) =>
        Clamp01(cornerOcclusionSum / CornersPerCell);

    // Default for IndoorOcclusionSettings.MinIndoorBrightness. 0 == interiors may go genuinely black,
    // which is the point of the feature, so that is what ships. The slider exists because "black" is a
    // taste and legibility call: a player who wants sealed rooms readable without switching the whole
    // effect off — or without enabling the map-wide accessibility floor, which also brightens the
    // outdoors — raises this instead. At 1 it cancels occlusion entirely, exactly equivalent to turning
    // the feature off; that equivalence is a property of the formula, not a special case.
    public const float DefaultMinIndoorBrightness = 0f;

    // Applies that floor as a ceiling on occlusion: capping at 1 - floor leaves exactly `floor` worth of
    // sky bleeding into a roofed cell. This is the only path by which either floor can reach a sealed
    // interior. The accessibility floor works by lifting CurSkyGlow, which is *gameplay* light, and
    // roofed cells never take sky glow at all (Verse.GlowGrid.GroundGlowAt returns early for them), so
    // lifting it cannot brighten a cave by one shade. With the floor at 0 the cap is 1 and this is the
    // identity.
    //
    // The adapter caps corners *before* averaging them into a boundary cell's centre, so a floored
    // interior still ramps down across its walls (floor 0.5 gives inner corners 0.5 and a wall centre of
    // 0.25) rather than the wall flattening out at the floor value.
    //
    // indoorSkyGlowFraction is a second, independent cap of the same shape (IndoorGlowPassthrough.
    // SkyFractionAt, 0 when no other mod is brightening interiors): where minIndoorBrightness is a flat,
    // map-wide floor, this one is per-cell and follows whatever grading that other mod applied, so a
    // doorway can keep more sky than a sealed cell three tiles past it under the same
    // MinIndoorBrightness. The two compose by Min — whichever floor currently promises more sky wins —
    // rather than adding, so a player who has raised MinIndoorBrightness for legibility never gets
    // *less* sky than that setting already guarantees just because the other mod's graded value happens
    // to be lower at that cell.
    public static float CapOcclusion(float occlusion, float minIndoorBrightness, float indoorSkyGlowFraction) =>
        Clamp01(Min(
            Clamp01(occlusion),
            Min(1f - Clamp01(minIndoorBrightness), 1f - Clamp01(indoorSkyGlowFraction))));

    // --- The general "somebody else lit this interior" term ---
    //
    // Replaces a bespoke re-derivation of Ambient Light's own private falloff formula, which we used to
    // reflect their map component and settings for (issue #80). That approach worked and was wrong in
    // shape: it fixed exactly one mod by name, and every other mod that brightens interiors would have
    // needed its own copy of the same scaffolding. This asks a question no mod has to opt into.
    //
    // THE SIGNAL. Verse.GlowGrid.GroundGlowAt gates its sky term on `!map.roofGrid.Roofed(c)`, so for a
    // roofed cell vanilla returns nothing but the artificial term — lamps, fires, cave plants, via
    // GetAccumulatedGlowAt. Therefore, on a roofed cell:
    //
    //     anything in GroundGlowAt above the artificial term was put there by another mod,
    //     and it is sky-sourced, because that is the only term vanilla suppressed.
    //
    // That is the whole mechanism. It is identically 0 on an unmodded install, so it cannot move a
    // single vertex by itself, and it picks up any mod that raises indoor glow however it does it —
    // postfix, transpiler, or writing the grid directly — without us naming one.
    //
    // WHY THE ARTIFICIAL TERM IS RECOMPUTED RATHER THAN ASKED FOR. The obvious version of this is
    // `GroundGlowAt(c) - GroundGlowAt(c, ignoreSky: true)`, and it does not work. Ambient Light's
    // postfix is declared `Postfix(ref float __result, GlowGrid __instance, IntVec3 c)` — it does not
    // take `ignoreSky`, so it fires on both calls and the difference is identically zero (verified by
    // decompiling AmbientLightFalloff.Patch_GlowForcedGround). Recomputing vanilla's own artificial
    // formula from the accumulated glow colour dodges that entirely, because mods that brighten
    // interiors patch GroundGlowAt, not GetAccumulatedGlowAt.
    //
    // WHY SUBTRACT THE LAMPS AT ALL, rather than capping on total glow. This value caps how much of the
    // SKY's colour a vertex shows. A lamp-lit room has plenty of glow and no sky reaching it, so capping
    // on the total would make every lit interior start taking sky colour — dawn pink on a windowless
    // workshop. Vanilla composes the two with Max, so the honest reading of "sky beyond what the lamps
    // already provide" is the difference, and it is 0 whenever the lamps dominate.

    // Vanilla's own artificial-glow formula, transcribed from GlowGrid.GroundGlowAt's second half so the
    // subtraction above is against exactly the quantity vanilla would have returned. `a == 1` is
    // vanilla's fully-lit sentinel and short-circuits to 1 before the channel maths; otherwise it takes
    // the brightest channel, scales by 3.6/255, and clamps at 0.5 (vanilla's own ceiling on artificial
    // ground glow, which is why a lamp never reads as bright as open daylight).
    public static float ArtificialGlow(byte r, byte g, byte b, byte a)
    {
        if (a == 1)
            return 1f;

        float brightest = r > g ? r : g;
        if (b > brightest)
            brightest = b;

        float scaled = brightest / 255f * 3.6f;
        return scaled < 0.5f ? scaled : 0.5f;
    }

    // The sky-sourced share of a roofed cell's glow: whatever another mod added on top of the lamps.
    // Gated on `roofed` because that is what makes the subtraction meaningful — on an UNROOFED cell
    // vanilla puts CurSkyGlow into groundGlow itself, and the difference would report the ordinary
    // outdoor sky as though a mod had injected it, capping occlusion everywhere outdoors. §7b never
    // occludes an unroofed cell anyway, so 0 is both the safe and the correct answer there.
    public static float IndoorSkyGlowFraction(float groundGlow, float artificialGlow, bool roofed)
    {
        if (!roofed)
            return 0f;

        float excess = groundGlow - artificialGlow;
        return excess <= 0f ? 0f : Clamp01(excess);
    }

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
    private static float Max(float a, float b) => a > b ? a : b;
}
