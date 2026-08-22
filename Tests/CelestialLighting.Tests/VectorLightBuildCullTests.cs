using System;
using System.Collections.Generic;

namespace CelestialLighting.Tests;

// The one claim the angular cull in VectorLightMath.Build makes: it removes work and changes nothing.
//
// WHY THIS IS A WHOLE FIXTURE RATHER THAN A CASE OR TWO. The cull is a performance change to
// geometry, which is the combination this repo has the least ability to catch downstream. A polygon
// that is subtly wrong does not throw, does not move a probe that anybody pins, and renders as a
// shadow edge a fraction of a cell out of place in one frame of one scene — the sun-shadow work has
// already recorded that a section Regenerate swallows exceptions and leaves the frame looking
// vanilla with everything green. So the equivalence has to be established here, exhaustively, where
// a failure is loud.
//
// EVERY ASSERTION IS BIT-FOR-BIT, not within a tolerance. The cull's argument is that a skipped
// segment is one the solver would have rejected anyway, so the arithmetic that survives is the
// identical arithmetic in the identical order. A tolerance would quietly accept a real defect at the
// one place this is most likely to have one — a ray aimed exactly along a corner, where the whole
// shadow edge lives.
//
// The oracle (VectorLightBuildOracle) is a verbatim transcription of the pre-cull implementation.
// See its header for why it is a transcription rather than a fresh rewrite.
[TestFixture]
public class VectorLightBuildCullTests
{
    private const int Rays = VectorLightMath.DefaultBaseRayCount;

    // ---- the cases that motivated the index --------------------------------------------------

    // Below the index's own MinSegments threshold, where Build takes the brute-force path. Included
    // so the fixture would still fail if the threshold were raised past every scene it tests — a
    // suite that only exercises the fast path proves nothing about the fast path.
    [Test]
    public void ASingleWallMatchesTheOracle()
    {
        AssertIdentical(VectorLightLayout.Grid(g => g.Wall(20, 12, 20, 28)), 14.5f, 20.5f, 14f);
    }

    [Test]
    public void AnEmptyWindowMatchesTheOracle()
    {
        AssertIdentical(VectorLightLayout.Grid(g => { }), 20.5f, 20.5f, 14f);
    }

    // Above the threshold, which is the path the whole change exists for.
    [Test]
    public void ACluttereredColonyMatchesTheOracle()
    {
        AssertIdentical(VectorLightLayout.Grid(VectorLightLayout.RoomBlock), 20.5f, 20.5f, 14f);
    }

    [Test]
    public void FreeStandingPillarsMatchTheOracle()
    {
        AssertIdentical(VectorLightLayout.Grid(g => g.Pillars(3)), 20.5f, 20.5f, 14f);
    }

    // ---- the geometry the cull is most likely to get wrong ------------------------------------

    // A wall due EAST of the light puts its arc at angle 0, in the middle of the range. A wall due
    // WEST puts it across the +-pi seam, where the arc wraps and the bucket range has to wrap with
    // it. The seam is the one place an interval scheme can silently drop half a wall, and the
    // failure looks like light leaking through a specific wall from a specific side.
    [TestCase(0f)]
    [TestCase((float)Math.PI)]
    [TestCase((float)(Math.PI / 2.0))]
    [TestCase((float)(-Math.PI / 2.0))]
    [TestCase(2.7f)]
    [TestCase(-2.7f)]
    public void AWallOnAnyBearingMatchesTheOracle(float bearing)
    {
        // A short wall placed on the given bearing, inside a cluttered window so the index is
        // actually built rather than skipped.
        int wx = 20 + (int)Math.Round(8.0 * Math.Cos(bearing));
        int wz = 20 + (int)Math.Round(8.0 * Math.Sin(bearing));

        AssertIdentical(VectorLightLayout.Grid(g => { g.Pillars(4); g.Wall(wx, wz, wx, wz + 2); }), 20.5f, 20.5f, 14f);
    }

