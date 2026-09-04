using System;
using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead.
//
// A checksum over the WHOLE §7c grid -- every cell's BFS depth and every cell's crossing strength --
// so that a rewrite of NativeSkyFalloffGrid.Rebuild can be held to producing the identical answer
// rather than to the half-dozen cells the scenarios happen to pin.
//
// WHY THIS EXISTS. The existing pins are good ones (door_strength_leak pins strengths to 1e-10, and
// sky_falloff_redraw pins composed vertex alphas) and they cover six cells out of 62,500. A rebuild
// that got the frontier wrong in a corner, or that dropped a seed at the map edge, would pass every
// one of them. The BFS is an adapter over live grids, so it has no offline unit test and cannot get
// one; this is the substitute, and it is only meaningful compared ACROSS BUILDS -- the oracle is the
// previous implementation's own output, captured before the change (see
// [[differential-tests-need-an-independent-oracle]]: a checksum both sides compute with the code under
// test proves nothing, which is why the baseline is taken on the OLD build first and pinned).
//
// Four metrics rather than one, because a single number cannot say what moved:
// DepthSum and DepthHash disagree in different ways (a depth moving between two cells preserves the
// sum), and the strength pair does the same for the multipliers, which is where door leakage lives.
// ReachedCells is the interpretable one -- if it drops, the flood stopped reaching somewhere.
//
// Costs a full sweep of the map through the public per-cell accessors, which is far slower than the
// grid it is reading. That is fine for a probe read and would not be for anything else.
public sealed class SkyFalloffGridChecksumProbe : IProbe
{
    public enum Metric
    {
        // Sum of every cell's BFS depth. Exactly representable as a float: 62,500 cells at a maximum
        // depth of 12 cannot exceed 750,000, well inside a float's 2^24 integer range.
        DepthSum,

        // FNV-1a over the depths in cell order, folded to six digits so it stays exact as a float.
        // Catches a depth moving from one cell to another, which DepthSum cannot.
        DepthHash,

        // FNV-1a over each strength scaled to a fixed-point long. 1e12 resolves the smallest live
        // value any scenario pins by two orders of magnitude -- door_strength_leak's own
        // 1.24541752e-10 -- so a strength changing at all changes this.
        StrengthHash,

        // How many cells the flood actually reached. The one metric a human can act on: if a rewrite
        // drops it, the flood stopped reaching somewhere and the hashes only say "something moved".
        ReachedCells,
    }

    private const long FnvOffsetBasis = unchecked((long)14695981039346656037UL);
    private const long FnvPrime = 1099511628211L;

    // Six digits: keeps every hash exactly representable as a float, and a collision between two
    // genuinely different grids at 1e-6 odds is not the failure mode worth designing against here.
    private const long HashModulus = 1000003L;

    private readonly Metric metric;

    public string Name { get; }

    public SkyFalloffGridChecksumProbe(string name, Metric metric)
    {
        Name = name;
        this.metric = metric;
    }

    public float Read(Map map)
    {
        if (map == null)
            return 0f;

        int maxDepth = NativeSkyFalloffMath.DefaultMaxDepth;
        float sensitivity = DoorLeakMath.DefaultSensitivity;

        long depthSum = 0;
        long depthHash = FnvOffsetBasis;
        long strengthHash = FnvOffsetBasis;
        int reached = 0;

        foreach (IntVec3 cell in map.AllCells)
        {
            int depth = NativeSkyFalloffGrid.DepthAt(map, cell, maxDepth, sensitivity);
            float strength = NativeSkyFalloffGrid.StrengthAt(map, cell, maxDepth, sensitivity);

            depthSum += depth;
            if (depth > 0)
                reached++;

            depthHash = Fold(depthHash, depth);
            strengthHash = Fold(strengthHash, (long)Math.Round(strength * 1e12d));
        }

        switch (metric)
        {
            case Metric.DepthSum:
                return depthSum;
            case Metric.DepthHash:
                return Mod(depthHash);
            case Metric.StrengthHash:
                return Mod(strengthHash);
            case Metric.ReachedCells:
                return reached;
            default:
                throw new ArgumentOutOfRangeException(nameof(metric), metric, null);
        }
    }

    private static long Fold(long hash, long value) => unchecked((hash ^ value) * FnvPrime);

    // Unchecked multiplication above can leave the accumulator negative; the modulus is taken on the
    // absolute value so the reported number is stable rather than sign-dependent on the last cell.
    private static float Mod(long hash) => Math.Abs(hash % HashModulus);
}
