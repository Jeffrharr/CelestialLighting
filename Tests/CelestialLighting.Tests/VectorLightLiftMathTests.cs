using System;
using System.Collections.Generic;

namespace CelestialLighting.Tests;

// Offline unit tests for §27 phase 5's lift (Source/VectorLightLiftMath.cs).
//
// THESE TESTS CARRY AN ORACLE, and that is the point of the file rather than an implementation
// detail. The claim under test is a DIFFERENCE — "our straight line beats vanilla's flood here and
// not there" — and a difference test that computes both sides with the code under test asserts that
// x - x is zero however wrong x is. So the vanilla side is computed by GlowFlood below, which is a
// transcription of Verse.Glow.ComputeGlowGridsJob (its 100/141 step costs, its intDist = 100 seed,
// its refusal to cut a diagonal between two blockers and its radius cutoff) written from the
// decompiled job and not from the mask. When the two agree it is because two independent routes
// arrived at the same number.
[TestFixture]
public class VectorLightLiftMathTests
{
    private const float Tolerance = 1e-4f;

    // A white-ish lamp colour in the units CompGlower hands GlowLight. Torches are warmer than this;
    // a neutral colour keeps the channel arithmetic legible and the hue questions get their own
    // tests below.
    private static readonly int[] Lamp = { 217, 217, 208 };

    // ---- the oracle ---------------------------------------------------------------------

    // Vanilla's flood, transcribed from ComputeGlowGridsJob. Returns the accumulated distance in
    // CELLS for every cell of a square window, with float.PositiveInfinity for anything the flood
    // never reached. Deliberately a plain Dijkstra over a dictionary rather than anything clever:
    // it exists to be obviously right, not to be fast.
    private sealed class GlowFlood
    {
        private static readonly int[] DirX = { 0, 1, 0, -1, 1, 1, -1, -1 };
        private static readonly int[] DirZ = { -1, 0, 1, 0, -1, 1, 1, -1 };

        // Which two cardinals flank each diagonal, by direction index, matching the job's switch.
        private static readonly int[] FlankA = { -1, -1, -1, -1, 0, 1, 2, 0 };
        private static readonly int[] FlankB = { -1, -1, -1, -1, 1, 2, 3, 3 };

        private readonly Dictionary<(int, int), int> dist = new Dictionary<(int, int), int>();

        public GlowFlood(Func<int, int, bool> blocked, int lightX, int lightZ, float radius)
        {
            int limit = (int)Math.Round(radius * 100f);
            SortedSet<(int, int, int)> queue = new SortedSet<(int, int, int)>();

            dist[(lightX, lightZ)] = 100;
            queue.Add((100, lightX, lightZ));

            while (queue.Count > 0)
            {
                (int d, int x, int z) = queue.Min;
                queue.Remove(queue.Min);

                if (d > dist[(x, z)])
                    continue;

                bool[] blockers = new bool[8];

                for (int i = 0; i < 8; i++)
                    blockers[i] = blocked(x + DirX[i], z + DirZ[i]);

                for (int i = 0; i < 8; i++)
                {
                    if (blockers[i])
                        continue;

                    // A diagonal is refused when BOTH the cardinals either side of it are blocked,
                    // which is what stops light squeezing through the corner where two walls meet.
                    if (i >= 4 && blockers[FlankA[i]] && blockers[FlankB[i]])
                        continue;

                    int step = i < 4 ? 100 : 141;
                    int next = d + step;

                    if (next > limit)
                        continue;

                    (int, int) cell = (x + DirX[i], z + DirZ[i]);

                    if (dist.TryGetValue(cell, out int known) && known <= next)
                        continue;

                    dist[cell] = next;
                    queue.Add((next, cell.Item1, cell.Item2));
                }
            }
        }

        // Distance in cells, or infinity where the flood never arrived.
        public float At(int x, int z) =>
            dist.TryGetValue((x, z), out int d) ? d / 100f : float.PositiveInfinity;

        // What vanilla writes into that array's ALPHA at this cell: `colorInt.a = (int)num2`, the
        // accumulated distance truncated to whole cells, and written only where the cell got light
        // at all. Zero therefore means "nothing delivered here" and never "delivered, at distance
        // zero" — the flood seeds its own cell at one cell, so no lit cell can carry a zero.
        public int Alpha(int x, int z, int colourChannel, float radius) =>
            Channel(x, z, colourChannel, radius) <= 0 ? 0 : (int)At(x, z);

