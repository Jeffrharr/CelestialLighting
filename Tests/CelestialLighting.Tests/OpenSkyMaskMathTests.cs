using System.Collections.Generic;

namespace CelestialLighting.Tests;

/// <summary>
/// Offline coverage for OpenSkyMaskMath.cs — §24's roof mask (issue #90). No RimWorld/Unity
/// assembly required.
///
/// The mask decides which cells the glare quad covers, and its failure mode is quiet: an off-by-one
/// leaks glare one cell under the eaves, and a dropped run leaves a dark stripe across open ground.
/// Neither throws, and both are hard to see in a screenshot of a 250x250 map, so the boundaries are
/// pinned here rather than left to the live A/B.
/// </summary>
[TestFixture]
public class OpenSkyMaskMathTests
{
    // Builds a grid from row strings, '#' roofed and '.' open, top row first in the array's own
    // order — so the literal in each test reads as a little map.
    private static bool[] Grid(params string[] rows)
    {
        int width = rows[0].Length;
        bool[] roofed = new bool[width * rows.Length];
        for (int z = 0; z < rows.Length; z++)
        {
            for (int x = 0; x < width; x++)
                roofed[z * width + x] = rows[z][x] == '#';
        }

        return roofed;
    }

    [Test]
    public void AnOpenMap_IsOneFullWidthRunPerRow_AndReportsAsWholeMap()
    {
        List<OpenSkyMaskMath.Run> runs = OpenSkyMaskMath.UnroofedRuns(
            Grid("....", "....", "...."), 4, 3);

        Assert.That(runs.Count, Is.EqualTo(3), "one maximal run per row");
        foreach (OpenSkyMaskMath.Run run in runs)
        {
            Assert.That(run.XStart, Is.EqualTo(0));
            Assert.That(run.XEnd, Is.EqualTo(3));
            Assert.That(run.Width, Is.EqualTo(4));
        }

        Assert.That(OpenSkyMaskMath.CoversWholeMap(runs, 4, 3), Is.True,
            "nothing roofed, so the caller should fall back to the shared whole-map plane");
    }

    // THE BOUNDARY THAT LEAKS GLARE UNDER THE EAVES IF IT IS WRONG. A run ending before a roofed cell
    // must stop at the cell BEFORE it, and a run starting after one must start at the cell AFTER.
    [Test]
    public void ARoofInTheMiddle_SplitsTheRow_WithNoCellLeakingOnEitherSide()
    {
        List<OpenSkyMaskMath.Run> runs = OpenSkyMaskMath.UnroofedRuns(
            Grid("..##.."), 6, 1);

        Assert.That(runs.Count, Is.EqualTo(2));

        Assert.That(runs[0].XStart, Is.EqualTo(0));
        Assert.That(runs[0].XEnd, Is.EqualTo(1), "the run must stop one cell short of the roof");

        Assert.That(runs[1].XStart, Is.EqualTo(4), "and resume one cell past it");
        Assert.That(runs[1].XEnd, Is.EqualTo(5));

        Assert.That(OpenSkyMaskMath.CoversWholeMap(runs, 6, 1), Is.False);
    }

    // A run still open at the right edge must close at the edge rather than be dropped — the
    // "forgot to flush the accumulator" bug, which produces a dark stripe down one side of the map.
    [Test]
    public void ARunReachingTheRightEdge_IsClosedRatherThanDropped()
    {
        List<OpenSkyMaskMath.Run> runs = OpenSkyMaskMath.UnroofedRuns(
            Grid("##...."), 6, 1);

        Assert.That(runs.Count, Is.EqualTo(1));
        Assert.That(runs[0].XStart, Is.EqualTo(2));
        Assert.That(runs[0].XEnd, Is.EqualTo(5), "the last run closes at the map edge");
    }

    [TestCase("######", 0)]
    [TestCase("#.#.#.", 3)]
    public void RoofPatterns_ProduceTheExpectedRunCount(string row, int expected)
    {
        Assert.That(OpenSkyMaskMath.UnroofedRuns(Grid(row), row.Length, 1).Count,
            Is.EqualTo(expected));
    }

    // A fully-roofed map yields nothing, and the caller must read that as "draw nothing" rather than
    // as "draw everything" — the inverted reading would flood a sealed cavern with sunlit snow glare.
    [Test]
    public void AFullyRoofedMap_YieldsNoRuns_AndIsNotReportedAsWholeMap()
    {
        List<OpenSkyMaskMath.Run> runs = OpenSkyMaskMath.UnroofedRuns(
            Grid("####", "####"), 4, 2);

        Assert.That(runs, Is.Empty);
        Assert.That(OpenSkyMaskMath.CoversWholeMap(runs, 4, 2), Is.False,
            "an empty mask is not a full-map mask");
    }

    // Rows are independent: a roof on one row must not shorten the row below it. Cheap to get wrong
    // by hoisting the run accumulator out of the row loop, and invisible in a single-row test.
    [Test]
    public void RowsDoNotBleedIntoEachOther()
    {
        List<OpenSkyMaskMath.Run> runs = OpenSkyMaskMath.UnroofedRuns(
            Grid("..##", "...."), 4, 2);

        Assert.That(runs.Count, Is.EqualTo(2));
        Assert.That(runs[0].Z, Is.EqualTo(0));
        Assert.That(runs[0].Width, Is.EqualTo(2));
        Assert.That(runs[1].Z, Is.EqualTo(1));
        Assert.That(runs[1].Width, Is.EqualTo(4), "the open row below is unaffected");
    }

    [Test]
    public void DegenerateInputs_ReturnEmpty_RatherThanThrowing()
    {
        Assert.That(OpenSkyMaskMath.UnroofedRuns(null!, 4, 4), Is.Empty);
        Assert.That(OpenSkyMaskMath.UnroofedRuns(Grid("...."), 0, 1), Is.Empty);
        Assert.That(OpenSkyMaskMath.UnroofedRuns(Grid("...."), 4, 0), Is.Empty);
        Assert.That(OpenSkyMaskMath.CoversWholeMap(null!, 4, 4), Is.False);
    }
}
