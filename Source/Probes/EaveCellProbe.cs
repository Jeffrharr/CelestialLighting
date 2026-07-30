using System;
using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead — the
// shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// Counts the cells on the map that §15's predicates admit. This is the input side of the subsystem
// measured directly.
//
// Why a count rather than something read off the shadow mesh. The visible effect IS a screenshot
// A/B — a porch going from sunlit to shaded is a large, unmistakable change, unlike §6a's 1-3/255
// moon shadow — so the screenshots are the evidence for the render half. What a screenshot cannot
// tell you is WHY a frame looks the way it does: an A/B showing no difference is equally consistent
// with "the predicate found nothing" and "the predicate worked but the mesh never rebuilt". This
// number separates those two, which is exactly the ambiguity that would otherwise cost an hour.
//
// TWO metrics because §15 has two predicates that differ on exactly one input, a mountain roof (see
// EavesMath). Reporting only one of them would leave the thick-roof seam invisible to the harness in
// precisely the way it was invisible in code review: the eave count is *correct* at a mined-out
// boundary and the shadow count is the one that was wrong. A scenario that pins both pins the split.
//
// Reads the same EavesMath predicates the renderer does rather than re-deriving them, so the probe
// can never agree with a formula the shipped code has stopped using. Deliberately NOT gated on
// CelestialLightingFeatures.EaveShadows: it reports what the map contains, not what is switched on,
// so a scenario can assert the count is stable across an A/B and attribute any image difference to
// the toggle alone.
public sealed class EaveCellProbe : IProbe
{
    public enum Metric
    {
        // Roofed, not under a mountain, in a room that breathes outdoor air — what §7b declines to
        // occlude and §15b paints. A porch.
        Eaves,

        // The same without the mountain veto — what §15 lets cast a roofline shadow. Never smaller
        // than Eaves, and larger by exactly the map's open-air thick-roofed cell count.
        ShadowCasters,
    }

    private readonly Metric metric;

    public string Name { get; }

    public EaveCellProbe(string name, Metric metric)
    {
        Name = name;
        this.metric = metric;
    }

    public float Read(Map map)
    {
        int count = 0;

        // Whole-map walk. This runs once per Probe step in a dev scenario, never per frame, so the
        // O(cells) cost that would be unacceptable inside Regenerate (the whole reason EaveShadowGrid
        // resolves a section window instead) is entirely fine here.
        foreach (IntVec3 cell in map.AllCells)
        {
            if (Admits(map, cell))
                count++;
        }

        return count;
    }

    private bool Admits(Map map, IntVec3 cell)
    {
        switch (metric)
        {
            case Metric.Eaves:
                return EaveCells.IsEave(map, cell);
            case Metric.ShadowCasters:
                return EaveCells.CastsRoofShadow(map, cell);
            default:
                throw new ArgumentOutOfRangeException(nameof(metric), metric, null);
        }
    }
}