        // What vanilla writes into its per-light array for this cell, one channel.
        public int Channel(int x, int z, int colourChannel, float radius)
        {
            float d = At(x, z);

            if (float.IsInfinity(d) || d > radius)
                return 0;

            float linear = 1f - d / radius;
            float inverseSquare = 1f / (d * d);
            float mixed = linear + 0.4f * (inverseSquare - linear);

            return (int)(colourChannel * Math.Min(Math.Max(mixed, 0f), 1f));
        }
    }

    // Our model's channel at a cell the polygon can see, through the shipped falloff.
    private static int OurChannel(int dx, int dz, float radius, int colourChannel, bool matchSeed)
    {
        float distance = VectorLightLiftMath.SightlineDistance(dx, dz, matchSeed);
        float falloff = VectorLightMath.Falloff(distance, radius);

        VectorLightLiftMath.Project(
            colourChannel, colourChannel, colourChannel, falloff, out int r, out int _, out int _);

        return r;
    }

    private static GlowFlood OpenGround(float radius) =>
        new GlowFlood((x, z) => false, 0, 0, radius);

    // ---- the closed form against the oracle ----------------------------------------------

    // The whole file rests on OctileFloodDistance being what the flood really accumulates, so it is
    // checked against the flood itself rather than against arithmetic done twice.
    [TestCase(1, 0)]
    [TestCase(0, 1)]
    [TestCase(3, 0)]
    [TestCase(2, 2)]
    [TestCase(3, 1)]
    [TestCase(4, 2)]
    [TestCase(-5, 3)]
    public void ClosedFormOctileMatchesTheFloodOnOpenGround(int dx, int dz)
    {
        GlowFlood flood = OpenGround(20f);

        Assert.That(
            VectorLightLiftMath.OctileFloodDistance(dx, dz),
            Is.EqualTo(flood.At(dx, dz)).Within(Tolerance));
    }

    // The seed, stated as a test because everything downstream is calibrated on it: the light's own
    // cell is one cell away from itself as far as the falloff is concerned.
    [Test]
    public void TheFloodSeedsItsOwnCellAtOneCell()
    {
        Assert.That(OpenGround(20f).At(0, 0), Is.EqualTo(1f).Within(Tolerance));
        Assert.That(VectorLightLiftMath.OctileFloodDistance(0, 0), Is.EqualTo(1f).Within(Tolerance));
    }

    // ---- #151's finding, confirmed rather than inherited ---------------------------------

