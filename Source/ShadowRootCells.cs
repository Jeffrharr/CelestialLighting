using RimWorld;
using Verse;

namespace CelestialLighting;

// The live-state adapter for §30's shadow root shade — the thin half that reads a map and hands
// primitives to ShadowRootShadeMath, matching how EaveCells wraps EavesMath.
//
// Kept beside EaveCells rather than folded into it because the two answer different questions about
// different cells: EaveCells asks "is this roofed cell open to the weather", which needs a room
// lookup, while this asks "does an edifice stand here", which is a single grid read. Sharing a file
// would imply they share a cost, and this one is deliberately cheap enough to call per cell inside a
// section regenerate.
public static class ShadowRootCells
{
    // Whether this cell takes the shadow root's shade.
    //
    // Reads the edifice grid directly rather than going through EaveShadowGrid. That grid resolves a
    // section window plus a one-cell skirt for the shadow MESH, and folds in §15's roofline casters —
    // both wrong here. A roofline caster is not a sprite standing in a cell, so it has no sprite to
    // be detached from, and §15b already shades those cells by their own rule; picking them up here
    // as well would shade the same cell twice through the same material. Asking the edifice grid
    // keeps this to exactly the buildings vanilla itself treats as static shadow casters.
    public static bool TakesRootShade(Map map, IntVec3 cell)
    {
        Building edifice = map.edificeGrid[cell];
        if (edifice == null)
            return false;

        return ShadowRootShadeMath.TakesRootShade(
            casterHeight: edifice.def.staticSunShadowHeight,
            isDoor: edifice is Building_Door);
    }
}
