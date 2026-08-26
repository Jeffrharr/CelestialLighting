namespace CelestialLighting;

// The two decisions the indoor sky occlusion gather phase makes before it touches anything live,
// pulled out here so an offline test can reach them. SkyOcclusionGather is the adapter around this;
// it holds the Map, the Section list and the worker pool, and none of that is testable without a
// running game.
//
// Pure math only — no UnityEngine or Verse types — same discipline as SkyOcclusionWindow, so the test
// project links this exact file rather than a copy.
public static class SkyOcclusionGatherMath
{
    // Below this many dirty sections in view, the gather phase stands down and every section builds
    // its own window inline exactly as it did before the phase existed.
    //
    // TWO, NOT ONE, and not a larger tuned number either. At one candidate there is by definition
    // nothing to overlap, so a worker would cost a thread hand-off to do the same work somewhere
    // else. At two the hand-off is already worth it on a section that measured ~65 us of window fill
    // (72.22 ms across 1,120 calls). Anything higher would be a guess: the cost of a section is
    // wildly uneven — a section of open field short-circuits EaveCells.Encloses on every unroofed
    // cell and never reaches a room query, one full of small rooms pays for all 361 — so a threshold
    // in units of SECTIONS cannot predict the work either way, and the dynamic partitioner is what
    // actually handles the imbalance.
    public const int MinSectionsToParallelise = 2;

    // Is the gather phase worth running at all this frame? False means "do nothing", which restores
    // the pre-feature path exactly rather than substituting a cheaper one — the property that makes
    // the flag-off arm a real baseline instead of a picture of the feature being absent.
    public static bool Worthwhile(int candidateSections, int workers) =>
        candidateSections >= MinSectionsToParallelise && workers > 1;

    // Will this section's lighting overlay regenerate on the strength of the flags it is carrying?
    //
    // Mirrors the test Verse.Section.TryUpdate makes per layer — `dirtyFlags & relevantChangeTypes`
    // — rather than hard-coding Roofs|GroundGlow, so the predicate cannot drift from the layer's own
    // declaration if Ludeon adds a third flag to it.
    //
    // A FALSE NEGATIVE IS FREE AND A FALSE POSITIVE IS CHEAP, which is why this is allowed to be an
    // approximation of TryUpdate rather than a reproduction of it. Guess "not dirty" for a section
    // that does regenerate and the postfix builds its window inline, as today. Guess "dirty" for one
    // that does not and a worker built a window nobody reads. Neither can produce a wrong pixel; the
    // gather phase is an optimisation and never a precondition.
    public static bool WillRegenerate(ulong sectionDirtyFlags, ulong layerRelevantChangeTypes) =>
        (sectionDirtyFlags & layerRelevantChangeTypes) != 0uL;
}