    // On a clear cardinal run the two models are the same model, so the max has nothing to take.
    // This is #151's headline reproduced against the oracle, and it has to hold or the lift is a
    // brightness change wearing a geometry change's clothes.
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(5)]
    [TestCase(9)]
    public void TheMaxAddsNothingOnAClearCardinalSightline(int dx)
    {
        GlowFlood flood = OpenGround(12f);

        int vanilla = flood.Channel(dx, 0, Lamp[0], 12f);
        int ours = OurChannel(dx, 0, 12f, Lamp[0], matchSeed: true);

        Assert.That(VectorLightLiftMath.LiftChannel(ours, vanilla, 255), Is.EqualTo(0));
    }

    // And on a clear diagonal, where the flood's 141 is very slightly SHORTER than the true 141.42,
    // so vanilla is fractionally the brighter of the two and the max keeps vanilla.
    [TestCase(1)]
    [TestCase(3)]
    [TestCase(6)]
    public void TheMaxAddsNothingOnAClearDiagonalSightline(int n)
    {
        GlowFlood flood = OpenGround(12f);

        int vanilla = flood.Channel(n, n, Lamp[0], 12f);
        int ours = OurChannel(n, n, 12f, Lamp[0], matchSeed: true);

        Assert.That(VectorLightLiftMath.LiftChannel(ours, vanilla, 255), Is.EqualTo(0));
    }

    // ---- where it does not hold ----------------------------------------------------------

    // Off the eight principal directions the octile metric runs long, so our straight line is
    // genuinely the shorter path to a cell both models can see. Small — this is the residue #151
    // named — but not zero, and the test asserts BOTH halves so that zeroing the lift fails it and
    // so does letting the lift run away into a brightness rescale.
    [Test]
    public void TheOctileResidueIsASmallButRealLiftOffTheAxes()
    {
        GlowFlood flood = OpenGround(12f);
        int peak = 0;

        for (int dz = -11; dz <= 11; dz++)
        {
            for (int dx = -11; dx <= 11; dx++)
            {
                int vanilla = flood.Channel(dx, dz, Lamp[0], 12f);
                int ours = OurChannel(dx, dz, 12f, Lamp[0], matchSeed: true);
                peak = Math.Max(peak, VectorLightLiftMath.LiftChannel(ours, vanilla, 255));
            }
        }

        Assert.That(peak, Is.GreaterThan(0), "the octile residue is real and the lift must find it");
        Assert.That(peak, Is.LessThan(20), "on open ground the lift is a residue, not a level change");
    }

    // The worst of that residue sits where the octile metric is worst, which is neither on an axis
    // nor on a diagonal. Pinned so a change to the distance model has to explain itself.
    [Test]
    public void TheResiduePeaksBetweenTheAxisAndTheDiagonal()
    {
        GlowFlood flood = OpenGround(12f);

        int onAxis = VectorLightLiftMath.LiftChannel(
            OurChannel(4, 0, 12f, Lamp[0], matchSeed: true), flood.Channel(4, 0, Lamp[0], 12f), 255);
        int between = VectorLightLiftMath.LiftChannel(
            OurChannel(4, 2, 12f, Lamp[0], matchSeed: true), flood.Channel(4, 2, Lamp[0], 12f), 255);

        Assert.That(onAxis, Is.EqualTo(0));
        Assert.That(between, Is.GreaterThan(onAxis));
    }

    // THE CASE THE SUBTRACTIVE MASK CANNOT EXPRESS, and the reason this arm exists. A sealed room
    // with one door: vanilla's flood treats the door as a blocker whether it is open or shut, so
    // beyond it vanilla delivers NOTHING and there is no light for a mask to keep. §27e's polygon
    // looks straight through an open one, so the max is the entire beam.
    [Test]
    public void AnOpenDoorIsTheWholeBeamBecauseVanillaDeliveredNoneOfIt()
    {
        // A wall along x = 4 with a door at (4, 0). The light is at the origin, inside.
        bool Blocked(int x, int z) => x == 4;

        GlowFlood flood = new GlowFlood(Blocked, 0, 0, 12f);

        // Two cells beyond the doorway, on the sightline through it.
        int vanilla = flood.Channel(6, 0, Lamp[0], 12f);
        int ours = OurChannel(6, 0, 12f, Lamp[0], matchSeed: true);

        Assert.That(vanilla, Is.EqualTo(0), "the flood must not have got out of the room");
        Assert.That(ours, Is.GreaterThan(0));
        Assert.That(VectorLightLiftMath.LiftChannel(ours, vanilla, 255), Is.EqualTo(ours));
    }

    // The other half of that: an OPEN aperture — a genuine hole in the wall, which vanilla's flood
    // does pass — is NOT where the max wins, because within the wedge our polygon can see, the
    // straight line runs through the hole and the flood walked the same line. #151 measured exactly
    // this and read it as the general case; it is the special one.
    [Test]
    public void AnOpenApertureIsNotWhereTheMaxWins()
    {
        bool Blocked(int x, int z) => x == 4 && z != 0;

        GlowFlood flood = new GlowFlood(Blocked, 0, 0, 12f);

        int vanilla = flood.Channel(6, 0, Lamp[0], 12f);
        int ours = OurChannel(6, 0, 12f, Lamp[0], matchSeed: true);

        Assert.That(vanilla, Is.GreaterThan(0), "the flood does pass a real hole");
        Assert.That(VectorLightLiftMath.LiftChannel(ours, vanilla, 255), Is.EqualTo(0));
    }

    // ---- the mask half of the composition ------------------------------------------------

    // A cell the polygon cannot see gets no lift, however bright our model says a clear line to it
    // would have been. Without this the max brightens the shadow it is standing in, which is #151's
    // structural objection and the thing the coverage gate answers.
    [Test]
    public void AFullyShadowedCellGetsNoLift()
    {
        Assert.That(VectorLightLiftMath.LiftChannel(200, 0, 0), Is.EqualTo(0));
    }

    // And a boundary cell gets the same ramp the subtraction uses, so the lit edge and the shadow
    // edge are one edge rather than two a fraction of a cell apart.
    [TestCase(255, 100)]
    [TestCase(128, 50)]
    [TestCase(64, 25)]
    [TestCase(0, 0)]
    public void TheLiftRampsWithCoverage(int coverage, int expected)
    {
        Assert.That(VectorLightLiftMath.LiftChannel(100, 0, coverage), Is.EqualTo(expected));
    }

    // Never negative: where vanilla is the brighter of the two the max keeps vanilla, and the mask's
    // own subtraction is the only thing allowed to take light away.
    [Test]
    public void VanillaBrighterThanUsLiftsNothing()
    {
        Assert.That(VectorLightLiftMath.LiftChannel(40, 90, 255), Is.EqualTo(0));
    }

    // ---- the seed, as a measurable choice ------------------------------------------------

    // Dropping the seed compares our curve at d against vanilla's at d + 1, which is not the same
    // quantity — so it wins on a clear cardinal sightline, where by construction there is no
    // geometry to win on. That is what makes the unmatched arm a brightness rescale, and it is
    // pinned here so the two conventions cannot be quietly swapped.
    // The levels are pinned rather than bounded because their SHAPE is the argument: 76 levels of
    // lift one cell out, 23 at two cells and 13 at four, on a run where the geometry is identical
    // and the matched arm correctly finds nothing. That is a halo around every lamp on the map, not
    // a shadow, and it is what "the max makes §27 brighter" would actually look like.
    [TestCase(1, 76)]
    [TestCase(2, 23)]
    [TestCase(4, 13)]
    public void DroppingTheSeedTurnsTheMaxIntoABrightnessRescale(int dx, int expected)
    {
        GlowFlood flood = OpenGround(12f);
        int vanilla = flood.Channel(dx, 0, Lamp[0], 12f);

        int matched = VectorLightLiftMath.LiftChannel(
            OurChannel(dx, 0, 12f, Lamp[0], matchSeed: true), vanilla, 255);
        int unmatched = VectorLightLiftMath.LiftChannel(
            OurChannel(dx, 0, 12f, Lamp[0], matchSeed: false), vanilla, 255);

        Assert.That(matched, Is.EqualTo(0));
        Assert.That(unmatched, Is.EqualTo(expected));
    }

    [Test]
    public void MatchedSeedDistanceIsEuclideanPlusOne()
    {
        Assert.That(
            VectorLightLiftMath.SightlineDistance(3f, 4f, matchSeed: true),
            Is.EqualTo(6f).Within(Tolerance));
        Assert.That(
            VectorLightLiftMath.SightlineDistance(3f, 4f, matchSeed: false),
            Is.EqualTo(5f).Within(Tolerance));
    }

    // ---- the projection ------------------------------------------------------------------

    // ColorInt.operator *(ColorInt, float) truncates rather than rounding, and our value has to
    // truncate the same way or the max picks us over vanilla by one level across whole regions
    // where the two models agree — a lift that is entirely rounding.
    [Test]
    public void ProjectionTruncatesTheSameWayColorIntDoes()
    {
        VectorLightLiftMath.Project(100, 100, 100, 0.999f, out int r, out int _, out int _);
        Assert.That(r, Is.EqualTo(99));
    }

    // Over 255 the three channels scale together, so a lamp saturates towards white rather than
    // towards whichever channel clipped first. Matching ProjectToColor32Fast, not a clamp.
    [Test]
    public void ProjectionPreservesHueWhenItSaturates()
    {
        VectorLightLiftMath.Project(255, 200, 100, 2f, out int r, out int g, out int b);

        Assert.That(r, Is.EqualTo(255));
        Assert.That(g, Is.EqualTo(200));
        Assert.That(b, Is.EqualTo(100));
    }

    [Test]
    public void ProjectionLeavesAnUnsaturatedColourAlone()
    {
        VectorLightLiftMath.Project(200, 100, 50, 0.5f, out int r, out int g, out int b);

        Assert.That(r, Is.EqualTo(100));
        Assert.That(g, Is.EqualTo(50));
        Assert.That(b, Is.EqualTo(25));
    }

    // ---- which renderer owns a cell -------------------------------------------------------
    //
    // The rule under test decides, per cell, whether our model REPLACES vanilla's contribution or
    // leaves it standing. Both directions are load-bearing and they fail in opposite ways: claiming
    // too much takes the near-field radiance off every lamp (which is what the global aperture beam
    // did, 19.89 -> 17.87 L* on the lamp cell), and claiming too little leaves an aperture's light
    // split between two renderers at two resolutions, which is the shape problem it exists for.
    //
    // The vanilla side of every one of these comes from GlowFlood, not from the rule's own
    // arithmetic run twice.

    // The integer accumulator, against the flood's own. Everything below rests on this being what
    // vanilla really counts to a cell it reached in a straight run.
    [TestCase(0, 0)]
    [TestCase(1, 0)]
    [TestCase(4, 0)]
    [TestCase(3, 3)]
    [TestCase(5, 2)]
    [TestCase(-7, 3)]
    [TestCase(2, -6)]
    public void ClearRunStepCostMatchesTheFloodsAccumulator(int dx, int dz)
    {
        GlowFlood flood = OpenGround(20f);

        Assert.That(
            VectorLightLiftMath.ClearRunStepCost(dx, dz) / 100f,
            Is.EqualTo(flood.At(dx, dz)).Within(Tolerance));
    }

    // And the truncation, against the byte the flood would have written. Separate from the test
    // above because the truncation is the part that makes the comparison robust, so it is worth
    // failing on its own rather than inside a distance assertion.
    [TestCase(1, 0)]
    [TestCase(3, 2)]
    [TestCase(4, 4)]
    [TestCase(8, 3)]
    public void ClearRunDistanceIsTheAlphaTheFloodWouldWrite(int dx, int dz)
    {
        GlowFlood flood = OpenGround(20f);

        Assert.That(
            VectorLightLiftMath.ClearRunDistance(dx, dz),
            Is.EqualTo(flood.Alpha(dx, dz, Lamp[0], 20f)));
    }

    // THE PROPERTY THAT KEEPS A TORCH LOOKING LIKE A TORCH. Over every cell of an open-ground lamp,
    // the rule must claim nothing that vanilla lit. Swept rather than sampled, because the failure
    // this guards against is one direction of one radius quietly crossing a boundary, and a handful
    // of TestCases would photograph exactly the directions that already work.
    [Test]
    public void NothingVanillaLitIsClaimedOnOpenGround()
    {
        const float radius = 12f;
        GlowFlood flood = OpenGround(radius);
        List<(int, int)> claimed = new List<(int, int)>();
        int lit = 0;

        for (int dz = -13; dz <= 13; dz++)
        {
            for (int dx = -13; dx <= 13; dx++)
            {
                int channel = flood.Channel(dx, dz, Lamp[0], radius);

                if (channel <= 0)
                    continue;

                lit++;

                if (VectorLightLiftMath.VanillaBentToArrive(
                        dx, dz, flood.Alpha(dx, dz, Lamp[0], radius), delivered: true))
                {
                    claimed.Add((dx, dz));
                }
            }
        }

        // The cell count is asserted alongside, so a sweep that matched nothing at all cannot pass
        // as a clean result — an empty region claims nothing however wrong the rule is. Pinned at
        // the measured count rather than a floor, so a change to the radius cutoff has to be
        // noticed here rather than absorbed.
        Assert.That(lit, Is.EqualTo(341));
        Assert.That(claimed, Is.Empty);
    }

    // PROVING THE RED, and the reason the rule is written on distance rather than on the two
    // brightnesses. The obvious reading of "vanilla < ours" is a level comparison, and on open
    // ground — where the answer must be "leave every cell alone" — a level comparison claims most
    // of the lamp, because the octile metric runs long off the eight principal directions and our
    // straight line is genuinely shorter. Without this test the distance form reads as paranoia.
    [Test]
    public void ALevelComparisonWouldClaimMostOfAnOpenGroundLamp()
    {
        const float radius = 12f;
        GlowFlood flood = OpenGround(radius);
        int lit = 0;
        int wouldClaim = 0;

        for (int dz = -13; dz <= 13; dz++)
        {
            for (int dx = -13; dx <= 13; dx++)
            {
                int vanilla = flood.Channel(dx, dz, Lamp[0], radius);

                if (vanilla <= 0)
                    continue;

                lit++;

                if (OurChannel(dx, dz, radius, Lamp[0], matchSeed: true) > vanilla)
                    wouldClaim++;
            }
        }

        Assert.That(wouldClaim, Is.GreaterThan(lit / 2));
    }

    // A cell vanilla never reached is ours by the same rule, with no special case for how it failed
    // to get there. Beyond the radius the flood stops at octile + 1 > radius while a straight line
    // reaches to radius; behind a wall it stops because there is no route at all.
    [Test]
    public void ACellVanillaNeverReachedIsOurs()
    {
        Assert.That(
            VectorLightLiftMath.VanillaBentToArrive(9, 0, 0, delivered: false), Is.True);
    }

    // ---- the aperture, which is what the rule is for -------------------------------------
    //
    // The gate scene's own geometry: a radius-10 torch six cells inside a room, one wall carrying a
    // single-cell gap on the torch's own row. The flood has to route THROUGH that hole, so its cost
    // to a cell outside is the octile distance to the hole plus the octile distance on from it —
    // which equals the direct octile inside the aperture's cone and exceeds it outside.

    private const float TorchRadius = 10f;

    private static GlowFlood GapRoom()
    {
        return new GlowFlood(
            (x, z) =>
            {
                bool sideWall = (x == -6 || x == 6) && z >= -4 && z <= 4 && z != 0;
                bool endWall = (z == -4 || z == 4) && x >= -6 && x <= 6;

                // The door column is a blocker to vanilla whatever the door is doing: RimWorld
                // writes def.blockLight into lightBlockers once, at spawn, and opening the door
                // touches the glow grid not at all.
                bool door = x == -6 && z == 0;

                return sideWall || endWall || door;
            },
            0, 0, TorchRadius);
    }

    // ON THE APERTURE'S AXIS THE RULE CLAIMS NOTHING, and that is correct rather than a miss.
    // Routing through a hole on the lamp's own row costs exactly the direct octile distance for the
    // cells straight out from it, so vanilla delivers precisely what our model claims and there is
    // no reason to take the cell off it.
    [TestCase(7)]
    [TestCase(8)]
    public void TheApertureAxisIsNotClaimed(int dx)
    {
        GlowFlood flood = GapRoom();

        Assert.That(flood.Channel(dx, 0, Lamp[0], TorchRadius), Is.GreaterThan(0));
        Assert.That(
            VectorLightLiftMath.VanillaBentToArrive(
                dx, 0, flood.Alpha(dx, 0, Lamp[0], TorchRadius), delivered: true),
            Is.False);
    }

    // THE FRINGE IS WHAT IT CLAIMS. Off the axis the detour through the hole costs more than the
    // straight line, so vanilla's flood is genuinely dimmer there than a clear run would be, and
    // this is the region where its cell-resolution blob is filling in the shoulder of a beam.
    [TestCase(7, 2)]
    [TestCase(7, -2)]
    public void TheApertureFringeIsClaimed(int dx, int dz)
    {
        GlowFlood flood = GapRoom();

        Assert.That(flood.Channel(dx, dz, Lamp[0], TorchRadius), Is.GreaterThan(0));
        Assert.That(
            VectorLightLiftMath.VanillaBentToArrive(
                dx, dz, flood.Alpha(dx, dz, Lamp[0], TorchRadius), delivered: true),
            Is.True);
    }

    // AND NOTHING INSIDE THE ROOM IS CLAIMED, which is the aperture-scene form of the open-ground
    // sweep: the torch is six cells from either wall and every cell between it and them is a clear
    // run for both models. A rule that took the room would be the global replacement again, and
    // this scene is where that was measured as a loss.
    [Test]
    public void NothingInsideTheLampsOwnRoomIsClaimed()
    {
        GlowFlood flood = GapRoom();
        List<(int, int)> claimed = new List<(int, int)>();
        int lit = 0;

        for (int dz = -3; dz <= 3; dz++)
        {
            for (int dx = -5; dx <= 5; dx++)
            {
                if (flood.Channel(dx, dz, Lamp[0], TorchRadius) <= 0)
                    continue;

                lit++;

                if (VectorLightLiftMath.VanillaBentToArrive(
                        dx, dz, flood.Alpha(dx, dz, Lamp[0], TorchRadius), delivered: true))
                {
                    claimed.Add((dx, dz));
                }
            }
        }

        Assert.That(lit, Is.EqualTo(77));
        Assert.That(claimed, Is.Empty);
    }
}
