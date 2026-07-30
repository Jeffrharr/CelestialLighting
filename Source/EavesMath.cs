namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file, the same discipline
// Formulas.cs / IndoorOcclusionMath.cs follow. Compiled into both Source (net481, runs inside
// RimWorld) and Tests (net8.0, runs standalone via `dotnet test`) through a linked <Compile
// Include>, so the exact shipped code is the code under test. Anything that needs Map/RoofGrid/Room
// belongs in the adapters (Patch_ShadowMeshPerimeter, Patch_IndoorSkyOcclusion), not here.
//
// Subsystem 15 (DESIGN.md §15): "eaves" — roofed cells that are not actually inside anything.
//
// Why this exists at all. Both of our roof-aware effects reached for `roofGrid.Roofed(cell)` as
// their test for "is this cell indoors", and that predicate is too coarse in both directions:
//
//   * §4's shadow mesh (Patch_ShadowMeshPerimeter) never consults the roof grid, so it only ever
//     considers *edifices* as shadow casters. A porch roof, an overhang, or the eave that oversails
//     a wall casts nothing — sunlight lands on the porch floor as if the roof above it were not
//     there. With §1's full elevation/azimuth sweep raking shadows across the map all day this
//     reads as an obvious hole, much more so than it did under vanilla's narrow shadow angles.
//
//   * §7b's sky occlusion (Patch_IndoorSkyOcclusion) treats every roofed cell as sealed, so the
//     same porch goes pitch black at noon while standing wide open to the sky on three sides.
//
// Both want nearly the same finer distinction, so it lives here once. A roofed cell is either
// ENCLOSED — part of a room that holds its own temperature, i.e. genuinely inside a building — or an
// EAVE: it has a roof overhead but breathes outdoor air. `Room.UsesOutdoorTemperature` is the game's
// own answer to that question (it is what decides whether a room heats, whether rain reaches it, and
// whether pawns count as sheltered), so keying off it means our notion of "indoors" can never drift
// from the one the simulation already uses.
//
// "Nearly", because the two consumers diverge on exactly one input — a mountain. §7b and §15b are
// asking what is above the cell, and a mountain is a ceiling; §4's mesh is asking what the roof's
// EDGE does to the sunlight next to it, and a mountain's edge is a roofline like any other. So there
// are two predicates below over the same inputs, CastsRoofShadow and IsEave, differing only in the
// thickRoof veto. They were one predicate until a shadow seam proved they are two questions.
//
// Provenance: the *idea* of using UsesOutdoorTemperature to separate porches from interiors is the
// load-bearing insight of Perspective: Eaves (Owlchemist, continued by Mlie — MIT, and inspected
// here as such). Nothing below is copied from it: that mod expresses the rule by rewriting the
// whole map's edifice array through a transpiler, which is both O(map) per section and impossible
// for us to compose with (see DESIGN.md §15's conflict note). This is the rule restated as a pure
// per-cell function that our own section walk can call directly.
public static class EavesMath
{
    // Shadow height an eave casts, in the same units as ThingDef.staticSunShadowHeight (1.0 == a
    // full-height wall; vanilla's Wall and Door both declare exactly 1.0). A roof is held up at wall
    // height, so its edge throws a wall's shadow — anything shorter would make a porch read as a
    // knee-high lip rather than a roofline.
    public const float RoofCasterHeight = 1f;

    // "There is a roof over this cell that is held up at wall height, and open air under it." The
    // base rule, shared by both predicates below. `hasRoom` is false when the cell is outside the
    // region grid entirely (RimWorld hands back a null Room there); such a cell is deliberately NOT
    // admitted, so an unknown room can never conjure a shadow caster out of nothing.
    //
    // This is the SHADOW question, and it is deliberately blind to thick roof. A roofline's outline
    // against the sun does not care what is holding the roof up: at the boundary between mined-out
    // mountain and built structure — the commonest shape in a mountain base — one continuous
    // roofline runs from RoofRockThick to RoofConstructed with no visible seam, so vetoing the
    // mountain half made one side throw a full shadow and the other side none, every frame the sun
    // was up. Admitting thick roof here is what closes that discontinuity.
    //
    // Admitting a whole cave's interior costs nothing to look at: only a caster blob's TRAILING
    // perimeter renders (the leading-edge skirts are backface-culled — DESIGN.md §15), and a cavern
    // cell's neighbours are either more cavern at the same height or solid rock, which vanilla
    // already declares at 1.0. Only the cells where a mountain roofline actually meets open sky
    // gain a quad. Patch_ShadowMeshPerimeter drops the footprint quad for the cells that gain
    // nothing, so it costs no vertices either.
    public static bool CastsRoofShadow(bool roofed, bool hasRoom, bool usesOutdoorTemperature) =>
        roofed && hasRoom && usesOutdoorTemperature;

