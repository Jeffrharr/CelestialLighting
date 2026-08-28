using Verse;

namespace CelestialLighting;

// The window during which our own writes to vanilla's light-blocker bit are allowed to change the
// glow grid WITHOUT flagging any section for redraw.
//
// THE PROBLEM, AND IT IS NOT VANILLA'S. A vanilla door swing touches light not at all:
// Building_Door.DoorOpen sets openInt, clears the reachability cache, raises a notification, and goes
// near neither the glow grid nor the map mesh. The regenerates a door swing provokes here are ours,
// bought deliberately — "light through open doors" clears vanilla's lightBlockers bit so vanilla's
// flood arrives through the opening beside our beam. GlowGrid.LightBlockerRemoved then calls
// DirtyCell, which raises Roofs and GroundGlow on the map mesh, and the lighting overlay regenerates.
// Measured on the door storm: 7.4 regenerates a frame against 1.9 with the write switched off, at
// roughly 1.2 ms of mask each.
//
// WHAT THIS SUPPRESSES, AND WHAT IT DELIBERATELY DOES NOT. Only the SECTION flagging, and only inside
// this scope. DirtyCell's other three effects all still happen: dirtyCells is set, anyDirtyCell is
// raised, and Notify_GlowChanged fires — so vanilla still re-floods the affected lights and every
// gameplay reader of GroundGlowAt sees exactly what it sees today. Plants, pawn vision, work speed
// and every other mod are untouched. That is what makes this a rendering change rather than a
// gameplay one, and it is the difference between this and the aperture spill, which replaces
// vanilla's wash with our own.
//
// WHY IT IS SAFE TO DROP THE FLAG AT ALL. The sections that actually need to look different after a
// door swing are the ones whose COVERAGE changed, and Patch_VectorLightDraw already flags exactly
// those from VectorLightMath.CoverageDelta — 1,318 of them over the same storm. Vanilla's flag is a
// blunter statement of the same fact: it names one cell and lets MapMeshDirty fan it out to the nine
// around it. Where the two disagree is where this can go wrong, and it is worth being precise about
// it rather than reassuring: vanilla's flood is geodesic and keeps bending after the doorway, so a
// cell lit only by a path that wraps around a corner beyond the door has its glow changed by the
// re-flood while our straight-line coverage never moved. That section is not flagged by either party
// and holds a stale overlay until something else dirties it.
//
// THE RESIDUE IS DIM BUT IT IS NOT NOTHING, and this is the failure class this repo keeps recording:
// a section holding a value that has already moved logs nothing, throws nothing and moves no other
// probe. SuppressedSections is the witness — it counts what was declined — and it is meant to be read
// beside vector_light_section_dirties, not instead of it.
public static class GlowDirtyScope
{
    // Depth rather than a bool. LightBlockerAdded and LightBlockerRemoved are both postfixed by
    // §27's own invalidation, and nothing forbids a future caller nesting one write inside another's
    // notification; a bool would be cleared by the inner scope's exit while the outer one was still
    // open, which would let exactly one section flag through for reasons nobody could reproduce.
    private static int depth;

    // How many MapMeshDirty calls this scope declined, and how many distinct sections those calls
    // would have flagged. BOTH, because they answer different questions: the call count says how
    // often we provoke vanilla, and the section count says what it costs — and the ratio between them
    // is the fan-out that made 240 door swings into thousands of regenerates. Read them together with
    // vector_light_section_dirties, which is what we flag deliberately.
    public static long SuppressedCalls;
    public static long SuppressedSections;

    public static bool Active => depth > 0;

    public static void ResetCounters()
    {
        SuppressedCalls = 0;
        SuppressedSections = 0;
    }

    // Wraps one write to vanilla's blocker bit. Structured as enter/exit around the caller's own call
    // rather than as a delegate, because the call it wraps is a plain void method on a struct-heavy
    // API and a closure here would allocate on a path that runs per door per swing.
    public static void Enter()
    {
        depth++;
    }

    public static void Exit()
    {
        if (depth > 0)
        {
            depth--;
        }
    }

    // Charge one declined dirty. Split out so the patch stays a guard clause and the arithmetic —
    // which is the part that has to agree with vanilla's own adjacency rule — lives in
    // SectionDirtyMath where an offline test can reach it.
    public static void NoteSuppressed(Map map, IntVec3 cell, bool regenAdjacentCells)
    {
        SuppressedCalls++;

        if (map == null)
        {
            return;
        }

        SuppressedSections += SectionDirtyMath.SectionsTouchedByCellDirty(
            cell.x, cell.z, regenAdjacentCells, Section.Size, map.Size.x, map.Size.z);
    }
}
