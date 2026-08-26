using System;
using System.Collections.Generic;
using System.Threading;
using CelestialLighting;
using NUnit.Framework;

namespace CelestialLighting.Tests;

// The parked-thread pool the gather phase runs its batch on. What has to hold is the same property
// CloudBakeTests pins for Parallel.For, because the pool is a drop-in for it: every index visited
// exactly once, and a result that does not depend on how many threads were involved.
//
// A RACE IS A WRONG ANSWER *SOMETIMES*, so these run the batch repeatedly rather than once. A single
// pass over a shared cursor will pass by luck on a machine that happens to schedule it serially,
// which is the failure mode that reaches a player and never reaches a test.
[TestFixture]
public class SectionWorkerPoolTests
{
    private static int Payload(int i) => (i * 2654435761u).GetHashCode() ^ i;

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(8)]
    public void EveryIndexVisitedExactlyOnce(int workers)
    {
        const int count = 113;

        // Repeated because the pool REUSES its threads: a batch that failed to reset the cursor, or
        // that let a helper from the previous batch keep pulling, would pass on the first pass and
        // fail on the second. One pass cannot see that at all.
        for (int pass = 0; pass < 25; pass++)
        {
            int[] actual = new int[count];
            int[] visits = new int[count];

            SectionWorkerPool.Run(count, workers, (start, end) =>
            {
                for (int i = start; i < end; i++)
                {
                    actual[i] = Payload(i);
                    Interlocked.Increment(ref visits[i]);
                }
            });

            for (int i = 0; i < count; i++)
            {
                Assert.That(visits[i], Is.EqualTo(1), $"index {i} on pass {pass}");
                Assert.That(actual[i], Is.EqualTo(Payload(i)), $"index {i} on pass {pass}");
            }
        }
    }

    // The independent oracle: a plain serial loop written here, against the pool's result. Not an
    // x-x==0 test -- the two sides are computed by different code.
    [TestCase(3)]
    [TestCase(8)]
    public void ParallelMatchesSerial(int workers)
    {
        const int count = 200;

        int[] serial = new int[count];
        for (int i = 0; i < count; i++)
            serial[i] = Payload(i);

        int[] pooled = new int[count];
        SectionWorkerPool.Run(count, workers, (start, end) =>
        {
            for (int i = start; i < end; i++)
                pooled[i] = Payload(i);
        });

        Assert.That(pooled, Is.EqualTo(serial));
    }

    // A worker throwing must surface on the CALLER. SkyOcclusionGather wraps its batch in a try/catch
    // that stands the phase down for the session and logs once; an exception swallowed on a helper
    // thread would leave that catch unreached, the batch half built, and the phase reporting success
    // over windows that were never filled.
    [Test]
    public void AWorkerFailureReachesTheCaller()
    {
        Assert.That(
            () => SectionWorkerPool.Run(64, 3, (start, end) =>
            {
                if (start == 40)
                    throw new InvalidOperationException("boom");
            }),
            Throws.Exception.With.InnerException.TypeOf<InvalidOperationException>());
    }

    // ...and the pool must still be usable afterwards. A failed batch that left the cursor or the
    // outstanding count dirty would hang the render thread on the next gather rather than fail it.
    [Test]
    public void ThePoolSurvivesAFailedBatch()
    {
        try
        {
            SectionWorkerPool.Run(64, 3, (start, end) =>
            {
                if (start == 40)
                    throw new InvalidOperationException("boom");
            });
        }
        catch (Exception)
        {
            // expected
        }

        int[] visits = new int[50];
        SectionWorkerPool.Run(50, 3, (start, end) =>
        {
            for (int i = start; i < end; i++)
                Interlocked.Increment(ref visits[i]);
        });

        Assert.That(visits, Is.All.EqualTo(1));
    }

    // Zero and negative counts must do nothing rather than park the caller forever.
    [TestCase(0)]
    [TestCase(-1)]
    public void AnEmptyBatchIsANoOp(int count)
    {
        int calls = 0;
        SectionWorkerPool.Run(count, 4, (_, __) => calls++);
        Assert.That(calls, Is.Zero);
    }
}
