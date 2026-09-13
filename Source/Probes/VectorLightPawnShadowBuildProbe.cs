using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// What the pawn-shadow pass's batch-and-parallelise pair actually DID, as opposed to what one
// shadow looks like.
//
// WHY THIS IS SEPARATE FROM VectorLightProbe, on the exact reasoning VectorLightBakeProbe gives for
// being separate from it: that probe answers shape questions by recomputing from the pure core, and
// a recomputation returns a correct answer whether or not batching or the fan-out actually ran. Every
// metric here is a counter the draw or the build phase itself incremented, so a flag that stopped
// reaching either one reads as a flat counter rather than as an unrelated shape metric holding still.
public sealed class VectorLightPawnShadowBuildProbe : IProbe
{
    public enum Metric
    {
        // Graphics.DrawMesh calls the pawn-shadow pass issued since the last reset, and the
        // individual shadows those calls carried. READ AS A RATIO (ShadowsPerDrawCall), never
        // DrawCalls alone: a batch of one shadow and a batch of nineteen both report one call, and
        // only the ratio says which frame the batching flag actually earned its keep on.
        DrawCalls,
        ShadowsDrawn,
        ShadowsPerDrawCall,

        // Build passes handed to the pool, and passes that ran on the calling thread because the
        // gathered batch was under the threshold. MEANINGLESS APART, on the same reasoning
        // VectorLightBakeProbe.Metric.ParallelBakePasses gives: most frames gather a handful of
        // pawns, which is supposed to stay serial, so a healthy scenario reports mostly serial
        // passes and a zero parallel count is not by itself a finding.
        ParallelBuildPasses,
        SerialBuildPasses,

        // The largest batch either build path was handed — the number that says a scenario actually
        // exercised the fan-out with enough pawns to mean anything, on the same reasoning
        // LargestBakeBatch exists for the light field's own threaded bake.
        LargestBuildBatch,

        // Wall-clock milliseconds the calling thread spent in the build phase since the last reset.
        // THE ONLY METRIC HERE THAT CAN SCORE THE THREADED PATH, because threading moves time off
        // the calling thread rather than removing it — a Circinus arm on Build reports the same
        // total work whether it ran on one thread or eight.
        BuildWallMs,

        // The batched draw's per-shadow CPU loop alone -- see VectorLightPawnShadows.AppendWallMs's
        // own header for why it is timed apart from the mesh upload around it. Reads 0 on the
        // unbatched arm, where the loop never runs, which is the expected baseline rather than a
        // missing measurement.
        AppendWallMs,

        // Side-effecting: zeroes every counter above and reads 0, following the same convention as
        // VectorLightBakeProbe.Metric.Reset and for the identical reason — so a scenario can open the
        // counting window and the profiling window at the same point rather than at whichever earlier
        // step happened to flip a feature flag.
        Reset,
    }

    private readonly Metric metric;

    public string Name { get; }

    public VectorLightPawnShadowBuildProbe(string name, Metric metric)
    {
        Name = name;
        this.metric = metric;
    }

    public float Read(Map map)
    {
        if (metric == Metric.DrawCalls)
            return VectorLightPawnShadows.DrawCalls;

        if (metric == Metric.ShadowsDrawn)
            return VectorLightPawnShadows.ShadowsDrawn;

        if (metric == Metric.ShadowsPerDrawCall)
            return Ratio(VectorLightPawnShadows.ShadowsDrawn, VectorLightPawnShadows.DrawCalls);

        if (metric == Metric.ParallelBuildPasses)
            return VectorLightPawnShadows.ParallelBuildPasses;

        if (metric == Metric.SerialBuildPasses)
            return VectorLightPawnShadows.SerialBuildPasses;

        if (metric == Metric.LargestBuildBatch)
            return VectorLightPawnShadows.LargestBuildBatch;

        if (metric == Metric.BuildWallMs)
            return (float)VectorLightPawnShadows.BuildWallMs;

        if (metric == Metric.AppendWallMs)
            return (float)VectorLightPawnShadows.AppendWallMs;

        // metric == Metric.Reset, the only value left unmatched above.
        VectorLightPawnShadows.ResetCounters();
        return 0f;
    }

    // Zero rather than NaN on an empty denominator, matching VectorLightBakeProbe's own helper: a
    // scenario that provoked nothing should read as "nothing happened" and fail its pin, not poison
    // the report with a value JSON cannot carry.
    private static float Ratio(int numerator, int denominator) =>
        denominator == 0 ? 0f : (float)numerator / denominator;
}
