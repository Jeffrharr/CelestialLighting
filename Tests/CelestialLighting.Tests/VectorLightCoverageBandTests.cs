using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CelestialLighting.Tests;

// The one claim VectorLightMath.CoverageRows makes: a grid filled a band of rows at a time is the
// same grid, whatever the bands are and whoever fills them.
//
// WHY IT IS A FIXTURE OF ITS OWN rather than three more cases in VectorLightCoverageBoundsTests.
// That fixture asks whether the ray-extreme bounds answer what the sampler would; this one asks
// whether the DECOMPOSITION is sound. They fail differently and they would be fixed differently: a
// broken bound is wrong in every arm alike and shows up in the serial path too, while a broken band
// is wrong only when somebody splits — so a shipped build with the threading flag off would pass
// every assertion in the other file while the on arm quietly produced a different shadow.
//
// EVERY ASSERTION IS BIT-FOR-BIT, for the same reason as the bounds fixture: the claim is identity,
// not similarity, so there is no rounding to be tolerant of. A tolerance would accept a defect
// exactly at a band SEAM, which is the one place this decomposition can go wrong and the one place
// a row's arithmetic would have to have depended on the row before it.
//
// THE ORACLE IS THE COMPARISON THAT COUNTS. Banded output against the whole-grid BuildCoverage call
// is worth having — it is the arm the shipped flag switches between — but both sides of it now run
// CoverageRows, so on its own it asserts x - x == 0 and would stay green if the row loop itself
// broke. VectorLightCoverageOracle shares none of that code (see its header), so every case here
// that can reach it does.
[TestFixture]
public class VectorLightCoverageBandTests
{
    private const int Rays = VectorLightMath.DefaultBaseRayCount;
    private const int Samples = VectorLightMath.DefaultCoverageSamples;

    // ---- the splits ----------------------------------------------------------------------------

