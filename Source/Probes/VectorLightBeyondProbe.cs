using System.Collections.Generic;
using RimWorldTestHarness.Mod.Probes;
using UnityEngine;
using Verse;
using Verse.Glow;

namespace CelestialLighting.Probes;

// Reads `ours` and the sampled `vanilla` SIDE BY SIDE, over the cells beyond an emitter's own room.
//
// WHY THIS EXISTS RATHER THAN ANOTHER COMPOSITION. The gap arms produced three facts that cannot all
// be true, and the repo has now theorised its way to a plausible wrong answer about this same pair of
// numbers three separate times: #151 interpolated vanilla's VALUE per vertex and measured a no-op;
// the aperture section's first account blamed the mask trimming vanilla to our coverage, which the
// geometry contradicts; and the double subtraction was real but explained only half the deficit. The
// three facts, at cells one to two past a one-cell gap:
//
//   the aperture arm delivers +2.57 L*, so our model supposedly has that much to give;
//   the max arm draws nothing, so the fragment program computes ours - vanilla ~ 0 there;
//   the frame shows vanilla contributing +0.5 L* over the local background.
//
// A composition cannot be designed on top of that. `max(0, ours - vanilla)` is only as good as the
// two terms going into it, and nothing in the mod has ever reported them as numbers.
//
// SCOPED BY ROOF, AND THE FIRST CUT SCOPED BY ROOM AND MEASURED ALMOST NOTHING. "Cells beyond an
// opening" looks like "cells not in the emitter's own room", and RimWorld already computes rooms — but
// a roofed box with any opening in it IS an outdoor room as far as the game is concerned, so in the
// scene this probe exists for the emitter's own room is the great outdoors and the test matched one
// cell out of the whole field. That is why Cells is reported beside the peaks: it read 1, and a peak
// over one cell is not a measurement. Roof is the discriminator that survives, and it is also the
// plainer statement of the question — the light that got OUT is the light that reached an unroofed
// cell from a roofed lamp.
//
// A distance cut-off would have mixed the far corners of the lit room in with the ground outside it,
// and the whole point is to separate the region the max gets right from the region it does not.
// Cells the emitter's polygon cannot see at all are excluded too — coverage 0 means the mask has
// taken vanilla's light off there and neither term describes anything on screen.
//
// ALL FOUR METRICS COME FROM ONE WALK, and are registered as four probes over one shared read so a
// scenario pinning several of them cannot have them disagree about which frame they describe.
public sealed class VectorLightBeyondProbe : IProbe, IProbeMetadata
{
    public enum Metric
    {
        // Peak of our own model's glow, in vanilla's units, over the scoped cells. This is the
        // number the aperture arm's brightness is supposed to be evidence for.
        Ours,

        // Peak of what the shader samples for vanilla at those same cells — the per-emitter glow
        // grid, weighted by coverage when vector_light_gap_parity is on, which is what the fragment
        // program actually subtracts.
        Vanilla,

        // Peak of max(0, ours - vanilla): what the max composition has left to draw. If this is near
        // zero while Ours is large, the max is degenerate there and no amount of tuning it will
        // produce a beam.
        Excess,

        // How many cells the three above were taken over. A peak of zero means something completely
        // different when this is zero, and this probe's FIRST cut read 1 here — room scoping matched
        // almost nothing, and without this metric the peaks beside it would have been read as a
        // finding.
        Cells,
    }

    private readonly Metric metric;

    public VectorLightBeyondProbe(string name, Metric metric)
    {
        Name = name;
        this.metric = metric;
    }

    public string Name { get; }

    public string Description => metric switch
    {
        Metric.Ours => "peak of our model's glow on unroofed cells lit by a sheltered lamp",
        Metric.Vanilla => "peak of the vanilla glow the shader subtracts there",
        Metric.Excess => "peak of max(0, ours - vanilla) there — what the max has left to draw",
        _ => "cells the three above were measured over",
    };

    public string Unit => metric == Metric.Cells ? "cells" : "glow (0-1)";

    public float Read(Map map)
    {
        if (map == null)
            return 0f;

        GlowGridPerLight.Reader reader = GlowGridPerLight.For(map);

        if (reader == null)
            return 0f;

        float ours = 0f;
        float vanilla = 0f;
        float excess = 0f;
        int cells = 0;

        foreach (VectorLightField.LightEntry entry in VectorLightField.LightsFor(map))
            Accumulate(map, reader, entry, ref ours, ref vanilla, ref excess, ref cells);

        return metric switch
        {
            Metric.Ours => ours,
            Metric.Vanilla => vanilla,
            Metric.Excess => excess,
            _ => cells,
        };
    }

    private static void Accumulate(
        Map map, GlowGridPerLight.Reader reader, VectorLightField.LightEntry entry,
        ref float ours, ref float vanilla, ref float excess, ref int cells)
    {
        if (!reader.TryResolveEmitter(entry.VanillaKey, out GlowLight light, out var colors))
            return;

        // Whether the LAMP is sheltered. Only a sheltered lamp can have light that "got out", so an
        // outdoor emitter contributes nothing here rather than reporting its whole field.
        bool homeRoofed = map.roofGrid.Roofed(entry.Cell);

        if (!homeRoofed)
            return;

        // Our model's peak channel, so `ours` and `vanilla` are compared in the same reduction the
        // shader's max effectively performs per channel.
        Color colour = entry.Color;
        float peakChannel = Mathf.Max(colour.r, Mathf.Max(colour.g, colour.b));

        CellRect reach = light.AffectedRect;

        for (int z = reach.minZ; z <= reach.maxZ; z++)
        {
            for (int x = reach.minX; x <= reach.maxX; x++)
            {
                IntVec3 cell = new IntVec3(x, 0, z);

                if (!cell.InBounds(map) || map.roofGrid.Roofed(cell))
                    continue;

                int coverage = VectorLightMath.CoverageAt(
                    entry.Coverage, entry.Cell.x, entry.Cell.z, entry.CoverageRadius, x, z);

                // Coverage zero means our polygon cannot see the cell at all, so the mask has taken
                // vanilla's light off it and neither term describes anything on screen.
                if (coverage <= 0)
                    continue;

                int local = light.WorldToLocalIndex(cell);

                if (local < 0 || local >= colors.Length)
                    continue;

                float dx = x + 0.5f - (entry.Cell.x + 0.5f);
                float dz = z + 0.5f - (entry.Cell.z + 0.5f);
                float distance = Mathf.Sqrt(dx * dx + dz * dz);

                if (distance > entry.Radius)
                    continue;

                // The same curve the gradient bakes and the fragment program reads out of it. Not
                // the seeded variant: the stock additive path's curve is what _Color.rgb * gradient.a
                // evaluates to, and that is the quantity being compared here.
                float mine = peakChannel * VectorLightMath.Falloff(distance, entry.Radius);

                Color32 own = colors[local];
                float theirs = Mathf.Max(own.r, Mathf.Max(own.g, own.b)) / 255f;

                // Weighted the same way the upload weights it, so this reports what the shader
                // actually subtracts rather than what the grid happens to hold.
                if (CelestialLightingFeatures.VectorLightGapParity)
                    theirs = theirs * coverage / 255f;

                ours = Mathf.Max(ours, mine);
                vanilla = Mathf.Max(vanilla, theirs);
                excess = Mathf.Max(excess, Mathf.Max(0f, mine - theirs));
                cells++;
            }
        }
    }
}
