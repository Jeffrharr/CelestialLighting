using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace CelestialLighting.Tests;

// §27 phase 5b (epic #174 phase 5): more lamps around a wall column must not deepen its shadow.
//
// THE PROPERTY, AND IT IS A DIRECTION RATHER THAN A NUMBER:
//
//     adding an emitter never lowers a cell's level
//
// stated on the max channel, which is the quantity GlowGrid.GroundGlowAt itself reads. It is stated
// as a property rather than as a worked example for the same reason §27e's quantisation bound was
// stated as "at most steps + 1 distinct values": the bug is a sign, and a sign is what a test can
// hold across every cell of a scene rather than at the one cell somebody happened to look at.
//
// VANILLA IS THE ORACLE. Its flood plus its projection satisfy the property unconditionally — the
// raw sum can only grow — so the test does not have to argue for it, only measure it. And "vanilla"
// here means VanillaGlowFlood, ComputeGlowGridsJob transcribed from the decompiled source and
// sharing no code with anything under test; a differential test whose two halves both come from our
// own arithmetic proves x - x == 0. See that file's header.
//
// THE RED ARM IS PART OF THE TEST. `OldCompositionDeepensTheShadowAsLampsAreAdded` asserts that the
// SHIPPED-BEFORE-THIS composition fails the property on this scene. Without it a monotonicity test
// over a scene that never saturates passes trivially and pins nothing, which is exactly how a
// direction-shaped bug survives a green suite.
[TestFixture]
public class VectorLightSaturationMathTests
{
    // ---- the pure core, on its own ---------------------------------------------------------

    [TestCase(10, 20, 30, 30)]
    [TestCase(300, 20, 30, 300)]
    [TestCase(0, 0, 0, 0)]
    public void PeakIsVanillasNormalisingChannel(int r, int g, int b, int expected)
    {
        Assert.That(VectorLightSaturationMath.Peak(r, g, b), Is.EqualTo(expected));
    }

    // 255 exactly is NOT saturation — vanilla's `if (num > 255)` is strict, and a cell sitting on the
    // ceiling has had nothing scaled away from it, so there is nothing for the correction to undo.
    [TestCase(255, 255, 255, false)]
    [TestCase(256, 0, 0, true)]
    [TestCase(0, 0, 256, true)]
    [TestCase(120, 200, 90, false)]
    public void SaturatesIsStrictlyOverTheCeiling(int r, int g, int b, bool expected)
    {
        Assert.That(VectorLightSaturationMath.Saturates(r, g, b), Is.EqualTo(expected));
    }

    // ColorInt.ProjectToColor32Fast, channel by channel: over the ceiling the whole colour is scaled
    // by 255/peak so the hue survives, rather than each channel being clipped where it happens to
    // cross. A per-channel clamp would tint the result, which is the mistake
    // VectorLightLiftMath.Project's header already records for the per-emitter case.
    [Test]
    public void ProjectPreservesHueRatherThanClipping()
    {
        int peak = VectorLightSaturationMath.Peak(400, 200, 100);

        Assert.That(VectorLightSaturationMath.ProjectChannel(400, peak), Is.EqualTo(255));
        Assert.That(VectorLightSaturationMath.ProjectChannel(200, peak), Is.EqualTo(127));
        Assert.That(VectorLightSaturationMath.ProjectChannel(100, peak), Is.EqualTo(63));
    }

    [Test]
    public void ProjectIsTheIdentityUnderTheCeiling()
    {
        int peak = VectorLightSaturationMath.Peak(200, 130, 40);

        Assert.That(VectorLightSaturationMath.ProjectChannel(200, peak), Is.EqualTo(200));
        Assert.That(VectorLightSaturationMath.ProjectChannel(130, peak), Is.EqualTo(130));
        Assert.That(VectorLightSaturationMath.ProjectChannel(40, peak), Is.EqualTo(40));
    }

    // The whole gate CorrectSaturation leans on: below the ceiling the correction is provably the
    // identity, so every unsaturated shadow in the mod keeps the byte it had before the correction
    // existed. Asserted over a sweep rather than one triple, because "provably" is only worth
    // writing down if something checks it.
    [Test]
    public void UnderTheCeilingTheCorrectionReproducesTheRawSubtraction()
    {
        for (int raw = 0; raw <= 255; raw += 5)
        {
            for (int shadow = 0; shadow <= raw; shadow += 7)
            {
                int deliveredR = 0;
                int deliveredG = 0;
                int deliveredB = 0;
                VectorLightSaturationMath.Accumulate(
                    ref deliveredR, ref deliveredG, ref deliveredB, raw, 0, 0);

                int oursR = 0;
                int oursG = 0;
                int oursB = 0;
                VectorLightSaturationMath.Accumulate(
                    ref oursR, ref oursG, ref oursB, raw - shadow, 0, 0);

                int newShadow = VectorLightSaturationMath.ShadowFrom(deliveredR, oursR);
                int newLift = VectorLightSaturationMath.LiftFrom(deliveredR, oursR);

                Assert.That(newShadow, Is.EqualTo(shadow), $"raw {raw} shadow {shadow}");
                Assert.That(newLift, Is.EqualTo(0), $"raw {raw} shadow {shadow}");
            }
        }
    }

    // ShadowFrom and LiftFrom are the two halves of one signed difference, split so the mask can keep
    // averaging non-negative ColorInts. Exactly one of them is ever non-zero.
    [TestCase(200, 120, 80, 0)]
    [TestCase(120, 200, 0, 80)]
    [TestCase(90, 90, 0, 0)]
    public void ShadowAndLiftAreTheTwoHalvesOfOneDifference(
        int delivered, int ours, int expectedShadow, int expectedLift)
    {
        Assert.That(VectorLightSaturationMath.ShadowFrom(delivered, ours), Is.EqualTo(expectedShadow));
        Assert.That(VectorLightSaturationMath.LiftFrom(delivered, ours), Is.EqualTo(expectedLift));
    }

