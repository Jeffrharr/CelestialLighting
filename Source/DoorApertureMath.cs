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
}
