using System;
using System.Threading.Tasks;

namespace CelestialLighting;

// §25e (DESIGN.md §25e, issue #144): how the cloud bakes get off the loading screen's critical path.
//
// THE PROBLEM IS A LOADING SCREEN, NOT A FRAME. Nothing in §25 is per-frame CPU work — the atlas and
// the volume are baked once in a static constructor and never touched again, which is the whole
// point of CloudSheetOverlay's "baked once, ever" design. But `once` is measured in seconds here:
// three noise fields over the same 384x384 atlas, one of them 20 layers deep with a 3-D fBm per
// voxel, all of it on Unity's main thread while the player watches a progress bar.
//
// TWO INDEPENDENT WINS, AND THEY MULTIPLY. Spreading a bake across cores makes it finish sooner;
// running it off the main thread means the main thread never waits for it at all. This file is the
// first; CloudVolumeShader does the second on top of it. Neither is a substitute for the other — a
// bake that is eight times faster still stalls the load for its duration, and a bake moved to a
// background thread still has to be FINISHED before the first cloud draws.
//
// NO UNITY TYPES HERE ON PURPOSE. Everything below is BCL, so the offline tools and the test project
// link it exactly as they link the pure cores. That matters more than it sounds: the thing worth
// testing about a parallel bake is that it produces what the serial one produced, and a helper that
// dragged UnityEngine in could not be called from the test that pins it.
public static class CloudBake
{
    // One core is left for whoever is already using it, and during load that is the game: RimWorld
    // is parsing XML on the main thread at exactly the moment this runs, so taking every core would
    // buy the bake its last worker by slowing down the load it is trying to get out of the way of.
    //
    // Floored at 1 rather than 2, because at 1 the loop below runs inline on the calling thread and
    // that is genuinely the right answer for a single-core machine — Parallel.For with one worker is
    // strictly worse than a `for`, and the partitioning cost is not zero.
    public static int WorkerCount(int processorCount) =>
        processorCount <= 2 ? 1 : processorCount - 1;

    // Runs `bakeBand(yStart, yEnd)` over `rowCount` rows, split across cores.
    //
    // BANDS ARE HANDED OUT DYNAMICALLY, one row at a time, rather than sliced into N equal blocks up
    // front. The cost of a row is wildly uneven — a row that lands between two blobs is all radial
    // falloff and skips the 3-D noise entirely, a row through three cloud cores pays for every voxel
    // — so an even split by COUNT is an uneven split by WORK, and a static partition finishes when
    // its unluckiest block does. Parallel.For's default partitioner already does this; the reason to
    // say so is that it is why the row is the unit and not the band.
    //
    // Falls back to a plain serial loop at one worker. Not an optimisation: `Parallel.For` on a
    // machine that cannot run two of anything is pure overhead, and this also gives the tests a way
    // to assert the serial path is the one the parallel path has to match.
    public static void Rows(int rowCount, int workers, Action<int, int> bakeBand)
    {
        if (bakeBand == null || rowCount <= 0)
            return;

        if (workers <= 1)
        {
            bakeBand(0, rowCount);
            return;
        }

        ParallelOptions options = new ParallelOptions { MaxDegreeOfParallelism = workers };
        Parallel.For(0, rowCount, options, y => bakeBand(y, y + 1));
    }

    // The same with this machine's worker count, which is what every caller in the mod wants.
    public static void Rows(int rowCount, Action<int, int> bakeBand) =>
        Rows(rowCount, WorkerCount(Environment.ProcessorCount), bakeBand);
}