    // A saturated cell can lose a shadow and GAIN a channel at the same time, with no max involved,
    // because the projection normalises the three channels together: take the red emitter out of a
    // red-and-yellow cell and the green that was being scaled down by it comes back up. This is why
    // VectorLightMask keeps its lift arrays live under the correction even with phase 5's max off.
    [Test]
    public void RemovingTheDominantChannelLiftsTheOthers()
    {
        // A warm emitter at (200,200,0) and a red one at (200,0,0). Vanilla folds them to (400,200,0)
        // -> (255,127,0): the red has scaled the green down by nearly half. Block the red and the
        // remaining green displays at its own full 200.
        int deliveredR = 0;
        int deliveredG = 0;
        int deliveredB = 0;
        VectorLightSaturationMath.Accumulate(
            ref deliveredR, ref deliveredG, ref deliveredB, 200, 200, 0);
        VectorLightSaturationMath.Accumulate(
            ref deliveredR, ref deliveredG, ref deliveredB, 200, 0, 0);

        Assert.That((deliveredR, deliveredG, deliveredB), Is.EqualTo((255, 127, 0)));

        int oursR = 0;
        int oursG = 0;
        int oursB = 0;
        VectorLightSaturationMath.Accumulate(ref oursR, ref oursG, ref oursB, 200, 200, 0);

        Assert.That(VectorLightSaturationMath.ShadowFrom(deliveredR, oursR), Is.EqualTo(55));
        Assert.That(VectorLightSaturationMath.LiftFrom(deliveredG, oursG), Is.EqualTo(73));
    }

    // ---- the premise CorrectSaturation's self-check rests on -------------------------------

    // CombineColorsJob.AddColors projects after EVERY addition rather than once at the end, so
    // reconstructing vanilla's displayed value as a single projection of the true sum is only valid
    // where the two agree. For same-hue emitters they always do — saturating a colour and then adding
    // more of the same colour lands back on the same capped ray — which is the ring of identical
    // lamps this whole phase is about, and every ordinary colony room.
    [Test]
    public void SameHueEmittersFoldToASingleProjection()
    {
        int[][] lamps =
        {
            new[] { 214, 148, 94 },
            new[] { 107, 74, 47 },
            new[] { 214, 148, 94 },
            new[] { 160, 111, 70 },
        };

        for (int take = 1; take <= lamps.Length; take++)
        {
            (int r, int g, int b) folded = Fold(lamps, take);
            (int r, int g, int b) once = ProjectSum(lamps, take);

            Assert.That(folded, Is.EqualTo(once), $"{take} same-hue lamps");
        }
    }

    // And the counterexample, stated rather than left implicit: two saturating red lamps followed by
    // a green one land somewhere a single projection does not, because the first saturation threw
    // away the red that would have set the divisor. CorrectSaturation detects exactly this by
    // comparing its reconstruction against VisualGlowAt and declines the cell, counting it in
    // SaturationSkipped rather than "correcting" against a value vanilla never displayed.
    [Test]
    public void MixedHueEmittersDoNotFoldToASingleProjection()
    {
        int[][] lamps =
        {
            new[] { 255, 0, 0 },
            new[] { 255, 0, 0 },
            new[] { 0, 255, 0 },
        };

        Assert.That(Fold(lamps, 3), Is.EqualTo((255, 255, 0)));
        Assert.That(ProjectSum(lamps, 3), Is.EqualTo((255, 127, 0)));

        (int r, int g, int b) folded = Fold(lamps, 3);
        (int r, int g, int b) once = ProjectSum(lamps, 3);

        Assert.That(
            VectorLightSaturationMath.Reconstructs(once.r, once.g, once.b, folded.r, folded.g, folded.b),
            Is.False,
            "the mixed-hue case is the one the slack has to reject");
    }

    // WHAT THE SINGLE PROJECTION COST, MEASURED, because the tolerance it needed is the whole reason
    // it was replaced. Same-hue emitters land on the same capped ray whichever order they are added
    // in, but the fold's per-step integer divide still leaves a level or two between it and a single
    // projection: swept over 200,000 random same-hue sets of up to fourteen emitters the worst gap is
    // 5. That is the noise a tolerance had to sit above — and a white sun lamp among warm lamps
    // parts the two by 43, which is what no tolerance can sit above while still rejecting a
    // reconstruction that is genuinely wrong. Replaying the fold leaves nothing to tolerate.
    //
    // Deterministically seeded, because a randomised test that fails once a fortnight is a test
    // nobody trusts — and the bound is what is being pinned, not the particular draw.
    [Test]
    public void SameHueFoldNoiseIsSmallAndMixedHueNoiseIsNot()
    {
        Assert.That(WorstFoldGap(withSunLamp: false), Is.EqualTo(5), "same-hue noise");

        int mixed = WorstFoldGap(withSunLamp: true);

        Assert.That(mixed, Is.EqualTo(28), "a white emitter among warm ones");
        Assert.That(mixed, Is.GreaterThan(SingleProjectionSlack),
            "if this ever drops under the old slack, the single projection was adequate after all "
            + "and the fold below is answering a question nobody asked");
    }

