using System.Collections.Generic;

namespace CelestialLighting.Tests;

// The one claim the silhouette memo makes: across a door swing it removes a window rescan and
// changes nothing about the segments that come out.
//
// WHY THAT NEEDS A FIXTURE OF ITS OWN. This is a cache over geometry, which is the combination the
// repo has the least ability to catch downstream. A silhouette held one step too long does not
// throw and does not move a counter — it draws one frame of one swing with a wall where a doorway
// has just opened, or a doorway where a wall has just gone up, on a subsystem whose own header
// already records a section Regenerate swallowing an exception and leaving the frame looking
// vanilla with every probe green. So the equivalence is established here, where a failure is loud,
// rather than hoped for from a live A/B that would average it away.
//
// EVERY ASSERTION IS BIT-FOR-BIT, not within a tolerance. The memo's argument is that a reused
// silhouette is the array a rescan would have rebuilt element for element, so there is no rounding
// to be generous about. A tolerance would accept a real defect at the one place this is most likely
// to have one — a leaf a fraction of a cell out of place, which is a shadow edge in the wrong cell.
//
// THE ORACLE IS THE RESCANNING PATH ITSELF, and it is independent of the thing under test in the
// way that matters: it never consults a Memo, so nothing about the reuse decision can make both
// sides wrong together. `HoldingASilhouetteAcrossTheTransitionIsWrong` is the red half — it shows
// the two paths genuinely disagreeing when the reuse test is bypassed, so a green run of the
// fixture above it means the test fired rather than that nothing was ever different. The leaves
// get a second oracle of their own, LeafGeometry, which shares no arithmetic with the subject at
// all.
[TestFixture]
public class VectorLightSilhouetteMathTests
{
    private const int Steps = DoorApertureMath.DefaultQuantisationSteps;

    // A 31x31 window, which is what a radius-14 lamp gets, with a wall running across the middle and
    // one door in it. The door is deliberately NOT at the end of the run: a door at the end would
    // leave the merged silhouette the same shape whether or not the run split, and this fixture is
    // partly about the merge.
    //
    // THE DOOR'S X AND THE WALL'S Z ARE DIFFERENT NUMBERS, AND THAT IS LOAD-BEARING. They were both
    // 15 in the first draft of this fixture, which made every axis confusion in the leaf placement
    // invisible: swapping the two faces of the door for the two ends of its slide left the segments
    // numerically identical and the whole fixture green. A scene whose coordinates coincide cannot
    // test code whose entire job is to keep two axes apart.
    private const int Width = 31;
    private const int Height = 31;
    private const int WallZ = 15;
    private const int WallFromX = 5;
    private const int WallToX = 25;
    private const int DoorX = 11;

    // The emitter this window belongs to. Only the identity matters to the pure core — it is what
    // CoversWindow compares — but it is spelled as a real light's cell rather than the origin so the
    // moved-light cases below read as a light that moved rather than as arithmetic on zero.
    private const int CentreX = 16;
    private const int CentreZ = 14;
    private const float Radius = 14f;

    // ---- the headline equivalence ---------------------------------------------------------------

    // The whole of issue #188 item C in one test: bake the silhouette once at the first open step,
    // then hold it for the rest of the swing and check every step against a full rescan.
    //
    // Steps 1..8 only. Step 0 is the shut door, where the whole-cell grid genuinely differs, and it
    // gets its own two tests below — the memo is required to REFUSE there, and required to notice
    // without being told.
    [TestCase(true)]
    [TestCase(false)]
    public void AHeldSilhouetteMatchesARescanAtEveryOpenStep(bool alongX)
    {
        VectorLightSilhouetteMath.Memo memo = MemoAt(Aperture(1), alongX);

        for (int step = 1; step <= Steps; step++)
        {
            float open = Aperture(step);
            List<VectorLightSilhouetteMath.Door> doors = DoorsAt(open, alongX);

            Assert.That(
                VectorLightSilhouetteMath.OcclusionUnchanged(memo, doors), Is.True,
                $"step {step} should be reusable: the door is a hole in the grid at every open step");

            AssertSameSegments(
                Rescan(open, alongX),
                VectorLightSilhouetteMath.Assemble(memo.Silhouette, doors, new List<VectorLightMath.Segment>()),
                $"step {step}");
        }
    }

