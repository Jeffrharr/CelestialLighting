using CelestialLighting;
using NUnit.Framework;

namespace CelestialLighting.Tests;

// §25e: the parallel cloud bake. Two questions, and only one of them is about threads.
//
// THE ONE THAT MATTERS IS THAT PARALLEL == SERIAL, BYTE FOR BYTE. Every other cloud test in this
// suite pins a value the bake produces, so a parallel bake that produced something subtly different
// would fail them all — but only if it did so on the tester's machine, on that run, with that
// scheduling. A race in a bake is not a wrong answer, it is a wrong answer sometimes, and the rest
// of the suite is not built to catch that. These are.
[TestFixture]
public class CloudBakeTests
{
    private const int AtlasSize = 96;
    private const int Cells = 3;
    private const int Seed = 20260810;
    private const int Layers = 8;

    // Small enough to run in a test suite, big enough that the row split is a real split: 96 rows
    // across the workers below is dozens of bands, not two.

    [TestCase(1, ExpectedResult = 1)]
    [TestCase(2, ExpectedResult = 1)]
    [TestCase(4, ExpectedResult = 3)]
    [TestCase(8, ExpectedResult = 7)]
    [TestCase(16, ExpectedResult = 15)]
    public int WorkerCountLeavesACoreForTheGame(int processors) =>
        CloudBake.WorkerCount(processors);

    // A dual-core machine gets the serial path, not a one-worker Parallel.For: at two cores the
    // game's own thread is half the machine, and the partitioner's overhead is not free.
    [Test]
    public void WorkerCountFallsBackToSerialOnSmallMachines()
    {
        Assert.That(CloudBake.WorkerCount(1), Is.EqualTo(1));
        Assert.That(CloudBake.WorkerCount(2), Is.EqualTo(1));
    }