    // The worst disagreement between vanilla's fold and one projection of the same emitters' sum,
    // over random rooms — with and without a white emitter in them.
    //
    // Deterministically seeded, because a randomised test that fails once a fortnight is a test
    // nobody trusts, and the bound is what is being pinned rather than the particular draw.
    private static int WorstFoldGap(bool withSunLamp)
    {
        Random random = new Random(7);
        int worst = 0;

        for (int trial = 0; trial < 200000; trial++)
        {
            int count = 2 + random.Next(13);
            int[][] lamps = new int[count][];

            for (int i = 0; i < count; i++)
            {
                double falloff = 0.05 + random.NextDouble() * 0.95;

                // A sun lamp's glowColor is (370,370,370) and its own flood projects that to white
                // before the room ever sees it; a torch is (184,136,83) all the way down.
                lamps[i] = withSunLamp && i == 0
                    ? White(falloff)
                    : new[] { (int)(184 * falloff), (int)(136 * falloff), (int)(83 * falloff) };
            }

            (int r, int g, int b) folded = Fold(lamps, count);
            (int r, int g, int b) once = ProjectSum(lamps, count);

            worst = Math.Max(worst, Math.Abs(once.r - folded.r));
            worst = Math.Max(worst, Math.Abs(once.g - folded.g));
            worst = Math.Max(worst, Math.Abs(once.b - folded.b));
        }

        return worst;
    }

    private static int[] White(double falloff)
    {
        int channel = Math.Min(255, (int)(370 * falloff));

        return new[] { channel, channel, channel };
    }

    // ---- the wall column -------------------------------------------------------------------

    // The scene the epic describes, built once and swept: a one-cell wall column with torches ringed
    // around it, in nested sets so each arm genuinely ADDS a torch to the previous one rather than
    // rearranging them. A sweep over non-nested rings would be measuring a different scene per arm,
    // and "adding an emitter" would not be what was tested.
    //
    // THE SAME FIXTURE THE LIVE SCENARIO BUILDS, cell for cell — Tests/Scenarios/vector_light_column
    // .json, generated by Tools/ScenarioGen/gen_wall_column.py. Two instruments on one scene rather
    // than two scenes, so the tables below are a prediction of the live run and not a rhyme with it.
    private const int GridSpan = 41;
    private const int ColumnX = 20;
    private const int ColumnZ = 20;
    private const float LampRadius = 10f;

    // TorchLamp, from Buildings_Furniture.xml: glowRadius 10, glowColor (184,136,83,0). The torch
    // rather than the standing lamp because it is the emitter the live scenario can actually build —
    // a standing lamp needs a power grid, and vector_light_column.json places these on bare concrete.
    private const int LampR = 184;
    private const int LampG = 136;
    private const int LampB = 83;

    // A hexagon two cells out, ordered so the first, the first two, the first four and all six are
    // each spread around the column.
    //
    // TWO CELLS OUT AND NOT FOUR, which is the difference between a scenario that reproduces the bug
    // and one that photographs a healthy shadow. Torches four cells out do saturate the cells between
    // them — the first draft measured that — but the lighting overlay carries one vertex per cell
    // corner and one per centre, so what a probe or a pixel actually reads is a 3x3 tent filter over
    // the cells. A one-cell column's shadow is one cell wide, and at four cells out the surviving
    // over-subtraction is small enough that the blur takes the whole of it: the live sweep came back
    // monotone at every probed cell while the per-cell arithmetic underneath was plainly not. Pulling
    // the ring in to two cells deepens the saturation until the defect survives its own render, which
    // is the only version of it that matters.
    private static readonly (int x, int z)[] Ring =
    {
        (ColumnX + 2, ColumnZ), (ColumnX - 2, ColumnZ),
        (ColumnX + 1, ColumnZ + 2), (ColumnX - 1, ColumnZ - 2),
        (ColumnX - 1, ColumnZ + 2), (ColumnX + 1, ColumnZ - 2),
    };

    private static readonly int[] LampCounts = { 1, 2, 4, 6 };

    // The sun lamp sits four cells WEST of the column, so the column blocks it from exactly the
    // cells the eastmost torch lights — the arrangement the report describes: a sun lamp's shadow
    // landing on a lit lamp. Not part of the ring, and not moved by the sweep: every arm of the
    // sun-lamp sweep has the same sun lamp and differs only in how many torches surround it.
    private static readonly (int x, int z) SunLampCell = (ColumnX - 4, ColumnZ);

    [Test]
    public void VanillaIsMonotoneInLampCount()
    {
        AssertMonotone(Levels(Composition.Vanilla), "vanilla");
    }

    [Test]
    public void CorrectedCompositionIsMonotoneInLampCount()
    {
        AssertMonotone(Levels(Composition.Corrected), "corrected");
    }

    // THE RED ARM. The composition §27 shipped before this phase fails the property on this scene, and
    // the assertion is that it fails — a monotonicity test that passes for both arms is measuring a
    // scene with no saturation in it and pinning nothing at all.
    [Test]
    public void OldCompositionDeepensTheShadowAsLampsAreAdded()
    {
        int[][] levels = Levels(Composition.Old);
        bool anyDrop = false;

        for (int arm = 1; arm < LampCounts.Length; arm++)
        {
            for (int i = 0; i < levels[arm].Length; i++)
                anyDrop |= levels[arm][i] < levels[arm - 1][i];
        }

        Assert.That(anyDrop, Is.True,
            "the pre-phase-5b composition is supposed to be non-monotone on this scene; if it is not, "
            + "the scene has stopped saturating and the monotone arms above are proving nothing");
    }

    // The correction never brightens a cell past what vanilla delivered there. §27 carves shadow; it
    // does not turn lamps up, and a fix for an over-subtraction that quietly became an under-one
    // would pass the monotonicity test above while breaking the subsystem's standing rule.
    [Test]
    public void CorrectedCompositionNeverExceedsVanilla()
    {
        int[][] vanilla = Levels(Composition.Vanilla);
        int[][] corrected = Levels(Composition.Corrected);

        for (int arm = 0; arm < LampCounts.Length; arm++)
        {
            for (int i = 0; i < corrected[arm].Length; i++)
            {
                Assert.That(corrected[arm][i], Is.LessThanOrEqualTo(vanilla[arm][i]),
                    $"{LampCounts[arm]} lamps, cell index {i}");
            }
        }
    }

