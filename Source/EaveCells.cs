using Verse;

namespace CelestialLighting;

// The live-state half of EavesMath (which carries the full "why" for §15): one place that turns a
// Map + cell into the eave/enclosed verdict, so every consumer asks the same question of the same
// grids in the same order.
//
// There are four consumers and they must never disagree — §7b's sky occlusion, §15's shadow-caster
// heights, §15b's eave shade layer, and the harness probe that counts eave cells. A disagreement
// between any two of them is invisible in code review and shows up as a porch that is shaded but not
// occluded (or counted but not drawn), so the predicate lives here exactly once rather than being
// re-derived per caller. §15's shadow heights ask the CastsRoofShadow variant rather than IsEave —
// the one deliberate divergence, spelled out in EavesMath — and it too is stated once, here.
//
// Takes the RoofDef rather than a `roofed` bool because thick roof is one of EavesMath's inputs, and
// every caller already has the def in hand or can get it for the same cost as the bool.
public static class EaveCells
{
    // Roofed, not under a mountain, and in a room that breathes outdoor air — a porch, a lean-to, an
    // overhang, the eave that oversails a wall.
    public static bool IsEave(Map map, IntVec3 cell, RoofDef roof)
    {
        // Short-circuit before the room query, which is the expensive half: most cells on any map are
        // unroofed, and an unroofed cell can be neither an eave nor enclosed.
        if (roof == null)
            return false;

        Room room = RoomLookup.RoomAtNoRebuild(map, cell);
        return EavesMath.IsEave(
            roofed: true,
            thickRoof: roof.isThickRoof,
            hasRoom: room != null,
            usesOutdoorTemperature: room != null && room.UsesOutdoorTemperature);
    }

    // Roofed and genuinely inside something. The complement of IsEave within roofed cells — see
    // EavesMath for why the null-room case falls on this side of the line and not the other.
    public static bool Encloses(Map map, IntVec3 cell, RoofDef roof) =>
        roof != null && !IsEave(map, cell, roof);

    // Convenience for callers holding only a cell. Kept separate rather than defaulting the roof
    // argument so the hot paths (a section walk that already read the roof grid) cannot fetch it
    // twice by accident.
    public static bool IsEave(Map map, IntVec3 cell) => IsEave(map, cell, map.roofGrid.RoofAt(cell));

    // IsEave without the mountain veto — §4's shadow mesh only, see EavesMath for why the two
    // questions parted company. Everything else about the query is identical, including the
    // short-circuit that keeps the room lookup off the unroofed majority of the map.
    public static bool CastsRoofShadow(Map map, IntVec3 cell, RoofDef roof)
    {
        if (roof == null)
            return false;

        Room room = RoomLookup.RoomAtNoRebuild(map, cell);
        return EavesMath.CastsRoofShadow(
            roofed: true,
            hasRoom: room != null,
            usesOutdoorTemperature: room != null && room.UsesOutdoorTemperature);
    }

    public static bool CastsRoofShadow(Map map, IntVec3 cell) =>
        CastsRoofShadow(map, cell, map.roofGrid.RoofAt(cell));
}