    // The same equivalence taken the other way round: a memo recorded at the LAST step of a swing
    // and reused backwards. A door interrupted mid-open animates back down through the same steps,
    // so the reuse has to be direction-blind, and a memo that only happened to work when recorded
    // early would pass the test above and fail this one.
    [Test]
    public void AHeldSilhouetteMatchesARescanWhileTheDoorCloses()
    {
        VectorLightSilhouetteMath.Memo memo = MemoAt(Aperture(Steps), alongX: false);

        for (int step = Steps; step >= 1; step--)
        {
            float open = Aperture(step);
            List<VectorLightSilhouetteMath.Door> doors = DoorsAt(open, alongX: false);

            Assert.That(VectorLightSilhouetteMath.OcclusionUnchanged(memo, doors), Is.True);
            AssertSameSegments(
                Rescan(open, alongX: false),
                VectorLightSilhouetteMath.Assemble(memo.Silhouette, doors, new List<VectorLightMath.Segment>()),
                $"closing step {step}");
        }
    }

    // ---- the step the memo has to refuse ---------------------------------------------------------

    // The transition that ends the reuse, and the only one in a swing: the door leaves zero, or
    // returns to it, and its cell stops or starts being a whole-cell occluder.
    [Test]
    public void TheShutStepIsRefused()
    {
        VectorLightSilhouetteMath.Memo memo = MemoAt(Aperture(1), alongX: false);

        Assert.That(
            VectorLightSilhouetteMath.OcclusionUnchanged(memo, DoorsAt(0f, alongX: false)), Is.False);
    }

    [Test]
    public void AMemoTakenShutIsRefusedOnceTheDoorMoves()
    {
        VectorLightSilhouetteMath.Memo memo = MemoAt(0f, alongX: false);

        Assert.That(
            VectorLightSilhouetteMath.OcclusionUnchanged(memo, DoorsAt(Aperture(1), alongX: false)),
            Is.False);
    }

    // THE RED HALF, kept as a test rather than performed by hand and described in a commit message.
    //
    // It bypasses OcclusionUnchanged and holds a silhouette across the shut/open transition anyway,
    // in both directions, and asserts the answer is WRONG. Without this, every green above is
    // consistent with a scene where the two paths could never have differed — which is the failure
    // mode a differential test is most likely to have, and the one nobody notices, because it looks
    // like a pass.
    //
    // BOTH DIRECTIONS, because they are different wrongnesses and a fixture that showed only one
    // would leave the other unmeasured: held open through a shut step, the wall keeps a doorway that
    // has closed; held shut through an open step, a doorway that has opened stays walled up. The
    // second is the one a player notices, because it is a beam that fails to appear.
    [TestCase(1, 0, TestName = "HoldingAnOpenSilhouetteThroughTheShutStepKeepsTheDoorway")]
    [TestCase(0, 1, TestName = "HoldingAShutSilhouetteThroughTheFirstOpenStepWallsUpTheDoorway")]
    public void HoldingASilhouetteAcrossTheTransitionIsWrong(int heldStep, int askedStep)
    {
        VectorLightSilhouetteMath.Memo memo = MemoAt(Aperture(heldStep), alongX: false);

        VectorLightMath.Segment[] held = VectorLightSilhouetteMath.Assemble(
            memo.Silhouette, DoorsAt(Aperture(askedStep), alongX: false),
            new List<VectorLightMath.Segment>());

        Assert.That(Describe(held), Is.Not.EqualTo(Describe(Rescan(Aperture(askedStep), alongX: false))),
            "the two steps have different whole-cell grids; if this passes, the scene has no door in "
            + "it and the whole fixture is measuring nothing");
    }

    // ---- what else ends a reuse ------------------------------------------------------------------

    [Test]
    public void AnInvalidatedMemoCoversNothing()
    {
        VectorLightSilhouetteMath.Memo memo = MemoAt(Aperture(1), alongX: false);
        Assert.That(VectorLightSilhouetteMath.CoversWindow(memo, CentreX, CentreZ, Radius), Is.True);

        memo.Invalidate();

        Assert.That(VectorLightSilhouetteMath.CoversWindow(memo, CentreX, CentreZ, Radius), Is.False);
    }

