namespace CelestialLighting.Tests;

// Offline unit tests for VectorLightMath.CoverageDelta — the comparison that decides whether a
// re-baked emitter obliges any section to regenerate at all.
//
// WHAT IS ACTUALLY AT RISK, and it is the same asymmetry SectionDirtyMathTests is built around. A
// delta that reports "changed" when the two grids agree costs performance and nothing else. A delta
// that reports "identical" when they differ leaves those cells rendering the PREVIOUS bake's
// shadow, with no exception, no log line and every probe healthy — because the section is never
// asked again. So the load-bearing test here is the brute-force differential, and the examples are
// there to say what the answer should look like rather than to establish it.
[TestFixture]
public class VectorLightCoverageDeltaTests
{
    private static byte[] Grid(int radiusCells, byte fill)
    {
        int span = radiusCells * 2 + 1;
        byte[] grid = new byte[span * span];

        for (int i = 0; i < grid.Length; i++)
        {
            grid[i] = fill;
        }

        return grid;
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(7)]
    [TestCase(14)]
    public void IdenticalGridsReportNoChange(int radiusCells)
    {
        byte[] previous = Grid(radiusCells, 255);
        byte[] current = Grid(radiusCells, 255);

        Assert.That(
            VectorLightMath.CoverageDelta(
                previous, current, radiusCells, out _, out _, out _, out _),
            Is.False);
    }

    // ONE BYTE IS ENOUGH, and the box it produces is that byte alone. A delta that rounded a single
    // changed cell up to the whole square would be correct and would also be exactly the waste this
    // exists to avoid, so the offsets are asserted rather than merely their non-emptiness.
    [TestCase(7, 0, 0)]
    [TestCase(7, 7, 7)]
    [TestCase(7, 14, 14)]
    [TestCase(7, 3, 11)]
    public void OneChangedCellReportsExactlyThatCell(int radiusCells, int xi, int zi)
    {
        int span = radiusCells * 2 + 1;
        byte[] previous = Grid(radiusCells, 255);
        byte[] current = Grid(radiusCells, 255);
        current[zi * span + xi] = 128;

        bool moved = VectorLightMath.CoverageDelta(
            previous, current, radiusCells,
            out int minX, out int minZ, out int maxX, out int maxZ);

        Assert.Multiple(() =>
        {
            Assert.That(moved, Is.True);
            Assert.That(minX, Is.EqualTo(xi));
            Assert.That(maxX, Is.EqualTo(xi));
            Assert.That(minZ, Is.EqualTo(zi));
            Assert.That(maxZ, Is.EqualTo(zi));
        });
    }

    // Two changed cells diagonal to each other, which is where tracking the two axes independently
    // is the difference between a box that contains both and one that misses a corner.
    [Test]
    public void TwoChangedCellsBoundBoth()
    {
        const int radiusCells = 7;
        int span = radiusCells * 2 + 1;
        byte[] previous = Grid(radiusCells, 255);
        byte[] current = Grid(radiusCells, 255);
        current[(2 * span) + 11] = 0;
        current[(9 * span) + 4] = 0;

        bool moved = VectorLightMath.CoverageDelta(
            previous, current, radiusCells,
            out int minX, out int minZ, out int maxX, out int maxZ);

        Assert.Multiple(() =>
        {
            Assert.That(moved, Is.True);
            Assert.That(minX, Is.EqualTo(4));
            Assert.That(maxX, Is.EqualTo(11));
            Assert.That(minZ, Is.EqualTo(2));
            Assert.That(maxZ, Is.EqualTo(9));
        });
    }

    // INCOMPARABLE MEANS "EVERYTHING CHANGED", never "nothing did". Each of these is a case the
    // adapter really routes here — a first bake with no previous grid, and a grid whose length
    // disagrees because the emitter's radius moved under it — and answering false for any of them
    // would leave a section rendering an emitter whose shape it has never seen.
    [Test]
    public void IncomparableGridsReportTheWholeSquare()
    {
        const int radiusCells = 5;
        int span = (radiusCells * 2) + 1;

        foreach ((byte[] previous, byte[] current) in new[]
        {
            (null!, Grid(radiusCells, 255)),
            (Grid(radiusCells, 255), null!),
            (Grid(radiusCells - 1, 255), Grid(radiusCells, 255)),
            (Grid(radiusCells + 1, 255), Grid(radiusCells, 255)),
        })
        {
            bool moved = VectorLightMath.CoverageDelta(
                previous, current, radiusCells,
                out int minX, out int minZ, out int maxX, out int maxZ);

            Assert.Multiple(() =>
            {
                Assert.That(moved, Is.True);
                Assert.That(minX, Is.EqualTo(0));
                Assert.That(minZ, Is.EqualTo(0));
                Assert.That(maxX, Is.EqualTo(span - 1));
                Assert.That(maxZ, Is.EqualTo(span - 1));
            });
        }
    }

    // THE DIFFERENTIAL, and the only test here that could catch an indexing error rather than a
    // logic one — a row stride read as a column would still pass every example above on a square
    // grid with a symmetric change in it.
    //
    // THE ORACLE IS INDEPENDENT, per the rule this repo learned the hard way: it scans the two grids
    // cell by cell with its own indexing and its own min/max, so it is not the code under test with
    // the loops renamed. A quarter of the cells are perturbed rather than all of them, so runs of
    // changed and unchanged cells interleave the way a moved shadow's do instead of every trial
    // reducing to "the whole square".
    [TestCase(3, 11)]
    [TestCase(7, 29)]
    [TestCase(12, 71)]
    public void DeltaAgreesWithABruteForceScan(int radiusCells, int seed)
    {
        int span = (radiusCells * 2) + 1;
        Random random = new Random(seed);

        for (int trial = 0; trial < 200; trial++)
        {
            byte[] previous = new byte[span * span];
            byte[] current = new byte[span * span];

            for (int i = 0; i < previous.Length; i++)
            {
                previous[i] = (byte)random.Next(256);
                current[i] = random.Next(4) == 0 ? (byte)random.Next(256) : previous[i];
            }

            int expectMinX = span;
            int expectMinZ = span;
            int expectMaxX = -1;
            int expectMaxZ = -1;

            for (int zi = 0; zi < span; zi++)
            {
                for (int xi = 0; xi < span; xi++)
                {
                    if (previous[(zi * span) + xi] != current[(zi * span) + xi])
                    {
                        expectMinX = Math.Min(expectMinX, xi);
                        expectMaxX = Math.Max(expectMaxX, xi);
                        expectMinZ = Math.Min(expectMinZ, zi);
                        expectMaxZ = Math.Max(expectMaxZ, zi);
                    }
                }
            }

            bool moved = VectorLightMath.CoverageDelta(
                previous, current, radiusCells,
                out int minX, out int minZ, out int maxX, out int maxZ);

            Assert.That(moved, Is.EqualTo(expectMaxX >= 0), $"trial {trial}");

            if (expectMaxX >= 0)
            {
                Assert.Multiple(() =>
                {
                    Assert.That(minX, Is.EqualTo(expectMinX), $"trial {trial} minX");
                    Assert.That(maxX, Is.EqualTo(expectMaxX), $"trial {trial} maxX");
                    Assert.That(minZ, Is.EqualTo(expectMinZ), $"trial {trial} minZ");
                    Assert.That(maxZ, Is.EqualTo(expectMaxZ), $"trial {trial} maxZ");
                });
            }
        }
    }
}
