using System.Diagnostics;
using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead.
//
// What ONE whole-map §7c BFS rebuild costs, timed directly rather than sampled by a profiler.
//
// WHY NOT CIRCINUS, WHICH IS ALREADY WIRED UP FOR THIS. It could not be trusted here. Armed on
// NativeSkyFalloffGrid.Rebuild it reported 24 calls / 442.67 ms in one run and 0 calls / 0 ms in the
// next on the SAME build, with circinus_falloff_patched reading 1 both times and the scenario's own
// depth probes returning 2 thirteen times over in both -- so the work certainly ran and the counter
// certainly missed it. Arms on the two enclosing methods (DepthAt, EnsureCurrent) zeroed in the same
// run, which rules out "the method was inlined" and points at the recorder. A number that is
// sometimes right is worse than no number: the failure mode reads as "this build is free".
//
// WHY NOT THE DUBS PER-FRAME TABLE. Rebuild is not per-frame. It runs once per map per invalidation,
// lazily on the next read, so it appears in no window at all -- and pricing a change to its internals
// off the per-frame table measures something else entirely. That is not hypothetical: an earlier
// attempt read door_strength_perf as 0.3734 -> 0.3192 ms/frame and called this change a win, when the
// rows that had actually moved were section-regenerate COUNTS (756 -> 428), which this change does not
// touch and which vary run to run anyway.
//
// So this forces the work and holds the stopwatch itself. MarkDirty invalidates the cached grid,
// DepthAt is the read that rebuilds it, and the pair repeated `Iterations` times gives a mean over
// identical map states -- which is what makes it comparable ACROSS BUILDS, the only comparison the
// number supports. Deliberately warms up once before timing: the first rebuild of a session pays for
// the arrays' first allocation and for whatever the JIT has not yet done.
//
// It is a benchmark, not an observation of ordinary play: nothing in a real game calls MarkDirty
// eleven times in a row. Read it as "what one rebuild costs", never as "what the mod costs per frame".
public sealed class SkyFalloffRebuildTimingProbe : IProbe
{
    // Ten timed rebuilds plus one warm-up. Enough to average out a stray GC on a 250x250 map without
    // making the probe itself a visible stall in the run.
    private const int Iterations = 10;

    public string Name { get; }

    public SkyFalloffRebuildTimingProbe(string name)
    {
        Name = name;
    }

    public float Read(Map map)
    {
        if (map == null)
            return 0f;

        IntVec3 cell = map.Center;
        int maxDepth = NativeSkyFalloffMath.DefaultMaxDepth;
        float sensitivity = DoorLeakMath.DefaultSensitivity;

        // Warm-up, untimed, and it also binds the grid's single-slot cache to this map -- MarkDirty is
        // a no-op for any other map, so timing without this would measure ten cache hits.
        NativeSkyFalloffGrid.MarkDirty(map);
        NativeSkyFalloffGrid.DepthAt(map, cell, maxDepth, sensitivity);

        var stopwatch = new Stopwatch();
        for (int i = 0; i < Iterations; i++)
        {
            NativeSkyFalloffGrid.MarkDirty(map);
            stopwatch.Start();
            NativeSkyFalloffGrid.DepthAt(map, cell, maxDepth, sensitivity);
            stopwatch.Stop();
        }

        return (float)(stopwatch.Elapsed.TotalMilliseconds / Iterations);
    }
}