    // A light that moved or was recoloured comes back through the roster resync with the same Memo
    // attached and a different window. Nothing invalidates it — a resync is not a blocker write — so
    // the window comparison is the only thing standing between that light and somebody else's wall.
    [TestCase(CentreX + 1, CentreZ, Radius)]
    [TestCase(CentreX, CentreZ + 1, Radius)]
    [TestCase(CentreX, CentreZ, Radius - 2f)]
    public void AMovedOrResizedLightCoversNothing(int centreX, int centreZ, float radius)
    {
        VectorLightSilhouetteMath.Memo memo = MemoAt(Aperture(1), alongX: false);

        Assert.That(VectorLightSilhouetteMath.CoversWindow(memo, centreX, centreZ, radius), Is.False);
    }

    [Test]
    public void ANullMemoCoversNothing()
    {
        Assert.That(VectorLightSilhouetteMath.CoversWindow(null, CentreX, CentreZ, Radius), Is.False);
    }

    // A door built or mined inside the window changes the count, not just a flag. That also fires
    // LightBlockerAdded/Removed and clears Valid, so this is the belt to that braces — but a mod that
    // spawns a door without going through Building.SpawnSetup would otherwise index off the end of
    // the shorter list, and an IndexOutOfRange inside a section Regenerate is swallowed.
    [Test]
    public void ADifferentNumberOfDoorsIsRefused()
    {
        VectorLightSilhouetteMath.Memo memo = MemoAt(Aperture(1), alongX: false);

        Assert.That(VectorLightSilhouetteMath.OcclusionUnchanged(memo, new List<VectorLightSilhouetteMath.Door>()),
            Is.False);
        Assert.That(VectorLightSilhouetteMath.OcclusionUnchanged(memo, null), Is.False);
    }

    // ---- the assembly itself ---------------------------------------------------------------------

    // A shut door and a fully open one both emit no leaves, for opposite reasons — one is an ordinary
    // blocker already in the grid, the other an ordinary hole. The array is then handed back
    // unwrapped, which is the common case and the one that must not allocate.
    [TestCase(0f)]
    [TestCase(1f)]
    public void ADoorAtEitherEndAddsNothingAndCopiesNothing(float open)
    {
        VectorLightMath.Segment[] silhouette = SilhouetteOf(Blocked(open), out _);

        VectorLightMath.Segment[] assembled = VectorLightSilhouetteMath.Assemble(
            silhouette, DoorsAt(open, alongX: false), new List<VectorLightMath.Segment>());

        Assert.That(assembled, Is.SameAs(silhouette));
    }

    // Four leaf segments for one part-open door — two leaves on each of the two faces light can
    // cross — and they must be the faces PERPENDICULAR to the way the leaves slide. Getting that
    // backwards produces a plausible-looking segment set that occludes along the wall instead of
    // across it, which renders as a door that shadows the room it is in rather than the one beyond.
    [TestCase(true)]
    [TestCase(false)]
    public void APartOpenDoorContributesFourLeaves(bool alongX)
    {
        VectorLightMath.Segment[] silhouette = SilhouetteOf(Blocked(Aperture(4)), out _);

        VectorLightMath.Segment[] assembled = VectorLightSilhouetteMath.Assemble(
            silhouette, DoorsAt(Aperture(4), alongX), new List<VectorLightMath.Segment>());

        Assert.That(assembled.Length, Is.EqualTo(silhouette.Length + 4));

        for (int i = silhouette.Length; i < assembled.Length; i++)
        {
            VectorLightMath.Segment leaf = assembled[i];

            // A leaf spans the axis it slides along and is flat on the other.
            if (alongX)
                Assert.That(leaf.Z1, Is.EqualTo(leaf.Z2), "a leaf sliding along X lies on one Z line");
            else
                Assert.That(leaf.X1, Is.EqualTo(leaf.X2), "a leaf sliding along Z lies on one X line");
        }
    }

    // Leaves are appended AFTER the silhouette, and the silhouette's own elements are untouched.
    // Order is not cosmetic here: Build fires a corner ray at every endpoint it is handed in the
    // order it is handed them, and the polygon's vertices come back in ray order.
    [Test]
    public void TheSilhouetteKeepsItsPlaceAtTheFrontOfTheAssembly()
    {
        VectorLightMath.Segment[] silhouette = SilhouetteOf(Blocked(Aperture(4)), out _);

        VectorLightMath.Segment[] assembled = VectorLightSilhouetteMath.Assemble(
            silhouette, DoorsAt(Aperture(4), alongX: false), new List<VectorLightMath.Segment>());

        for (int i = 0; i < silhouette.Length; i++)
            Assert.That(Describe(assembled[i]), Is.EqualTo(Describe(silhouette[i])), $"segment {i}");
    }

