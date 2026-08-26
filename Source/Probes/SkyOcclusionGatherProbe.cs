using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// What indoor sky occlusion's gather phase actually DID, as opposed to what the frame cost.
//
// WHY A COUNTER AND NOT ONLY A TIMER. The gather phase has a failure mode that costs nothing, breaks
// nothing, produces the correct pixels, and is invisible to every timing instrument in the repo: it
// simply stops matching. Its candidate predicate is an approximation of Section.TryUpdate's, so a
// vanilla change to when a section regenerates, or a mod that patches the glow accessors and closes
// the safety gate, leaves every section falling through to the inline build — which is exactly what
// happened before the phase existed. The A/B would then report "no regression", correctly, about a
// feature doing nothing at all. Hits against Misses is the only thing that separates "the gather
// phase is working" from "the gather phase is absent".
//
// The model is VectorLightBakeProbe, and the reasoning there applies unchanged: a probe that
// RECOMPUTES an answer cannot see a cache, so only state the phase itself wrote will do.
public sealed class SkyOcclusionGatherProbe : IProbe
{
    public enum Metric
    {
        // Frames on which the phase ran and built at least MinSectionsToParallelise windows. Zero
        // means it never fired — check the feature flag and the safety gate before reading anything
        // else here.
        Passes,

        // Windows built on workers, summed over every pass. The quantity that moved off the main
        // thread.
        Sections,

        // Postfixes that found their window already built, and postfixes that built their own.
        //
        // A MISS IS NOT AN ERROR and the ratio is not expected to be 1. Section.DrawSection ->
        // RegenerateDirtyLayers is a second entry point into Regenerate that runs after the gather
        // phase, and a section arriving through it was never a candidate. What the ratio has to show
        // is that the BULK of a whole-map rebake is hits; a scenario pinning Hits at some exact
        // number would be pinning the harness's camera position, not the feature.
        Hits,
        Misses,

        // Hits as a share of all postfix calls, in [0, 1]. Derived rather than pinned on its own, and
        // present because it is the one number that says "the phase is carrying the rebake" without
        // the reader dividing two pins in their head.
        HitFraction,

        // Sections per pass: what one provocation actually batched. Read beside Passes — a phase
        // firing constantly with two sections a time is a different animal from one firing once with
        // a hundred, and only this ratio tells them apart.
        SectionsPerPass,

        // Zeroes every counter and reads back 0. A probe used as an action, the same wart
        // VectorLightBakeProbe carries and for the same reason: the counters accumulate across a
        // whole run, so a scenario measuring a second arm would report the first arm's numbers still
        // inside them. Put one between arms.
        Reset,
    }

    private readonly Metric metric;

    public SkyOcclusionGatherProbe(string name, Metric metric)
    {
        Name = name;
        this.metric = metric;
    }

    public string Name { get; }

    public float Read(Map map)
    {
        switch (metric)
        {
            case Metric.Passes:
                return SkyOcclusionGather.GatherPasses;
            case Metric.Sections:
                return SkyOcclusionGather.GatheredSections;
            case Metric.Hits:
                return SkyOcclusionGather.GatherHits;
            case Metric.Misses:
                return SkyOcclusionGather.GatherMisses;
            case Metric.HitFraction:
                return Ratio(
                    SkyOcclusionGather.GatherHits,
                    SkyOcclusionGather.GatherHits + SkyOcclusionGather.GatherMisses);
            case Metric.SectionsPerPass:
                return Ratio(SkyOcclusionGather.GatheredSections, SkyOcclusionGather.GatherPasses);
            case Metric.Reset:
                SkyOcclusionGather.ResetCounters();
                return 0f;
            default:
                return 0f;
        }
    }

    // 0 rather than NaN on an empty denominator, so a scenario that measured nothing reports a
    // readable zero instead of a value that fails every tolerance comparison in a way that looks like
    // a wild result rather than an absent one.
    private static float Ratio(float numerator, float denominator) =>
        denominator <= 0f ? 0f : numerator / denominator;
}