    // The band counts a fan-out would actually choose, plus the two degenerate ends. One band is the
    // serial path and must be bit-identical to it by construction; `span` bands is one row each,
    // which is the split with the most seams and therefore the most chances for a row to have
    // depended on its predecessor.
    //
    // The counts that do not divide the span evenly are the point of the odd numbers: a partition
    // whose last band is shorter than the others is the shape a `(span + n - 1) / n` chunk size
    // produces, and the last band is where an off-by-one lands.
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(7)]
    [TestCase(8)]
    [TestCase(29)]
    public void AnyNumberOfBandsMatchesTheOracle(int bands)
    {
        AssertBandedMatchesOracle(
            VectorLightLayout.Grid(VectorLightLayout.RoomBlock), 20.5f, 20.5f, 14f, bands);
    }

    // MORE BANDS THAN ROWS, which is not a hypothetical: a fan-out that divides the span by the
    // processor count on a 32-thread machine hands a radius-3 emitter (span 7) more bands than it
    // has rows, and the surplus ones come out with rowStart >= rowEnd. They must be no-ops rather
    // than an exception from inside a pool thread — see CoverageRows' clamp.
    [TestCase(40)]
    [TestCase(200)]
    public void MoreBandsThanRowsMatchesTheOracle(int bands)
    {
        AssertBandedMatchesOracle(VectorLightLayout.Grid(g => g.Pillars(3)), 20.5f, 20.5f, 3f, bands);
    }

    // ---- the scenes ----------------------------------------------------------------------------

    // The bounds fixture's own population, banded. Coverage is the stage after Build in one bake and
    // these are the geometries the polygon fixture sweeps, so a scene that catches something in one
    // stage is not silently untested in the other.
    //
    // THE TWO EXTREMES ARE THE ONES THAT MATTER HERE. An unobstructed emitter answers nearly every
    // cell from the nearest-ray bound and never builds a ray fan at all, so its bands exercise the
    // path where `fanBuilt` stays false; a lamp sealed in a small room falls through to the sampler
    // almost everywhere, so every one of its bands builds a fan of its own. The second is what would
    // catch a fan wrongly hoisted out of the band and shared across threads.
    [Test]
    public void AnUnobstructedEmitterMatchesTheOracleInBands()
    {
        AssertBandedMatchesOracle(VectorLightLayout.Grid(g => { }), 20.5f, 20.5f, 14f, 4);
    }

    [Test]
    public void AnEmitterSealedInASmallRoomMatchesTheOracleInBands()
    {
        AssertBandedMatchesOracle(
            VectorLightLayout.Grid(g => g.Wall(17, 17, 23, 23)), 20.5f, 20.5f, 14f, 4);
    }

    [Test]
    public void ASingleWallMatchesTheOracleInBands()
    {
        AssertBandedMatchesOracle(
            VectorLightLayout.Grid(g => g.Wall(20, 12, 20, 28)), 14.5f, 20.5f, 14f, 4);
    }

    [Test]
    public void FreeStandingPillarsMatchTheOracleInBands()
    {
        AssertBandedMatchesOracle(VectorLightLayout.Grid(g => g.Pillars(3)), 20.5f, 20.5f, 14f, 4);
    }

    // A door caught mid-swing, which is the scene the whole change is for: the door frame is where
    // BuildCoverage's worst case was measured, and a partly-open leaf is the geometry that produces
    // the narrow wedges a band seam could cut through.
    [Test]
    public void APartlyOpenDoorMatchesTheOracleInBands()
    {
        List<VectorLightMath.Segment> segments = new List<VectorLightMath.Segment>(
            VectorLightLayout.Grid(g => { g.Pillars(4); g.Wall(26, 10, 26, 30); }));

        segments.Add(new VectorLightMath.Segment(26f, 20f, 26f, 20.35f));
        segments.Add(new VectorLightMath.Segment(26f, 20.65f, 26f, 21f));
        segments.Add(new VectorLightMath.Segment(27f, 20f, 27f, 20.35f));
        segments.Add(new VectorLightMath.Segment(27f, 20.65f, 27f, 21f));

        AssertBandedMatchesOracle(segments.ToArray(), 20.5f, 20.5f, 14f, 5);
    }

    // ---- the arguments that are not the geometry -----------------------------------------------

    // A fractional radius leaves the circle sitting inside its square by a fraction of a cell, which
    // is where an off-by-one in the row range stops being invisible — the last band of a grid whose
    // bottom rows are entirely outside the circle would look correct while writing nothing.
    [TestCase(4.0f)]
    [TestCase(4.3f)]
    [TestCase(7.5f)]
    [TestCase(10.9f)]
    [TestCase(14.0f)]
    public void AnyRadiusMatchesTheOracleInBands(float radius)
    {
        AssertBandedMatchesOracle(VectorLightLayout.Grid(g => g.Pillars(5)), 20.5f, 20.5f, radius, 3);
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    public void AnySampleCountMatchesTheOracleInBands(int samples)
    {
        AssertBandedMatchesOracle(
            VectorLightLayout.Grid(VectorLightLayout.RoomBlock), 20.5f, 20.5f, 14f, 4, samples,
            $"{samples} samples");
    }

    // The guards, banded. Each returns before writing anything and must leave the caller's grid as
    // it found it — which for a freshly allocated one is all zeroes, and is what BuildCoverage
    // documents itself as returning in both cases.
    [Test]
    public void NoSamplesMatchesTheOracleInBands()
    {
        AssertBandedMatchesOracle(
            VectorLightLayout.Grid(g => g.Pillars(5)), 20.5f, 20.5f, 14f, 4, 0, "no samples");
    }

    [Test]
    public void AnEmptyPolygonWritesNothing()
    {
        VectorLightMath.LightPolygon empty = default;
        byte[] grid = new byte[VectorLightMath.CoverageCellCount(6)];

        for (int i = 0; i < grid.Length; i++)
            grid[i] = 7;

        VectorLightMath.CoverageRows(empty, 20, 20, 6, Samples, grid, 0, 13);

        // Untouched rather than zeroed. The caller owns the array, so a guard that cleared it would
        // be making a decision about somebody else's buffer — and BuildCoverage, which allocates its
        // own, gets the zeroes it promises from `new byte[]`.
        Assert.That(grid, Is.All.EqualTo((byte)7));
    }

    // ---- the band range itself ------------------------------------------------------------------

    // A BAND WRITES ITS OWN ROWS AND NOTHING ELSE, which is the property that makes a partition
    // safe. Without it two concurrent bands would race on rows neither was given, and the damage
    // would be a few bytes in one shadow rather than anything that throws.
    //
    // Tested by filling the grid with a sentinel first: a band that wrote outside its range would
    // replace a sentinel with a coverage byte, and a coverage byte of exactly the sentinel value in
    // exactly the wrong row is the only way this passes wrongly.
    [Test]
    public void ABandLeavesEveryOtherRowAlone()
    {
        VectorLightMath.LightPolygon polygon = PolygonFor(
            VectorLightLayout.Grid(VectorLightLayout.RoomBlock), 20.5f, 20.5f, 14f);

        int radiusCells = 14;
        int span = VectorLightMath.CoverageSpan(radiusCells);
        byte[] grid = new byte[span * span];

        const byte Sentinel = 0xAB;

        for (int i = 0; i < grid.Length; i++)
            grid[i] = Sentinel;

        const int From = 9;
        const int To = 17;

        VectorLightMath.CoverageRows(polygon, 20, 20, radiusCells, Samples, grid, From, To);

        byte[] whole = VectorLightMath.BuildCoverage(polygon, 20, 20, radiusCells, Samples);

        for (int zi = 0; zi < span; zi++)
        {
            bool inBand = zi >= From && zi < To;

            for (int xi = 0; xi < span; xi++)
            {
                int i = zi * span + xi;

                Assert.That(
                    grid[i], Is.EqualTo(inBand ? whole[i] : Sentinel),
                    $"cell ({xi - radiusCells}, {zi - radiusCells}), row {zi}");
            }
        }
    }

    // The ranges a caller can hand in by accident, each of which must be a no-op rather than an
    // exception. Negative and past-the-end are the two halves of the clamp; the inverted and empty
    // pairs are what the last band of an over-divided partition looks like.
    [TestCase(-5, 0)]
    [TestCase(0, 0)]
    [TestCase(13, 13)]
    [TestCase(17, 4)]
    [TestCase(29, 40)]
    [TestCase(-3, -1)]
    public void AnEmptyOrOutOfRangeBandWritesNothing(int from, int to)
    {
        VectorLightMath.LightPolygon polygon = PolygonFor(
            VectorLightLayout.Grid(g => g.Pillars(3)), 20.5f, 20.5f, 14f);

        byte[] grid = new byte[VectorLightMath.CoverageCellCount(14)];

        for (int i = 0; i < grid.Length; i++)
            grid[i] = 0x5C;

        VectorLightMath.CoverageRows(polygon, 20, 20, 14, Samples, grid, from, to);

        Assert.That(grid, Is.All.EqualTo((byte)0x5C), $"rows [{from}, {to})");
    }

    // A range that runs off the END must still fill the rows it legitimately covers, which the
    // clamp does and an outright early return would not. This is the partition a caller writes as
    // `band * chunk` to `(band + 1) * chunk` without a final `Math.Min`.
    [Test]
    public void ABandRunningPastTheEndStillFillsTheRowsItCovers()
    {
        VectorLightMath.LightPolygon polygon = PolygonFor(
            VectorLightLayout.Grid(g => g.Pillars(3)), 20.5f, 20.5f, 14f);

        int span = VectorLightMath.CoverageSpan(14);
        byte[] grid = new byte[span * span];

        VectorLightMath.CoverageRows(polygon, 20, 20, 14, Samples, grid, 0, span + 11);

        Assert.That(grid, Is.EqualTo(VectorLightMath.BuildCoverage(polygon, 20, 20, 14, Samples)));
    }

    // ---- the sweep -------------------------------------------------------------------------------

    // Randomised layouts at a FIXED seed, on the bounds fixture's own reasoning: the cases above
    // test the partitions somebody already suspected, and these test the arithmetic. The band count
    // is randomised alongside the geometry so that no single split is the only one a given scene
    // ever sees.
    [Test]
    public void TwoHundredRandomLayoutsMatchTheOracleInBands()
    {
        Random random = new Random(20260911);

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
            int bands = random.Next(1, 12);

            AssertBandedMatchesOracle(
                layout.Segments(), lightX, lightZ, radius, bands, Samples,
                $"trial {trial}, {bands} bands");
        }
    }

    // ---- the hazard the fan-out introduces ---------------------------------------------------------

    // BANDS OF ONE GRID RUNNING AT ONCE, which is what VectorLightField does under
    // `vector_light_parallel_coverage` and the thing no single-threaded assertion above can reach.
    // Two bands sharing a scratch would interleave their writes into the same column, row and ray
    // arrays; the result is not a crash but a handful of wrong bytes in one emitter's grid, which
    // renders as a cell of a shadow at the wrong depth and which nothing downstream validates.
    //
    // A REAL Parallel.For RATHER THAN A SIMULATION, for the reason the bounds fixture gives: what is
    // under test is whether per-band ownership holds when the pool decides the schedule, and a
    // hand-rolled loop over N fake workers would be testing the loop. Repeated enough that the pool
    // has to reuse threads, which is the case where a leaked buffer shows.
    [Test]
    public void ConcurrentBandsOfOneGridMatchTheSerialGrid()
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

        // The field's ownership rule, reproduced: one scratch per thread, created on first use.
        ThreadLocal<VectorLightMath.CoverageScratch> scratch =
            new ThreadLocal<VectorLightMath.CoverageScratch>(
                () => new VectorLightMath.CoverageScratch());

        const int Repeats = 12;

        for (int repeat = 0; repeat < Repeats; repeat++)
        {
            for (int i = 0; i < scenes.Length; i++)
            {
                (int cellX, int cellZ, float radius, Action<VectorLightLayout> build) scene = scenes[i];

                VectorLightMath.LightPolygon polygon = VectorLightMath.Build(
                    scene.cellX + 0.5f, scene.cellZ + 0.5f, scene.radius,
                    VectorLightLayout.Grid(scene.build), Rays);

                int radiusCells = (int)Math.Ceiling(scene.radius);
                int span = VectorLightMath.CoverageSpan(radiusCells);

                byte[] serial = VectorLightMath.BuildCoverage(
                    polygon, scene.cellX, scene.cellZ, radiusCells, Samples,
                    new VectorLightMath.CoverageScratch());

                byte[] threaded = new byte[span * span];

                // One band per ROW, which is the finest partition there is and therefore the one
                // with the most seams for the pool to schedule across.
                Parallel.For(0, span, zi =>
                {
                    VectorLightMath.CoverageRows(
                        polygon, scene.cellX, scene.cellZ, radiusCells, Samples, threaded,
                        zi, zi + 1, scratch.Value!);
                });

                Assert.That(threaded, Is.EqualTo(serial), $"repeat {repeat}, scene {i}");
            }
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------

    // Fill a grid in `bands` contiguous row ranges and assert it against the oracle, cell by cell.
    //
    // THE SCRATCH IS SHARED ACROSS THE BANDS HERE, deliberately, and the threaded case above is
    // where it is not. Serially there is only ever one band in flight, so sharing is what the
    // shipped serial path does and testing it with a fresh scratch per band would test an
    // arrangement nothing uses. It also makes the reuse hazard live in every case in this fixture:
    // the arrays are grown and never shrunk, so band 2 reads buffers still holding band 1's numbers
    // past its own span.
    private static void AssertBandedMatchesOracle(
        VectorLightMath.Segment[] segments, float lightX, float lightZ, float radius, int bands,
        int samples = Samples, string what = "")
    {
        VectorLightMath.LightPolygon polygon = VectorLightMath.Build(lightX, lightZ, radius, segments, Rays);

        // The cell the emitter stands on, the way VectorLightField derives both from the same
        // position — so the grid under test is the one the game would bake, offsets included.
        int cellX = (int)Math.Floor(lightX);
        int cellZ = (int)Math.Floor(lightZ);
        int radiusCells = (int)Math.Ceiling(radius);
        int span = VectorLightMath.CoverageSpan(radiusCells);

        byte[] banded = new byte[span * span];
        VectorLightMath.CoverageScratch scratch = new VectorLightMath.CoverageScratch();

        // The chunking a fan-out writes: a ceiling divide, so the last band is the short one and
        // every band but the last is the same length.
        int chunk = (span + bands - 1) / bands;

        for (int band = 0; band < bands; band++)
        {
            VectorLightMath.CoverageRows(
                polygon, cellX, cellZ, radiusCells, samples, banded,
                band * chunk, (band + 1) * chunk, scratch);
        }

        byte[] expected =
            VectorLightCoverageOracle.BuildCoverage(polygon, cellX, cellZ, radiusCells, samples);

        Assert.That(banded.Length, Is.EqualTo(expected.Length), $"grid size {what}");

        for (int i = 0; i < expected.Length; i++)
        {
            // Exact equality on purpose — see the fixture header. The index is reported as a cell
            // offset because "byte 407 differs" says nothing about where the defect is.
            Assert.That(
                banded[i], Is.EqualTo(expected[i]),
                $"cell ({i % span - radiusCells}, {i / span - radiusCells}), {bands} bands {what}");
        }

        // The arm the feature flag actually switches between, asserted second because on its own it
        // would be the same code on both sides. The oracle above is what makes it evidence.
        Assert.That(
            banded, Is.EqualTo(VectorLightMath.BuildCoverage(polygon, cellX, cellZ, radiusCells, samples)),
            $"against the whole-grid call, {bands} bands {what}");
    }

    private static VectorLightMath.LightPolygon PolygonFor(
        VectorLightMath.Segment[] segments, float lightX, float lightZ, float radius)
    {
        return VectorLightMath.Build(lightX, lightZ, radius, segments, Rays);
    }
}