    // Two doors in one wall, so the door list is not a stand-in for "the door". The memo compares
    // them positionally, and a fixture with one door cannot tell a positional comparison from one
    // that happens to look at the first entry.
    [Test]
    public void OneDoorOfTwoChangingIsEnoughToRefuse()
    {
        List<VectorLightSilhouetteMath.Door> open = new List<VectorLightSilhouetteMath.Door>
        {
            Door(DoorX, Aperture(1), alongX: false),
            Door(DoorX + 4, Aperture(1), alongX: false),
        };

        VectorLightSilhouetteMath.Memo memo = new VectorLightSilhouetteMath.Memo
        {
            Silhouette = new VectorLightMath.Segment[0],
            Valid = true,
        };
        memo.Doors.AddRange(open);

        List<VectorLightSilhouetteMath.Door> second = new List<VectorLightSilhouetteMath.Door>
        {
            Door(DoorX, Aperture(1), alongX: false),
            Door(DoorX + 4, 0f, alongX: false),
        };

        Assert.That(VectorLightSilhouetteMath.OcclusionUnchanged(memo, open), Is.True);
        Assert.That(VectorLightSilhouetteMath.OcclusionUnchanged(memo, second), Is.False);
    }

    // ---- the leaves, against an oracle that shares no arithmetic ---------------------------------

    // Pulling the leaf emission out of VectorLightBlockers and into the pure core moved code that
    // nothing offline could reach into code that everything offline can. This is what that buys.
    //
    // WHY AN ORACLE AND NOT THE OLD CODE TRANSCRIBED. A transcription would call the same
    // DoorApertureMath.LeafSpans the subject does, so the two sides would agree about a leaf in the
    // wrong place as readily as about one in the right place — the shape of assertion that reduces
    // to x - x == 0. LeafGeometry below derives the four segments from the description in prose
    // instead: each leaf is half a cell when shut and shrinks to nothing when open, they sit at the
    // two ends of the cell's span along the slide axis, and each appears on both of the faces
    // perpendicular to it.
    [TestCase(true)]
    [TestCase(false)]
    public void EveryApertureStepPlacesItsLeavesWhereTheGeometrySaysAlong(bool alongX)
    {
        VectorLightMath.Segment[] silhouette = SilhouetteOf(Blocked(Aperture(1)), out _);

        for (int step = 1; step < Steps; step++)
        {
            float open = Aperture(step);

            VectorLightMath.Segment[] assembled = VectorLightSilhouetteMath.Assemble(
                silhouette, DoorsAt(open, alongX), new List<VectorLightMath.Segment>());

            VectorLightMath.Segment[] expected = LeafGeometry(DoorX, WallZ, alongX, open);
            VectorLightMath.Segment[] actual = new VectorLightMath.Segment[assembled.Length - silhouette.Length];
            System.Array.Copy(assembled, silhouette.Length, actual, 0, actual.Length);

            AssertSameSegments(expected, actual, $"leaves at step {step}");
        }
    }

    // The four leaf edges of a door at (cellX, cellZ) opened `open` of the way, derived from the
    // geometry rather than from the code under test. Order matches the assembly's: low leaf then
    // high leaf on the near face, then the same two on the far face.
    private static VectorLightMath.Segment[] LeafGeometry(
        int cellX, int cellZ, bool alongX, float open)
    {
        // Each leaf retracts into its own side of the doorway, so the gap between them is centred
        // and exactly `open` of a cell wide.
        double leaf = (1.0 - open) / 2.0;

        double slideMin = alongX ? cellX : cellZ;
        double nearFace = alongX ? cellZ : cellX;

        List<VectorLightMath.Segment> segments = new List<VectorLightMath.Segment>(4);

        for (int face = 0; face < 2; face++)
        {
            double at = nearFace + face;

            AddIfLongEnough(segments, alongX, at, slideMin, slideMin + leaf);
            AddIfLongEnough(segments, alongX, at, slideMin + 1.0 - leaf, slideMin + 1.0);
        }

        return segments.ToArray();
    }

