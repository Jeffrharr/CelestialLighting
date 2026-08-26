using CelestialLighting;
using NUnit.Framework;

namespace CelestialLighting.Tests;

// The gather phase's two pre-flight decisions, plus the one property of its plumbing that can be
// pinned without a running game.
//
// WHAT THESE TESTS CANNOT REACH, stated up front so nobody mistakes a green suite for proof the
// threading is safe. The fill itself — Patch_IndoorSkyOcclusion.BuildWindow — reads Map, RoofGrid,
// EdificeGrid and RegionGrid, so it cannot be constructed offline at all. That its parallel result
// equals its serial one rests on three things, none of which is this file: the scheduler is
// CloudBake.Rows, which CloudBakeTests already pins serial-equals-parallel; each section writes only
// its own SkyOcclusionWindow and its own slot in the result array, so there is nothing shared to
// race on; and the two lazily-built caches that WOULD race are warmed on the main thread before any
// worker starts. The live A/B measuring median CIELAB dE 0.00 is the evidence for the whole of that
// argument, and it is the reason the scenario exists.
[TestFixture]
public class SkyOcclusionGatherMathTests
{
    // One candidate has nothing to overlap with, so the phase must decline and let the single
    // section build inline — a worker there costs a thread hand-off to do the same work elsewhere.
    [TestCase(0, 3, false)]
    [TestCase(1, 3, false)]
    [TestCase(2, 3, true)]
    [TestCase(112, 3, true)]
    public void WorthwhileNeedsTwoSections(int candidates, int workers, bool expected)
    {
        Assert.That(SkyOcclusionGatherMath.Worthwhile(candidates, workers), Is.EqualTo(expected));
    }

    // A single-worker machine gets the serial path whatever the batch size. This mirrors
    // CloudBake.WorkerCount returning 1 at two cores or fewer, where CloudBake.Rows runs the body
    // inline: Parallel.For with one worker is strictly worse than a for loop, and the partitioning
    // cost is not zero. Pinned here as well as there because Worthwhile is what decides whether the
    // main-thread warm-up work (the falloff BFS, the room caches) is paid at all — a batch that will
    // run serially anyway must not pay for it.
    [TestCase(2, 1, false)]
    [TestCase(112, 1, false)]
    [TestCase(112, 0, false)]
    [TestCase(2, 2, true)]
    public void WorthwhileNeedsMoreThanOneWorker(int candidates, int workers, bool expected)
    {
        Assert.That(SkyOcclusionGatherMath.Worthwhile(candidates, workers), Is.EqualTo(expected));
    }

    // The candidate predicate is a mask test against the LAYER's declared flags rather than a
    // hard-coded Roofs|GroundGlow, so these cases are written in terms of an arbitrary mask.
    [TestCase(0UL, 0b0110UL, false)]
    [TestCase(0b0001UL, 0b0110UL, false)]
    [TestCase(0b0010UL, 0b0110UL, true)]
    [TestCase(0b0100UL, 0b0110UL, true)]
    [TestCase(0b0110UL, 0b0110UL, true)]
    [TestCase(0b1001UL, 0b0110UL, false)]
    public void WillRegenerateIsAMaskTest(ulong dirtyFlags, ulong relevant, bool expected)
    {
        Assert.That(SkyOcclusionGatherMath.WillRegenerate(dirtyFlags, relevant), Is.EqualTo(expected));
    }

    // A section carrying no flags at all can never be a candidate — the case that matters because it
    // is the overwhelming majority of sections on any frame, and a predicate that answered true here
    // would batch the whole viewport every frame forever.
    [Test]
    public void WillRegenerateIsFalseForACleanSection()
    {
        Assert.That(SkyOcclusionGatherMath.WillRegenerate(0UL, ulong.MaxValue), Is.False);
    }

    // The plumbing, in the shape SkyOcclusionGather uses it: N sections, each worker writing only its
    // own index. Every slot must be written exactly once, and the result must not depend on the
    // worker count.
    //
    // THIS IS AN INDEPENDENT ORACLE AND NOT AN x-x==0 TEST. The expected array is built by a plain
    // for loop written here; the actual one goes through CloudBake.Rows. If Rows ever visited a slot
    // twice, skipped one, or handed the same index to two workers, `visits` would show it — which is
    // exactly what a differential test that computed both sides through the code under test could
    // not do. Proven red once by hand (changing the loop body to `i + 1`) before being believed.
    [TestCase(1)]
    [TestCase(3)]
    [TestCase(8)]
    public void RowsWritesEverySectionSlotExactlyOnce(int workers)
    {
        const int sections = 113;

        int[] expected = new int[sections];
        for (int i = 0; i < sections; i++)
            expected[i] = Payload(i);

        int[] actual = new int[sections];
        int[] visits = new int[sections];

        CloudBake.Rows(sections, workers, (start, end) =>
        {
            for (int i = start; i < end; i++)
            {
                actual[i] = Payload(i);
                System.Threading.Interlocked.Increment(ref visits[i]);
            }
        });

        Assert.That(actual, Is.EqualTo(expected));
        Assert.That(visits, Is.All.EqualTo(1));
    }

    // Stands in for one section's window fill: cheap, deterministic, and a function of the index
    // alone, which is the property the real fill has too (a section resolves its own cells and its
    // own one-cell skirt, and nothing else).
    private static int Payload(int i) => (i * 2654435761u).GetHashCode() ^ i;
}
