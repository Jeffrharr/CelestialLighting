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
    // What ComputeGlowGridsJob.PrepareFill seeds the light's own cell at, in cells: `value.intDist =
    // 100` against a CardinalCost of 100. Vanilla's falloff therefore never sees a distance below 1
    // and sees `octile + 1` at every other cell, which is the offset our straight line has to carry
    // to be the same quantity.
    public const float VanillaSeedDistance = 1f;

    // ComputeGlowGridsJob's own step costs, as cells rather than as the hundredths it accumulates in.
    public const float CardinalStep = 1f;

    public const float DiagonalStep = 1.41f;

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
