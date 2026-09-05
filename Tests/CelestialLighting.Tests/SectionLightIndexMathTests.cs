using System.Collections.Generic;
using CelestialLighting;
using NUnit.Framework;

namespace CelestialLighting.Tests;

// The index has exactly one claim: for every section, it returns the same items a full scan would
// have returned, in the same order. So every test here is differential against that scan.
//
// THE ORACLE IS THE LOOP THE INDEX REPLACES, written out longhand and independent of the code under
// test — an index that agreed with itself would assert x - x == 0, which this repo has a note about.
// The order half is not decoration: vanilla's glow fold projects after every addition and is
// therefore not commutative, so a bucket in the wrong order changes the colour of every saturated
// cell while still containing the right lights.
[TestFixture]
public class SectionLightIndexMathTests
{
    private const int SectionSize = 17;

    private sealed class Item
    {
        public int MinX;
        public int MaxX;
        public int MinZ;
        public int MaxZ;
    }

    // What the mask did before the index: walk every light, keep the ones whose bounds meet this
    // section. Deliberately spelled as the original loop rather than in terms of section indices.
    private static List<int> ScanOracle(
        List<Item> items, int sectionX, int sectionZ, int mapWidth, int mapHeight)
    {
        int minX = sectionX * SectionSize;
        int minZ = sectionZ * SectionSize;
        int maxX = System.Math.Min(minX + SectionSize - 1, mapWidth - 1);
        int maxZ = System.Math.Min(minZ + SectionSize - 1, mapHeight - 1);

        List<int> hit = new List<int>();

        for (int i = 0; i < items.Count; i++)
        {
            Item it = items[i];

            bool overlaps = it.MaxX >= minX && it.MinX <= maxX
                && it.MaxZ >= minZ && it.MinZ <= maxZ;

            if (overlaps)
                hit.Add(i);
        }

        return hit;
    }

    private static int[] RangesFor(List<Item> items, int mapWidth, int mapHeight)
    {
        int[] ranges = new int[items.Count * SectionLightIndexMath.IntsPerItem];

        for (int i = 0; i < items.Count; i++)
        {
            Item it = items[i];
            int at = i * SectionLightIndexMath.IntsPerItem;

            bool on = SectionDirtyMath.SectionRange(
                SectionDirtyMath.Changed(it.MinX, it.MinZ, it.MaxX, it.MaxZ, 0),
                SectionSize, mapWidth, mapHeight,
                out int minSx, out int minSz, out int maxSx, out int maxSz);

            ranges[at] = on ? minSx : SectionLightIndexMath.Absent;
            ranges[at + 1] = maxSx;
            ranges[at + 2] = minSz;
            ranges[at + 3] = maxSz;
        }

        return ranges;
    }

    private static void AssertMatchesScan(List<Item> items, int mapWidth, int mapHeight)
    {
        int across = (mapWidth + SectionSize - 1) / SectionSize;
        int count = SectionDirtyMath.SectionCount(mapWidth, mapHeight, SectionSize);

        int[] starts = null;
        int[] flat = null;
        SectionLightIndexMath.Build(
            RangesFor(items, mapWidth, mapHeight), items.Count, across, count, ref starts, ref flat);

        int up = (mapHeight + SectionSize - 1) / SectionSize;

        for (int sz = 0; sz < up; sz++)
        {
            for (int sx = 0; sx < across; sx++)
            {
                int section = sz * across + sx;
                List<int> expected = ScanOracle(items, sx, sz, mapWidth, mapHeight);

                List<int> actual = new List<int>();

                for (int k = starts[section]; k < starts[section + 1]; k++)
                    actual.Add(flat[k]);

                Assert.That(actual, Is.EqualTo(expected),
                    $"section ({sx},{sz}) on a {mapWidth}x{mapHeight} map");
            }
        }
    }

    private static Item Square(int x, int z, int radius) => new Item
    {
        MinX = x - radius, MaxX = x + radius, MinZ = z - radius, MaxZ = z + radius,
    };

    [Test]
    public void AnEmptyIndexHasEmptyBuckets()
    {
        AssertMatchesScan(new List<Item>(), 68, 68);
    }

    [Test]
    public void OneLightInOneSection()
    {
        AssertMatchesScan(new List<Item> { Square(8, 8, 3) }, 68, 68);
    }

    // The case the whole design turns on: a light wider than a section lands in several buckets, and
    // must appear in each of them.
    [Test]
    public void ALightStraddlingFourSectionsIsInAllFour()
    {
        AssertMatchesScan(new List<Item> { Square(17, 17, 6) }, 68, 68);
    }

    [Test]
    public void ALightWiderThanTheWholeMap()
    {
        AssertMatchesScan(new List<Item> { Square(34, 34, 400) }, 68, 68);
    }

    // Off the map entirely must be dropped, not clamped onto section 0 — the failure
    // SectionDirtyMath.SectionRange's own header records.
    [Test]
    public void ALightOffTheMapIsInNoBucket()
    {
        List<Item> items = new List<Item> { Square(-400, 30, 4), Square(30, 30, 4) };
        AssertMatchesScan(items, 68, 68);
    }