    // THE FIX IS CONFINED, and this is the assertion that says so rather than the comment. One torch
    // cannot saturate anything — its own light is a Color32 and tops out at 255 — so the correction
    // is a provable no-op there, and §27's one-lamp shadow has to come out byte-identical to the one
    // it already shipped. Without this, the two monotone arms above are also satisfied by a fix that
    // simply turned the shadows down everywhere.
    [Test]
    public void OneTorchCannotSaturateSoTheShadowIsUnchanged()
    {
        int[] old = Levels(Composition.Old)[0];
        int[] corrected = Levels(Composition.Corrected)[0];
        int[] vanilla = Levels(Composition.Vanilla)[0];
        int behind = ColumnZ * GridSpan + (ColumnX - 1);

        Assert.That(corrected, Is.EqualTo(old));
        Assert.That(vanilla[behind] - corrected[behind], Is.GreaterThan(20),
            "there is still a shadow behind the column for the correction to have left alone");
    }

    // THE SWEEP, AS A TABLE, because the bug's whole shape is in how these two rows run and a
    // pass/fail on a direction does not show it. Deepest shadow anywhere in the scene, in levels of
    // glow out of 255, against the torch count:
    //
    //     torches    1     2     4     6
    //     old       46    46    93   143     deepens — the reported complaint
    //     corrected 46    46    32     9     shallows, which is what more lamps physically do
    //
    // THE FIRST TWO COLUMNS BEING IDENTICAL IS PART OF THE RESULT, not a weak showing. Below
    // vanilla's ceiling the correction is provably the identity, so §27's shadows in every scene that
    // does not saturate are exactly the shadows it already shipped; one torch cannot saturate a cell
    // at all and two barely do. A fix that moved those columns would be a retune of the whole
    // subsystem wearing a bug report's clothes.
    //
    // Measured, not derived — these are bytes at the end of vanilla's Dijkstra, our coverage bake,
    // two integer projections and the overlay's own vertex averaging, so a change to the falloff or
    // the ray count has to re-run this and write the new numbers back rather than reason about them.
    [Test]
    public void DeepestShadowSweepIsMeasuredRatherThanDerived()
    {
        Assert.That(DeepestShadows(Composition.Old), Is.EqualTo(new[] { 46, 46, 93, 143 }));
        Assert.That(DeepestShadows(Composition.Corrected), Is.EqualTo(new[] { 46, 46, 32, 9 }));
    }

    // The single cell the live scenario photographs, called out because it is where the direction is
    // legible in three rows rather than in a max over a scene. One cell west of the column, which the
    // torch two cells east of it can never see:
    //
    //     torches         1      2      4      6
    //     vanilla        62    168    255    255     the oracle: monotone
    //     old            19    123    171    133     falls 38 as two more torches come on
    //     corrected      19    123    228    255
    [Test]
    public void TheCellBehindTheColumnGoesDarkerUnderTheOldCompositionAsTorchesAreAdded()
    {
        int behind = ColumnZ * GridSpan + (ColumnX - 1);

        Assert.That(Column(Composition.Vanilla, behind), Is.EqualTo(new[] { 62, 168, 255, 255 }));
        Assert.That(Column(Composition.Old, behind), Is.EqualTo(new[] { 19, 123, 171, 133 }));
        Assert.That(Column(Composition.Corrected, behind), Is.EqualTo(new[] { 19, 123, 228, 255 }));
    }

    // ---- the same column, with a sun lamp on the other side of it -------------------------

    // THE SCENE THE BUG WAS REPORTED FROM: a sun lamp with a lot of ordinary lights next to it, and
    // the sun lamp's shadows falling across cells those lights are lighting. Same column, same ring
    // of torches, plus one sun lamp four cells the other side of the column — so the cells the ring
    // lights brightest are exactly the cells the column hides from the sun lamp.
    //
    // WHY THIS SCENE AND NOT THE TORCH RING ABOVE. Every fixture in this repo, offline and live, was
    // a ring of IDENTICAL torches, and a single hue is precisely the case where one projection of the
    // true sum agrees with vanilla's fold. A sun lamp is white — glowColor (370,370,370), over the
    // ceiling before its own flood has projected — so a grow room beside a workshop is the mixed-hue
    // case, the correction's self-check rejects the cell, and the composition falls back to the
    // over-subtraction it was written to remove. The torch ring cannot see any of that.
    //
    // ONE CELL EAST OF THE COLUMN, which the sun lamp can never see and the eastmost torch sits two
    // cells beyond:
    //
    //     torches              1      2      4      6
    //     vanilla            223    255    255    255     the oracle: monotone
    //     old                158    146    109     80     deepens with every torch added
    //     single projection  160    179    241    116     tracks the fix, then falls off a cliff
    //     corrected          160    179    241    255     lands on vanilla, because the torches
    //                                                     really do light this cell
    //
    // THE CLIFF IS THE BUG. At four torches the reconstruction still lands within the old slack and
    // the cell is corrected; at six it does not, the cell is declined, and the frame keeps the raw
    // subtraction — 116 against the 255 vanilla is showing. Nothing about the scene changed except
    // that another lamp came on.
    [Test]
    public void SunLampTableIsMeasuredRatherThanDerived()
    {
        int lit = ColumnZ * GridSpan + (ColumnX + 1);

        Assert.That(Column(Composition.Vanilla, lit, true), Is.EqualTo(new[] { 223, 255, 255, 255 }));
        Assert.That(Column(Composition.Old, lit, true), Is.EqualTo(new[] { 158, 146, 109, 80 }));
        Assert.That(
            Column(Composition.SingleProjection, lit, true),
            Is.EqualTo(new[] { 160, 179, 241, 116 }));
        Assert.That(
            Column(Composition.Corrected, lit, true), Is.EqualTo(new[] { 160, 179, 241, 255 }));
    }

