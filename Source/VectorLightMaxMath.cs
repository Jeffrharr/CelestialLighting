using System;

namespace CelestialLighting;

// §27's composition, phase 3b: `max(vanilla, ours)` per cell, with the shadow mask applied after.
//
// WHAT IT REPLACES. Phase 3 ships the mask plus a flat additive beam — a share of the falloff curve
// laid over the WHOLE visibility polygon. The polygon is the lit region, so it lifts the open room by
// exactly as much as it lifts the doorway, and DESIGN.md §27 records the consequence measured in
// play: the lit room renders at 1.175x vanilla and reads as "too bright". The beam's level and the
// room's brightness were the same quantity, so there was no value that bought one without the other.
//
// THE MAX SEPARATES THEM, and that is the whole of why it is worth building:
//
//     composed(c) = lit(c) * max( delivered(c), straight(c) )
//
// `delivered` is what vanilla's flood actually put in the cell — falloff of the GEODESIC distance,
// the path that bent around the walls. `straight` is the same falloff of the same curve with the
// same emitter colour, evaluated on the straight-line distance instead. Wherever an unobstructed
// lamp can see a cell the two are the same number and the max adds NOTHING, so the open room is
// left at exactly vanilla's own level. Where vanilla's light had to bend — through a doorway, past
// a corner — it arrived dimmer than the straight line, and there the max takes ours. Where vanilla
// arrived not at all (an open door, which its glow grid never learns about) the max is the whole
// straight-line value.
//
// So the room stops moving and the beam survives, which is what the flat term could not do.
//
// WRITTEN AS AN ADDITION, BECAUSE THAT IS WHAT THE MASK ALREADY IS. The mask edits vanilla's own
// vertex colours by subtracting `delivered * (1 - lit)`. Adding `(straight - delivered)+ * lit` in
// the same pass composes to `lit * max(delivered, straight)` exactly — the two terms are one
// signed accumulation over the same cells in the same lane, so there is no second pass, no second
// mesh and specifically no shader. Issue #151 wanted one only because phase 2b tried to compute this
// difference in a fragment program, where vanilla's per-emitter glow had to be smuggled in through a
// spare UV channel; the mask is holding that number in C# already.
//
// WHY PHASE 2b MEASURED THIS AS A NO-OP, since DESIGN.md says so and this file says otherwise. Phase
// 2b took the max against `VectorLightMath.Falloff`, which clamps at MinFalloffDistance and is
// evaluated on a euclidean distance in cells. Vanilla's flood is neither: it accumulates an INTEGER
// cost in hundredths, 100 a cardinal step and 141 a diagonal, and it seeds the emitter's own cell at
// 100 rather than 0 — so its falloff is evaluated one whole cell further out than a naive euclidean
// reading of the same geometry. Against a curve sampled a cell too close, `ours` is unconditionally
// the larger of the two and the max degenerates to `ours` everywhere, which is a second lighting
// model wearing a max's clothes. Getting the no-op right is the same work as getting the beam right:
// both need `straight` to be vanilla's own arithmetic, transcribed rather than approximated.
//
// Hence this file, which is a transcription of Verse.Glow.ComputeGlowGridsJob and Verse.ColorInt and
// nothing else. It is deliberately NOT expressed in terms of VectorLightMath.Falloff: that curve is
// §27's own draw, with a source clamp §27 needs so its fan has a finite value at the apex, and a
// difference taken across two curves that disagree by a clamp does not cancel where the whole
// correctness of this one is that it cancels to the bit.
//
// Pure by the repo's convention: no UnityEngine, no Verse, primitives in and primitives out.
public static class VectorLightMaxMath
{
    // ComputeGlowGridsJob's own step costs, in hundredths of a cell. Its Directions array puts the
    // four cardinals first and the four diagonals last, and Flood charges `(i < 4) ? 100 : 141`.
    public const int CardinalCost = 100;
    public const int DiagonalCost = 141;

    // PrepareFill seeds the emitter's OWN cell at intDist = 100, not 0 — see its
    // `if (i == num) value.intDist = 100;`. Every distance vanilla's falloff ever sees is therefore
    // one cell larger than the geometry, and this constant is the entire reason phase 2b's max
    // measured as a no-op: sampling the curve at the raw octile distance makes `straight` about
    // 1.25x `delivered` two cells out from an unobstructed lamp, growing towards 2x at the rim, so
    // the difference that is supposed to be zero in open ground is instead a lift over the whole
    // room. It is not a rounding error and it does not average out.
    public const int SourceSeedCost = 100;