    // A light pressed against a wall sees it subtending an enormous arc — approaching a half turn,
    // which is the case BucketRange gives up on and hands to every bucket. That branch is otherwise
    // unreachable from a realistic scene, and an untaken branch in a cull is a silent wrong answer.
    [Test]
    public void ALightHardAgainstAWallMatchesTheOracle()
    {
        AssertIdentical(VectorLightLayout.Grid(g => { g.Pillars(4); g.Wall(20, 21, 40, 21); }), 20.5f, 20.5f, 14f);
    }

    // Sub-cell segments off the integer grid: door leaves, which SegmentsAround appends alongside the
    // silhouette. Their endpoint angles are not shared with any wall corner, so they exercise the
    // arc arithmetic on inputs the grid never produces.
    [Test]
    public void PartlyOpenDoorLeavesMatchTheOracle()
    {
        List<VectorLightMath.Segment> segments = new List<VectorLightMath.Segment>(
            VectorLightLayout.Grid(g => { g.Pillars(4); g.Wall(26, 10, 26, 30); }));

        // Two leaves sliding apart in a doorway at (26, 20), on both faces, exactly as
        // VectorLightBlockers.AddDoorLeaves emits them.
        segments.Add(new VectorLightMath.Segment(26f, 20f, 26f, 20.35f));
        segments.Add(new VectorLightMath.Segment(26f, 20.65f, 26f, 21f));
        segments.Add(new VectorLightMath.Segment(27f, 20f, 27f, 20.35f));
        segments.Add(new VectorLightMath.Segment(27f, 20.65f, 27f, 21f));

        AssertIdentical(segments.ToArray(), 20.5f, 20.5f, 14f);
    }

    // ---- the sweep ---------------------------------------------------------------------------

    // Randomised layouts at a FIXED seed, which is the part of this fixture most likely to catch
    // something nobody thought of. Hand-written cases test the geometry somebody already suspected;
    // two hundred random ones test the arithmetic. The seed is fixed so a failure is reproducible
    // rather than a flake that vanishes on re-run.
    [Test]
    public void TwoHundredRandomLayoutsMatchTheOracle()
    {
        Random random = new Random(20260821);

        for (int trial = 0; trial < 200; trial++)
        {
            VectorLightLayout layout = new VectorLightLayout();
            int walls = random.Next(4, 40);

            for (int i = 0; i < walls; i++)
            {
                int x = random.Next(4, 37);
                int z = random.Next(4, 37);
                int length = random.Next(1, 9);

                if (random.Next(2) == 0)
                    layout.Wall(x, z, x, Math.Min(z + length, 39));
                else
                    layout.Wall(x, z, Math.Min(x + length, 39), z);
            }

            // The light lands anywhere, including inside a wall — which is legal here, because
            // SegmentsAround deliberately treats an emitter's own cell as open and a wall-mounted
            // lamp is exactly that case.
            float lightX = random.Next(8, 33) + 0.5f;
            float lightZ = random.Next(8, 33) + 0.5f;
            float radius = 4f + (float)random.NextDouble() * 12f;

            AssertIdentical(layout.Segments(), lightX, lightZ, radius, $"trial {trial}");
        }
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static void AssertIdentical(
        VectorLightMath.Segment[] segments, float lightX, float lightZ, float radius, string what = "")
    {
        VectorLightMath.LightPolygon actual =
            VectorLightMath.Build(lightX, lightZ, radius, segments, Rays);
        VectorLightMath.LightPolygon expected =
            VectorLightBuildOracle.Build(lightX, lightZ, radius, segments, Rays);

        Assert.That(actual.Count, Is.EqualTo(expected.Count), $"ray count {what}");

        for (int i = 0; i < expected.Count; i++)
        {
            // Exact equality on purpose — see the fixture header.
            Assert.That(actual.Angles[i], Is.EqualTo(expected.Angles[i]), $"angle {i} {what}");
            Assert.That(actual.Distances[i], Is.EqualTo(expected.Distances[i]), $"distance {i} {what}");
        }
    }
}