    [Test]
    public void ALightStraddlingTheWestEdgeKeepsItsOnMapHalf()
    {
        AssertMatchesScan(new List<Item> { Square(2, 30, 8) }, 68, 68);
    }

    // A map whose size is not a multiple of the section size: the last section is a stub, and both
    // the index and the oracle have to clip to the map rather than to the section.
    [Test]
    public void AMapThatDoesNotDivideEvenlyIntoSections()
    {
        AssertMatchesScan(new List<Item> { Square(70, 70, 9), Square(5, 5, 2) }, 75, 75);
    }

    // ORDER, held separately from membership, because a bucket can hold the right lights in the
    // wrong order and every membership assertion above would still pass.
    [Test]
    public void BucketsAreAscendingInItemIndex()
    {
        List<Item> items = new List<Item>();

        for (int i = 0; i < 40; i++)
            items.Add(Square(20 + (i % 3) * 17, 20 + (i % 5) * 17, 10));

        int across = (85 + SectionSize - 1) / SectionSize;
        int count = SectionDirtyMath.SectionCount(85, 85, SectionSize);

        int[] starts = null;
        int[] flat = null;
        SectionLightIndexMath.Build(
            RangesFor(items, 85, 85), items.Count, across, count, ref starts, ref flat);

        for (int s = 0; s < count; s++)
        {
            for (int k = starts[s] + 1; k < starts[s + 1]; k++)
            {
                Assert.That(flat[k], Is.GreaterThan(flat[k - 1]),
                    $"bucket {s} is not ascending: the glow fold is not commutative");
            }
        }
    }

    // A fixed-seed sweep, because the hand-named cases above are the ones somebody thought of.
    [Test]
    public void RandomLayoutsMatchTheScan()
    {
        System.Random random = new System.Random(20260905);

        for (int trial = 0; trial < 200; trial++)
        {
            int mapWidth = 34 + random.Next(0, 120);
            int mapHeight = 34 + random.Next(0, 120);

            List<Item> items = new List<Item>();
            int n = random.Next(0, 30);

            for (int i = 0; i < n; i++)
            {
                items.Add(Square(
                    random.Next(-20, mapWidth + 20),
                    random.Next(-20, mapHeight + 20),
                    random.Next(0, 20)));
            }

            AssertMatchesScan(items, mapWidth, mapHeight);
        }
    }

    // The buffers are reused across builds and nothing clears them, so a build must not be able to
    // read a larger predecessor's leftovers. Descending sizes share one pair of arrays and are
    // compared against fresh ones — the shape of test the coverage scratch needed for the same
    // reason.
    [Test]
    public void ARebuiltIndexMatchesAFreshOne()
    {
        int[] sharedStarts = null;
        int[] sharedItems = null;

        for (int n = 30; n >= 0; n -= 3)
        {
            List<Item> items = new List<Item>();
            System.Random random = new System.Random(1000 + n);

            for (int i = 0; i < n; i++)
                items.Add(Square(random.Next(0, 80), random.Next(0, 80), random.Next(1, 15)));

            int across = (85 + SectionSize - 1) / SectionSize;
            int count = SectionDirtyMath.SectionCount(85, 85, SectionSize);
            int[] ranges = RangesFor(items, 85, 85);

            int sharedTotal = SectionLightIndexMath.Build(
                ranges, items.Count, across, count, ref sharedStarts, ref sharedItems);

            int[] freshStarts = null;
            int[] freshItems = null;
            int freshTotal = SectionLightIndexMath.Build(
                ranges, items.Count, across, count, ref freshStarts, ref freshItems);

            Assert.That(sharedTotal, Is.EqualTo(freshTotal), $"total differs at n = {n}");

            for (int s = 0; s <= count; s++)
                Assert.That(sharedStarts[s], Is.EqualTo(freshStarts[s]), $"starts[{s}] at n = {n}");

            for (int k = 0; k < freshTotal; k++)
                Assert.That(sharedItems[k], Is.EqualTo(freshItems[k]), $"items[{k}] at n = {n}");
        }
    }

    [TestCase(0, 0, 68, 68, 4, 0)]
    [TestCase(17, 0, 68, 68, 4, 1)]
    [TestCase(0, 17, 68, 68, 4, 4)]
    [TestCase(-1, 5, 68, 68, 4, SectionLightIndexMath.Absent)]
    [TestCase(68, 5, 68, 68, 4, SectionLightIndexMath.Absent)]
    public void SectionAtAgreesWithTheGridTheBuildUses(
        int cellX, int cellZ, int mapWidth, int mapHeight, int across, int expected)
    {
        Assert.That(
            SectionLightIndexMath.SectionAt(cellX, cellZ, SectionSize, mapWidth, mapHeight, across),
            Is.EqualTo(expected));
    }
}