    [Test]
    public void CorrectedCompositionIsMonotoneWithASunLampInTheRoom()
    {
        AssertMonotone(Levels(Composition.Corrected, true), "corrected, sun lamp");
    }

    // THE RED ARM, and it is the shipped composition rather than the pre-correction one — which is
    // the whole finding. The correction was measured monotone on a ring of identical torches and is
    // NOT monotone once a sun lamp is in the room: it holds until the reconstruction stops matching
    // and then hands the cell back to the arithmetic it was replacing.
    [Test]
    public void SingleProjectionCompositionIsNotMonotoneWithASunLampInTheRoom()
    {
        int[][] levels = Levels(Composition.SingleProjection, true);
        int worstDrop = 0;

        for (int arm = 1; arm < LampCounts.Length; arm++)
        {
            for (int i = 0; i < levels[arm].Length; i++)
                worstDrop = Math.Max(worstDrop, levels[arm - 1][i] - levels[arm][i]);
        }

        Assert.That(worstDrop, Is.EqualTo(155),
            "the shipped single-projection correction is supposed to fail this scene; if it stops "
            + "failing, the scene has stopped rejecting reconstructions and the arm below proves "
            + "nothing");
    }

    // The direction, as a table, on the whole scene rather than one cell — deepest shadow anywhere,
    // in levels of glow out of 255, against the torch count:
    //
    //     torches              1      2      4      6
    //     old                 73    119    146    175     deepens: the reported complaint
    //     single projection   69     93     60    163     shallows, then falls back
    //     corrected           69     93     60     38     shallows, which is what more lamps do
    [Test]
    public void SunLampDeepestShadowSweepIsMeasuredRatherThanDerived()
    {
        Assert.That(DeepestShadows(Composition.Old, true), Is.EqualTo(new[] { 73, 119, 146, 175 }));
        Assert.That(
            DeepestShadows(Composition.SingleProjection, true),
            Is.EqualTo(new[] { 69, 93, 60, 163 }));
        Assert.That(
            DeepestShadows(Composition.Corrected, true), Is.EqualTo(new[] { 69, 93, 60, 38 }));
    }

    // The correction never brightens a cell past what vanilla delivered there, with the sun lamp in
    // the room as without it. §27 carves shadow; it does not turn lamps up.
    [Test]
    public void CorrectedCompositionNeverExceedsVanillaWithASunLamp()
    {
        int[][] vanilla = Levels(Composition.Vanilla, true);
        int[][] corrected = Levels(Composition.Corrected, true);

        for (int arm = 0; arm < LampCounts.Length; arm++)
        {
            for (int i = 0; i < corrected[arm].Length; i++)
            {
                Assert.That(corrected[arm][i], Is.LessThanOrEqualTo(vanilla[arm][i]),
                    $"{LampCounts[arm]} torches and a sun lamp, cell index {i}");
            }
        }
    }

    private static int[] Column(Composition composition, int cell, bool withSunLamp = false)
    {
        int[][] levels = Levels(composition, withSunLamp);
        int[] column = new int[LampCounts.Length];

        for (int arm = 0; arm < LampCounts.Length; arm++)
            column[arm] = levels[arm][cell];

        return column;
    }

    private static int[] DeepestShadows(Composition composition, bool withSunLamp = false)
    {
        int[][] vanilla = Levels(Composition.Vanilla, withSunLamp);
        int[][] ours = Levels(composition, withSunLamp);
        int[] deepest = new int[LampCounts.Length];

        for (int arm = 0; arm < LampCounts.Length; arm++)
        {
            for (int i = 0; i < ours[arm].Length; i++)
                deepest[arm] = Math.Max(deepest[arm], vanilla[arm][i] - ours[arm][i]);
        }

        return deepest;
    }

    // ---- the model ------------------------------------------------------------------------

    private enum Composition
    {
        // Vanilla's own fold — CombineColorsJob.AddColors over every emitter reaching the cell, in
        // order, projecting after each. The glow grid as the game ships it, and the oracle.
        Vanilla,

        // The fold, less each emitter's own light scaled by the share of the cell its polygon cannot
        // see. §27 as it shipped before the saturation correction: the edit applied to the byte
        // vanilla had already scaled down.
        Old,

        // The correction as first written: the same edit applied to a reconstruction of the raw sum
        // and projected ONCE, with a self-check that falls back to Old wherever that reconstruction
        // misses what vanilla displayed. Kept as an arm because the fallback is the bug — on a
        // mixed-hue scene the check rejects the cell and the frame keeps the over-subtraction.
        SingleProjection,

        // The correction as it stands: vanilla's fold replayed with our geometry deciding how much
        // of each emitter arrived, reassembled through VectorLightSaturationMath exactly as
        // VectorLightMask.CorrectCell reassembles it — including the round trip out through
        // ShadowFrom/LiftFrom and back through the mask's own `delivered - shadow + lift`, so the
        // test exercises the split rather than the intermediate.
        Corrected,
    }

    // What the correction's first cut allowed between its reconstruction and vanilla's displayed
    // value before it declined the cell. Lives here rather than in the shipped file because the
    // shipped reconstruction is now exact and has nothing to tolerate; this is the historical
    // constant, kept so the arm above can be run as it actually behaved.
    private const int SingleProjectionSlack = 8;

