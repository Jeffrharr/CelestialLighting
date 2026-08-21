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
// THE FIX IS ONE LINE OF ALGEBRA. Do the edit before the projection instead of after it:
//
//     P     = proj(R)                                   what vanilla displays
//     R'    = R - SUM own(e)*(1 - lit(e)) + SUM lift(e)  the raw sum with our geometry applied
//     ours  = proj(R')                                   what we should display
//
// and hand VectorLightMask.Compose the DIFFERENCE, `P - proj(R')`, in place of the raw subtraction.
// Nothing about the accumulation changes; only the space the result is measured in.
//
// THE PROPERTY, STATED, because a direction is what a test can hold and an example is not:
//
//     Adding an emitter to a cell never lowers that cell's level.
//
// read on the MAX CHANNEL, which is the quantity `GlowGrid.GroundGlowAt` itself reads and the only
// one that survives hue rotation — proj scales channels together, so a red channel genuinely can
// fall when a green lamp is added, for vanilla as much as for us. On the max channel:
//
//   * VANILLA IS THE ORACLE and satisfies it unconditionally. max(proj(R)) is min(max(R), 255), and
//     adding an emitter can only raise max(R).
//   * OURS SATISFIES IT under this file. Adding an emitter adds `own(e)*lit(e) + lift(e)` to R',
//     which is non-negative componentwise whatever the geometry says, so max(R') cannot fall.
//   * THE OLD COMPOSITION DOES NOT. `P - SUM own(e)*(1 - lit(e))` loses a full raw `own` per blocked
//     emitter against a P that has stopped growing. VectorLightSaturationMathTests pins that arm red
//     on purpose: a monotonicity test that only ever passes is a test of nothing.
//
// WHAT IS DELIBERATELY NOT CHANGED. Unsaturated cells. Where max(R) <= 255 we have P == R and
// proj(R') == R', so `P - proj(R')` is the raw subtraction back again, to the byte — the correction
// is confined to exactly the cells that saturate, and everywhere else the shipped shadow is
// untouched. That is worth having as a property rather than as a hope, so VectorLightMask tests
// max(R) itself and leaves the accumulators alone when it is under the ceiling.
public static class VectorLightSaturationMath
{
    // Where ColorInt.ProjectToColor32Fast starts scaling. Not a clamp: at 256 the whole colour is
    // rescaled by 255/256, not just the offending channel.
    public const int Ceiling = 255;

    // How far our reconstruction of vanilla's sum may sit from the value vanilla actually displayed
    // before the cell is left alone. See Reconstructs.
    //
    // EIGHT, AND THE NUMBER IS MEASURED RATHER THAN PICKED. `CombineColorsJob.AddColors` projects
    // after EVERY addition instead of once at the end, and each of those projections is an integer
    // divide, so a fold over N emitters and a single projection of their true sum disagree by a few
    // levels even when they are describing exactly the same light. Over 200,000 random same-hue
    // emitter sets of up to fourteen lights the worst disagreement was **5**; in the six-torch
    // fixture this phase is built on it is **0 or 1**. The case the check exists to reject — two
    // saturating red lamps followed by a green one, where the fold has thrown away the red that
    // would have set the divisor — misses by **128**. Eight sits two orders of magnitude clear of
    // the thing being rejected and comfortably above the noise.
    //
    // THIS WAS AN EXACT EQUALITY FIRST, and the live run is what corrected it: on a six-torch ring
    // the exact test rejected 50 of 85 candidate cells over a one-level rounding difference, so the
    // corrected arm fell back to the broken composition on the majority of the cells the scenario was
    // built to measure and came back non-monotone. An exactness that rejects the case it was written
    // for is not rigour.
    public const int ReconstructionSlack = 8;

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

    // Whether a reconstructed sum, projected, describes the value vanilla actually displayed — i.e.
    // whether the raw sum is a model of this cell that the correction can safely edit.
    //
    // Every channel has to agree; a single channel out by more than the slack means the emitter set
    // we summed is not the one vanilla folded, and the honest answer is to leave the cell with
    // today's arithmetic rather than rewrite it against a value the game never showed.
    public static bool Reconstructs(
        int projectedR, int projectedG, int projectedB,
        int deliveredR, int deliveredG, int deliveredB)
    {
        return Within(projectedR, deliveredR)
            && Within(projectedG, deliveredG)
            && Within(projectedB, deliveredB);
    }

    private static bool Within(int projected, int delivered)
    {
        int gap = projected - delivered;

        if (gap < 0)
            gap = -gap;

        return gap <= ReconstructionSlack;
    }

    // One channel of the raw sum with our geometry applied: vanilla's own accumulation, less the
    // light our polygons say never arrived, plus what our model says arrived and vanilla's flood
    // never delivered.
    //
    // FLOORED AT ZERO HERE, before the projection, and the order matters. A cell every emitter is
    // blocked from has R' = 0 on every channel and therefore a peak of 0, so the projection is
    // skipped entirely; letting a negative through would make `peak` meaningless and could rescale
    // the other two channels off a number that is not a level.
    public static int CorrectedRaw(int raw, int shadow, int lift)
    {
        int corrected = raw - shadow + lift;

        return corrected < 0 ? 0 : corrected;
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