    // The OCCLUSION/SHADE question: is this cell a porch — roofed, but open to the weather? Same
    // rule plus a `thickRoof` veto, and that veto is load-bearing rather than a nicety.
    // UsesOutdoorTemperature is `TouchesMapEdge || OpenRoofCount >= 25% of cells`, and a cave system
    // that reaches the map edge — the common case, not a corner one — satisfies the first disjunct
    // for its whole interior. Without this veto every cell of such a cave would classify as an eave:
    // §7b would stop occluding it (a lit-at-61%-of-sky cavern, the exact bug §7b exists to fix) and
    // §15b would paint it as an open-air porch. There is no sky under a mountain, which is the same
    // exception vanilla itself makes in SectionLayer_LightingOverlay.
    //
    // So the split: the veto guards what is UNDER the roof (is there sky above this cell?), not what
    // the roof's edge does to the sunlight beside it. Those two questions shared one predicate until
    // the seam above proved they are not the same question.
    public static bool IsEave(bool roofed, bool thickRoof, bool hasRoom, bool usesOutdoorTemperature) =>
        !thickRoof && CastsRoofShadow(roofed, hasRoom, usesOutdoorTemperature);

    // Exact complement of IsEave *within roofed cells*: every roofed cell is one or the other, and
    // an unroofed cell is neither. Stated as `!(...)` rather than `hasRoom && !usesOutdoor...` on
    // purpose — the null-room case has to fall on the enclosed side here even though it falls on the
    // not-an-eave side above. Both defaults are the conservative one for their own caller: an
    // unknown room adds no shadow, and it also does not suddenly un-occlude a cell vanilla was
    // already treating as roofed.
    public static bool IsEnclosed(bool roofed, bool thickRoof, bool hasRoom, bool usesOutdoorTemperature) =>
        roofed && !IsEave(roofed, thickRoof, hasRoom, usesOutdoorTemperature);

    // Effective shadow-caster height for one cell: whatever edifice stands there, or a wall-height
    // roofline if a roof is casting here. `edificeShadowHeight` is 0 for an empty cell, which is how
    // vanilla encodes "casts nothing".
    //
    // A mountain roofline is capped at wall height like any other, and that is a knowing compromise
    // rather than an oversight: Formulas.ShadowCasterAlphaByte packs the height into a vertex-colour
    // BYTE and the shader is `position += colour.a * _CastVect`, so alpha 255 = 1.0 = exactly one
    // wall of extrusion. There is no room in the channel to say "and this one is a cliff". A
    // mountain that under-throws reads as unremarkable; a mountain that throws nothing beside a
    // shed that throws fully reads as a bug, and the defect being fixed here is the discontinuity,
    // not the length.
    //
    // Takes the MAX rather than overwriting, which is a deliberate divergence from how Perspective:
    // Eaves does it — that mod substitutes a fixed dummy caster into any cell whose edifice is not
    // exactly 1.0, which silently shortens a modded caster taller than a wall (a watchtower, a
    // battlement) back down to wall height wherever a roof happens to cover it. Max never lowers an
    // existing caster, so adding a roof over something can only ever add shadow.
    public static float CasterHeight(float edificeShadowHeight, bool castsRoofShadow)
    {
        if (!castsRoofShadow)
            return edificeShadowHeight;

        return edificeShadowHeight > RoofCasterHeight ? edificeShadowHeight : RoofCasterHeight;
    }
}
