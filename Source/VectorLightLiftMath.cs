using System;

namespace CelestialLighting;

// §27 phase 5: the LIFT — how much light the mask puts back, decided by max(vanilla, ours) rather
// than by a strength slider.
//
// WHERE THIS SITS. Phase 3's mask (VectorLightMask) subtracts each emitter's own light back out of
// the cells our polygon says it cannot reach, which carves a shadow and can do nothing else: it is
// subtractive, so a lit cell lands at vanilla's value and never above it. Phase 3's other half, the
// beam, therefore keeps the additive MoteGlow pass running over the top at a reduced strength, and
// that strength is a TASTE KNOB — VectorLightSettings.BeamStrength exists because a flat lift over
// the whole lit region brightens the cells vanilla already lit correctly along with the ones it did
// not, and had to be cut back until the room stopped looking wrong.
//
// THIS REPLACES THE FLAT LIFT WITH A SELF-LIMITING ONE. Per emitter, per cell:
//
//     lift(e, c) = max(0, ours(e, c) - vanilla(e, c)) * lit(e, c)
//
// which composed with the mask's own subtraction gives, for each emitter we modelled,
//
//     lit(e, c) * max( vanilla(e, c), ours(e, c) )
//
// in place of vanilla's own contribution. The max sets the level and the coverage carves the
// darkness — issue #151's composition, with the operator it was missing.
//
// WHY THIS IS NOT #151's NO-OP. #151 measured max(vanilla, ours) as near-degenerate and closed on
// that basis, and its reasoning is right as far as it goes: our falloff IS vanilla's falloff, so
// wherever our polygon can see a cell by a clear straight line, that line IS the geodesic the flood
// walked, and the max has nothing to take. What #151 then concluded — that a max can never matter —
// does not follow, because it holds only where the two models SEE THE SAME GEOMETRY. Three places
// they do not:
//
//  1. AN OPEN DOOR. RimWorld's glow grid never learns a door opened (Building.SpawnSetup writes
//     def.blockLight into lightBlockers once and Building_Door.DoorOpen touches the grid not at
//     all), so beyond an open door vanilla delivers whatever bent the long way round, or nothing.
//     §27e's polygon looks straight through. There, vanilla is ~0 and ours is a full beam, so the
//     max is the whole beam. The subtractive mask cannot express this AT ALL: there is no vanilla
//     light in those cells to keep.
//  2. THE OCTILE RESIDUE. The flood accumulates 100 per cardinal step and 141 per diagonal, so its
//     distance to a cell off the eight principal directions runs up to ~8% long — worst around
//     22.5 degrees, exactly the angles a straight line is made of.
//  3. THE RADIUS CUTOFF. Vanilla stops at (octile + 1) > glowRadius; a straight line reaches to
//     glowRadius. The last cell or so of every light's rim is ours alone. It is also the dimmest
//     part of the light, so this one is real and small.
//
// THE SEED IS THE WHOLE CALIBRATION, and getting it wrong is the difference between a geometry
// correction and a brightness rescale. ComputeGlowGridsJob.PrepareFill seeds the light's own cell at
// intDist = 100 — one cell, not zero — so vanilla's curve is evaluated at octile + 1 EVERYWHERE.
// Evaluating our straight line at the raw Euclidean distance instead makes ours brighter than
// vanilla in every cell of every lamp, and the max then reads as somebody turning the lights up: at
// two cells from a radius-6 torch it is worth about a hundred levels of glow. §27's standing rule is
// that it changes WHERE light reaches and not how bright a lamp is, so the seed is matched and the
// max is left to win only on geometry. VectorLightLiftMath.MatchesVanillaSeed is the flag that
// makes that a measurement rather than an assertion.
public static class VectorLightLiftMath
{
    // ComputeGlowGridsJob's own step costs, in the hundredths of a cell it accumulates in: `(i < 4)
    // ? 100 : 141` per step, on top of PrepareFill's `value.intDist = 100` seed at the light's own
    // cell. INTEGERS FIRST and the float forms derived from them, because the two are used for
    // different jobs and drifting apart would be silent — the floats calibrate our curve against
    // vanilla's, and the integers below reproduce vanilla's accumulator exactly.
    public const int SeedStepCost = 100;

    public const int CardinalStepCost = 100;

    public const int DiagonalStepCost = 141;

    // What ComputeGlowGridsJob.PrepareFill seeds the light's own cell at, in cells: `value.intDist =
    // 100` against a CardinalCost of 100. Vanilla's falloff therefore never sees a distance below 1
    // and sees `octile + 1` at every other cell, which is the offset our straight line has to carry
    // to be the same quantity.
    public const float VanillaSeedDistance = SeedStepCost / 100f;

