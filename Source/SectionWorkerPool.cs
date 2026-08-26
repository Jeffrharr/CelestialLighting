using System;
using System.Threading;

namespace CelestialLighting;

// A set of worker threads created once and parked on a semaphore, as an alternative to
// CloudBake.Rows' Parallel.For for the indoor sky occlusion gather phase.
//
// WHY THIS MIGHT BE WORTH ANYTHING, stated as a hypothesis because that is all it is until the live
// A/B reports. RimWorld runs on Mono (MonoBleedingEdge; there is no System.Private.CoreLib in the
// Managed folder), whose thread pool injects new threads far more lazily than CoreCLR's — it adds
// them on a timer when work queues up rather than eagerly. The gather phase is exactly the workload
// that punishes: **bursty**. A whole-map rebake asks for every core at once, then nothing happens for
// seconds, by which time the pool has retired the threads again. If the pool is cold at each burst,
// part of every gather is spent waiting for threads to be created rather than doing work.
//
// Parked threads have no ramp. They are created on the first batch and then sit in a semaphore wait
// forever, so the second burst and the two-hundredth cost the same.
//
// WHAT THIS IS NOT. It is not backgrounding, and swapping thread pools cannot be: this still blocks
// the caller until every index is done, because blocking is a property of JOINING rather than of the
// pool. See DESIGN.md for why the fill is not made asynchronous (the window before first consumption
// is a few hundred microseconds, and the wide window crosses the point where rooms and glow settle).
//
// NO UNITY TYPES, same discipline as CloudBake, so the test project links this exact file and the
// serial-equals-parallel property can be pinned offline.
public static class SectionWorkerPool
{
    // One batch at a time. The gather phase is the only caller and it runs on the main thread, so
    // this is a guard against a future second caller rather than live contention.
    private static readonly object Gate = new object();

    // Released once per helper at the start of a batch; each helper takes exactly one permit.
    private static SemaphoreSlim wake;

    // Set when the last helper finishes its share. The caller waits on this after draining its own.
    private static ManualResetEventSlim finished;

    private static Thread[] helpers;
    private static Action<int, int> body;
    private static int itemCount;

    // The shared cursor. Helpers and the caller pull indices off it one at a time with an interlocked
    // increment, which is the same DYNAMIC partitioning Parallel.For's default partitioner gives and
    // is here for the same reason: the cost of a section is wildly uneven — open field short-circuits
    // EaveCells.Encloses on every unroofed cell, a section of small rooms pays for all 361 — so an
    // even split by COUNT is an uneven split by WORK.
    private static int nextIndex;

    // Helpers still working on this batch. The CALLER is not counted: it drains on its own thread and
    // then waits, so counting it would mean waiting for itself.
    private static int outstanding;

    // First exception from any participant. Rethrown on the caller so SkyOcclusionGather's own
    // try/catch sees it — a worker throwing into the void would otherwise leave the batch half built
    // and the phase reporting success.
    private static Exception failure;

    // How many threads were parked. Exposed for the probe: a pool that never spun up and a pool that
    // spun up and is doing nothing look identical from the outside.
    public static int HelperCount => helpers?.Length ?? 0;

    // Batches run through this pool since the last reset. Same reasoning as the gather phase's own
    // counters — a scheduling change that silently stops being used costs nothing and reads as
    // "no regression".
    public static int Batches;

    public static void Run(int count, Action<int, int> band) =>
        Run(count, CloudBake.WorkerCount(Environment.ProcessorCount), band);

    // Mirrors CloudBake.Rows' signature exactly so the two are swappable at the call site and an A/B
    // is a one-line diff.
    public static void Run(int count, int workers, Action<int, int> band)
    {
        if (band == null || count <= 0)
            return;

        // At one worker the caller does everything inline. Not an optimisation: a pool of zero helpers
        // is pure overhead, and this also gives the tests a serial path for the parallel one to match.
        if (workers <= 1)
        {
            band(0, count);
            return;
        }

        lock (Gate)
        {
            EnsureHelpers(workers - 1);

            body = band;
            itemCount = count;
            nextIndex = 0;
            failure = null;
            outstanding = helpers.Length;
            finished.Reset();
            Batches++;

            // Release AFTER the batch state is written. SemaphoreSlim.Release/Wait carry the memory
            // barrier, so a helper that wakes is guaranteed to see body, itemCount and nextIndex.
            wake.Release(helpers.Length);

            // The caller is a participant, not just a coordinator — it would otherwise sit idle while
            // N-1 threads do N threads' work.
            Drain();

            finished.Wait();

            Action<int, int> _ = body;
            body = null;

            if (failure != null)
                throw new Exception("Section worker pool batch failed", failure);
        }
    }

    // Threads are created on the first batch and never retired. IsBackground so a parked helper can
    // never keep the process alive at shutdown — the whole point is that they sleep indefinitely, and
    // a foreground thread doing that would hang the game on quit.
    private static void EnsureHelpers(int wanted)
    {
        if (helpers != null && helpers.Length >= wanted)
            return;

        // Grown rather than replaced would leave the old helpers waiting on a stale semaphore, so the
        // pool is sized once on first use. ProcessorCount does not change at runtime, so `wanted` is
        // the same on every call and this branch runs exactly once.
        wake = new SemaphoreSlim(0);
        finished = new ManualResetEventSlim(false);
        helpers = new Thread[wanted];

        for (int i = 0; i < wanted; i++)
        {
            Thread thread = new Thread(HelperLoop)
            {
                IsBackground = true,
                Name = "CelestialLighting occlusion worker " + i,
            };
            helpers[i] = thread;
            thread.Start();
        }
    }

    private static void HelperLoop()
    {
        while (true)
        {
            wake.Wait();
            Drain();

            // The last helper out sets the gate. Decrementing before the check would let two helpers
            // both see zero; Interlocked.Decrement returning the new value makes "I was last" exact.
            if (Interlocked.Decrement(ref outstanding) == 0)
                finished.Set();
        }
    }

    // Pull indices until the batch is exhausted. One index at a time, so a participant that draws a
    // cheap section comes back for another rather than finishing early and idling.
    private static void Drain()
    {
        Action<int, int> local = body;
        int count = itemCount;

        while (true)
        {
            int index = Interlocked.Increment(ref nextIndex) - 1;

            if (index >= count)
                return;

            try
            {
                local(index, index + 1);
            }
            catch (Exception e)
            {
                // First failure wins and the participant stops pulling. The others drain the rest, so
                // the batch still terminates and the caller still gets its finished signal — a
                // participant that stopped signalling here would hang the render thread.
                Interlocked.CompareExchange(ref failure, e, null);
                return;
            }
        }
    }

    public static void ResetCounters() => Batches = 0;
}