    private static void AddIfLongEnough(
        List<VectorLightMath.Segment> into, bool alongX, double face, double start, double end)
    {
        // A leaf shorter than a thousandth of a cell is not worth a pair of corner rays, and a
        // zero-length one is a degenerate input to the ray/segment solve.
        if (end - start < DoorApertureMath.MinimumLeafLength)
        {
            return;
        }

        into.Add(alongX
            ? new VectorLightMath.Segment((float)start, (float)face, (float)end, (float)face)
            : new VectorLightMath.Segment((float)face, (float)start, (float)face, (float)end));
    }

    // ---- scene construction ----------------------------------------------------------------------

    private static float Aperture(int step) => (float)step / Steps;

    // The window as VectorLightBlockers.FillWindow would have left it: the wall run, with the door
    // cell blocked only when the door is shut. DoorOcclusionMath owns that rule live; here it is
    // spelled out directly, because a scene that asked the shipped rule what its own scene looks
    // like would be describing the code rather than testing it.
    private static bool[] Blocked(float open)
    {
        bool[] blocked = new bool[Width * Height];

        for (int x = WallFromX; x <= WallToX; x++)
            blocked[WallZ * Width + x] = x != DoorX || open <= 0f;

        return blocked;
    }

    private static List<VectorLightSilhouetteMath.Door> DoorsAt(float open, bool alongX)
    {
        return new List<VectorLightSilhouetteMath.Door> { Door(DoorX, open, alongX) };
    }

    private static VectorLightSilhouetteMath.Door Door(int x, float open, bool alongX)
    {
        return new VectorLightSilhouetteMath.Door(x, WallZ, alongX, open, blocks: open <= 0f);
    }

    private static VectorLightMath.Segment[] SilhouetteOf(
        bool[] blocked, out VectorLightMath.Segment[] silhouette)
    {
        silhouette = VectorLightMath.SilhouetteSegments(blocked, Width, Height, 0, 0);
        return silhouette;
    }

    // The oracle: the whole path from a freshly scanned window, consulting no memo at all.
    private static VectorLightMath.Segment[] Rescan(float open, bool alongX)
    {
        return VectorLightSilhouetteMath.Build(
            Blocked(open), Width, Height, 0, 0, DoorsAt(open, alongX),
            new List<VectorLightMath.Segment>(), out _);
    }

    // A memo as VectorLightBlockers.Record would have left it after a rescan at this aperture.
    private static VectorLightSilhouetteMath.Memo MemoAt(float open, bool alongX)
    {
        VectorLightSilhouetteMath.Build(
            Blocked(open), Width, Height, 0, 0, DoorsAt(open, alongX),
            new List<VectorLightMath.Segment>(), out VectorLightMath.Segment[] silhouette);

        VectorLightSilhouetteMath.Memo memo = new VectorLightSilhouetteMath.Memo
        {
            Silhouette = silhouette,
            CentreX = CentreX,
            CentreZ = CentreZ,
            Radius = Radius,
            Valid = true,
        };
        memo.Doors.AddRange(DoorsAt(open, alongX));
        return memo;
    }

    // ---- comparison ------------------------------------------------------------------------------

    private static void AssertSameSegments(
        VectorLightMath.Segment[] expected, VectorLightMath.Segment[] actual, string what)
    {
        Assert.That(actual.Length, Is.EqualTo(expected.Length), $"{what}: segment count");

        for (int i = 0; i < expected.Length; i++)
            Assert.That(Describe(actual[i]), Is.EqualTo(Describe(expected[i])), $"{what}: segment {i}");
    }

    // Compared as a string rather than field by field so a failure names the two segments rather
    // than reporting that 15.0 is not 15.125 four assertions deep.
    private static string Describe(VectorLightMath.Segment s) =>
        $"({s.X1:R},{s.Z1:R})-({s.X2:R},{s.Z2:R})";

    private static string Describe(VectorLightMath.Segment[] segments)
    {
        string[] parts = new string[segments.Length];

        for (int i = 0; i < segments.Length; i++)
            parts[i] = Describe(segments[i]);

        return string.Join(" ", parts);
    }
}
