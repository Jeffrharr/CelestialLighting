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
//     there. With §1/§3's full elevation/azimuth sweep raking shadows across the map all day this
//     reads as an obvious hole, much more so than it did under vanilla's narrow shadow angles.
//
//   * §7b's sky occlusion (Patch_IndoorSkyOcclusion) treats every roofed cell as sealed, so the
//     same porch goes pitch black at noon while standing wide open to the sky on three sides
//     (issue #33).
//
// Both want the same finer distinction, so it lives here once. A roofed cell is either ENCLOSED —
// part of a room that holds its own temperature, i.e. genuinely inside a building — or an EAVE: it
// has a roof overhead but breathes outdoor air. `Room.UsesOutdoorTemperature` is the game's own
// answer to that question (it is what decides whether a room heats, whether rain reaches it, and
// whether pawns count as sheltered), so keying off it means our notion of "indoors" can never drift
// from the one the simulation already uses.
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

    // The one distinction this file exists for. `hasRoom` is false when the cell is outside the
    // region grid entirely (RimWorld hands back a null Room there); such a cell is deliberately NOT
    // an eave, so an unknown room can never conjure a shadow caster out of nothing.
    //
    // `thickRoof` (a mountain) is an outright veto, and it is load-bearing rather than a nicety.
    // UsesOutdoorTemperature is `TouchesMapEdge || OpenRoofCount >= 25% of cells`, and a cave system
    // that reaches the map edge — the common case, not a corner one — satisfies the first disjunct
    // for its whole interior. Without this veto every cell of such a cave would classify as an eave:
    // §7b would stop occluding it (a lit-at-61%-of-sky cavern, the exact bug §7b exists to fix) and
    // every one of its cells would start casting a roofline shadow. There is no sky under a mountain
    // in any case, which is the same exception vanilla itself makes in SectionLayer_LightingOverlay.
    public static bool IsEave(bool roofed, bool thickRoof, bool hasRoom, bool usesOutdoorTemperature) =>
        roofed && !thickRoof && hasRoom && usesOutdoorTemperature;

    // Exact complement of IsEave *within roofed cells*: every roofed cell is one or the other, and
    // an unroofed cell is neither. Stated as `!(...)` rather than `hasRoom && !usesOutdoor...` on
    // purpose — the null-room case has to fall on the enclosed side here even though it falls on the
    // not-an-eave side above. Both defaults are the conservative one for their own caller: an
    // unknown room adds no shadow, and it also does not suddenly un-occlude a cell vanilla was
    // already treating as roofed.
    public static bool IsEnclosed(bool roofed, bool thickRoof, bool hasRoom, bool usesOutdoorTemperature) =>
        roofed && !IsEave(roofed, thickRoof, hasRoom, usesOutdoorTemperature);

    // Effective shadow-caster height for one cell: whatever edifice stands there, or a wall-height
    // roofline if the cell is an eave. `edificeShadowHeight` is 0 for an empty cell, which is how
    // vanilla encodes "casts nothing".
    //
    // Takes the MAX rather than overwriting, which is a deliberate divergence from how Perspective:
    // Eaves does it — that mod substitutes a fixed dummy caster into any cell whose edifice is not
    // exactly 1.0, which silently shortens a modded caster taller than a wall (a watchtower, a
    // battlement) back down to wall height wherever a roof happens to cover it. Max never lowers an
    // existing caster, so adding a roof over something can only ever add shadow.
    public static float CasterHeight(float edificeShadowHeight, bool isEave)
    {
        if (!isEave)
            return edificeShadowHeight;

        return edificeShadowHeight > RoofCasterHeight ? edificeShadowHeight : RoofCasterHeight;
    }
}