    // The two emitters these sweeps are built from, straight out of Buildings_Furniture.xml.
    //
    // THE SUN LAMP IS WHITE AND EVERYTHING ELSE IS WARM, which is the whole point of having it here.
    // Its glowColor is (370,370,370) — over the ceiling before its own flood has even projected —
    // against a torch's (184,136,83). Every fixture in this repo was a ring of identical torches
    // until this one, and a single hue is exactly the case where a single projection of the true sum
    // agrees with vanilla's fold. A grow room next to a workshop is not that case.
    private readonly struct Emitter
    {
        public Emitter(int r, int g, int b, float radius)
        {
            R = r;
            G = g;
            B = b;
            Radius = radius;
        }

        public int R { get; }

        public int G { get; }

        public int B { get; }

        public float Radius { get; }
    }

    private static readonly Emitter Torch = new Emitter(LampR, LampG, LampB, LampRadius);

    private static readonly Emitter SunLamp = new Emitter(370, 370, 370, 14f);

    // Level per cell of the grid, per arm of the lamp-count sweep.
    private static int[][] Levels(Composition composition, bool withSunLamp = false)
    {
        int[][] levels = new int[LampCounts.Length][];

        for (int arm = 0; arm < LampCounts.Length; arm++)
            levels[arm] = LevelsFor(LampCounts[arm], composition, withSunLamp);

        return levels;
    }

    // ONE SUN LAMP FIRST, THEN THE TORCHES, and the order is the scene rather than a convenience:
    // vanilla folds its lights in the order they registered, a sun lamp is built before the torches
    // that fill in around it, and the fold is not commutative once a cell saturates. Put the white
    // emitter last instead and the numbers move.
    //
    // The sun lamp sits ON the column's other side rather than in the ring, so the column blocks it
    // from the same cells the ring lights — which is the arrangement the report describes: a shadow
    // thrown by the sun lamp landing on cells other lamps are lighting brightly.
    private static int[] LevelsFor(int lampCount, Composition composition, bool withSunLamp)
    {
        bool[] blocked = new bool[GridSpan * GridSpan];
        blocked[ColumnZ * GridSpan + ColumnX] = true;

        VectorLightMath.Segment[] segments =
            VectorLightMath.SilhouetteSegments(blocked, GridSpan, GridSpan, 0, 0);

        int cells = GridSpan * GridSpan;
        int[] rawR = new int[cells];
        int[] rawG = new int[cells];
        int[] rawB = new int[cells];
        int[] shadowR = new int[cells];
        int[] shadowG = new int[cells];
        int[] shadowB = new int[cells];
        int[] foldR = new int[cells];
        int[] foldG = new int[cells];
        int[] foldB = new int[cells];
        int[] oursR = new int[cells];
        int[] oursG = new int[cells];
        int[] oursB = new int[cells];

        if (withSunLamp)
        {
            AddLamp(
                SunLampCell, SunLamp, segments, rawR, rawG, rawB, shadowR, shadowG, shadowB,
                foldR, foldG, foldB, oursR, oursG, oursB);
        }

        for (int lamp = 0; lamp < lampCount; lamp++)
        {
            AddLamp(
                Ring[lamp], Torch, segments, rawR, rawG, rawB, shadowR, shadowG, shadowB,
                foldR, foldG, foldB, oursR, oursG, oursB);
        }

        int[] cellR = new int[cells];
        int[] cellG = new int[cells];
        int[] cellB = new int[cells];

        for (int i = 0; i < cells; i++)
        {
            Compose(
                composition, rawR[i], rawG[i], rawB[i], shadowR[i], shadowG[i], shadowB[i],
                foldR[i], foldG[i], foldB[i], oursR[i], oursG[i], oursB[i],
                out cellR[i], out cellG[i], out cellB[i]);
        }

        return CentreLevels(blocked, cellR, cellG, cellB);
    }

    // SectionLayer_LightingOverlay.GenerateLightingOverlay's own vertex averaging, transcribed:
    // each lattice corner is the mean of the up-to-four cells meeting there that are in bounds and do
    // not block light, and each cell's centre vertex is the mean of its own four corners. The level
    // is the max channel of that centre.
    //
    // WHY THE TEST GOES THROUGH THIS RATHER THAN READING CELLS, and it cost a live run to learn. The
    // per-cell arithmetic is where the bug lives, but nothing renders a cell — the overlay's mesh
    // carries one vertex per corner plus one per centre, so what a screenshot shows and what
    // RenderedLightCellProbe reads is a 3x3 tent filter over the cells. A defect that the filter
    // swallows is not a defect a player can see, and a fix for one is not a fix that can be
    // photographed. The first cut of this file measured cells, reported a healthy 36-level
    // non-monotonicity, and then had its live scenario come back monotone at every probed cell: the
    // arithmetic was right and the claim was not.
    //
    // So the offline sweep and the live probe now read THE SAME QUANTITY on THE SAME FIXTURE, and the
    // tables in this file are a prediction of vector_light_column.json rather than a separate result.
    private static int[] CentreLevels(bool[] blocked, int[] cellR, int[] cellG, int[] cellB)
    {
        int stride = GridSpan + 1;
        int[] cornerR = new int[stride * stride];
        int[] cornerG = new int[stride * stride];
        int[] cornerB = new int[stride * stride];

        for (int z = 0; z <= GridSpan; z++)
        {
            for (int x = 0; x <= GridSpan; x++)
            {
                int sumR = 0;
                int sumG = 0;
                int sumB = 0;
                int counted = 0;

                for (int corner = 0; corner < 4; corner++)
                {
                    int cx = x - (corner % 2 == 0 ? 1 : 0);
                    int cz = z - (corner < 2 ? 1 : 0);

                    if (!InBounds(cx, cz) || blocked[cz * GridSpan + cx])
                        continue;

                    sumR += cellR[cz * GridSpan + cx];
                    sumG += cellG[cz * GridSpan + cx];
                    sumB += cellB[cz * GridSpan + cx];
                    counted++;
                }

                int at = z * stride + x;
                cornerR[at] = counted > 0 ? sumR / counted : 0;
                cornerG[at] = counted > 0 ? sumG / counted : 0;
                cornerB[at] = counted > 0 ? sumB / counted : 0;
            }
        }

        int[] levels = new int[GridSpan * GridSpan];

        for (int z = 0; z < GridSpan; z++)
        {
            for (int x = 0; x < GridSpan; x++)
            {
                int botLeft = z * stride + x;

                levels[z * GridSpan + x] = VectorLightSaturationMath.Level(
                    (cornerR[botLeft] + cornerR[botLeft + 1]
                        + cornerR[botLeft + stride] + cornerR[botLeft + stride + 1]) / 4,
                    (cornerG[botLeft] + cornerG[botLeft + 1]
                        + cornerG[botLeft + stride] + cornerG[botLeft + stride + 1]) / 4,
                    (cornerB[botLeft] + cornerB[botLeft + 1]
                        + cornerB[botLeft + stride] + cornerB[botLeft + stride + 1]) / 4);
            }
        }

        return levels;
    }