    // Rows() must visit every row exactly once, whatever the worker count. This is the property the
    // whole scheme rests on: a row visited twice is harmless here only because the bake is
    // idempotent per row, and a row visited ZERO times is a band of empty sky nobody would notice
    // until a screenshot.
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(4)]
    [TestCase(8)]
    public void RowsVisitsEveryRowExactlyOnce(int workers)
    {
        const int rowCount = 257;   // prime, so no worker count divides it evenly
        int[] visits = new int[rowCount];

        CloudBake.Rows(rowCount, workers, (yStart, yEnd) =>
        {
            for (int y = yStart; y < yEnd; y++)
                System.Threading.Interlocked.Increment(ref visits[y]);
        });

        Assert.That(visits, Is.All.EqualTo(1));
    }

    [Test]
    public void RowsIgnoresAnEmptyRange()
    {
        int calls = 0;
        CloudBake.Rows(0, 4, (_, _) => calls++);
        Assert.That(calls, Is.Zero);
    }

    // The volume baked in one serial pass and in parallel bands must be the same bytes.
    //
    // Compared against the FULL-RANGE entry point rather than against a hand-written serial loop, so
    // this also pins that FillBlobVolume and FillBlobVolumeRows have not drifted apart — they are
    // one delegating to the other today, and the day somebody optimises one of them is the day that
    // stops being obvious.
    [TestCase(1)]
    [TestCase(3)]
    [TestCase(8)]
    public void FillBlobVolumeBandsMatchWhole(int workers)
    {
        byte[] whole = new byte[AtlasSize * AtlasSize * Layers];
        byte[] banded = new byte[AtlasSize * AtlasSize * Layers];

        CloudVolumeMath.FillBlobVolume(whole, AtlasSize, Cells, Layers, Seed, 4,
            CloudDeckMath.PresentShapeCuts(), CloudDeckMath.ShapeGains(),
            CloudDeckMath.FrequenciesU(), CloudDeckMath.FrequenciesV(),
            CloudField.PresentBlobCoreFraction, CloudField.PresentRimBite,
            CloudSheetMath.PresenceAlphaGamma);

        CloudBake.Rows(AtlasSize, workers, (yStart, yEnd) => CloudVolumeMath.FillBlobVolumeRows(
            banded, AtlasSize, Cells, Layers, Seed, 4,
            CloudDeckMath.PresentShapeCuts(), CloudDeckMath.ShapeGains(),
            CloudDeckMath.FrequenciesU(), CloudDeckMath.FrequenciesV(),
            CloudField.PresentBlobCoreFraction, CloudField.PresentRimBite,
            CloudSheetMath.PresenceAlphaGamma, yStart, yEnd));

        Assert.That(banded, Is.EqualTo(whole));
    }

    // ...and the same for the 2-D atlas, which is baked twice at load with two different shapings.
    // Both shapings are covered because the rim-bite branch is the one that reads `radius` a second
    // time, and a row split is exactly where a stale per-row local would show up.
    [TestCase(1)]
    [TestCase(3)]
    [TestCase(8)]
    public void FillBlobAtlasBandsMatchWhole(int workers)
    {
        float[] whole = new float[AtlasSize * AtlasSize];
        float[] banded = new float[AtlasSize * AtlasSize];

        CloudField.FillBlobAtlas(whole, AtlasSize, Cells, Seed, 4,
            CloudDeckMath.PresentShapeCuts(), CloudDeckMath.ShapeGains(),
            CloudDeckMath.FrequenciesU(), CloudDeckMath.FrequenciesV(),
            CloudField.PresentBlobCoreFraction, CloudField.PresentRimBite);

        CloudBake.Rows(AtlasSize, workers, (yStart, yEnd) => CloudField.FillBlobAtlasRows(
            banded, AtlasSize, Cells, Seed, 4,
            CloudDeckMath.PresentShapeCuts(), CloudDeckMath.ShapeGains(),
            CloudDeckMath.FrequenciesU(), CloudDeckMath.FrequenciesV(),
            CloudField.PresentBlobCoreFraction, CloudField.PresentRimBite, yStart, yEnd));

        Assert.That(banded, Is.EqualTo(whole));
    }

    // The plain shaping too — no rim bite, the falloff multiplying the shape — because that is what
    // CloudSheetOverlay.Atlas (as opposed to PresentAtlas) still bakes.
    [Test]
    public void FillBlobAtlasBandsMatchWholeWithoutRimBite()
    {
        float[] whole = new float[AtlasSize * AtlasSize];
        float[] banded = new float[AtlasSize * AtlasSize];

        CloudField.FillBlobAtlas(whole, AtlasSize, Cells, Seed, 4,
            CloudDeckMath.ShapeCuts(), CloudDeckMath.ShapeGains(),
            CloudDeckMath.FrequenciesU(), CloudDeckMath.FrequenciesV(),
            CloudField.BlobCoreFraction, 0f);

        CloudBake.Rows(AtlasSize, 8, (yStart, yEnd) => CloudField.FillBlobAtlasRows(
            banded, AtlasSize, Cells, Seed, 4,
            CloudDeckMath.ShapeCuts(), CloudDeckMath.ShapeGains(),
            CloudDeckMath.FrequenciesU(), CloudDeckMath.FrequenciesV(),
            CloudField.BlobCoreFraction, 0f, yStart, yEnd));

        Assert.That(banded, Is.EqualTo(whole));
    }

    // A band outside the atlas writes nothing rather than throwing. Rows() never asks for one, but
    // the range is public now and the guard is one comparison.
    [Test]
    public void FillBlobVolumeIgnoresAnEmptyBand()
    {
        byte[] volume = new byte[AtlasSize * AtlasSize * Layers];

        CloudVolumeMath.FillBlobVolumeRows(volume, AtlasSize, Cells, Layers, Seed, 4,
            CloudDeckMath.PresentShapeCuts(), CloudDeckMath.ShapeGains(),
            CloudDeckMath.FrequenciesU(), CloudDeckMath.FrequenciesV(),
            CloudField.PresentBlobCoreFraction, CloudField.PresentRimBite,
            CloudSheetMath.PresenceAlphaGamma, yStart: 4, yEnd: 4);

        Assert.That(volume, Is.All.Zero);
    }
}
