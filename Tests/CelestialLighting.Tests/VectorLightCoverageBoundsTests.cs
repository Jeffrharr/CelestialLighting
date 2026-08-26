using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CelestialLighting.Tests;

// The one claim the ray-extreme bounds in VectorLightMath.BuildCoverage make: they remove work and
// change nothing.
//
// WHY THIS IS A WHOLE FIXTURE, on the same reasoning as VectorLightBuildCullTests. A coverage grid
// that is subtly wrong does not throw and moves no probe anybody pins. It renders as a cell of a
// shadow a shade too light or too dark somewhere in one emitter's square, and the mask that consumes
// it runs inside a section Regenerate, which swallows exceptions and leaves the frame looking
// vanilla with everything green. If the equivalence is not established here it is not established.
//
// EVERY ASSERTION IS BIT-FOR-BIT. The argument for the bounds is that a cell they answer is one the
// sampler would have answered identically, so there is no rounding to be tolerant of. A tolerance
// would accept a defect exactly where one is most likely — the rim, where cells straddle the
// polygon's edge and the bounds stop being able to answer.
//
// THE SCENES ARE THE POLYGON FIXTURE'S SCENES, via the shared VectorLightLayout. Coverage is the
// stage after Build in the same bake, and reusing the population means a geometry that catches
// something in one stage is not silently untested in the other.
//
// The oracle (VectorLightCoverageOracle) is a verbatim transcription of the pre-bounds loop. See its
// header for why it is a transcription rather than a fresh rewrite.
[TestFixture]
public class VectorLightCoverageBoundsTests
{
    private const int Rays = VectorLightMath.DefaultBaseRayCount;
    private const int Samples = VectorLightMath.DefaultCoverageSamples;

    // ---- the two cases each bound exists for ---------------------------------------------------

    // NOTHING IN THE WAY, which is the case the nearest-ray bound is for: every ray reaches the
    // radius, so every cell inside the circle takes the fully-lit path and only the rim and the
    // corners are sampled. It is also the case where a wrong bound would be least visible, because
    // almost every cell it touches is one nobody expects a shadow in.
    [Test]
    public void AnUnobstructedEmitterMatchesTheOracle()
    {
        AssertIdentical(VectorLightLayout.Grid(g => { }), 20.5f, 20.5f, 14f);
    }

    // SEALED IN A SMALL ROOM, the opposite extreme: the nearest ray is a couple of cells away, so
    // the lit bound answers almost nothing and the whole grid falls through to the sampler bar the
    // corners. This is the case that would hide a broken fast path by never taking it, which is why
    // it is here rather than only the open one.
    [Test]
    public void AnEmitterSealedInASmallRoomMatchesTheOracle()
    {
        AssertIdentical(VectorLightLayout.Grid(g => g.Wall(17, 17, 23, 23)), 20.5f, 20.5f, 14f);
    }

    // ---- the polygon fixture's scenes ----------------------------------------------------------

    [Test]
    public void ASingleWallMatchesTheOracle()
    {
        AssertIdentical(VectorLightLayout.Grid(g => g.Wall(20, 12, 20, 28)), 14.5f, 20.5f, 14f);
    }

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

    [TestCase(0f)]
    [TestCase((float)Math.PI)]
    [TestCase((float)(Math.PI / 2.0))]
    [TestCase((float)(-Math.PI / 2.0))]
    [TestCase(2.7f)]
    [TestCase(-2.7f)]
    public void AWallOnAnyBearingMatchesTheOracle(float bearing)
    {
        int wx = 20 + (int)Math.Round(8.0 * Math.Cos(bearing));
        int wz = 20 + (int)Math.Round(8.0 * Math.Sin(bearing));

        AssertIdentical(
            VectorLightLayout.Grid(g => { g.Pillars(4); g.Wall(wx, wz, wx, wz + 2); }), 20.5f, 20.5f, 14f);
    }

    [Test]
    public void PartlyOpenDoorLeavesMatchTheOracle()
    {
        List<VectorLightMath.Segment> segments = new List<VectorLightMath.Segment>(
            VectorLightLayout.Grid(g => { g.Pillars(4); g.Wall(26, 10, 26, 30); }));

        segments.Add(new VectorLightMath.Segment(26f, 20f, 26f, 20.35f));
        segments.Add(new VectorLightMath.Segment(26f, 20.65f, 26f, 21f));
        segments.Add(new VectorLightMath.Segment(27f, 20f, 27f, 20.35f));
        segments.Add(new VectorLightMath.Segment(27f, 20.65f, 27f, 21f));

        AssertIdentical(segments.ToArray(), 20.5f, 20.5f, 14f);
    }

    // ---- the arguments that are not the geometry -----------------------------------------------

    // The grid is stored over ceil(radius), so a fractional radius leaves the circle sitting inside
    // the square by a fraction of a cell rather than touching it — which is where an off-by-one in
    // either bound stops being invisible. Swept rather than spot-checked because the interesting
    // part is the fraction, not the size.
    [TestCase(4.0f)]
    [TestCase(4.3f)]
    [TestCase(7.5f)]
    [TestCase(10.9f)]
    [TestCase(14.0f)]
    public void AnyRadiusMatchesTheOracle(float radius)
    {
        AssertIdentical(VectorLightLayout.Grid(g => g.Pillars(5)), 20.5f, 20.5f, radius);
    }