    private static bool InBounds(int x, int z) =>
        x >= 0 && z >= 0 && x < GridSpan && z < GridSpan;

    // One emitter's contribution to every accumulator, run through the same two subsystems the game
    // runs it through: VanillaGlowFlood for what vanilla delivered, and VectorLightMath's polygon and
    // coverage bake for what §27 says it can see.
    //
    // FOUR ACCUMULATORS AND NOT TWO, because vanilla's own accumulation is a FOLD. `raw` and `shadow`
    // are the plain sums the correction's gate is asked about; `fold` is vanilla's answer, projected
    // after every emitter exactly as CombineColorsJob.AddColors projects; `ours` is that same fold
    // with this emitter's blocked share taken out before it is folded in. Lamps are added in a fixed
    // order here for the same reason VectorLightMask walks vanilla's `lights` list front to back: the
    // fold is lossy and therefore not commutative, so an order is part of the answer.
    private static void AddLamp(
        (int x, int z) lamp, Emitter emitter, VectorLightMath.Segment[] segments,
        int[] rawR, int[] rawG, int[] rawB, int[] shadowR, int[] shadowG, int[] shadowB,
        int[] foldR, int[] foldG, int[] foldB, int[] oursR, int[] oursG, int[] oursB)
    {
        VanillaGlowFlood.Result flood = VanillaGlowFlood.Flood(
            emitter.R, emitter.G, emitter.B, emitter.Radius,
            (dx, dz) => Blocked(lamp.x + dx, lamp.z + dz));

        VectorLightMath.LightPolygon polygon = VectorLightMath.Build(
            lamp.x + 0.5f, lamp.z + 0.5f, emitter.Radius, segments,
            VectorLightMath.DefaultBaseRayCount);

        int coverageRadius = (int)Math.Ceiling(emitter.Radius);
        byte[] coverage = VectorLightMath.BuildCoverage(
            polygon, lamp.x, lamp.z, coverageRadius, VectorLightMath.DefaultCoverageSamples);

        for (int dz = -flood.Radius; dz <= flood.Radius; dz++)
        {
            for (int dx = -flood.Radius; dx <= flood.Radius; dx++)
            {
                int x = lamp.x + dx;
                int z = lamp.z + dz;

                if (x < 0 || z < 0 || x >= GridSpan || z >= GridSpan)
                    continue;

                int local = flood.Index(dx, dz);
                int cell = z * GridSpan + x;

                rawR[cell] += flood.R[local];
                rawG[cell] += flood.G[local];
                rawB[cell] += flood.B[local];

                int lit = VectorLightMath.CoverageAt(coverage, lamp.x, lamp.z, coverageRadius, x, z);
                int shadowed = 255 - lit;

                // VectorLightMask.AccumulateEmitter's own integer form, byte scaled by byte.
                int blockedR = flood.R[local] * shadowed / 255;
                int blockedG = flood.G[local] * shadowed / 255;
                int blockedB = flood.B[local] * shadowed / 255;

                shadowR[cell] += blockedR;
                shadowG[cell] += blockedG;
                shadowB[cell] += blockedB;

                // Vanilla's guard: an emitter delivering nothing to a cell is not a step of the
                // fold at all, so it must not be run through a projection.
                if (flood.R[local] == 0 && flood.G[local] == 0 && flood.B[local] == 0)
                    continue;

                Fold(foldR, foldG, foldB, cell, flood.R[local], flood.G[local], flood.B[local]);
                Fold(
                    oursR, oursG, oursB, cell,
                    flood.R[local] - blockedR,
                    flood.G[local] - blockedG,
                    flood.B[local] - blockedB);
            }
        }
    }

    private static void Fold(int[] r, int[] g, int[] b, int cell, int addR, int addG, int addB)
    {
        int cellR = r[cell];
        int cellG = g[cell];
        int cellB = b[cell];

        VectorLightSaturationMath.Accumulate(ref cellR, ref cellG, ref cellB, addR, addG, addB);

        r[cell] = cellR;
        g[cell] = cellG;
        b[cell] = cellB;
    }

    private static bool Blocked(int x, int z) => x == ColumnX && z == ColumnZ;