    // ComputeGlowGridsJob's own step costs, as cells rather than as the hundredths it accumulates in.
    public const float CardinalStep = CardinalStepCost / 100f;

    public const float DiagonalStep = DiagonalStepCost / 100f;

    // The distance to evaluate our falloff at, for a cell our polygon can see by a clear line.
    //
    // `matchSeed` false is the diagnostic arm, not a tuning option: it drops the seed and so compares
    // our curve at d against vanilla's at d + 1, which is not the same quantity and wins everywhere
    // by construction. It exists to be shot next to the matched one so the choice is evidence.
    public static float SightlineDistance(float dx, float dz, bool matchSeed)
    {
        float euclidean = (float)Math.Sqrt(dx * dx + dz * dz);

        return matchSeed ? euclidean + VanillaSeedDistance : euclidean;
    }

    // What vanilla's flood would accumulate along a clear straight run to the same cell: its octile
    // metric, plus the seed. Not used by the mask — it reads the real per-light arrays — but it is
    // the closed form the offline tests check the lift against, and writing it down here is what
    // makes "the two models agree on a clear sightline" a statement with a residue attached rather
    // than an assurance.
    public static float OctileFloodDistance(float dx, float dz)
    {
        float ax = Math.Abs(dx);
        float az = Math.Abs(dz);
        float longer = Math.Max(ax, az);
        float shorter = Math.Min(ax, az);

        return longer - shorter + shorter * DiagonalStep + VanillaSeedDistance;
    }

    // ---- WHICH RENDERER OWNS A CELL ----
    //
    // The composition above puts our excess on top of whatever vanilla delivered, and that is right
    // wherever vanilla delivered the light by the same route we did. Where it did not — an aperture,
    // a corner, an open door — the two halves split the delivery: vanilla's share arrives at CELL
    // resolution as a soft blob and ours arrives at POLYGON resolution as a wedge, and the sum of a
    // blob and a wedge is a blob. So the per-cell rule is a MAX DELIVERED BY ONE RENDERER rather
    // than a sum of two:
    //
    //   vanilla >= ours  -> leave vanilla alone, draw nothing. This is the near field, and it is what
    //                       keeps a torch looking like a torch. Losing it is what sank the aperture
    //                       beam, which replaced globally and took the lamp cell 19.89 -> 17.87 L*.
    //   vanilla <  ours  -> the mask removes vanilla's whole contribution at that cell and the fan
    //                       draws our whole model there, at the polygon's own resolution.
    //
    // WHY THE TEST IS ON DISTANCE AND NOT ON THE TWO BRIGHTNESSES, which is the trap this rule dies
    // in if taken literally. Our model evaluates the seeded curve at the straight-line distance and
    // vanilla evaluates the same curve at its OCTILE distance, and the octile metric overestimates
    // every direction off the eight principal ones by up to 8%. So on a perfectly clear sightline
    // `ours > vanilla` is true almost everywhere, by the octile residue alone — a literal
    // brightness comparison hands us the entire near field, replacement goes global, and the torch's
    // radiance is gone again. That residue is real light and the fan already delivers it additively;
    // it is not a reason to take the cell over.
    //
    // What the rule actually wants to ask is "did vanilla's flood have to BEND to get here", and
    // vanilla answers that itself. ComputeGlowGridsJob is a Dijkstra fill over the octile metric, so
    // the cost it accumulates to an unobstructed cell is EXACTLY the octile distance to it, and any
    // cell it had to detour to costs strictly more. It then writes that accumulated distance into
    // the alpha channel of its own per-light array (`colorInt.a = (int)num2`, preserved through
    // ProjectToColor32Fast as `(byte)a`), which is a number this mod is already holding in both
    // places the decision has to be made. Nothing is recomputed and nothing is estimated.
    //
    // TRUNCATION IS WHAT MAKES IT ROBUST rather than a rounding hazard. Alpha is a whole number of
    // cells, so the comparison is between two truncated integers and a float discrepancy of a few
    // ulps cannot flip it. The one family of distances where a float round could cross an integer
    // boundary is the exact multiples of 100 — which is `shorter == 0`, the cardinal runs, where both
    // sides are exact integers anyway. The cost is resolution: a detour of under a cell is invisible
    // to this test, and that is the right answer, since a cell vanilla reached by nearly the straight
    // line is a cell vanilla lit correctly.
    //
    // ON OPEN GROUND THE TWO SIDES ARE THE SAME INTEGER, not merely close, so the near field is
    // protected EXACTLY rather than by a tolerance that could be wrong. That is the property the
    // aperture beam did not have and could not be given, and it is why this can be a hard rule with
    // no strength knob attached to it.
    //
    // HOW MUCH THIS CLAIMS, MEASURED OFFLINE BEFORE IT WAS BUILT, because the answer is small enough
    // that finding out afterwards would have read as a broken feature. Modelled over the gate scene
    // (a radius-10 torch six cells inside a room with a one-cell gap in one wall and a door in the
    // other), of every cell vanilla lights, the rule claims TWO — the fringe cells either side of the
    // aperture's cone, each holding 7 levels of glow. Everything else is either a clear run for both
    // models or a cell vanilla never reached, where the composition already behaves this way.
    //
    // That is not a defect in the rule, it is what our polygon and vanilla's flood sharing a BLOCKER
    // SET implies. Where they see the same walls, the only thing they can disagree about is the
    // metric, and the seed match above already removes that. Real disagreement needs the two to see
    // different geometry, which in this game means an open door — where vanilla delivers nothing and
    // the composition has always degenerated to our whole model on its own. So the rule's honest
    // scope is the fringe of an aperture cone, and its value is that it states the composition's
    // intent as one rule instead of leaving a doorway and a gap on two different accidental paths.