    // The sample count decides where inside a cell the extreme samples sit, and the bounds are
    // derived from exactly those two positions. One sample per axis is the degenerate case where the
    // two coincide; the default is two; higher counts move them towards the cell edges.
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    public void AnySampleCountMatchesTheOracle(int samples)
    {
        AssertIdentical(
            VectorLightLayout.Grid(VectorLightLayout.RoomBlock), 20.5f, 20.5f, 14f, samples, $"{samples} samples");
    }

    // Zero samples produces an all-zero grid in both, and the shipped one returns it before dividing
    // by the count. A guard nobody exercises is a guard that gets deleted.
    [Test]
    public void NoSamplesMatchesTheOracle()
    {
        AssertIdentical(VectorLightLayout.Grid(g => g.Pillars(5)), 20.5f, 20.5f, 14f, 0, "no samples");
    }

    // THE STRADDLE CASE IN AxisSpan, which nothing else in this fixture can reach.
    //
    // A cell in the light's own row or column spans the light on one axis, so its nearest sample is
    // in the middle of the span rather than at either end. Bounding it by the nearer endpoint
    // instead would overstate how far away the cell is, and an overstated near bound reports a cell
    // fully unlit that the sampler finds partly lit.
    //
    // It only bites in a radius window a few thousandths of a cell wide — the difference between the
    // straddling axis contributing nothing and contributing half a sample pitch — and only at ODD
    // sample counts, where a sample sits exactly on the axis and the true nearest distance is
    // attained rather than merely bounded. At even counts the four samples sit off-axis in both
    // directions, the loose bound stays below every one of them, and the defect is invisible. Hence
    // the fine step and the odd counts: coarser or evener and this passes against a broken bound.
    [TestCase(3, 9.6f, 9.68f)]
    [TestCase(5, 9.6f, 9.68f)]
    [TestCase(3, 13.66f, 13.68f)]
    public void ACellInTheLightsOwnColumnMatchesTheOracle(int samples, float from, float to)
    {
        for (float radius = from; radius <= to; radius += 0.0005f)
            AssertIdentical(
                VectorLightLayout.Grid(g => { }), 20.5f, 20.5f, radius, samples, $"radius {radius}");
    }

    // ---- the sweep ---------------------------------------------------------------------------

    // Randomised layouts at a FIXED seed, for the reason the polygon fixture sweeps them: the
    // hand-written cases test the geometry somebody already suspected, and these test the
    // arithmetic. The light lands anywhere, including inside a wall, where the nearest ray goes to
    // roughly zero and the lit bound can answer nothing at all.
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

            float lightX = random.Next(8, 33) + 0.5f;
            float lightZ = random.Next(8, 33) + 0.5f;
            float radius = 4f + (float)random.NextDouble() * 12f;

