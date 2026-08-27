namespace CelestialLighting;

// Pure core, DESIGN.md §27e phase 2: the sub-cell geometry of a door part-way through its slide, so
// §27's rays track the leaf edges as they animate instead of the aperture popping open in one frame.
//
// Verse-free, like DoorOcclusionMath beside it. The adapter (VectorLightBlockers) reads OpenPct and
// Rotation off the live Building_Door; everything about WHERE the leaves are lives here.
//
// WHAT THIS BUYS OVER THE BOOLEAN. DoorOcclusionMath answers "does this cell occlude", which is all
// a bool grid can carry, and a bool grid is what SilhouetteSegments consumes. But VectorLightMath.Build
// takes an arbitrary Segment[] and fires a corner ray at every endpoint it is given — it has no idea
// those segments came from a grid. So a partially-open door does not need the grid to learn about
// fractions: drop the door cell from the grid entirely and hand Build the two leaf edges directly.
// The beam then narrows to exactly the gap between them, and because Build already puts a ray on
// every segment endpoint, the penumbra tracks the leaves for free.
//
// THE MODEL, AND WHERE IT KNOWINGLY SIMPLIFIES. Vanilla draws two movers, each a full 1x1 quad, slid
// +/- 0.45 * OpenPct along the wall (Building_Door.DrawAt -> DrawMovers). Two 1-wide quads sliding
// 0.45 cannot geometrically clear a 1-wide cell, so the visible opening is produced by the door
// ARTWORK inside those quads rather than by the quads' own extents — there is no exact occluder
// outline to copy, only an apparent one. So this models the apparent thing: each leaf occupies half
// the cell when shut and recedes to its own side as OpenPct rises, leaving a centred gap of exactly
// OpenPct cells. Shut is exactly the closed door's full-width occluder and open is exactly a bare
// doorway, which are the two ends that have to be right because both are pinned against measurements
// taken before this file existed.
//
// It is a rendering-side approximation of a rendering-side illusion, which is the appropriate kind
// of wrong here: the feature's whole claim is that the beam looks like it tracks the door.
public static class DoorApertureMath
{
    // Below this, a leaf is not worth handing to Build: it still costs two corner rays and a segment
    // test per light, and it can no longer occlude anything a ray could resolve. Doors spend only a
    // frame or two this close to fully open, so the saving is small; the reason it exists is that a
    // ZERO-length segment is a degenerate input to the ray/segment intersection, and feeding one in
    // every frame a door finishes opening is how you get a division by zero in a hot path.
    public const float MinimumLeafLength = 0.001f;

    // Where the two leaves stand, expressed along the wall axis in cells, for a door cell whose span
    // on that axis is [axisMin, axisMin + 1].
    //
    // Returns the two leaf intervals as [aStart, aEnd] and [bStart, bEnd]. Leaf A holds the low side
    // and leaf B the high side; each is half a cell when shut and shrinks to nothing when open, so
    // the gap between them is centred and exactly `openPct` wide. openPct is clamped rather than
    // trusted: OpenPct is `protected virtual` and a modded door is free to return anything.
    public static void LeafSpans(
        float axisMin, float openPct,
        out float aStart, out float aEnd, out float bStart, out float bEnd)
    {
        float p = openPct < 0f ? 0f : (openPct > 1f ? 1f : openPct);
        float leaf = 0.5f * (1f - p);

        aStart = axisMin;
        aEnd = axisMin + leaf;
        bStart = axisMin + 1f - leaf;
        bEnd = axisMin + 1f;
    }

    // Whether a leaf of this length is worth emitting at all. Named rather than inlined because both
    // faces ask it and because the threshold is the interesting part, not the comparison.
    public static bool LeafWorthEmitting(float start, float end) =>
        end - start >= MinimumLeafLength;

    // QUANTISATION -- the performance lever, and the reason this feature is affordable at all.
    //
    // OpenPct changes every tick while a door animates, and every distinct value means a fresh bake
    // for every light whose window covers that door. A wooden door takes tens of ticks to swing, so
    // tracking it exactly would turn one bake per door use into tens of them, on a path whose entire
    // cost model assumes geometry changes when a player builds something.
    //
    // Snapping to a small number of steps caps that at `steps` bakes per swing no matter how slow the
    // door or how fast the game is running. The eye cannot resolve the difference: a door swing is
    // under a second, so eight steps is already finer than the animation reads, and the alternative
    // is paying per tick for detail nobody can see.
    //
    // Rounds rather than truncates so the sequence is symmetric between opening and closing -- a door
    // that truncated on the way open and on the way shut would visibly stick at one end.
    public static float Quantise(float openPct, int steps)
    {
        if (steps <= 0)
        {
            return openPct;
        }

        float p = openPct < 0f ? 0f : (openPct > 1f ? 1f : openPct);
        return (float)System.Math.Round(p * steps) / steps;
    }