    // What vanilla's accumulator would hold at a cell it reached by a clear straight run: its own
    // step costs, in its own hundredths, seed included. The integer twin of OctileFloodDistance.
    public static int ClearRunStepCost(int dx, int dz)
    {
        int ax = Math.Abs(dx);
        int az = Math.Abs(dz);
        int longer = Math.Max(ax, az);
        int shorter = Math.Min(ax, az);

        return (longer - shorter) * CardinalStepCost + shorter * DiagonalStepCost + SeedStepCost;
    }

    // The same run as ComputeGlowGridsJob would have written it into alpha — `(int)(intDist / 100f)`,
    // which for a non-negative accumulator is integer division by the cardinal step.
    public static int ClearRunDistance(int dx, int dz) =>
        ClearRunStepCost(dx, dz) / CardinalStepCost;

    // Whether vanilla's flood arrived at this cell by a longer route than the straight line our
    // polygon can see along — i.e. whether this is a cell our model should own outright.
    //
    // `delivered` false means vanilla never arrived at all, which is the far side of an open door
    // (the glow grid never learns a door opened) and the last cell of every light's rim (vanilla
    // stops at octile + 1 > radius where a straight line reaches to radius). Both are ours by the
    // same rule, and both are already what the composition does today, since there is no vanilla
    // light at such a cell either to keep or to subtract.
    public static bool VanillaBentToArrive(int dx, int dz, int deliveredDistance, bool delivered)
    {
        if (!delivered)
            return true;

        return deliveredDistance > ClearRunDistance(dx, dz);
    }

    // ---- THE OTHER REASON TO OWN A CELL: VANILLA'S SHARE IS NOT WORTH ANYTHING ON SCREEN ----
    //
    // The detour rule above asks a GEOMETRY question and, measured, answers "almost never" — our
    // polygon is cast against the same blockers vanilla floods around, so the two sets barely
    // overlap. But the composition is degenerate past an aperture for a reason that has nothing to
    // do with geometry, and this is it.
    //
    // `vanilla + max(0, ours - vanilla)` IS `max(vanilla, ours)`, exactly, at every cell. The
    // subtraction is not an alternative to the max — it is the max written as an increment, because
    // the frame already holds vanilla. So the operator was never the problem, and no rearrangement
    // of it can be: what varies is WHICH RENDERER carries the max once it is computed.
    //
    // Past a door vanilla is 0, so the whole max rides the fan. Past a gap vanilla is 0.0902
    // against our 0.0911, so 99% of the same max rides vanilla's lighting overlay and 0.9% rides the
    // fan. Those two paths are not interchangeable at low levels: measured on the gate scene, the
    // mod adds +2.34 L* past the door and +0.36 past the gap from cones of identical shape and
    // near-identical glow. A max that is arithmetically right can still be delivered almost entirely
    // through the path that shows least.
    //
    // So this rule picks the renderer on whether vanilla's delivered share is worth anything, rather
    // than on how the light got there. Below the floor, vanilla is contributing a level the overlay
    // renders as nearly nothing; the cell goes to our model whole and the fan draws it.
    //
    // WHAT IT CANNOT DISTINGUISH, and the thing to look for in the frames. "Dim because it came
    // through a hole" and "dim because it is the outer rim of the lamp" are the same number to this
    // rule, so it claims every light's rim as well as every aperture. On the rim our model and
    // vanilla agree, so the level does not change hands so much as change PATH — and if the two
    // paths do not deliver equally, that shows up as a ring at each lamp's edge. That is the
    // measurement this flag exists to take, and it is why the floor is a hard threshold on the first
    // cut rather than a ramp: a ramp wide enough to hide a ring is wide enough not to claim the gap
    // (0.0902 sits ABOVE most of the rim, so any floor that takes the gap takes the rim too).
    //
    // Modelled offline over the gate scene before it was built. At 0.10 it claims 10 unroofed cells
    // and NOTHING inside the lamp's room — the near field is at 0.6745, six times clear of it:
    //
    //     floor 0.05 -> 7 outdoor, 0 room      floor 0.15 -> 11 outdoor, 8 room
    //     floor 0.10 -> 10 outdoor, 0 room     floor 0.20 -> 11 outdoor, 22 room
    //
    // 0.15 is where it starts eating the room, so 0.10 is the value with a measured margin either
    // side rather than a round number that happened to work.