    // Mathf.Lerp's third argument in SetGlowFromDist. Same 0.4 as VectorLightMath.InverseSquareWeight
    // and deliberately restated rather than referenced: this file is a transcription, and it should
    // keep reading like vanilla's source even if §27's own curve is ever retuned away from it.
    public const float InverseSquareWeight = 0.4f;

    // What vanilla's flood WOULD accumulate reaching a cell `dx, dz` away with nothing in the way:
    // the octile distance in its own integer hundredths, plus the seed above.
    //
    // Integer, because vanilla's is. The flood adds ints and divides by 100f exactly once, at the
    // point of use, so a float accumulation here would differ in the last place at some distances and
    // the difference would land as an off-by-one on whichever colour channel happened to sit against
    // an integer boundary — a faint scatter of lit cells that reads as a discretisation artefact
    // rather than as an arithmetic one.
    public static int FreeFloodCost(int dx, int dz)
    {
        int ax = dx < 0 ? -dx : dx;
        int az = dz < 0 ? -dz : dz;

        int diagonals = ax < az ? ax : az;
        int straights = (ax > az ? ax : az) - diagonals;

        return SourceSeedCost + diagonals * DiagonalCost + straights * CardinalCost;
    }

    // SetGlowFromDist's curve, at the precision and in the order vanilla writes it.
    //
    // WRITTEN IN VANILLA'S ORDER OF OPERATIONS, NOT THE TIDY ONE. It hoists `-1f / glowRadius` into a
    // local and forms `1f + num * num2`, which is not bit-identical to `1f - distance / radius` in
    // float arithmetic. The difference is an ULP, and an ULP is enough here for the same reason the
    // integer distance is: this value is differenced against vanilla's own, and a difference that
    // should be exactly zero must actually be exactly zero, not nearly.
    public static float VanillaFalloff(float distance, float radius)
    {
        // SetGlowFromDist's `if (num2 <= glowLight.glowRadius)`, and nothing else — in particular no
        // clamp at a minimum distance, because vanilla has none. At the source it saturates, which is
        // what vanilla does too; the projection below is what bounds it.
        if (radius <= 0f || distance > radius)
            return 0f;

        float perRadius = -1f / radius;
        float inverseSquare = 1f / (distance * distance);
        float linear = 1f + perRadius * distance;

        return linear + InverseSquareWeight * (inverseSquare - linear);
    }

    // ColorInt.ProjectToColor32Fast's RGB half: pass the channels through untouched unless the
    // brightest exceeds a byte, in which case scale all three by 255/brightest so the HUE survives
    // and only the level is clipped. Alpha is vanilla's business (it carries the flood's distance)
    // and is no part of what §27 composes.
    public static void ProjectLikeVanilla(int r, int g, int b, out int outR, out int outG, out int outB)
    {
        int brightest = r;

        if (g > brightest)
            brightest = g;

        if (b > brightest)
            brightest = b;

        if (brightest > 255)
        {
            outR = r * 255 / brightest;
            outG = g * 255 / brightest;
            outB = b * 255 / brightest;
            return;
        }

        outR = r;
        outG = g;
        outB = b;
    }

