using System;

namespace CelestialLighting;

// §27 phase 5b (epic #174 phase 5): the mask edits vanilla's light in the space vanilla ADDED it in,
// not in the space vanilla DISPLAYS it in.
//
// THE BUG THIS EXISTS FOR, and it is a direction rather than a magnitude. Ring lamps around a
// free-standing wall column and the shadows behind it get DEEPER as lamps are added. Physically it
// is the other way round — every lamp you add fills in some of the region the others cannot see, so
// more lamps means shallower shadows, always. A composition that runs the wrong way here is wrong in
// a way no amount of retuning can rescue, which is why the property below is stated as a direction
// and pinned as one.
//
// WHERE THE DIRECTION COMES FROM. Vanilla accumulates its glow grid as a sum over emitters and then
// PROJECTS that sum into a byte — CombineColorsJob.AddColors sums into a ColorInt and calls
// ColorInt.ProjectToColor32Fast, which is not a clamp: over 255 it scales all three channels by
// 255/max together, so the hue survives and the level saturates. Call the raw sum R and the
// displayed value P = proj(R).
//
// VectorLightMask subtracts, per emitter, `own(e) * (1 - lit(e))` — a quantity out of R — from the
// mesh vertex, which holds P. In an unsaturated cell R and P are the same number and that is exactly
// right. In a SATURATED cell they are not, and the mismatch has a sign: the subtraction is at full
// raw strength while the thing it is subtracted from has been scaled down. Six lamps at 150 each
// makes R = 900 and P = 255; blocking two of them takes 300 off 255 and lands on nothing. Add a
// seventh lamp the column also blocks and the SAME cell goes further negative, so the frame gets
// darker as the room gets brighter. That is the observed complaint, arithmetic-first.
//
// THE FIX IS TO DO THE EDIT BEFORE THE PROJECTION instead of after it. Write fold(...) for
// vanilla's own accumulation — `AddColors` run over the emitters reaching the cell, in vanilla's
// order, projecting after every addition, which is what a cell actually displays:
//
//     P     = fold(own(e) over all e)                   what vanilla displays
//     ours  = fold(own(e)*lit(e) + lift(e) over all e)  the same fold with our geometry in charge
//
// and hand VectorLightMask.Compose the DIFFERENCE, `P - ours`, in place of the raw subtraction.
// Nothing about the per-emitter accumulation changes; only the space the result is measured in.
//
// THE FOLD IS REPLAYED RATHER THAN APPROXIMATED, and that is the second version of this. The first
// reconstructed P as `proj(R)`, one projection of the true sum R, which is right for emitters of a
// single hue and wrong by up to 28 levels for a white sun lamp among warm lamps — see Accumulate.
// Against a mixed-hue cell the approximation could not be told apart from a cell we had simply
// mis-summed, so the self-check rejected it and the correction stood down exactly where a player
// notices: the sun lamp's shadow falling across cells its neighbours are lighting.
//
// THE PROPERTY, STATED, because a direction is what a test can hold and an example is not:
//
//     Adding an emitter to a cell never lowers that cell's level.
//
// read on the MAX CHANNEL, which is the quantity `GlowGrid.GroundGlowAt` itself reads and the only
// one that survives hue rotation — proj scales channels together, so a red channel genuinely can
// fall when a green lamp is added, for vanilla as much as for us. On the max channel:
//
//   * VANILLA IS THE ORACLE and satisfies it unconditionally: every step of its fold adds a
//     non-negative amount before projecting, and the projection is monotone on the peak.
//   * OURS SATISFIES IT under this file, because ours is vanilla's own fold with each emitter's
//     contribution replaced by `own(e)*lit(e) + lift(e)` — non-negative componentwise whatever the
//     geometry says, so no step of the fold can subtract.
//   * THE OLD COMPOSITION DOES NOT. `P - SUM own(e)*(1 - lit(e))` loses a full raw `own` per blocked
//     emitter against a P that has stopped growing. VectorLightSaturationMathTests pins that arm red
//     on purpose: a monotonicity test that only ever passes is a test of nothing.
//
// WHAT IS DELIBERATELY NOT CHANGED. Unsaturated cells. Where the raw sum is under 255 no step of
// either fold projects anything, so P is the plain sum and `P - ours` is the raw subtraction back
// again, to the byte — the correction is confined to exactly the cells that saturate, and
// everywhere else the shipped shadow is untouched. That is worth having as a property rather than
// as a hope, so VectorLightMask tests the raw sum itself and leaves the accumulators alone when it
// is under the ceiling.
public static class VectorLightSaturationMath
{
    // Where ColorInt.ProjectToColor32Fast starts scaling. Not a clamp: at 256 the whole colour is
    // rescaled by 255/256, not just the offending channel.
    public const int Ceiling = 255;