            AssertIdentical(layout.Segments(), lightX, lightZ, radius, Samples, $"trial {trial}");
        }
    }

    // ---- the scratch buffers -------------------------------------------------------------------

    // A REUSED SCRATCH MUST ANSWER WHAT A FRESH ONE WOULD, and the case that would catch it failing
    // is a big emitter followed by a small one: the arrays are grown and never shrunk, so the second
    // bake reads buffers still holding the first bake's numbers past its own span and ray count.
    // Nothing clears them — the claim is that the classification pass overwrites every entry it goes
    // on to read — and a stale byte here would render as one cell of one shadow at the wrong depth,
    // which no probe in the repo pins and no screenshot would separate from noise.
    //
    // Descending sizes, and both of the two dimensions that grow independently: `radiusCells` sizes
    // the column and row arrays, while the ray count sizes the sine and cosine arrays and is set by
    // how much wall the emitter can see, not by its radius.
    [Test]
    public void AReusedScratchMatchesAFreshOne()
    {
        VectorLightMath.CoverageScratch shared = new VectorLightMath.CoverageScratch();

        (int cellX, int cellZ, float radius, Action<VectorLightLayout> build)[] bakes =
        {
            (20, 20, 14f, g => g.Pillars(3)),
            (20, 20, 4f, g => g.Wall(17, 17, 23, 23)),
            (20, 20, 12f, VectorLightLayout.RoomBlock),
            (20, 20, 3f, g => { }),
            (20, 20, 14f, g => g.Wall(20, 12, 20, 28)),
        };

        for (int i = 0; i < bakes.Length; i++)
        {
            (int cellX, int cellZ, float radius, Action<VectorLightLayout> build) bake = bakes[i];

            VectorLightMath.LightPolygon polygon = VectorLightMath.Build(
                bake.cellX + 0.5f, bake.cellZ + 0.5f, bake.radius,
                VectorLightLayout.Grid(bake.build), Rays);

            int radiusCells = (int)Math.Ceiling(bake.radius);

            byte[] reused = VectorLightMath.BuildCoverage(
                polygon, bake.cellX, bake.cellZ, radiusCells, Samples, shared);

            // A scratch of its own, so the comparison is against a buffer that cannot be carrying
            // anything — the same argument the oracle makes about the algorithm.
            byte[] fresh = VectorLightMath.BuildCoverage(
                polygon, bake.cellX, bake.cellZ, radiusCells, Samples,
                new VectorLightMath.CoverageScratch());

            Assert.That(reused, Is.EqualTo(fresh), $"bake {i} (radius {bake.radius})");
        }
    }

    // THE HAZARD THE THREADED BAKE INTRODUCES, tested at the only level it can be: the pure core.
    // VectorLightField.BakeSelected hands a batch of emitters to Parallel.For, and every one of them
    // calls BuildCoverage. If two of those shared a scratch their writes would interleave inside the
    // same column and row arrays, and the result would not be a crash — it would be a handful of
    // wrong bytes in a coverage grid, i.e. one cell of one shadow at the wrong depth, which nothing
    // downstream validates. The field answers that with a [ThreadStatic] scratch; this asserts the
    // arrangement actually produces serial answers.
    //
    // A REAL Parallel.For RATHER THAN A SIMULATION, because the thing under test is whether
    // thread-local ownership holds when the pool decides how to schedule, and a hand-rolled loop
    // over N fake "workers" would be testing the loop. Enough emitters and enough repeats that the
    // pool has to reuse threads, which is the case where a leaked buffer would show.
    [Test]
    public void ConcurrentBakesMatchSerialOnes()
    {
        (int cellX, int cellZ, float radius, Action<VectorLightLayout> build)[] scenes =
        {
            (20, 20, 14f, g => g.Pillars(3)),
            (20, 20, 9f, VectorLightLayout.RoomBlock),
            (20, 20, 4f, g => g.Wall(17, 17, 23, 23)),
            (14, 20, 14f, g => g.Wall(20, 12, 20, 28)),
            (20, 20, 12f, g => g.Pillars(5)),
            (20, 20, 6f, g => { }),
        };

        VectorLightMath.LightPolygon[] polygons = new VectorLightMath.LightPolygon[scenes.Length];
        byte[][] serial = new byte[scenes.Length][];

        for (int i = 0; i < scenes.Length; i++)
        {
            polygons[i] = VectorLightMath.Build(
                scenes[i].cellX + 0.5f, scenes[i].cellZ + 0.5f, scenes[i].radius,
                VectorLightLayout.Grid(scenes[i].build), Rays);

            serial[i] = VectorLightMath.BuildCoverage(
                polygons[i], scenes[i].cellX, scenes[i].cellZ,
                (int)Math.Ceiling(scenes[i].radius), Samples,
                new VectorLightMath.CoverageScratch());
        }

        // The field's ownership rule, reproduced: one scratch per thread, created on first use.
        ThreadLocal<VectorLightMath.CoverageScratch> scratch =
            new ThreadLocal<VectorLightMath.CoverageScratch>(
                () => new VectorLightMath.CoverageScratch());

        const int Repeats = 40;
        byte[][] threaded = new byte[scenes.Length * Repeats][];

        Parallel.For(0, scenes.Length * Repeats, job =>
        {
            int i = job % scenes.Length;

            threaded[job] = VectorLightMath.BuildCoverage(
                polygons[i], scenes[i].cellX, scenes[i].cellZ,
                (int)Math.Ceiling(scenes[i].radius), Samples, scratch.Value!);
        });

        for (int job = 0; job < threaded.Length; job++)
            Assert.That(threaded[job], Is.EqualTo(serial[job % scenes.Length]), $"job {job}");
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static void AssertIdentical(
        VectorLightMath.Segment[] segments, float lightX, float lightZ, float radius,
        int samples = Samples, string what = "")
    {
        VectorLightMath.LightPolygon polygon =
            VectorLightMath.Build(lightX, lightZ, radius, segments, Rays);

        // The cell the emitter stands on, the way VectorLightField derives both from the same
        // position — so the grid under test is the one the game would bake, offsets included.
        int cellX = (int)Math.Floor(lightX);
        int cellZ = (int)Math.Floor(lightZ);
        int radiusCells = (int)Math.Ceiling(radius);

        byte[] actual = VectorLightMath.BuildCoverage(polygon, cellX, cellZ, radiusCells, samples);
        byte[] expected = VectorLightCoverageOracle.BuildCoverage(polygon, cellX, cellZ, radiusCells, samples);

        Assert.That(actual.Length, Is.EqualTo(expected.Length), $"grid size {what}");

        int span = radiusCells * 2 + 1;

        for (int i = 0; i < expected.Length; i++)
        {
            // Exact equality on purpose — see the fixture header. The index is reported as a cell
            // offset because "byte 407 differs" says nothing about where the defect is.
            Assert.That(
                actual[i], Is.EqualTo(expected[i]),
                $"cell ({i % span - radiusCells}, {i / span - radiusCells}) {what}");
        }
    }
}