    // Default step count. Eight is where the visible stepping disappears in the filmed sweep while
    // the bake count stays in single figures per swing; see DESIGN.md §27e phase 2 for the measured
    // comparison against tracking every tick.
    public const int DefaultQuantisationSteps = 8;

    // ---- an open door is a hole in the wall, to vanilla's glow grid as well as to our polygon ----

    // Whether this door's cell should be a HOLE in vanilla's light-blocker grid right now.
    //
    // WHY THIS EXISTS AT ALL. RimWorld's glow grid never learns a door opened: Building.SpawnSetup
    // writes def.blockLight into lightBlockers once, and Building_Door.DoorOpen touches the grid not
    // at all. So a bare one-cell GAP in a wall floods light through and an open DOOR one cell away
    // delivers nothing — two identical apertures, rendered from two completely different vanilla
    // inputs. Every attempt to reconcile the two by adjusting what WE draw has failed, because the
    // disagreement is not in our model; it is that vanilla is lighting one of them and not the other.
    // Moving the bit removes the disagreement at its source, and everything downstream — the
    // polygon, the mask, the subtraction, the lift — then sees one situation instead of two.
    //
    // FULLY OPEN, NOT OPENING. `Building_Door.Open` flips true on the first tick of the swing while
    // the leaves take tens of ticks to finish sliding, so keying on it would flood a room with light
    // through a door the player can still see closed. The grid is whole-cell and binary — it cannot
    // express a half-open door — so the honest reading of a binary grid is the one that is only ever
    // true when the cell unambiguously IS a hole. Our own polygon has no such limit and tracks the
    // leaves continuously; see LeafSpans above.
    //
    // AND THE ASYMMETRY IS DELIBERATE. This goes false again on the first tick of a CLOSE, not at the
    // end of one, because `Open` is what says which end the door is heading for and a closing door
    // has stopped being a hole. Erring toward "blocked" at both edges of the swing is the safe
    // direction: this term is gameplay light, so the failure that matters is light appearing where a
    // door is shut, not light arriving a few ticks late.
    //
    // A SEE-THROUGH DOOR IS NEVER A HOLE, and that is not the same statement as "it is always open".
    // SpawnSetup only writes lightBlockers when def.blockLight is true, so a glass door's bit was
    // never set — and a rule that cleared it on open and SET it on close would make glass doors start
    // blocking gameplay light the first time anyone shut one, which is a regression vanilla does not
    // have. `blocksLightWhenShut` false means "this cell is not ours to write", not "it is open".
    public static bool GlowGridHoleWanted(
        bool blocksLightWhenShut, bool headingOpen, float openFraction)
    {
        if (!blocksLightWhenShut)
        {
            return false;
        }

        return headingOpen && openFraction >= FullyOpen;
    }

    // What counts as fully slid. Exactly 1 rather than a tolerance: OpenPct is a ratio of an integer
    // tick counter to its own maximum, so it reaches 1 exactly, and a tolerance here would open the
    // grid a tick or two early for no benefit.
    public const float FullyOpen = 1f;

    // The aperture OUR OWN RENDERER is currently drawing this door at, which is the one the glow grid
    // has to agree with.
    //
    // WHY THE GRID CANNOT JUST READ OpenPct. §27 draws a door at one of two apertures depending on
    // whether leaf tracking is on. With it ON the polygon follows OpenPct, so the beam grows with the
    // leaves. With it OFF -- phase 1's behaviour, and still what every arm that films the two against
    // each other uses -- the polygon treats the door as a bare doorway the instant `Open` goes true,
    // and OpenPct is not consulted at all.
    //
    // So a glow grid keyed on OpenPct regardless would, with tracking off, hold the cell blocked for
    // the whole slide while our own fan already drew a full-width beam through it: the two halves
    // disagreeing for tens of ticks, which is the exact failure this pair of flags exists to remove.
    // Asking "what is the renderer showing" instead makes them agree by construction under both
    // settings, and it is the reason this is a function rather than a field read at the call site.
    //
    // It also makes the feature reachable in a PAUSED scenario. OpenPct is a ratio of a tick counter,
    // and a scenario that has jumped the clock is not ticking, so a door opened by the harness stays
    // at 0 forever and a rule keyed on it can never fire -- the feature would read as dead in every
    // capture while working in play, which is the most expensive kind of wrong there is.
    public static float RenderedOpenFraction(bool trackingLeaves, float openFraction)
    {
        return trackingLeaves ? openFraction : FullyOpen;
    }
}