    // The level below which vanilla's delivered glow is treated as not worth keeping, as the byte
    // its per-light array stores: 0.10 of full glow, i.e. 26 of 255.
    public const int DimDeliveryFloor = 26;

    // `deliveredPeak` is the largest of vanilla's three channels at this cell, which is the channel
    // its own hue-preserving projection normalises the other two against — so it is the one number
    // that says how much light arrived, independent of the lamp's colour.
    //
    // Zero passes, and that is the same statement the detour rule makes about a cell vanilla never
    // reached: there is nothing there either to keep or to take.
    public static bool VanillaTooDimToKeep(int deliveredPeak) =>
        deliveredPeak < DimDeliveryFloor;

    // One emitter's colour at one cell, in the byte space vanilla writes into its per-light array.
    //
    // MIRRORS ComputeGlowGridsJob EXACTLY, because the whole point of comparing the two is that they
    // are the same quantity and any difference is geometry. That means the TRUNCATING multiply of
    // ColorInt.operator *(ColorInt, float) — `(int)(r * b)`, not a round — and it means
    // ProjectToColor32Fast's hue-preserving normalisation rather than a per-channel clamp: over 255
    // the three channels are scaled by 255/max together, so a lamp near its own cell goes white by
    // saturating rather than by whichever channel happened to clip first. Clamping per channel here
    // would tint our value against vanilla's and the max would then pick channels from two different
    // colours.
    public static void Project(
        int colourR, int colourG, int colourB, float falloff, out int r, out int g, out int b)
    {
        r = Scale(colourR, falloff);
        g = Scale(colourG, falloff);
        b = Scale(colourB, falloff);

        int peak = Math.Max(r, Math.Max(g, b));

        if (peak <= 255)
            return;

        r = r * 255 / peak;
        g = g * 255 / peak;
        b = b * 255 / peak;
    }

    private static int Scale(int channel, float falloff)
    {
        int scaled = (int)(channel * falloff);

        return scaled < 0 ? 0 : scaled;
    }

    // How much of one emitter's light to ADD BACK at one cell: the excess of our model over what
    // vanilla actually delivered there, gated by the share of the cell our polygon covers.
    //
    // GATED BY COVERAGE, WHICH IS WHAT MAKES IT A MASKED MAX RATHER THAN #151's. An ungated max
    // brightens the shadow it is standing in — our model's value at a cell says how bright the cell
    // WOULD be on a clear sightline, and the coverage is the statement that there is no such line.
    // Multiplying by it means a fully shadowed cell gets no lift at all and a boundary cell gets the
    // same ramp the subtraction uses, so the two halves share one edge rather than each drawing
    // their own a fraction of a cell apart.
    //
    // INTEGER, AND IN THE SAME EXPRESSION SHAPE AS THE SUBTRACTION IT RIDES WITH. These are bytes
    // scaled by a byte; a float round trip per channel would buy nothing but conversions, and having
    // the two terms round differently would put a level of noise on the boundary cells where they
    // meet.
    public static int LiftChannel(int ourChannel, int vanillaChannel, int coverage)
    {
        int excess = ourChannel - vanillaChannel;

        if (excess <= 0 || coverage <= 0)
            return 0;

        return excess * coverage / 255;
    }
}