    // One step of vanilla's own accumulation, transcribed — and the reason nothing here
    // reconstructs a cell as a single projection of the true sum any more.
    //
    // `CombineColorsJob.AddColors` projects after EVERY addition, so what a cell displays is a FOLD
    // over the emitters reaching it and not one projection at the end. The fold is lossy: once a
    // colour has been scaled back to the ceiling, the light that set the divisor is gone, and a
    // later addition of a different hue lands somewhere the true sum never goes.
    //
    // FOR ONE HUE THE TWO AGREE, which is why a single projection stood up for as long as every
    // fixture in this repo was a ring of identical torches — the worst disagreement over 200,000
    // random same-hue sets is 5 levels. A sun lamp is WHITE, (370,370,370) before its own
    // projection, where a standing lamp is warm (214,148,94) and a torch warmer still. So a grow
    // room beside a workshop — a colony's most ordinary bright room — is the mixed-hue case, and
    // there the two answers part by up to 28 levels. That is past any tolerance which could still
    // reject a reconstruction that is genuinely wrong, so the self-check rejected the cell, the
    // correction stood down, and the cell kept the raw over-subtraction the correction exists to
    // remove. The visible result is the sun lamp's shadow falling across cells other lamps are
    // lighting brightly.
    //
    // Replaying the fold makes the reconstruction EXACT rather than close, which is what lets the
    // self-check go back to equality instead of carrying a tolerance sized against its own error.
    //
    // MIRRORS AddColors INCLUDING ITS GUARD: an addend of nothing leaves the accumulator alone
    // rather than passing it through a projection. Vanilla clamps the addend to non-negative first;
    // every addend here is a byte or a byte scaled by a coverage, so the clamp has nothing to do and
    // is expressed as the zero test rather than duplicated.
    public static void Accumulate(ref int r, ref int g, ref int b, int addR, int addG, int addB)
    {
        if (addR <= 0 && addG <= 0 && addB <= 0)
            return;

        int sumR = r + addR;
        int sumG = g + addG;
        int sumB = b + addB;
        int peak = Peak(sumR, sumG, sumB);

        r = ProjectChannel(sumR, peak);
        g = ProjectChannel(sumG, peak);
        b = ProjectChannel(sumB, peak);
    }

    // The channel vanilla's projection normalises against — its own `num`.
    public static int Peak(int r, int g, int b)
    {
        int peak = r;

        if (g > peak)
            peak = g;

        if (b > peak)
            peak = b;

        return peak;
    }

    // Whether a raw sum is in the region where the mask's arithmetic and vanilla's disagree.
    //
    // The test is on the SUM OVER EVERY EMITTER, ours and everybody else's, because that is what
    // vanilla projected. A cell lit to 200 by one of our lamps and to 200 by a mod's light saturates
    // just as hard as one lit by two of ours, and over-subtracts just as hard.
    public static bool Saturates(int r, int g, int b) => Peak(r, g, b) > Ceiling;

    // One channel of ColorInt.ProjectToColor32Fast, with the peak passed in so the three channels
    // share one divisor. Passing the peak rather than recomputing it per channel is not a
    // micro-optimisation: computing it per channel would clamp each channel independently, which is
    // a DIFFERENT operator — it tints the result against vanilla's and is the mistake
    // VectorLightLiftMath.Project's header already records for the per-emitter case.
    public static int ProjectChannel(int channel, int peak)
    {
        if (peak <= Ceiling)
            return channel < 0 ? 0 : channel;

        if (channel <= 0)
            return 0;

        return channel * Ceiling / peak;
    }

    // Whether our replay of vanilla's fold reproduces the value vanilla actually displayed — i.e.
    // whether this cell is one we understand well enough to edit.
    //
    // EXACT, AND THE EXACTNESS IS THE POINT. Accumulate above is a transcription of the arithmetic
    // that produced `delivered`, run over the same emitters in the same order, so agreement is the
    // expected outcome rather than a lucky one. A disagreement means the cell holds light that did
    // not come from vanilla's glow grid the way we think it did — a mod writing the accumulated
    // array directly, or a version change under the reflection GlowGridPerLight leans on — and
    // there the honest answer is to leave the cell with today's arithmetic and count it.
    //
    // THIS USED TO BE A TOLERANCE OF EIGHT LEVELS, sized against the gap between a single
    // projection and vanilla's fold, and the tolerance is exactly what the sun lamp broke: a white
    // emitter among warm ones parts the two by up to 28 levels, so the check rejected the cell and
    // the correction stood down on the frames it was written for. Reconstructing the fold removes
    // that noise rather than tolerating it, so there is no longer anything for a slack to absorb.
    public static bool Reconstructs(
        int projectedR, int projectedG, int projectedB,
        int deliveredR, int deliveredG, int deliveredB)
    {
        return projectedR == deliveredR
            && projectedG == deliveredG
            && projectedB == deliveredB;
    }

    // What VectorLightMask should subtract from the mesh vertex, given what vanilla displayed at this
    // cell and what we say it should display. Non-negative by construction; the other direction is
    // LiftFrom below, and exactly one of the two is non-zero per channel.
    //
    // KEPT AS TWO NON-NEGATIVE HALVES rather than one signed delta because that is the shape
    // VectorLightMask already averages over the four cells meeting at a vertex, and because
    // ColorInt's own operators are vanilla's — asking them to carry a negative channel through an
    // integer divide is a guess about a struct we do not own. See VectorLightMask's cellLift header.
    public static int ShadowFrom(int delivered, int corrected)
    {
        int shadow = delivered - corrected;

        return shadow < 0 ? 0 : shadow;
    }

    public static int LiftFrom(int delivered, int corrected)
    {
        int lift = corrected - delivered;

        return lift < 0 ? 0 : lift;
    }

    // The level a player reads off a cell: vanilla's own summary of a glow colour, which is the max
    // channel and nothing else (GlowGrid.GroundGlowAt scales `Mathf.Max(Mathf.Max(r, g), b)` and
    // never looks at the other two).
    //
    // WHY THE TESTS MEASURE THIS AND NOT A CHANNEL. The monotonicity property is false per channel
    // for VANILLA, so a per-channel test would fail the oracle: proj normalises against the peak, so
    // adding a bright green lamp to a red-lit cell genuinely lowers the red channel. It is the level
    // that has to be monotone, and the level is the max.
    public static int Level(int r, int g, int b) => Peak(r, g, b);
}