    // What vanilla WOULD have stored for this cell had its light gone straight there: the whole
    // chain, from the emitter's own glowColor through the falloff, the non-negative clamp and the
    // projection, exactly as ComputeGlowGridsJob writes it into the per-light array the mask reads.
    //
    // The point of transcribing the whole chain rather than the curve alone is that the mask reads
    // `delivered` out of that array POST-projection, as bytes. A difference is only meaningful
    // between two numbers in the same space, and every step here — the integer distance, the int
    // truncation in `ColorInt * float`, the projection's scaling — is a place where a value computed
    // in float and rounded once at the end would land a unit or two away from vanilla's own.
    //
    // Returns false when vanilla would have stored nothing at all: past the radius, past the flood's
    // own cost ceiling, or with every channel truncating to zero. That last case is vanilla's
    // `if (colorInt.r > 0 || colorInt.g > 0 || colorInt.b > 0)` and it matters at the rim, where the
    // curve underflows a byte — treating it as a stored zero rather than as "nothing stored" is what
    // put a crescent of owed light around the outer edge of a lamp in an earlier attempt at this.
    public static bool StraightLineGlow(
        int colourR, int colourG, int colourB, int dx, int dz, float radius,
        out int r, out int g, out int b)
    {
        r = 0;
        g = 0;
        b = 0;

        if (radius <= 0f)
            return false;

        int cost = FreeFloodCost(dx, dz);

        // Flood's own ceiling: `int num = Mathf.RoundToInt(glowLight.glowRadius * 100f)` and then
        // `if (num5 > num) continue`, so a cell whose cheapest path costs more than that is never
        // reached at all — checked here in the same integer space rather than inferred from the float
        // distance, which disagrees at the rim on exactly the cells most likely to be looked at.
        if (cost > RoundToIntLikeUnity(radius * 100f))
            return false;

        float distance = cost / 100f;
        float falloff = VanillaFalloff(distance, radius);

        if (falloff <= 0f)
            return false;

        // `ColorInt * float` truncates each channel toward zero — it is `(int)((float)a.r * b)` — and
        // then ClampToNonNegative floors at zero. Both are load-bearing at the rim.
        int scaledR = (int)(colourR * falloff);
        int scaledG = (int)(colourG * falloff);
        int scaledB = (int)(colourB * falloff);

        if (scaledR < 0)
            scaledR = 0;

        if (scaledG < 0)
            scaledG = 0;

        if (scaledB < 0)
            scaledB = 0;

        if (scaledR <= 0 && scaledG <= 0 && scaledB <= 0)
            return false;

        ProjectLikeVanilla(scaledR, scaledG, scaledB, out r, out g, out b);
        return true;
    }

    // Mathf.RoundToInt, which is `(int)Math.Round((double)f)` — banker's rounding, to even. A radius
    // of x.xx5 * 100 is exactly the case where a naive `(int)(f + 0.5f)` would put the flood's
    // ceiling one hundredth of a cell away from vanilla's, and one hundredth is a whole cell's worth
    // of difference at the rim, where the last ring of cells sits within a step of the cutoff.
    public static int RoundToIntLikeUnity(float value) => (int)Math.Round((double)value);

    // The light this emitter OWES the cell: how much brighter the straight line is than what the
    // flood delivered, restricted to the share of the cell our polygon can actually see.
    //
    // Zero in three separate ways, and each one is a promise the flat beam could not make:
    //
    //  - `straight <= delivered` — vanilla already got there by the short path, so the open room is
    //    untouched. This is the case that fires over almost every cell of almost every colony.
    //  - `coverage == 0` — our polygon cannot see the cell, so there is nothing to owe; whatever
    //    vanilla bent into it is the mask's business, not this term's.
    //  - the emitter is unobstructed — no blockers means no bending means nothing owed anywhere,
    //    which is why VectorLightMask can keep skipping those emitters outright.
    //
    // Per channel rather than on a luminance, because the two inputs are per channel and a coloured
    // lamp that owes light owes it in its own colour.
    public static int OwedChannel(int straight, int delivered, int coverage)
    {
        if (coverage <= 0)
            return 0;

        int owed = straight - delivered;

        if (owed <= 0)
            return 0;

        return owed * (coverage >= 255 ? 255 : coverage) / 255;
    }

    // The mask's per-cell accumulation for one emitter, as a SIGNED quantity: what vanilla put in the
    // cell by bending, taken out, less what it owes by the straight path, put back.
    //
    //     net = delivered * (1 - lit)  -  (straight - delivered)+ * lit
    //
    // Subtracting that from vanilla's own value leaves `lit * max(delivered, straight)`, which is the
    // composition this file exists to state. Returning one signed number rather than two terms is
    // what keeps the mask a single accumulator with a single clamp at the end: two accumulators would
    // clamp twice, and clamping a subtraction before adding to it loses light at exactly the cells
    // where both terms are large — the boundary cells of a doorway, which is the whole subject.
    public static int NetShadowChannel(int straight, int delivered, int coverage)
    {
        int shadowed = 255 - (coverage >= 255 ? 255 : coverage < 0 ? 0 : coverage);

        return delivered * shadowed / 255 - OwedChannel(straight, delivered, coverage);
    }
}
