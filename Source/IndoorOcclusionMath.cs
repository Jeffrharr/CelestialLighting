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

    // Does this cell block the sky outright — is it *interior*? The single question the whole subsystem
    // turns on, and deliberately narrower than "is this cell roofed":
    //
    //   - A door is never interior. It is the boundary itself, so a doorway reads exactly like open
    //     ground here. This used to also carry a flat door leak at the vertices (see CornerOcclusion's
    //     history), but §7c's distance-graded sky falloff now reaches door-adjacent corners through
    //     CapOcclusion's skyFalloffFraction term — a fixed cap here would only ever double-count or
    //     conflict with that gradient, never improve on it, so it was removed rather than composed.
    //     Stated first so the door rule is single-sourced and cannot be reached past by the roof cases
    //     below.
    //   - An unroofed cell is never interior — the sky genuinely is overhead. This is what keeps the
    //     feature from touching the outdoors at all.
    //   - A cell holding up the roof over it — a wall — is not interior either, exactly as vanilla
    //     decides for both of its vertex passes. Getting this wrong is what painted exterior walls
    //     black and pushed the darkness a cell past them onto open ground.
    //   - Unless the solid cell *is* the mountain: natural rock under a thick roof is interior, because
    //     there is no wall face there to catch the light — it is unmined stone all the way up.
    //
    // That last clause used to read `thickRoof || !holdsRoof`, i.e. a thick roof buried whatever was
    // under it, wall or not (#129). The analogy it was drawn from does not survive the trip: vanilla's
    // `roofDef.isThickRoof` short-circuit lives in the *corner* pass only — its centre pass tests a
    // bare `roofed && !holdsRoof` with no thickness term at all (see the two-pass listing at the head
    // of this file) — and even at the corner all it does is raise the cover to the 100 floor. We
    // collapsed both passes onto this one predicate and turned that floor into a full 255 blackout, so
    // "the mountain holds its own roof up" came out as "the wall's own sky reads zero", which is a
    // gameplay-mechanics answer to a rendering question. Live, it swallowed the whole wall ring of a
    // mountain room into the same black square as its floor: no wall texture, no boundary, no ramp.
    //
    // Splitting on natural rock rather than dropping the thickness term outright keeps the half of the
    // old behaviour that was right. Unmined stone genuinely has no sky and should stay buried; a
    // *built* wall under a mined-out mountain roof is the same stone wall it would be under a
    // constructed one, and now gets the same corner ramp. It is the same "one predicate was really two
    // questions" split EavesMath.IsEave/CastsRoofShadow already made against this same thickRoof veto.
    //
    // Note this leaves an interior partition wall inside a mountain base still fully occluded, and
    // correctly so: it is not the rule below that darkens it but its own four corners, every one of
    // them shared with interior floor. No sky reaches that wall, so none is drawn on it.
    public static bool BlocksSky(bool roofed, bool thickRoof, bool holdsRoof, bool isDoor, bool naturalRock)
    {
        if (isDoor)
            return false;

        if (!roofed)
            return false;

        if (!holdsRoof)
            return true;

        return thickRoof && naturalRock;
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
    // This used to also cap a door-touching corner at a flat `doorSkyLeak`, so the corner shared by a
    // doorway and the room behind it read a shade brighter than the rest of that room's edge. Removed:
    // §7c's distance-graded sky falloff already brightens that same corner through CapOcclusion's
    // skyFalloffFraction term, and unlike this flat cap it scales with distance from the opening, so
    // it fully supersedes rather than complements the old cap. Cells outside the map simply do not
    // contribute (they cannot be interior), which needs no special case here and keeps the two sections
    // that each bake a shared boundary vertex in exact agreement — no 17-cell seams.
    public static float CornerOcclusion(bool anyNeighbourBlocksSky) =>
        anyNeighbourBlocksSky ? 1f : 0f;

    // The centre vertex of a cell. An interior cell is flat-out fully occluded; anything else — wall,
    // door, open ground — takes the mean of its own four corners, which is exactly what vanilla does
    // for a cell it did not force to 100. That mean is the whole reason a boundary reads as a ramp
    // rather than a starburst: it is the value bilinear interpolation across the quad would have
    // produced anyway, so the four triangles fanning out of the centre shade as one flat surface.
    //
    // Concretely, for an exterior wall: inner corners 1.0, outer corners 0.0, centre 0.5 — a straight
    // gradient across the wall tile, reaching exactly 0 on its outer face.
    public static float CentreOcclusion(bool blocksSky, float cornerOcclusionSum) =>
        blocksSky ? 1f : Clamp01(cornerOcclusionSum / CornersPerCell);

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
    // skyFalloffFraction is a second, independent cap of the same shape (SkyFalloffSource.FractionAt,
    // 0 when nothing is lighting interiors): where minIndoorBrightness is a flat, map-wide floor, this
    // one is per-cell and graded by distance from an opening, so a doorway keeps more sky than a sealed
    // cell three tiles past it even under the same MinIndoorBrightness. The two
    // compose by Min — whichever floor currently promises more sky wins — rather than adding, so a
    // player who has raised MinIndoorBrightness for legibility never gets *less* sky than that setting
    // already guarantees just because the other source's graded value happens to be lower at that cell.
    public static float CapOcclusion(float occlusion, float minIndoorBrightness, float skyFalloffFraction) =>
        Clamp01(Min(
            Clamp01(occlusion),
            Min(1f - Clamp01(minIndoorBrightness), 1f - Clamp01(skyFalloffFraction))));

    // --- Decoupling the indoor floor from the outdoor one (the "both sliders say 0.50" problem) ---
    //
    // The two floors compound, and neither of them says so. Every lighting-overlay vertex renders as
    //
    //     skyColour x (1 - cover)    composed with the artificial glow carried in the vertex's RGB
    //
    // where `skyColour` is the one global material §7a has already lerped toward black by its own
    // `keep` factor, and `cover` is the alpha this file computes. So a sealed room does not render at
    // MinIndoorBrightness of the sky — it renders at `keep x MinIndoorBrightness` of it. On the shipped
    // Cinematic preset both knobs sit at 0.50, which comes out as 0.50 outdoors against 0.25 indoors at
    // the night floor, and 1.00 against 0.50 at noon. `Presets` already records the symptom from the
    // other side: the pair was raised 0.30 -> 0.50 because "the two floors compound" and 0.30 was
    // unplayable once multiplied. That is this same fact read as a tuning problem rather than a units
    // problem.
    //
    // It is a units problem. MinNightBrightness is a fraction of the *undarkened* sky; MinIndoorBrightness
    // was a fraction of whatever §7a had already left of it. Dividing the indoor floor by §7a's own keep
    // factor puts both in the first unit — `keep x (minIndoor / keep) = minIndoor` — so an interior floors
    // at MinIndoorBrightness of the same sky MinNightBrightness is a fraction of, and the two numbers can
    // finally be compared, set equal, or deliberately split by a player who wants interiors darker than
    // the night outside them.
    //
    // Three things worth knowing before touching this:
    //
    //   - **Noon is unchanged by construction.** In daylight §7a darkens nothing, keep is 1, and this
    //     returns minIndoorBrightness untouched — the pre-feature formula exactly. The whole effect of
    //     the change lives below the point where §7a starts pulling the overlay toward black.
    //   - **The sky is a multiply with no headroom** (issue #103), so a floor cannot be honoured past the
    //     point where admitting *all* of the sky still is not bright enough. That is the `keep <= floor`
    //     branch: the cap saturates at "take the whole sky" and asking for more would be asking the
    //     multiply for a value above (1,1,1).
    //
    //     "All of the sky" is 61% of it, not 100%, and that matters to what the shipped preset actually
    //     gets. CoverAlpha never LOWERS what vanilla baked, on purpose — that is what keeps this
    //     composable with mods that write the same alpha — and vanilla bakes RoofedAreaMinSkyCover (100)
    //     on every roofed cell. So the brightest a roofed cell can render through this path is
    //     `keep x (1 - 100/255)` = `keep x 0.608`, whatever the floor asks for. On the Cinematic pair
    //     (0.50 / 0.50) at the night floor that is 0.304 against 0.500 outdoors, where the compounded
    //     version gave 0.249: the gap closes by 22% and does not shut. Full parity would mean lowering
    //     vanilla's own clamp, which is a separate decision with a real interop cost, and is deliberately
    //     not taken here. The upshot is the reassuring one — an interior stays visibly an interior at the
    //     floor rather than dissolving into the ground outside it.
    //   - **keep == 0 is not a divide-by-zero to guard, it is the saturated case.** A fully black overlay
    //     renders black whatever the cover is, so no cap delivers the floor and 1 ("admit everything") is
    //     the honest best effort — the same answer the branch above gives, which is why this returns
    //     before dividing rather than clamping afterwards.
    //
    // Feeds CapOcclusion's minIndoorBrightness argument in place of the raw setting; with the feature off
    // the adapter passes the raw setting instead, which is why nothing here needs a flag of its own.
    public static float EffectiveIndoorFloor(float minIndoorBrightness, float overlayKeep)
    {
        float floor = Clamp01(minIndoorBrightness);
        if (floor <= 0f)
            return 0f;

        float keep = Clamp01(overlayKeep);
        if (keep <= floor)
            return 1f;

        return Clamp01(floor / keep);
    }

    // --- The general "somebody else lit this interior" term (replaces the Ambient Light interop) ---
    //
    // Supersedes a byte-for-byte re-derivation of Ambient Light's own private falloff formula, which we
    // used to reflect their map component and settings for (issue #80). That approach worked and was
    // wrong in shape: it fixed exactly one mod by name, broke whenever that mod refactored, and every
    // other mod that brightens interiors would have needed its own copy of the same scaffolding. This
    // asks a question no mod has to opt into.
    //
    // THE SIGNAL. Verse.GlowGrid.GroundGlowAt gates its sky term on `!map.roofGrid.Roofed(c)`, so for a
    // roofed cell vanilla returns nothing but the artificial term — lamps, fires, cave plants, via
    // GetAccumulatedGlowAt. Therefore, on a roofed cell:
    //
    //     anything in GroundGlowAt above the artificial term was put there by another mod,
    //     and it is sky-sourced, because that is the only term vanilla suppressed.
    //
    // Identically 0 on an unmodded install, so it cannot move a single vertex by itself, and it picks
    // up any mod that raises indoor glow however it does it — postfix, transpiler, or writing the grid
    // directly — without us naming one. §7c's own native BFS is unaffected: it never goes through
    // GroundGlowAt, so it neither feeds this term nor is shadowed by it (see SkyFalloffSource for how
    // the two are arbitrated).
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
    // already provide" is the difference, and it is 0 whenever the lamps dominate. Live-verified in
    // Tests/Scenarios/indoor_glow_lamp.json.

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
}