    private static void Compose(
        Composition composition, int rawR, int rawG, int rawB,
        int shadowR, int shadowG, int shadowB, int foldR, int foldG, int foldB,
        int oursR, int oursG, int oursB, out int outR, out int outG, out int outB)
    {
        // What the player is looking at, in every arm: vanilla's own fold. The arms differ only in
        // what they take off it.
        int deliveredR = foldR;
        int deliveredG = foldG;
        int deliveredB = foldB;

        if (composition == Composition.Vanilla)
        {
            outR = deliveredR;
            outG = deliveredG;
            outB = deliveredB;
            return;
        }

        if (composition == Composition.Old)
        {
            // VectorLightMask.Compose, pre-correction: one clamp at the end, no lift.
            outR = Math.Max(0, deliveredR - shadowR);
            outG = Math.Max(0, deliveredG - shadowG);
            outB = Math.Max(0, deliveredB - shadowB);
            return;
        }

        if (composition == Composition.SingleProjection)
        {
            SingleProjectionCompose(
                rawR, rawG, rawB, shadowR, shadowG, shadowB, deliveredR, deliveredG, deliveredB,
                out outR, out outG, out outB);
            return;
        }

        // Out through the split and back through the mask's own composition, rather than reading
        // `ours` directly. The mask never handles `ours`; it handles two non-negative halves and a
        // subtract-then-add, and a bug in the split would be invisible to a test that skipped it.
        outR = deliveredR
            - VectorLightSaturationMath.ShadowFrom(deliveredR, oursR)
            + VectorLightSaturationMath.LiftFrom(deliveredR, oursR);
        outG = deliveredG
            - VectorLightSaturationMath.ShadowFrom(deliveredG, oursG)
            + VectorLightSaturationMath.LiftFrom(deliveredG, oursG);
        outB = deliveredB
            - VectorLightSaturationMath.ShadowFrom(deliveredB, oursB)
            + VectorLightSaturationMath.LiftFrom(deliveredB, oursB);
    }

    // The correction's first cut, transcribed with its self-check intact: reconstruct the cell as one
    // projection of the raw sum, compare that against what vanilla displayed, and decline the cell —
    // leaving the pre-correction subtraction in place — when the two disagree by more than the slack.
    private static void SingleProjectionCompose(
        int rawR, int rawG, int rawB, int shadowR, int shadowG, int shadowB,
        int deliveredR, int deliveredG, int deliveredB,
        out int outR, out int outG, out int outB)
    {
        int rawPeak = VectorLightSaturationMath.Peak(rawR, rawG, rawB);
        int onceR = VectorLightSaturationMath.ProjectChannel(rawR, rawPeak);
        int onceG = VectorLightSaturationMath.ProjectChannel(rawG, rawPeak);
        int onceB = VectorLightSaturationMath.ProjectChannel(rawB, rawPeak);

        bool reconstructs = Math.Abs(onceR - deliveredR) <= SingleProjectionSlack
            && Math.Abs(onceG - deliveredG) <= SingleProjectionSlack
            && Math.Abs(onceB - deliveredB) <= SingleProjectionSlack;

        if (!reconstructs)
        {
            outR = Math.Max(0, deliveredR - shadowR);
            outG = Math.Max(0, deliveredG - shadowG);
            outB = Math.Max(0, deliveredB - shadowB);
            return;
        }

        int correctedR = Math.Max(0, rawR - shadowR);
        int correctedG = Math.Max(0, rawG - shadowG);
        int correctedB = Math.Max(0, rawB - shadowB);
        int peak = VectorLightSaturationMath.Peak(correctedR, correctedG, correctedB);

        int oursR = VectorLightSaturationMath.ProjectChannel(correctedR, peak);
        int oursG = VectorLightSaturationMath.ProjectChannel(correctedG, peak);
        int oursB = VectorLightSaturationMath.ProjectChannel(correctedB, peak);

        outR = deliveredR
            - VectorLightSaturationMath.ShadowFrom(deliveredR, oursR)
            + VectorLightSaturationMath.LiftFrom(deliveredR, oursR);
        outG = deliveredG
            - VectorLightSaturationMath.ShadowFrom(deliveredG, oursG)
            + VectorLightSaturationMath.LiftFrom(deliveredG, oursG);
        outB = deliveredB
            - VectorLightSaturationMath.ShadowFrom(deliveredB, oursB)
            + VectorLightSaturationMath.LiftFrom(deliveredB, oursB);
    }

    private static void AssertMonotone(int[][] levels, string what)
    {
        for (int arm = 1; arm < LampCounts.Length; arm++)
        {
            for (int i = 0; i < levels[arm].Length; i++)
            {
                Assert.That(levels[arm][i], Is.GreaterThanOrEqualTo(levels[arm - 1][i]),
                    $"{what}: cell ({i % GridSpan}, {i / GridSpan}) fell from "
                    + $"{levels[arm - 1][i]} at {LampCounts[arm - 1]} lamps to {levels[arm][i]} at "
                    + $"{LampCounts[arm]}");
            }
        }
    }

    // ---- CombineColorsJob's fold, for the two premise tests --------------------------------

    private static (int r, int g, int b) Fold(IReadOnlyList<int[]> lamps, int take)
    {
        int r = 0;
        int g = 0;
        int b = 0;

        for (int i = 0; i < take; i++)
        {
            int sumR = r + lamps[i][0];
            int sumG = g + lamps[i][1];
            int sumB = b + lamps[i][2];
            int peak = VectorLightSaturationMath.Peak(sumR, sumG, sumB);

            r = VectorLightSaturationMath.ProjectChannel(sumR, peak);
            g = VectorLightSaturationMath.ProjectChannel(sumG, peak);
            b = VectorLightSaturationMath.ProjectChannel(sumB, peak);
        }

        return (r, g, b);
    }

    private static (int r, int g, int b) ProjectSum(IReadOnlyList<int[]> lamps, int take)
    {
        int r = 0;
        int g = 0;
        int b = 0;

        for (int i = 0; i < take; i++)
        {
            r += lamps[i][0];
            g += lamps[i][1];
            b += lamps[i][2];
        }

        int peak = VectorLightSaturationMath.Peak(r, g, b);

        return (
            VectorLightSaturationMath.ProjectChannel(r, peak),
            VectorLightSaturationMath.ProjectChannel(g, peak),
            VectorLightSaturationMath.ProjectChannel(b, peak));
    }
}
